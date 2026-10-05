#region

using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>How a fault at a call site counts toward an extension's threshold.</summary>
public enum FaultKind
{
    /// <summary>Every throw counts.</summary>
    Counted,

    /// <summary>
    ///     A site that runs per frame or per input event: the same site and exception type count once a
    ///     minute, so one broken handler cannot trip the threshold from a single burst.
    /// </summary>
    Recurring,

    /// <summary>Logged against the extension, never counted.</summary>
    LogOnly
}

/// <summary>An extension's fault record this session, as Settings shows it.</summary>
/// <param name="Count">Faults counted toward the threshold since the last reset.</param>
/// <param name="LastSite">Where the last fault happened, or null with none.</param>
/// <param name="Suspended">True when the extension is off for the rest of the session.</param>
/// <param name="StartupFailed">True when it failed while starting, so turning it back on needs a restart.</param>
public sealed record ExtensionFaultState(int Count, string? LastSite, bool Suspended, bool StartupFailed)
{
    /// <summary>No faults.</summary>
    public static ExtensionFaultState None { get; } = new(0, null, false, false);
}

/// <summary>
///     Contains a defective extension: every host call into extension code that can throw goes through
///     <see cref="Run" /> (or a guard built from it), which logs the throw against the extension, counts it,
///     and returns a fallback so the host carries on. An extension that faults
///     <see cref="WindowLimit" /> times inside <see cref="Window" />, or <see cref="SessionLimit" /> times in
///     a session, is switched off for the rest of the session through <see cref="IFeatureSuspension" />.
///     The switch-off is always posted to the UI thread, never applied inline: faults arrive while the
///     host iterates handler lists, on queue threads and inside the gate's own change event. Faults the
///     extension raises while it is being switched off, or after, are logged and not counted.
///     <para>
///         One instance per process, built in the desktop head before Avalonia starts so the backstops can
///         attribute a crash; the composition root takes that instance, or builds its own in a test host.
///     </para>
/// </summary>
public sealed class ExtensionFaults
{
    /// <summary>Faults inside <see cref="Window" /> that switch an extension off.</summary>
    public const int WindowLimit = 3;

    /// <summary>Faults in one session that switch an extension off.</summary>
    public const int SessionLimit = 10;

    /// <summary>Full log entries per call site before the site is logged once a minute.</summary>
    public const int LoggedPerSite = 3;

    /// <summary>The sliding window <see cref="WindowLimit" /> counts in.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Action<Action> _post;
    private readonly ExtensionScope[] _scopes;
    private readonly Dictionary<string, Tally> _tallies = new(StringComparer.Ordinal);
    private IFeatureSuspension? _switch;

    /// <param name="scopes">Every loaded extension.</param>
    /// <param name="post">Runs an action later on the UI thread; null posts to the Avalonia dispatcher.</param>
    /// <param name="clock">The clock; null for the system's.</param>
    public ExtensionFaults(IEnumerable<ExtensionScope> scopes, Action<Action>? post = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = [.. scopes];
        _post = post ?? (static a => Dispatcher.UIThread.Post(a));
        _clock = clock ?? TimeProvider.System;
        foreach (ExtensionScope scope in _scopes)
        {
            _tallies[scope.Id] = new Tally();
        }
    }

    /// <summary>The process's instance, once the desktop head installed one.</summary>
    public static ExtensionFaults? Current { get; private set; }

    /// <summary>Every extension this instance knows.</summary>
    public IReadOnlyList<ExtensionScope> Scopes => _scopes;

    /// <summary>Raised on the UI thread when an extension's fault state changes.</summary>
    public event Action? Changed;

    /// <summary>One scope per loaded pack.</summary>
    public static ExtensionFaults For(IReadOnlyList<PackStatus> statuses, Action<Action>? post = null) =>
        new(statuses.Where(s => s.IsCompatible).Select(ExtensionScope.For), post);

    /// <summary>One scope per pack, named by its master switch.</summary>
    public static ExtensionFaults For(IReadOnlyList<IExtension> packs, Action<Action>? post = null) =>
        new(packs.Select(p => ExtensionScope.For(p)), post);

    /// <summary>Makes this the process's <see cref="Current" />.</summary>
    public ExtensionFaults Install()
    {
        Current = this;
        return this;
    }

    /// <summary>
    ///     Hands this instance the switch it turns extensions off with. An extension that already failed to
    ///     start is suspended on it at once, before anything reads the gate.
    /// </summary>
    public void AttachSwitch(IFeatureSuspension target)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<ExtensionScope> pending = [];
        lock (_lock)
        {
            _switch = target;
            foreach (ExtensionScope scope in _scopes)
            {
                if (_tallies[scope.Id].Suspended)
                {
                    pending.Add(scope);
                }
            }
        }

        foreach (ExtensionScope scope in pending)
        {
            target.SuspendForSession(scope.FeatureId);
        }
    }

    /// <summary>The extension with this id, or null.</summary>
    public ExtensionScope? ScopeOf(string extensionId) => _scopes.FirstOrDefault(s => s.Id == extensionId);

    /// <summary>The extension whose master switch this is, or null.</summary>
    public ExtensionScope? ScopeOfFeature(string featureId) => _scopes.FirstOrDefault(s => s.FeatureId == featureId);

    /// <summary>The extension <paramref name="assembly" /> belongs to, or null for the app's own code.</summary>
    public ExtensionScope? Owner(Assembly? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        foreach (ExtensionScope scope in _scopes)
        {
            if (scope.Owns(assembly))
            {
                return scope;
            }
        }

        return null;
    }

    /// <summary>The extension whose method <paramref name="handler" /> runs, or null for a host handler.</summary>
    public ExtensionScope? Owner(Delegate handler) => Owner(handler.Method.DeclaringType?.Assembly);

    /// <summary>The extension whose code is deepest on <paramref name="exception" />'s stack, or null.</summary>
    public ExtensionScope? Attribute(Exception exception) =>
        _scopes.Length == 0 ? null : StackAttribution.Find(exception, Owner);

    /// <summary>A guard that runs code as <paramref name="scope" />'s.</summary>
    public ExtensionGuard GuardFor(ExtensionScope scope) => new(this, scope);

    /// <summary>The guard for the extension with this id or master switch, or a guard over a scope built from the pack.</summary>
    public ExtensionGuard GuardFor(IExtension pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return new ExtensionGuard(this, ScopeOf(pack.Id) ?? ScopeOfFeature(pack.FeatureId) ?? ExtensionScope.For(pack));
    }

    /// <summary>The fault record of the extension behind <paramref name="featureId" />.</summary>
    public ExtensionFaultState StateOf(string featureId)
    {
        lock (_lock)
        {
            return ScopeOfFeature(featureId) is { } scope && _tallies.TryGetValue(scope.Id, out Tally? tally)
                ? new ExtensionFaultState(tally.Total, tally.LastSite, tally.Suspended, tally.StartupFailed)
                : ExtensionFaultState.None;
        }
    }

    /// <summary>Runs extension code, containing a throw.</summary>
    public void Run(ExtensionScope scope, string site, Action body, FaultKind kind = FaultKind.Counted)
    {
        try
        {
            body();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(scope, site, ex, kind);
        }
    }

    /// <summary>Runs extension code, returning <paramref name="fallback" /> when it throws.</summary>
    public T Run<T>(ExtensionScope scope, string site, Func<T> body, T fallback, FaultKind kind = FaultKind.Counted)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(scope, site, ex, kind);
            return fallback;
        }
    }

    /// <summary>
    ///     Awaits extension work, containing a throw or a faulted task. A cancellation counts only when
    ///     <paramref name="own" /> was not cancelled: the extension's own token stopping it is not a fault.
    /// </summary>
    public async Task RunAsync(ExtensionScope scope, string site, Func<Task> body, CancellationToken own = default)
    {
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(scope, site, ex);
        }
    }

    /// <summary><see cref="RunAsync(ExtensionScope, string, Func{Task}, CancellationToken)" /> with a result.</summary>
    public async Task<T> RunAsync<T>(ExtensionScope scope, string site, Func<Task<T>> body, T fallback,
        CancellationToken own = default)
    {
        try
        {
            return await body().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested)
        {
            return fallback;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(scope, site, ex);
            return fallback;
        }
    }

    /// <summary>
    ///     Records a fault: logs it (rate-limited per site), counts it unless <paramref name="kind" /> says
    ///     otherwise, and posts the switch-off when the threshold trips. Thread-safe.
    /// </summary>
    public void Report(ExtensionScope scope, string site, Exception exception, FaultKind kind = FaultKind.Counted)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(exception);
        Record(scope, site, exception, kind, false);
    }

    /// <summary>
    ///     A fault while the extension was starting (its registration, its commands, its lifecycle): it does
    ///     not start this session, whatever the count.
    /// </summary>
    public void FailStartup(ExtensionScope scope, string site, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(exception);
        Record(scope, site, exception, FaultKind.Counted, true);
    }

    /// <summary>True when <paramref name="extensionId" /> failed while starting this session.</summary>
    public bool StartupFailed(string extensionId)
    {
        lock (_lock)
        {
            return _tallies.TryGetValue(extensionId, out Tally? tally) && tally.StartupFailed;
        }
    }

    /// <summary>A binding error in the extension's views: logged against it, never counted.</summary>
    public void ReportBinding(ExtensionScope scope, string message)
    {
        ArgumentNullException.ThrowIfNull(scope);
        int count;
        bool log;
        lock (_lock)
        {
            Tally tally = TallyOf(scope);
            count = tally.Total;
            log = tally.ShouldLog("binding", _clock.GetUtcNow());
        }

        if (log)
        {
            AppLog.ExtensionFaulted(Log, scope.Name, "binding: " + message, count, null);
        }
    }

    /// <summary>
    ///     Turns the extension back on for the session and resets its count. Not for an extension that failed
    ///     to start: its services do not exist until a restart.
    /// </summary>
    public void Resume(string featureId)
    {
        if (ScopeOfFeature(featureId) is not { } scope)
        {
            return;
        }

        IFeatureSuspension? target;
        lock (_lock)
        {
            Tally tally = TallyOf(scope);
            if (tally.StartupFailed)
            {
                return;
            }

            tally.Reset();
            target = _switch;
        }

        target?.ResumeForSession(featureId);
        RaiseChanged();
    }

    /// <summary>Forgets the count of an extension the user kept off; its master switch now says off by itself.</summary>
    public void Acknowledge(string featureId)
    {
        if (ScopeOfFeature(featureId) is not { } scope)
        {
            return;
        }

        IFeatureSuspension? target;
        lock (_lock)
        {
            Tally tally = TallyOf(scope);
            tally.Reset();
            target = _switch;
        }

        target?.ResumeForSession(featureId);
        RaiseChanged();
    }

    private void Record(ExtensionScope scope, string site, Exception exception, FaultKind kind, bool startup)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        bool log;
        bool trip = false;
        bool counts;
        int count;
        lock (_lock)
        {
            Tally tally = TallyOf(scope);
            counts = kind != FaultKind.LogOnly && !tally.Suspended && tally.Counts(site, exception, kind, now);
            if (counts || startup)
            {
                tally.Add(site, now);
            }

            if (startup && !tally.Suspended)
            {
                tally.StartupFailed = true;
                tally.Suspended = true;
                tally.SuspendPending = _switch is not null;
                trip = tally.SuspendPending;
            }
            else if (counts && !tally.SuspendPending
                     && (tally.Recent.Count >= WindowLimit || tally.Total >= SessionLimit))
            {
                tally.SuspendPending = true;
                trip = true;
            }

            count = tally.Total;
            log = tally.ShouldLog(site, now);
        }

        if (log)
        {
            AppLog.ExtensionFaulted(Log, scope.Name, site, count, exception);
        }

        if (trip)
        {
            _post(() => Suspend(scope));
        }
        else if (counts || startup)
        {
            _post(RaiseChanged);
        }
    }

    // Runs on the UI thread. Marked suspended before the switch moves, so every fault the extension raises
    // while it is being turned off (Detach, OnDisabledAsync, its own Changed handlers) is not counted.
    private void Suspend(ExtensionScope scope)
    {
        IFeatureSuspension? target;
        int count;
        string site;
        lock (_lock)
        {
            Tally tally = TallyOf(scope);
            if (!tally.SuspendPending)
            {
                return;
            }

            tally.SuspendPending = false;
            tally.Suspended = true;
            target = _switch;
            count = tally.Total;
            site = tally.LastSite ?? "";
        }

        AppLog.ExtensionSuspended(Log, scope.Name, count, site);
        try
        {
            target?.SuspendForSession(scope.FeatureId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.OperationFailed(Log, "turn off extension " + scope.Name, ex);
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Action? handler = Changed;
        if (handler is null)
        {
            return;
        }

        foreach (Action each in handler.GetInvocationList().Cast<Action>())
        {
            try
            {
                each();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.OperationFailed(Log, "extension fault notice", ex);
            }
        }
    }

    // A scope the instance was not built with (a test's pack, a host built without the loader) gets a
    // tally on first fault.
    private Tally TallyOf(ExtensionScope scope)
    {
        if (!_tallies.TryGetValue(scope.Id, out Tally? tally))
        {
            tally = new Tally();
            _tallies[scope.Id] = tally;
        }

        return tally;
    }

    private static ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory);

    private sealed class Tally
    {
        private readonly Dictionary<string, (int Logged, DateTimeOffset Last)> _logged = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Site, Type Type), DateTimeOffset> _recurring = [];

        public Queue<DateTimeOffset> Recent { get; } = new();
        public int Total { get; private set; }
        public string? LastSite { get; private set; }
        public bool Suspended { get; set; }
        public bool SuspendPending { get; set; }
        public bool StartupFailed { get; set; }

        public bool Counts(string site, Exception exception, FaultKind kind, DateTimeOffset now)
        {
            if (kind != FaultKind.Recurring)
            {
                return true;
            }

            (string, Type) key = (site, exception.GetType());
            if (_recurring.TryGetValue(key, out DateTimeOffset last) && now - last < Window)
            {
                return false;
            }

            _recurring[key] = now;
            return true;
        }

        public void Add(string site, DateTimeOffset now)
        {
            Total++;
            LastSite = site;
            Recent.Enqueue(now);
            while (Recent.Count > 0 && now - Recent.Peek() >= Window)
            {
                Recent.Dequeue();
            }
        }

        public bool ShouldLog(string site, DateTimeOffset now)
        {
            _logged.TryGetValue(site, out (int Logged, DateTimeOffset Last) entry);
            if (entry.Logged >= LoggedPerSite && now - entry.Last < Window)
            {
                return false;
            }

            _logged[site] = (entry.Logged + 1, now);
            return true;
        }

        public void Reset()
        {
            Recent.Clear();
            _recurring.Clear();
            Total = 0;
            LastSite = null;
            Suspended = false;
            SuspendPending = false;
        }
    }
}

/// <summary>Finds the first stack frame, deepest first, whose assembly a matcher claims.</summary>
public static class StackAttribution
{
    /// <summary>
    ///     The first match on <paramref name="exception" />'s stack. The innermost exception is searched first,
    ///     since it is where the failure started; a wrapper's frames are mostly whoever caught and rethrew it.
    /// </summary>
    public static T? Find<T>(Exception exception, Func<Assembly, T?> match) where T : class
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(match);
        List<Exception> chain = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add(current);
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            Exception current = chain[i];
            if (current is AggregateException { InnerExceptions.Count: > 1 } many)
            {
                foreach (Exception inner in many.InnerExceptions)
                {
                    if (Find(inner, match) is { } hit)
                    {
                        return hit;
                    }
                }
            }

            foreach (StackFrame frame in new StackTrace(current, false).GetFrames())
            {
                if (frame.GetMethod()?.DeclaringType?.Assembly is { } assembly && match(assembly) is { } hit)
                {
                    return hit;
                }
            }
        }

        return null;
    }
}

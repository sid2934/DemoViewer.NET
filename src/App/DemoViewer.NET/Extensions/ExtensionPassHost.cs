#region

using System.Diagnostics;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     An extension's pass as one of the app's passes. A throw from <see cref="IExtensionPass.Interest" /> or
///     <see cref="IExtensionPass.Run" /> reaches the queue and the scheduler, which skip the pass for that demo
///     and count the fault once against the extension. On top of that the host never asks the pass on the UI
///     thread, hands it the parse only for its turn, and switches it off for the session when its runs keep
///     overrunning their budget: a run on the held parse cannot be abandoned, so a hang is the UI watchdog's.
/// </summary>
internal sealed class ExtensionPassHost : IDemoPass, IPassScheduling
{
    /// <summary>How long a run may take before it counts as an overrun.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(5);

    /// <summary>Overruns in a session that switch the pass off.</summary>
    public const int DefaultMaxOverruns = 3;

    private static ILogger? _log;

    private readonly TimeSpan _budget;
    private readonly ExtensionGuard _guard;
    private readonly int _maxOverruns;
    private readonly PassNeeds _needs;
    private readonly Func<bool> _onUiThread;
    private int _overruns;
    private volatile bool _quarantined;

    /// <param name="inner">The extension's pass.</param>
    /// <param name="after">The pass ids it runs after, as registered.</param>
    /// <param name="guard">The extension's guard.</param>
    /// <param name="onUiThread">True on the UI thread; the dispatcher's check when null.</param>
    /// <param name="budget">How long a run may take; <see cref="DefaultBudget" /> when null.</param>
    /// <param name="maxOverruns">Overruns that switch the pass off for the session.</param>
    public ExtensionPassHost(IExtensionPass inner, IReadOnlyList<string> after, ExtensionGuard guard,
        Func<bool>? onUiThread = null, TimeSpan? budget = null, int maxOverruns = DefaultMaxOverruns)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        After = after ?? throw new ArgumentNullException(nameof(after));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _onUiThread = onUiThread ?? (static () => Dispatcher.UIThread.CheckAccess());
        _budget = budget ?? DefaultBudget;
        _maxOverruns = Math.Max(1, maxOverruns);
        // Read once: a throwing getter makes the factory fail, which the registry reports.
        Id = inner.Id;
        _needs = new PassNeeds(ParseMode.Retained, ForwardNeeds.None,
            guard.Run("pass " + Id + " needs", () => inner.ReadsUserCommands, true));
    }

    private static ILogger Log => _log ??= DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory);

    /// <summary>The extension's pass.</summary>
    public IExtensionPass Inner { get; }

    /// <summary>True once the pass overran its budget too often; it wants no demo for the rest of the session.</summary>
    public bool IsQuarantined => _quarantined;

    public string Id { get; }

    public IReadOnlyList<string> After { get; }

    // Switching the extension off cancels its queued passes with its jobs.
    public string Owner => _guard.Scope.Id;

    public PassNeeds Needs(VisitedDemo demo) => _needs;

    public PassInterest Interest(VisitedDemo demo, PassLevel level)
    {
        if (_quarantined)
        {
            return PassInterest.No;
        }

        if (_onUiThread())
        {
            AppLog.PassAskedOnUiThread(Log, Id, _guard.Scope.Name, demo.FileName);
            return PassInterest.No;
        }

        return Inner.Interest(demo.Path) switch
        {
            DemoInterest.Yes => PassInterest.Yes,
            DemoInterest.AfterUpstream => PassInterest.IfUpstreamRuns,
            _ => PassInterest.No
        };
    }

    public void Run(PassInput input)
    {
        if (input.Retained is not { } parsed)
        {
            throw new InvalidOperationException("An extension pass runs on the retained parse.");
        }

        Context context = new(input.Demo.Path, parsed, input.CancellationToken);
        long started = Stopwatch.GetTimestamp();
        try
        {
            Inner.Run(context);
        }
        finally
        {
            context.End();
            NoteRunTime(Stopwatch.GetElapsedTime(started));
        }
    }

    public void OnFailed(VisitedDemo demo, Exception failure) =>
        _guard.Run("pass " + Id + " failure handler", () => Inner.OnFailed(demo.Path));

    // At most the backlog's level. A demo the user asked for is planned at user-requested level by the request
    // itself, so a pass cannot lift its own whole-library backlog ahead of the user's work.
    public PassLevel LevelFor(VisitedDemo demo) =>
        Inner.PriorityFor(demo.Path) switch
        {
            JobPriority.UserRequested or JobPriority.Backlog => PassLevel.Backlog,
            _ => PassLevel.Background
        };

    public long OrderHint(VisitedDemo demo) => Inner.OrderHint(demo.Path);

    private void NoteRunTime(TimeSpan elapsed)
    {
        if (elapsed <= _budget || Interlocked.Increment(ref _overruns) != _maxOverruns)
        {
            return;
        }

        _quarantined = true;
        AppLog.PassQuarantined(Log, Id, _guard.Scope.Name, _budget, _maxOverruns);
        _guard.Report("pass " + Id,
            new TimeoutException($"Pass {Id} overran its {_budget} budget {_maxOverruns} times and is off for this session."));
    }

    private sealed class Context(string path, ParsedDemo parsed, CancellationToken cancellationToken) : IPassContext
    {
        private ParsedDemo? _parsed = parsed;

        public string DemoPath => path;

        public ParsedDemo Parsed => _parsed ?? throw new ObjectDisposedException(nameof(IPassContext), "The pass's turn has ended.");

        public CancellationToken CancellationToken => cancellationToken;

        public void End() => _parsed = null;
    }
}

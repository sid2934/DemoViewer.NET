#region

using System.Runtime.CompilerServices;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Runs code as one extension's: a throw is reported to <see cref="ExtensionFaults" /> against that
///     extension and the caller gets a fallback. The <c>Wrap</c> helpers build a guarded delegate once, at
///     registration, so a per-frame call allocates nothing.
/// </summary>
/// <param name="faults">Where faults are reported.</param>
/// <param name="scope">The extension the guarded code belongs to.</param>
public sealed class ExtensionGuard(ExtensionFaults faults, ExtensionScope scope)
{
    /// <summary>Where faults are reported.</summary>
    public ExtensionFaults Faults { get; } = faults ?? throw new ArgumentNullException(nameof(faults));

    /// <summary>The extension the guarded code belongs to.</summary>
    public ExtensionScope Scope { get; } = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>A guard for a pack in a host with no process-wide fault tracker (designer, tests).</summary>
    public static ExtensionGuard Standalone(IExtension pack) => ExtensionFaults.For([pack], static a => a()).GuardFor(pack);

    /// <inheritdoc cref="ExtensionFaults.Run" />
    public void Run(string site, Action body, FaultKind kind = FaultKind.Counted) => Faults.Run(Scope, site, body, kind);

    /// <inheritdoc cref="ExtensionFaults.Run{T}" />
    public T Run<T>(string site, Func<T> body, T fallback, FaultKind kind = FaultKind.Counted) =>
        Faults.Run(Scope, site, body, fallback, kind);

    /// <inheritdoc cref="ExtensionFaults.RunAsync(ExtensionScope, string, Func{Task}, CancellationToken)" />
    public Task RunAsync(string site, Func<Task> body, CancellationToken own = default) =>
        Faults.RunAsync(Scope, site, body, own);

    /// <inheritdoc cref="ExtensionFaults.RunAsync{T}" />
    public Task<T> RunAsync<T>(string site, Func<Task<T>> body, T fallback, CancellationToken own = default) =>
        Faults.RunAsync(Scope, site, body, fallback, own);

    /// <summary>
    ///     The feature a contribution at <paramref name="site" /> is gated on: <paramref name="requested" /> when the
    ///     extension owns it, its master switch when it names none. Naming another owner's feature is reported and
    ///     gated on the master switch instead.
    /// </summary>
    public string OwnFeature(string site, string? requested)
    {
        string own = Scope.OwnFeature(requested);
        if (requested is not null && !string.Equals(own, requested, StringComparison.Ordinal))
        {
            Report(site, new ArgumentException($"'{requested}' is not one of {Scope.Id}'s features; gated on {own} instead."));
        }

        return own;
    }

    /// <summary>Reports a fault the caller caught itself.</summary>
    public void Report(string site, Exception exception, FaultKind kind = FaultKind.Counted) =>
        Faults.Report(Scope, site, exception, kind);

    /// <summary>A guarded copy of <paramref name="body" />.</summary>
    public Action Wrap(string site, Action body, FaultKind kind = FaultKind.Counted)
    {
        ArgumentNullException.ThrowIfNull(body);
        return () =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Report(site, ex, kind);
            }
        };
    }

    /// <summary>A guarded copy of <paramref name="body" />.</summary>
    public Action<T> Wrap<T>(string site, Action<T> body, FaultKind kind = FaultKind.Counted)
    {
        ArgumentNullException.ThrowIfNull(body);
        return arg =>
        {
            try
            {
                body(arg);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Report(site, ex, kind);
            }
        };
    }

    /// <summary>A guarded copy of <paramref name="body" />, answering <paramref name="fallback" /> on a throw.</summary>
    public Func<TResult> Wrap<TResult>(string site, Func<TResult> body, TResult fallback, FaultKind kind = FaultKind.Counted)
    {
        ArgumentNullException.ThrowIfNull(body);
        return () =>
        {
            try
            {
                return body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Report(site, ex, kind);
                return fallback;
            }
        };
    }

    /// <summary>A guarded copy of <paramref name="body" />, answering <paramref name="fallback" /> on a throw.</summary>
    public Func<T, TResult> Wrap<T, TResult>(string site, Func<T, TResult> body, TResult fallback,
        FaultKind kind = FaultKind.Counted)
    {
        ArgumentNullException.ThrowIfNull(body);
        return arg =>
        {
            try
            {
                return body(arg);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Report(site, ex, kind);
                return fallback;
            }
        };
    }
}

/// <summary>
///     The guard each extension-contributed object was collected under, so a host site that only holds the
///     object (a module, a tab descriptor) can run it as its extension's. Weak keys: an entry dies with its
///     object.
/// </summary>
public static class ExtensionGuards
{
    private static readonly ConditionalWeakTable<object, ExtensionGuard> _byObject = new();

    /// <summary>Records <paramref name="guard" /> for <paramref name="contributed" />.</summary>
    public static void Register(object contributed, ExtensionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(contributed);
        ArgumentNullException.ThrowIfNull(guard);
        _byObject.AddOrUpdate(contributed, guard);
    }

    /// <summary>The guard <paramref name="contributed" /> was collected under, or null for host code.</summary>
    public static ExtensionGuard? For(object? contributed) =>
        contributed is not null && _byObject.TryGetValue(contributed, out ExtensionGuard? guard) ? guard : null;
}

/// <summary>
///     Invokes a multicast host event so a throwing extension handler cannot skip the subscribers after it.
///     Only handlers whose method is an extension's are guarded: a host handler that throws still throws,
///     so host bugs surface. The per-handler owners are cached against the delegate instance, which
///     is immutable, so a per-frame raise with an unchanged subscriber list does no attribution work.
/// </summary>
/// <param name="site">The call site's name in the log.</param>
/// <param name="kind">How a fault here counts.</param>
public sealed class MulticastGuard<THandler>(string site, FaultKind kind = FaultKind.Counted) where THandler : Delegate
{
    private volatile Snapshot? _cache;

    /// <summary>Invokes every handler in <paramref name="multicast" /> with <paramref name="state" />.</summary>
    public void Invoke<TState>(ExtensionFaults faults, THandler? multicast, TState state, Action<THandler, TState> invoke)
    {
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(invoke);
        if (multicast is null)
        {
            return;
        }

        Snapshot? cached = _cache;
        if (cached is null || !ReferenceEquals(cached.Multicast, multicast))
        {
            cached = new Snapshot(faults, multicast);
            _cache = cached;
        }

        if (!cached.Any)
        {
            invoke(multicast, state);
            return;
        }

        foreach ((THandler handler, ExtensionScope? owner) in cached.Entries)
        {
            if (owner is null)
            {
                invoke(handler, state);
                continue;
            }

            try
            {
                invoke(handler, state);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                faults.Report(owner, site, ex, kind);
            }
        }
    }

    private sealed class Snapshot
    {
        public Snapshot(ExtensionFaults faults, THandler multicast)
        {
            Multicast = multicast;
            Delegate[] list = multicast.GetInvocationList();
            Entries = new (THandler, ExtensionScope?)[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                ExtensionScope? owner = faults.Owner(list[i]);
                Any |= owner is not null;
                Entries[i] = ((THandler)list[i], owner);
            }
        }

        public THandler Multicast { get; }
        public (THandler Handler, ExtensionScope? Owner)[] Entries { get; }
        public bool Any { get; }
    }
}

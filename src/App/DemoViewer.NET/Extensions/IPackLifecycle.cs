namespace DemoViewer.NET.Extensions;

/// <summary>Why <see cref="IPackLifecycle.OnEnabledAsync" /> is running.</summary>
public enum PackStartReason
{
    /// <summary>The pack resolved on at startup.</summary>
    Startup,

    /// <summary>The user turned the pack on in a running session.</summary>
    EnabledInSession
}

/// <summary>
///     Optional pack lifecycle, resolved from the pack's own registrations. Called only while the pack's
///     umbrella id resolves on.
/// </summary>
public interface IPackLifecycle
{
    /// <summary>Startup loads and subscriptions.</summary>
    Task OnEnabledAsync(PackStartReason reason, CancellationToken ct);

    /// <summary>
    ///     Unsubscribe, cancel owned jobs, release resident indexes. The returned task completes when the
    ///     release has run (off the UI thread, as a queue item), so a caller can observe the memory gone.
    /// </summary>
    Task OnDisabledAsync();

    /// <summary>Flushes, within <paramref name="budget" />. Must never construct a store that was not built.</summary>
    void OnShutdown(TimeSpan budget);
}

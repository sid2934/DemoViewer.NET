namespace DemoViewer.NET.Extensions;

/// <summary>
///     Packs whose <see cref="IPackLifecycle.OnEnabledAsync" /> actually ran this session. Shutdown reads
///     this instead of calling every pack's lifecycle: a pack that resolved off at startup never built
///     anything, so its shutdown must never run either.
/// </summary>
internal sealed class PackLifecycleRegistry
{
    private readonly List<IPackLifecycle> _ran = [];

    /// <summary>Every lifecycle whose <see cref="IPackLifecycle.OnEnabledAsync" /> this session called.</summary>
    public IReadOnlyList<IPackLifecycle> Ran => _ran;

    /// <summary>Records that <paramref name="lifecycle" /> started, for a matching shutdown flush.</summary>
    public void MarkRan(IPackLifecycle lifecycle) => _ran.Add(lifecycle);
}

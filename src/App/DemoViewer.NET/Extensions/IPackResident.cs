namespace DemoViewer.NET.Extensions;

/// <summary>
///     A pack-built singleton whose state can be dropped in session and rebuilt later. The object keeps
///     its identity (core surfaces hold references to it and the container owns it once), so "release"
///     means: unsubscribe from the sources that would refill it, drop the state built since
///     <see cref="Attach" />, and answer empty until the next load. Both calls are idempotent.
/// </summary>
public interface IPackResident
{
    /// <summary>Subscribes to the sources that keep the state current. Loads nothing by itself.</summary>
    void Attach();

    /// <summary>Unsubscribes and drops the state; pending writes are flushed first. The object stays usable.</summary>
    void Release();
}

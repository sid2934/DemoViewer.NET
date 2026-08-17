namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     Read-only view over the authoritative entity set at the current tick. A
///     thin wrapper over the live <c>EntitySet</c> (no copy) inside the <c>Advanced</c> callback, and
///     over a captured snapshot for on-activation resync. Exposes no mutators.
/// </summary>
public interface IReadOnlyEntityView
{
    /// <summary>All live entities (in-PVS and dormant).</summary>
    IEnumerable<IReadOnlyEntity> All();

    /// <summary>Live entities of the given serializer class, e.g. <c>"CCSPlayerPawn"</c>.</summary>
    IEnumerable<IReadOnlyEntity> OfClass(string className);

    /// <summary>The entity with the given network serial, or null.</summary>
    IReadOnlyEntity? BySerial(int serial);

    /// <summary>The entity at the given entity-array index, or null.</summary>
    IReadOnlyEntity? ByIndex(int entityIndex);

    /// <summary>
    ///     Resolves a CS2 entity handle (masks the low 14 bits to an entity index, then
    ///     <see cref="ByIndex" />) — the only raw-graph traversal the pilot needs after the host join,
    ///     for one-hop weapon lookups. Handles arrive as <c>UInt64</c>; coerce, don't
    ///     <c>is uint</c> (project_cs2_wire_encoding).
    /// </summary>
    IReadOnlyEntity? ResolveHandle(ulong handle);
}

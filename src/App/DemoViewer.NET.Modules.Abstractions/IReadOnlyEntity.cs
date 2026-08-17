namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     A read-only view of one live entity at the current tick. Exposes the
///     allocation-free field accessor; never a mutator. The indexer maps to the underlying
///     <c>EntityState["path"]</c> accessor — NEVER <c>EntityState.Fields</c> (which rebuilds a full
///     dict per entity; profiling showed it as the dominant entity-tracking alloc).
/// </summary>
public interface IReadOnlyEntity
{
    /// <summary>Serializer / server-class name, e.g. <c>"CCSPlayerPawn"</c>.</summary>
    string ClassName { get; }

    /// <summary>Network serial number (identity across the entity's lifetime).</summary>
    int Serial { get; }

    /// <summary>True when the entity is in the current PVS (not dormant).</summary>
    bool IsInPvs { get; }

    /// <summary>
    ///     Allocation-free boxed field read by dotted path, e.g. <c>["m_iHealth"]</c>,
    ///     <c>["m_pWeaponServices.m_hActiveWeapon"]</c>. Null when the field is unseen/absent.
    ///     NOTE: world position is NOT a leaf here — read it from
    ///     <see cref="IPlayerState.WorldPosition" /> (host-reconstructed); <c>m_vecOrigin</c>/<c>.Origin</c>
    ///     returns null.
    /// </summary>
    object? this[string fieldPath] { get; }

    /// <summary>Typed field read with coercion; returns false when absent or the type mismatches.</summary>
    bool TryGet<T>(string fieldPath, out T value);
}

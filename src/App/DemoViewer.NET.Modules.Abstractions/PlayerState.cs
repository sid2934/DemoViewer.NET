#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     Per-tick, host-joined player state. The host performs the CS2 player-join ONCE per
///     tick (reverse <c>m_hController</c> via PawnLookup + cell→world position via PositionUtil) and
///     hands every module this list on the snapshot, so no module ever touches <c>m_hController</c> or
///     re-rolls the position math.
///     <para>
///         <b>TRANSIENT — valid only inside the <c>Advanced</c> callback.</b> Copy out the scalars you
///         need; do not retain the instance (it may be pooled and re-aimed for the next push).
///     </para>
///     Identity (slot/steamID/name) lives on the stable <see cref="PlayerRosterEntry" />; join by
///     <see cref="Slot" />. Team is here, NOT on the roster, because it is volatile (side-swap at half).
/// </summary>
// The module contract binds the public name to `PlayerState` (not `IPlayerState`); CA1715's
// I-prefix convention is deliberately overridden so the as-built signature matches the spec exactly.
[SuppressMessage("Naming", "CA1715:Identifiers should have correct prefix",
    Justification = "The module contract binds the name to 'PlayerState'.")]
public interface IPlayerState
{
    /// <summary>Stable 0-based player slot (also the join key into the roster).</summary>
    int Slot { get; }

    /// <summary>VOLATILE team number (side-swap at half / spectate); lives here, not on the roster.</summary>
    int Team { get; }

    /// <summary>
    ///     False for spectators / unassigned / pre-spawn slots (no live pawn) — the module skips these
    /// rather than rendering a phantom marker.
    /// </summary>
    bool HasLivePawn { get; }

    /// <summary>The current pawn via the reverse <c>m_hController</c> join, or null when no live pawn.</summary>
    IReadOnlyEntity? Pawn { get; }

    /// <summary>The bound controller entity, or null.</summary>
    IReadOnlyEntity? Controller { get; }

    /// <summary>
    ///     Reconstructed world position (cell + in-cell offset → world; PositionUtil owns the verified
    ///     constant). Null when there is no live pawn.
    /// </summary>
    (float X, float Y, float Z)? WorldPosition { get; }
}

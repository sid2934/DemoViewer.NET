namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     Per-entity level assignment with change detection, so anything holding temporal state can drop it
///     exactly when an entity changes floor.
///     <para>
///         <b>This is the boltobserv streak.</b> A marker's drawn position is chased toward its sample
///         over several frames; a player who takes the Nuke lift or a Vertigo drop moves hundreds of
///         units in X/Y at the same instant they change floor, and a smoothed dot glides that whole
///         distance in view. The teleport rule catches some of it and misses the rest — the two floors'
///         positions can be close in plan. Snapping on the crossing itself catches all of it.
///     </para>
///     <para>
///         Assignment uses the <b>sticky spatial band and no dwell</b> (plan D4): an entity must never
///         lag its own level, but it must also not report a crossing every frame while a player stands
///         on a boundary. Keyed by roster <c>Slot</c> — <c>PlayerMarker</c> carries a SteamId only from
///         B2, and slots are what every layer already has.
///     </para>
/// </summary>
public sealed class LevelCrossingTracker
{
    private readonly HashSet<int> _crossed = new(8);
    private readonly Dictionary<int, MapLevelId> _levels = new(16);

    /// <summary>The slots that changed level on the current frame.</summary>
    public IReadOnlyCollection<int> CrossedSlots => _crossed;

    /// <summary>How many entities are currently tracked.</summary>
    public int Count => _levels.Count;

    /// <summary>
    ///     Resolves and records this frame's level for one entity.
    /// </summary>
    /// <param name="slot">Roster slot.</param>
    /// <param name="worldZ">The entity's world Z.</param>
    /// <param name="space">The level set.</param>
    /// <returns>True when the level changed since the previous frame.</returns>
    public bool Update(int slot, double worldZ, MapSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);

        if (space.Levels.Count == 0)
        {
            return false;
        }

        bool known = _levels.TryGetValue(slot, out MapLevelId previous);
        MapLevel? resolved = space.LevelFor(worldZ, known ? previous : null);
        if (resolved is null)
        {
            return false;
        }

        _levels[slot] = resolved.Id;
        if (!known || resolved.Id == previous)
        {
            return false;
        }

        _crossed.Add(slot);
        return true;
    }

    /// <summary>Whether <see cref="Update" /> reported a change for this slot on the CURRENT frame.</summary>
    /// <param name="slot">Roster slot.</param>
    public bool Crossed(int slot) => _crossed.Count > 0 && _crossed.Contains(slot);

    /// <summary>The level this slot is on, or <see cref="MapLevelId.None" /> when it is not tracked.</summary>
    /// <param name="slot">Roster slot.</param>
    public MapLevelId LevelOf(int slot) =>
        _levels.TryGetValue(slot, out MapLevelId id) ? id : MapLevelId.None;

    /// <summary>
    ///     Clears the per-frame crossing set. Called by the frame owner after every layer has advanced —
    ///     a crossing is true for exactly one frame, which is what makes "snap once" mean once.
    /// </summary>
    public void EndFrame()
    {
        if (_crossed.Count > 0)
        {
            _crossed.Clear();
        }
    }

    /// <summary>
    ///     Drops every assignment: a demo change, a <see cref="MapSpace" /> rebuild, or a
    ///     <c>SceneTime.IsDiscontinuity</c>. After a rebuild every cached assignment is stale, so the
    ///     next frame re-resolves and — correctly — reports no crossing for entities that merely got
    ///     re-keyed.
    /// </summary>
    public void Reset()
    {
        _levels.Clear();
        _crossed.Clear();
    }
}

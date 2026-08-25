namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>How the active level is being chosen.</summary>
public enum LevelSelectionMode
{
    /// <summary>The user picked a level from the strip and it stays picked.</summary>
    Manual,

    /// <summary>The active level tracks the followed player, through <see cref="LevelHysteresis" />.</summary>
    AutoFollow
}

/// <summary>
///     Owns "which level does <see cref="SingleLayout" /> show".
///     <para>
///         Under the stacked layout every floor has its own pane and the follow camera simply holds a
///         pane whose player is elsewhere (the pre-v2 <c>TryFollow</c> returns false and the band does
///         not move). Under a single pane there is nowhere to hold: "which level is shown" stops being
///         a per-pane filter and becomes a decision. This is that decision.
///     </para>
///     <para>
///         <b>It holds when the followed marker is absent</b> — a dead, disconnected or not-yet-spawned
///         player leaves the view exactly where it was, mirroring the viewport's graceful-orphan follow,
///         rather than snapping to the lowest floor.
///     </para>
/// </summary>
public sealed class LevelSelection
{
    private readonly LevelHysteresis _hysteresis;
    private readonly MapSpace _space;
    private MapLevelId _active = MapLevelId.None;

    /// <summary>Creates a selection over a level set.</summary>
    /// <param name="space">The level set. Not owned.</param>
    /// <param name="options">Hysteresis tuning; the defaults when null.</param>
    public LevelSelection(MapSpace space, LevelHysteresisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(space);
        _space = space;
        _hysteresis = new LevelHysteresis(options);
    }

    /// <summary>How the active level is currently chosen. Starts on <see cref="LevelSelectionMode.AutoFollow" />.</summary>
    public LevelSelectionMode Mode { get; private set; } = LevelSelectionMode.AutoFollow;

    /// <summary>The level a single-pane layout should show. <see cref="MapLevelId.None" /> before the first update.</summary>
    public MapLevelId ActiveLevelId => _active;

    /// <summary>The roster slot AutoFollow tracks; null (or negative) means nothing is followed.</summary>
    public int? FollowedSlot { get; set; }

    /// <summary>The hysteresis driving AutoFollow. Exposed for diagnostics and tests.</summary>
    public LevelHysteresis Hysteresis => _hysteresis;

    /// <summary>Raised whenever <see cref="ActiveLevelId" /> changes.</summary>
    public event Action? ActiveLevelChanged;

    /// <summary>
    ///     Call once per scene frame, before layout. Allocation-free: an indexed scan for the followed
    ///     slot, a dictionary-free level lookup and two doubles.
    /// </summary>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="frame">The frame being advanced to.</param>
    /// <returns>True when the active level changed.</returns>
    public bool Update(in SceneTime time, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IReadOnlyList<MapLevel> levels = _space.Levels;
        if (levels.Count == 0)
        {
            return false;
        }

        if (Mode == LevelSelectionMode.Manual)
        {
            // A pinned level that a rebuild took away falls back like an unknown one would.
            return _space.ById(_active) is null && Adopt(TopMost());
        }

        if (FollowedSlot is not { } slot || slot < 0)
        {
            return _active.IsNone && Adopt(TopMost());
        }

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Slot != slot)
            {
                continue;
            }

            return Adopt(_hysteresis.Update(in time, markers[i].WorldZ, _space));
        }

        // The followed marker is not in this frame. Hold — do NOT fall back to level 0.
        return _active.IsNone && Adopt(TopMost());
    }

    /// <summary>Pins a level. Switches to <see cref="LevelSelectionMode.Manual" />.</summary>
    /// <param name="id">The level to show.</param>
    public void PickManually(MapLevelId id)
    {
        Mode = LevelSelectionMode.Manual;
        _hysteresis.ForceTo(id);
        Adopt(id);
    }

    /// <summary>
    ///     Re-arms AutoFollow. The dwell is cleared so the next update switches immediately rather than
    ///     making the user wait 0.35 s for a decision they just asked for.
    /// </summary>
    public void EnableAutoFollow()
    {
        Mode = LevelSelectionMode.AutoFollow;

        // Reset, not ForceTo(_active): a cleared chooser adopts the followed player's floor on the very
        // next frame. Making the user wait out a dwell for a decision they just asked for reads as a
        // dead control.
        _hysteresis.Reset();
    }

    /// <summary>
    ///     Subscribe to <see cref="MapSpace.LevelSetChanged" />. Drops the dwell (every cached level
    ///     assignment is stale) and falls back to the top-most level when the active one is gone.
    /// </summary>
    public void OnLevelSetChanged()
    {
        _hysteresis.Reset();

        if (_space.Levels.Count == 0)
        {
            Adopt(MapLevelId.None);
            return;
        }

        if (_space.ById(_active) is null)
        {
            Adopt(TopMost());
        }
    }

    private MapLevelId TopMost()
    {
        IReadOnlyList<MapLevel> levels = _space.Levels;
        return levels.Count == 0 ? MapLevelId.None : levels[^1].Id;
    }

    private bool Adopt(MapLevelId id)
    {
        if (id == _active)
        {
            return false;
        }

        _active = id;
        ActiveLevelChanged?.Invoke();
        return true;
    }
}

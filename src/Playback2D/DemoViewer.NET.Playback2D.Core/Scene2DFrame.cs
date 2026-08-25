namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     One frame's complete world state, published by reference to the compositor and every layer.
///     <para>
///         <b>Lifetime contract (decision D6).</b> A frame is valid only until the next
///         <c>SceneFrameBuilder.Build</c> call on the <b>same builder</b>. The builder double-buffers
///         two instances with pooled backing lists and refills the off-screen one in place, which is
///         what makes the §6 zero-steady-state-allocation budget reachable. Consumers must not retain a
///         frame across pushes; a consumer that needs one (export) drives its own builder.
///     </para>
///     <para>
///         Every property is <c>init</c>-only to callers, so a fixture or a test builds one with an
///         object initializer and nothing outside can mutate a published frame. The builder writes the
///         <c>internal</c> backing fields directly instead — that is what lets it refill in place rather
///         than allocating a frame per push.
///     </para>
///     <para>
///         Deliberately absent: overlay visibility toggles (they are <c>ISceneLayer.IsEnabled</c>,
///         decision D5) and resolved floor levels (derived from <see cref="Map" />'s section heights by
///         B1's <c>MapSpaceFactory</c>, decision D3).
///     </para>
/// </summary>
public sealed class Scene2DFrame
{
    /// <summary>The empty frame a consumer holds before the first build. Safe to render.</summary>
    public static readonly Scene2DFrame Empty = new();

    internal IReadOnlyList<AreaEffect> AreaEffectsField = [];
    internal BombMarker? BombField;
    internal int FollowSlotField = -1;
    internal SceneGameInfo GameInfoField = SceneGameInfo.Empty;
    internal IReadOnlyList<KillFeedRow> KillFeedField = [];
    internal SceneMapInfo MapField = SceneMapInfo.Unknown;
    internal IReadOnlyList<PlayerMarker> MarkersField = [];
    internal SceneTime TimeField;
    internal IReadOnlyList<GrenadeTrail> TrailsField = [];
    internal SceneVision VisionField = SceneVision.Off;

    /// <summary>The injected clock for this frame.</summary>
    public SceneTime Time
    {
        get => TimeField;
        init => TimeField = value;
    }

    /// <summary>One entry per drawable player, including gray death markers at last-known positions.</summary>
    public IReadOnlyList<PlayerMarker> Markers
    {
        get => MarkersField;
        init => MarkersField = value;
    }

    /// <summary>Active smoke clouds and burning inferno cells.</summary>
    public IReadOnlyList<AreaEffect> AreaEffects
    {
        get => AreaEffectsField;
        init => AreaEffectsField = value;
    }

    /// <summary>Live grenade flight trails with ≥2 points, newest point last.</summary>
    public IReadOnlyList<GrenadeTrail> Trails
    {
        get => TrailsField;
        init => TrailsField = value;
    }

    /// <summary>The planted-C4 ring state, or null when no live ticking bomb is positioned.</summary>
    public BombMarker? Bomb
    {
        get => BombField;
        init => BombField = value;
    }

    /// <summary>The kill rows currently inside the display window, oldest first.</summary>
    public IReadOnlyList<KillFeedRow> KillFeed
    {
        get => KillFeedField;
        init => KillFeedField = value;
    }

    /// <summary>Round-level HUD state.</summary>
    public SceneGameInfo GameInfo
    {
        get => GameInfoField;
        init => GameInfoField = value;
    }

    /// <summary>Per-map static facts: identity, extents, floor inputs, radar layers.</summary>
    public SceneMapInfo Map
    {
        get => MapField;
        init => MapField = value;
    }

    /// <summary>Solved line-of-sight geometry, or <see cref="SceneVision.Off" />.</summary>
    public SceneVision Vision
    {
        get => VisionField;
        init => VisionField = value;
    }

    /// <summary>The followed roster slot, or -1 for none. Read by the camera rigs and the marker layer.</summary>
    public int FollowSlot
    {
        get => FollowSlotField;
        init => FollowSlotField = value;
    }
}

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The stable layer ids. <b>Persisted keys</b>: feature gates, settings and the layer panel all
///     store them, so they are never renamed once shipped.
/// </summary>
public static class SceneLayerIds
{
    /// <summary>Baked radar image, falling back to the synthetic grid.</summary>
    public const string Radar = "playback2d.radar";

    /// <summary>Grenade flight trails.</summary>
    public const string Trails = "playback2d.trails";

    /// <summary>Smoke clouds and inferno cells.</summary>
    public const string AreaEffects = "playback2d.areaeffects";

    /// <summary>View cones and could-see sightlines.</summary>
    public const string Vision = "playback2d.vision";

    /// <summary>Player discs, rings, heading stubs and labels.</summary>
    public const string Markers = "playback2d.markers";

    /// <summary>Planted-C4 diamond and timer rings.</summary>
    public const string Bomb = "playback2d.bomb";

    /// <summary>Per-band floor caption.</summary>
    public const string FloorLabel = "playback2d.floorlabel";

    /// <summary>User ink: dry, animated and wet annotation strokes.</summary>
    public const string Annotations = "playback2d.annotations";

    /// <summary>
    ///     Player cards down both pane edges, T on one side and CT on the other. Ordered 65, between the
    ///     floor caption and the clock, so a card sits over the map but under the scoreboard it would
    ///     otherwise crowd at the top centre.
    /// </summary>
    public const string HudRoster = "hud.roster";

    /// <summary>
    ///     Round number, score and the main countdown, burned into an export. Deliberately un-prefixed:
    ///     it is a HUD layer, not a 2D-playback overlay, and the same id names it in
    ///     <c>dv2d render --layers</c> and in a saved export preset.
    /// </summary>
    public const string HudClock = "hud.clock";

    /// <summary>The kill feed, burned into an export.</summary>
    public const string HudKillFeed = "hud.killfeed";

    /// <summary>
    ///     The layers a stack registers <b>only when the caller names them</b>. Off under a null or empty
    ///     include set: an export that silently burned in a scoreboard, or someone else's telestration,
    ///     would be a surprise rather than a feature.
    ///     <para>
    ///         <b>One set, three readers</b>: <c>SceneLayerCatalog.CreateSceneStack</c>,
    ///         <c>SceneExportSession.OptInLayerIds</c>, and <c>ExportRequest.LayerIds</c>'s contract must
    ///         all agree, so a new opt-in layer is one line HERE rather than three.
    ///     </para>
    /// </summary>
    public static IReadOnlySet<string> OptIn { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Annotations,
            HudRoster,
            HudClock,
            HudKillFeed
        };
}

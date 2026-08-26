namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The stable layer ids. <b>Persisted keys</b> — feature gates, settings and the layer panel all
///     store them, so they are never renamed once shipped (registry §3.3).
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

    /// <summary>User ink: dry, animated and wet annotation strokes (B2).</summary>
    public const string Annotations = "playback2d.annotations";

    /// <summary>
    ///     Player cards down both pane edges — T on one side, CT on the other (D3b, registry D0 §3.1).
    ///     Ordered 65, between the floor caption and the clock, so a card sits over the map but under the
    ///     scoreboard it would otherwise crowd at the top centre.
    /// </summary>
    public const string HudRoster = "hud.roster";

    /// <summary>
    ///     Round number, score and the main countdown, burned into an export (B4 D15). Deliberately
    ///     un-prefixed: it is a HUD layer, not a 2D-playback overlay, and the same id names it in
    ///     <c>dv2d render --layers</c> and in a saved export preset.
    /// </summary>
    public const string HudClock = "hud.clock";

    /// <summary>The kill feed, burned into an export (B4 D15).</summary>
    public const string HudKillFeed = "hud.killfeed";

    /// <summary>
    ///     The layers a stack registers <b>only when the caller names them</b>. Off under a null or empty
    ///     include set: an export that silently burned in a scoreboard — or someone else's telestration —
    ///     would be a surprise, not a feature.
    ///     <para>
    ///         <b>One set, three readers</b> (registry §3.1): <c>SceneLayerCatalog.CreateSceneStack</c>,
    ///         <c>SceneExportSession.OptInLayerIds</c>, and <c>ExportRequest.LayerIds</c>'s
    ///         contract. Those were three hand-written pair-lists, and an id that learned two of the three
    ///         was force-enabled on every export by the third — so a new opt-in layer is one line HERE.
    ///     </para>
    /// </summary>
    public static IReadOnlySet<string> OptIn { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Annotations, HudRoster, HudClock, HudKillFeed };
}

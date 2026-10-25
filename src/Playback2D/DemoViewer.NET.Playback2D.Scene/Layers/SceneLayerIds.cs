namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The stable layer ids. <b>Persisted keys</b>: feature gates, settings and the layer panel all
///     store them, so they are never renamed once shipped.
///     <para>
///         <b>static readonly, not const, deliberately.</b> A const is copied into every assembly that
///         reads it, so an extension compiled against one would keep passing the old string after an id
///         changed here: the layer would never register, and nothing would reference this type for a
///         binding test to catch. A field load cannot go stale that way. It is also why the switches over
///         these ids use <c>when</c> clauses rather than constant patterns.
///     </para>
/// </summary>
public static class SceneLayerIds
{
    /// <summary>Baked radar image, falling back to the synthetic grid.</summary>
    public static readonly string Radar = "playback2d.radar";

    /// <summary>Grenade flight trails.</summary>
    public static readonly string Trails = "playback2d.trails";

    /// <summary>Smoke clouds and inferno cells.</summary>
    public static readonly string AreaEffects = "playback2d.areaeffects";

    /// <summary>View cones and could-see sightlines.</summary>
    public static readonly string Vision = "playback2d.vision";

    /// <summary>Player discs, rings, heading stubs and labels.</summary>
    public static readonly string Markers = "playback2d.markers";

    /// <summary>Planted-C4 diamond and timer rings.</summary>
    public static readonly string Bomb = "playback2d.bomb";

    /// <summary>The strat canvas's drag ghost and destination pins; empty on every other frame.</summary>
    public static readonly string Guides = "playback2d.guides";

    /// <summary>Per-band floor caption.</summary>
    public static readonly string FloorLabel = "playback2d.floorlabel";

    /// <summary>User ink: dry, animated and wet annotation strokes.</summary>
    public static readonly string Annotations = "playback2d.annotations";

    /// <summary>
    ///     Place outlines and labels from the baked <c>zones.json</c> plus the user overlay. Opt-in and
    ///     off by default like the ink: it needs a <c>PlaceResolver</c>, which only a map with a zones
    ///     file can supply, and a map overlay nobody asked for is clutter.
    /// </summary>
    public static readonly string Zones = "playback2d.zones";

    /// <summary>
    ///     Player cards down both pane edges, T on one side and CT on the other. Ordered 65, between the
    ///     floor caption and the clock, so a card sits over the map but under the scoreboard it would
    ///     otherwise crowd at the top centre.
    /// </summary>
    public static readonly string HudRoster = "hud.roster";

    /// <summary>
    ///     Round number, score and the main countdown, burned into an export. Deliberately un-prefixed:
    ///     it is a HUD layer, not a 2D-playback overlay, and the same id names it in
    ///     <c>dv2d render --layers</c> and in a saved export preset.
    /// </summary>
    public static readonly string HudClock = "hud.clock";

    /// <summary>The kill feed, burned into an export.</summary>
    public static readonly string HudKillFeed = "hud.killfeed";

    /// <summary>
    ///     The Query Canvas tokens: the Situations tab's placed place tokens. Opt-in like the ink, and for
    ///     the same reason: it draws a document a caller has to supply, so an export or a fixture render
    ///     that did not name it never grows ten discs it did not ask for.
    /// </summary>
    public static readonly string Query = "playback2d.query";

    /// <summary>
    ///     The Overlay View heatmap: every matched state of a Situations result set stacked onto the
    ///     map. Opt-in for the query tokens' reason: it draws a document a caller has to supply, and
    ///     an export or a fixture render that did not name it never grows a heat wash it did not ask for.
    /// </summary>
    public static readonly string Overlay = "playback2d.overlay";

    /// <summary>The Utility Book map: landing groups and a focused group's throw positions and flights.</summary>
    public static readonly string Utility = "playback2d.utility";

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
            Zones,
            HudRoster,
            HudClock,
            HudKillFeed,
            Query,
            Overlay
        };
}

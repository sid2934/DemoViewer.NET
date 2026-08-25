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
}

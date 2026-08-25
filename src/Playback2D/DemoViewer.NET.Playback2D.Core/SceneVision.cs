namespace DemoViewer.NET.Playback2D.Core;

/// <summary>One clipped ray end on a view cone's fan, in world space.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
public readonly record struct ConePoint(float X, float Y);

/// <summary>
///     One player's horizontal FOV footprint: a fan of rays across their view cone, each already
///     clipped to the first collision surface at eye height.
/// </summary>
public sealed class VisionCone
{
    /// <summary>The viewing player's roster slot.</summary>
    public int Slot { get; init; }

    /// <summary>The viewing player's team (2 = T, 3 = CT).</summary>
    public int Team { get; init; }

    /// <summary>Cone apex world X (the player's eye).</summary>
    public float ApexX { get; init; }

    /// <summary>Cone apex world Y.</summary>
    public float ApexY { get; init; }

    /// <summary>Cone apex world Z.</summary>
    public float ApexZ { get; init; }

    /// <summary>The clipped ray ends, ordered by angle, so the fan fills as a single polygon.</summary>
    public IReadOnlyList<ConePoint> Fan { get; init; } = [];
}

/// <summary>A could-see segment between one viewer and one target, already resolved in world space.</summary>
/// <param name="ViewerSlot">The viewing player's roster slot.</param>
/// <param name="ViewerTeam">The viewing player's team.</param>
/// <param name="X0">Segment start world X.</param>
/// <param name="Y0">Segment start world Y.</param>
/// <param name="Z0">Segment start world Z.</param>
/// <param name="X1">Segment end world X.</param>
/// <param name="Y1">Segment end world Y.</param>
/// <param name="Z1">Segment end world Z.</param>
public readonly record struct Sightline(
    int ViewerSlot,
    int ViewerTeam,
    float X0,
    float Y0,
    float Z0,
    float X1,
    float Y1,
    float Z1);

/// <summary>
///     The frame's line-of-sight geometry, already solved. The visibility engine lives in
///     <c>CS2DemoKit.Analysis.Visibility</c>, which Core may not reference, so the solve happens in
///     Pipeline and Core's <c>VisionLayer</c> only draws the result (decision D4). That also
///     pre-satisfies the §6 mitigation "vision Advance moves off the UI thread".
/// </summary>
public sealed class SceneVision
{
    /// <summary>The frame state when the overlay is off or no engine is loaded for this map.</summary>
    public static readonly SceneVision Off = new();

    /// <summary>Whether a visibility engine was loaded for this map — false means "no data", not "nothing seen".</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Per-player FOV footprints.</summary>
    public IReadOnlyList<VisionCone> Cones { get; init; } = [];

    /// <summary>Could-see segments between opposing players.</summary>
    public IReadOnlyList<Sightline> Sightlines { get; init; } = [];
}

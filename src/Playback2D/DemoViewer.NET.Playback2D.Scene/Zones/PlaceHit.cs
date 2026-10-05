#region

using System.Numerics;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>Which step of the resolver's cascade answered.</summary>
public enum PlaceHitKind
{
    /// <summary>A place volume contains the point: the game's own rule, exact.</summary>
    Volume,

    /// <summary>No volume; the nearest same-floor nav area within the snap distance answered.</summary>
    NearestArea,

    /// <summary>Nothing within reach.</summary>
    None
}

/// <summary>A resolved place. <see cref="Name" /> is null and <see cref="PlaceId" /> is -1 for a miss.</summary>
/// <param name="PlaceId">The effective place id, or -1.</param>
/// <param name="Name">The raw place name, or null.</param>
/// <param name="Kind">Which cascade step answered.</param>
/// <param name="SnapDistance">XY distance to the area that answered; 0 for a volume hit, infinite for a miss.</param>
public readonly record struct PlaceHit(int PlaceId, string? Name, PlaceHitKind Kind, double SnapDistance)
{
    /// <summary>The miss.</summary>
    public static readonly PlaceHit None = new(-1, null, PlaceHitKind.None, double.PositiveInfinity);

    /// <summary>True when a place answered.</summary>
    public bool IsHit => Kind != PlaceHitKind.None;
}

/// <summary>
///     The drawable outline of one place on one floor: the boundary edges of its nav areas (an area
///     polygon edge with no same-place neighbour across it) and a label position at the area-weighted
///     centroid. A custom zone's outline is its own polygon.
/// </summary>
/// <param name="PlaceId">The effective place id.</param>
/// <param name="Name">The place name, for the label.</param>
/// <param name="Origin">Baked or custom, so the layer can style the two apart.</param>
/// <param name="Edges">World-XY segments.</param>
/// <param name="LabelAt">Where the label goes, world XY.</param>
public sealed record PlaceOutline(
    int PlaceId,
    string Name,
    PlaceOrigin Origin,
    IReadOnlyList<(Vector2 A, Vector2 B)> Edges,
    Vector2 LabelAt);

/// <summary>
///     One thing the overlay loader skipped or flagged, for the Rule Workbench's diagnostics surface.
///     <see cref="Code" /> is dotted like a ruleset diagnostic (<c>zones.overlay.unknown-floor</c>).
/// </summary>
/// <param name="Code">The stable code.</param>
/// <param name="Message">What was written and what was expected.</param>
/// <param name="Entry">The overlay entry involved (a zone name, a merge target), or null.</param>
public sealed record ZoneDiagnostic(string Code, string Message, string? Entry = null);

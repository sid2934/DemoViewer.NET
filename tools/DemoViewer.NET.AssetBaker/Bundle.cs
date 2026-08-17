#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>The world→radar affine placement (raw overview-txt values; consumer applies).</summary>
public sealed record RadarTransform(
    double PosX,
    double PosY,
    double Scale,
    double Rotate,
    double Zoom,
    int ImageSize);

/// <summary>The map's world-space playable rectangle (derived from the radar coverage).</summary>
public sealed record WorldBounds(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>One nav-derived walkable floor band (world Z) — feeds the app's FloorSplitter.SetSectionHeights.</summary>
public sealed record FloorBand(double MinZ, double MaxZ);

/// <summary>overview-txt verticalsection → which radar PNG applies over a Z band (image-selection, not storeys).</summary>
public sealed record RadarLayer(double MinZ, double MaxZ, string Image);

/// <summary>
///     Reference to the baked world-collision triangle blob (<c>collision.tris</c>) — the geometry the app
///     raycasts for 3D line-of-sight. Absent (null) when a map has no extractable collision. Bounds are the
///     collision AABB (world units), useful as a coarse pre-reject and a frame-alignment sanity check.
/// </summary>
public sealed record CollisionMeshRef(
    string File,
    int TriangleCount,
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);

/// <summary>
///     A baked, ship-ready map-asset bundle (docs/asset-pipeline/design.md §0.5). Serialized as bundle.json next to
///     the radar PNG(s). The app loads this VRF-free and selects it by (MapName × MapVersion). The nav-derived
///     <see cref="Floors" /> are the headline: real walkable Z bands, computed once, no runtime nav parse.
/// </summary>
public sealed record AssetBundle(
    int SchemaVersion,
    string MapName,
    string MapVersion, // CRC32 (hex) over the source bytes: radar vtex + nav + overview txt. Content-derived.
    string BakerVersion, // baker + VRF version, so a decode-behaviour change invalidates the shipped bundle.
    RadarTransform Transform,
    WorldBounds Bounds,
    IReadOnlyList<FloorBand> Floors,
    IReadOnlyList<RadarLayer> RadarLayers,
    IReadOnlyList<string> RadarImages,
    CollisionMeshRef? CollisionMesh = null) // additive; older/2D consumers ignore unknown fields
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, _jsonOptions);
}

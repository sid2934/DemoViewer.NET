#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline;

// The on-disk shape of a SceneFixture. Deliberately a separate, plain, mutable DTO layer rather than
// serializing the Core types directly: the scene types are positional record structs, and letting
// System.Text.Json pick a constructor for those is exactly the kind of implicit contract a persisted
// format should not have. Every level that can carry forward-compatible data has [JsonExtensionData],
// so a fixture written by a NEWER build survives a read/write round trip through this one intact
// (design §5.4's tolerant-reader rule, enforced by SceneFixtureTests).

internal sealed class SceneFixtureDto
{
    public string? SchemaVersion { get; set; }
    public SceneTimeDto? Time { get; set; }
    public ViewportTransformDto? Camera { get; set; }
    public SizeDto? Size { get; set; }
    public string? MapName { get; set; }
    public string? MapVersion { get; set; }
    public Scene2DFrameDto? Frame { get; set; }
    public JsonElement? Annotations { get; set; }
    public string? SourceDemoId { get; set; }
    public string? Notes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class Scene2DFrameDto
{
    public SceneTimeDto? Time { get; set; }
    public List<PlayerMarkerDto>? Markers { get; set; }
    public List<AreaEffectDto>? AreaEffects { get; set; }
    public List<GrenadeTrailDto>? Trails { get; set; }
    public BombMarkerDto? Bomb { get; set; }
    public List<KillFeedRowDto>? KillFeed { get; set; }
    public SceneGameInfoDto? GameInfo { get; set; }
    public SceneMapInfoDto? Map { get; set; }
    public SceneVisionDto? Vision { get; set; }
    public int? FollowSlot { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SceneTimeDto
{
    public int Tick { get; set; }
    public int FrameIndex { get; set; }
    public double DemoSeconds { get; set; }
    public double DeltaSeconds { get; set; }
    public bool IsDiscontinuity { get; set; }
}

internal sealed class ViewportTransformDto
{
    public double ViewWidth { get; set; }
    public double ViewHeight { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double BaseScale { get; set; }
    public double Zoom { get; set; }
    public double PanX { get; set; }
    public double PanY { get; set; }
}

internal sealed class SizeDto
{
    public int Width { get; set; }
    public int Height { get; set; }
}

internal sealed class WorldBoundsDto
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

internal sealed class PlayerMarkerDto
{
    public int Slot { get; set; }
    public int Team { get; set; }
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public float WorldZ { get; set; }
    public float YawDegrees { get; set; }
    public string? Ring { get; set; }
    public double RingAlpha { get; set; }
    public string? Label { get; set; }
    public bool IsAlive { get; set; }
    public float PitchDegrees { get; set; }
    public float DuckAmount { get; set; }
    public ulong SteamId { get; set; }
}

internal sealed class AreaEffectDto
{
    public string? Kind { get; set; }
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public float WorldZ { get; set; }
    public float WorldRadius { get; set; }
}

internal sealed class GrenadeTrailDto
{
    public string? Kind { get; set; }
    public List<TrailPointDto>? Points { get; set; }
    public int LastTick { get; set; }
    public double Alpha { get; set; }
}

internal sealed class TrailPointDto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

internal sealed class BombMarkerDto
{
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public float WorldZ { get; set; }
    public double DetonationFraction { get; set; }
    public bool BeingDefused { get; set; }
    public double DefuseFraction { get; set; }
}

internal sealed class KillFeedRowDto
{
    public int Tick { get; set; }
    public string? Attacker { get; set; }
    public string? Assister { get; set; }
    public string? Victim { get; set; }
    public string? Weapon { get; set; }
    public bool Headshot { get; set; }
    public bool Penetrated { get; set; }
    public bool NoScope { get; set; }
    public bool ThroughSmoke { get; set; }
    public bool AttackerBlind { get; set; }
    public bool AttackerInAir { get; set; }
    public bool AssistedFlash { get; set; }
}

internal sealed class SceneGameInfoDto
{
    public string? Phase { get; set; }
    public string? BombState { get; set; }
    public int RoundNumber { get; set; }
    public int RoundsPlayed { get; set; }
    public double RoundSeconds { get; set; }
    public string? RoundTime { get; set; }
    public bool BombTicking { get; set; }
    public bool DefuseInProgress { get; set; }
    public string? DefuseKitNote { get; set; }
    public double DefuseSeconds { get; set; }
    public string? DefuseTime { get; set; }
    public int TScore { get; set; }
    public int CtScore { get; set; }
}

internal sealed class SceneMapInfoDto
{
    public string? MapName { get; set; }
    public WorldBoundsDto? NetworkedBounds { get; set; }
    public WorldBoundsDto? ObservedBounds { get; set; }
    public List<double>? SectionHeights { get; set; }
    public List<MapRadarImageDto>? Radars { get; set; }
}

// The SKImage itself is never serialized — a fixture describes a scene, not a decoded bitmap.
// MapAssetPipeline re-attaches the image by Name at load (B1); until then it stays null.
internal sealed class MapRadarImageDto
{
    public string? Name { get; set; }
    public WorldBoundsDto? Bounds { get; set; }
    public double MinZ { get; set; }
    public double MaxZ { get; set; }
}

internal sealed class SceneVisionDto
{
    public bool IsAvailable { get; set; }
    public List<VisionConeDto>? Cones { get; set; }
    public List<SightlineDto>? Sightlines { get; set; }
}

internal sealed class VisionConeDto
{
    public int Slot { get; set; }
    public int Team { get; set; }
    public float ApexX { get; set; }
    public float ApexY { get; set; }
    public float ApexZ { get; set; }
    public List<ConePointDto>? Fan { get; set; }
}

internal sealed class ConePointDto
{
    public float X { get; set; }
    public float Y { get; set; }
}

internal sealed class SightlineDto
{
    public int ViewerSlot { get; set; }
    public int ViewerTeam { get; set; }
    public float X0 { get; set; }
    public float Y0 { get; set; }
    public float Z0 { get; set; }
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float Z1 { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SceneFixtureDto))]
internal sealed partial class SceneFixtureJsonContext : JsonSerializerContext;

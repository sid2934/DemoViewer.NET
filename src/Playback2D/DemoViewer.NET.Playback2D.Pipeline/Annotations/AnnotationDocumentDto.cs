#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Annotations;

// The on-disk shape of an annotation sidecar. A separate mutable DTO layer rather than the Core value
// types, for the same reason SceneFixtureDto is: the Core types are positional records and letting
// System.Text.Json pick a constructor for those is an implicit contract a persisted format must not
// have.
//
// Both the ROOT and the ELEMENT carry [JsonExtensionData]. That is the tolerant-reader half of the
// format contract: a v2 field written by a newer build survives being loaded, edited and saved by this
// build instead of being silently dropped on the floor.

internal sealed class AnnotationDocumentDto
{
    public int SchemaVersion { get; set; } = AnnotationStore.SchemaVersion;

    public DemoIdentityDto? Demo { get; set; }

    public ClockIdentityDto? Clock { get; set; }

    public List<AnnotationElementDto>? Elements { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class DemoIdentityDto
{
    public string? Sha256 { get; set; }

    public string? FileName { get; set; }

    public long SizeBytes { get; set; }
}

internal sealed class ClockIdentityDto
{
    public string? Kind { get; set; }

    public int TickRate { get; set; }

    public int FrameCount { get; set; }

    public int FirstTick { get; set; }

    public int LastTick { get; set; }
}

internal sealed class AnnotationElementDto
{
    public string? Id { get; set; }

    public string? Kind { get; set; }

    public uint ColorArgb { get; set; }

    public float WidthWorld { get; set; }

    public float Opacity { get; set; } = 1f;

    public bool RevealOnFadeIn { get; set; }

    /// <summary><c>world</c> or <c>entity</c>.</summary>
    public string? Space { get; set; }

    public double LevelMinZ { get; set; }

    public ulong SteamId { get; set; }

    public float Dx { get; set; }

    public float Dy { get; set; }

    public int? FromTick { get; set; }

    public int? UntilTick { get; set; }

    public int FadeInTicks { get; set; }

    public int FadeOutTicks { get; set; }

    /// <summary>Flat <c>[x, y, pressure, x, y, pressure, …]</c> — a stroke is thousands of numbers.</summary>
    public List<float>? Points { get; set; }

    public string? Text { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(AnnotationDocumentDto))]
internal sealed partial class AnnotationJsonContext : JsonSerializerContext;

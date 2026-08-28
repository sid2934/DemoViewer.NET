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

    /// <summary>Flat <c>[x, y, pressure, x, y, pressure, …]</c>: a stroke is thousands of numbers.</summary>
    public List<float>? Points { get; set; }

    public string? Text { get; set; }

    /// <summary>The authoring cadence for an <c>EnvelopeMode.RealTime</c> stroke; absent for every other element.</summary>
    /// <remarks>
    ///     Nullable: with <c>DefaultIgnoreCondition = WhenWritingNull</c> below, an element with no
    ///     cadence emits no field, so this stays additive within schema 1 rather than a version bump.
    /// </remarks>
    public AnnotationTimingDto? Timing { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     A stroke's <c>StrokeTiming</c> on disk: the sparse table of boundaries where the authoring speed
///     changed, plus how long the whole stroke took.
///     <para>
///         One nested object rather than two sibling fields on the element, because the two halves are
///         one value: <c>StrokeTiming.Equals</c> compares both, so a document carrying runs without a
///         duration is not a degraded cadence but a different one, and a shape that cannot express that
///         split cannot be hand-edited into it.
///     </para>
/// </summary>
internal sealed class AnnotationTimingDto
{
    /// <summary>
    ///     Flat <c>[sampleIndex, tickOffset, sampleIndex, tickOffset, …]</c>, exactly as
    ///     <see cref="AnnotationElementDto.Points" /> flattens its triples. A pair per boundary rather
    ///     than an object per boundary: the table is small but the file is INDENTED, so an object costs
    ///     four lines and two key names where a pair costs two numbers.
    /// </summary>
    public List<int>? Runs { get; set; }

    /// <summary>Ticks from the first sample to the last. 0 for an instant stroke.</summary>
    public int DurationTicks { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(AnnotationDocumentDto))]
internal sealed partial class AnnotationJsonContext : JsonSerializerContext;

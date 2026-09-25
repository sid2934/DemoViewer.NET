#region

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Services.Tags;

// The on-disk shape of a tag sidecar (<config>/tags/demos/<sha256>.dvtag.json) and of the index beside
// it. Mutable classes serialized directly, the DemoCacheRecord idiom, rather than a DTO layer over
// records: the session edits instances field by field and clones them through the same context, so a
// second shape would only be a second place for a field to go missing.
//
// Every object carries a [JsonExtensionData] bag. That is the tolerant-reader half of the format: a field
// written by a newer build survives load, edit and save by this one. Property order is declaration order,
// which is what keeps the schema golden byte-stable.

/// <summary>The spellings of <see cref="TagInstance.Source" />.</summary>
public static class TagSources
{
    /// <summary>Made by a person, with the palette or by hand.</summary>
    public const string Human = "human";

    /// <summary>An accepted Suggested Tags proposal. Kept distinct so the Matrix can stratify by it.</summary>
    public const string Suggested = "suggested";

    /// <summary>Reserved for a future importer (tag-store.md §3.8). Nothing writes it today.</summary>
    public const string Import = "import";
}

/// <summary>One demo's tags: the source of truth for everything the Round Tagger stores.</summary>
public sealed class TagDocument
{
    /// <summary>Advisory: a higher number is read for what this build understands.</summary>
    public int SchemaVersion { get; set; } = TagStore.SchemaVersion;

    /// <summary>Which demo. Only <see cref="TagDemoHeader.Sha256" /> takes part in matching.</summary>
    public TagDemoHeader Demo { get; set; } = new();

    /// <summary>The frame clock the ticks were written against.</summary>
    public TagClockHeader Clock { get; set; } = new();

    /// <summary>The palette id the session used last. Advisory; the palette need not exist.</summary>
    public string? Palette { get; set; }

    /// <summary>The instances, in the order they were made.</summary>
    public List<TagInstance> Instances { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>A fresh document for a demo.</summary>
    /// <param name="demo">The demo's identity.</param>
    /// <param name="clock">The clock the session's parse is on.</param>
    public static TagDocument Create(DemoIdentity demo, ClockIdentity clock)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(clock);
        return new TagDocument
        {
            Demo = new TagDemoHeader
            {
                Sha256 = demo.Sha256.ToLowerInvariant(),
                FileName = demo.FileName,
                SizeBytes = demo.SizeBytes
            },
            Clock = TagClockHeader.From(clock)
        };
    }

    /// <summary>A deep copy, unknown fields included: what the autosave hands off the UI thread.</summary>
    public TagDocument Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, TagJsonContext.Default.TagDocument),
            TagJsonContext.Default.TagDocument)!;
}

/// <summary>The <c>demo</c> header. The name and size are for a person reading the file.</summary>
public sealed class TagDemoHeader
{
    /// <summary>Lowercase-hex SHA-256 of the demo's bytes; the file is named by it.</summary>
    public string Sha256 { get; set; } = "";

    public string? FileName { get; set; }

    public long SizeBytes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The <c>clock</c> header: <see cref="ClockIdentity" /> as the annotation sidecar spells it.</summary>
public sealed class TagClockHeader
{
    public string Kind { get; set; } = ClockIdentity.DvFrameClock;

    public int TickRate { get; set; }

    public int FrameCount { get; set; }

    public int FirstTick { get; set; }

    public int LastTick { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The header for a clock.</summary>
    /// <param name="clock">The session's clock.</param>
    public static TagClockHeader From(ClockIdentity clock) => new()
    {
        Kind = clock.Kind,
        TickRate = clock.TickRate,
        FrameCount = clock.FrameCount,
        FirstTick = clock.FirstTick,
        LastTick = clock.LastTick
    };

    /// <summary>The clock this header records.</summary>
    public ClockIdentity ToClock() => new(Kind, TickRate, FrameCount, FirstTick, LastTick);
}

/// <summary>
///     A timeline instance: a named span of one demo with the labels said about it.
///     <para>
///         <b>Ticks, never frame indices.</b> <see cref="FromTick" /> and <see cref="ToTick" /> are frame-clock
///         ticks, inclusive, the same values the annotation sidecar and <c>CachedRound.StartTickFrameClock</c>
///         carry. Frame indices shift on a re-parse; ticks do not.
///     </para>
///     <para>
///         <b>Two label namespaces.</b> <see cref="Labels" /> is what a person said; <see cref="Facts" /> is what
///         the parser derived and belongs to the refresh pass, which rewrites the whole array without reading
///         the human one. Both share one element shape so a query can treat them as one set with a
///         namespace bit.
///     </para>
/// </summary>
public sealed class TagInstance
{
    /// <summary>Stable across edits; the key every delta and every query ref uses.</summary>
    public Guid Id { get; set; }

    /// <summary>A free string. The palette that made it is not required to exist.</summary>
    public string Code { get; set; } = "";

    public int FromTick { get; set; }

    public int ToTick { get; set; }

    /// <summary>The round containing <see cref="FromTick" />, derived and stored; null when unresolvable.</summary>
    public int? Round { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ModifiedUtc { get; set; }

    /// <summary>Who made it: one of <see cref="TagSources" />. A string so an unknown value survives.</summary>
    public string Source { get; set; } = TagSources.Human;

    /// <summary>
    ///     Free-form provenance for an accepted proposal (Suggested Tags §3.4: detector, proposal id,
    ///     confidence and so on). Opaque to this store and preserved as written; absent on a hand-made
    ///     instance. It replaces the withdrawn per-instance <c>suggestion</c> block (overview correction 2).
    /// </summary>
    public JsonObject? Provenance { get; set; }

    /// <summary>The human namespace: ordered, groups may repeat, an empty group is a bare label.</summary>
    public List<TagLabel> Labels { get; set; } = [];

    /// <summary>The parser namespace: rewritten wholesale by the refresh pass, never edited by the palette.</summary>
    public List<TagLabel> Facts { get; set; } = [];

    /// <summary>When and against which Round Facts schema <see cref="Facts" /> was last computed.</summary>
    public TagFactsStamp? FactsStamp { get; set; }

    public string? Note { get; set; }

    /// <summary>Clicked points; empty when the code press had no click.</summary>
    public List<TagPosition> Positions { get; set; } = [];

    /// <summary>Clicked from-to pairs; empty when none.</summary>
    public List<TagMovement> Movements { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>A deep copy, unknown fields included. History entries hold copies, never live instances.</summary>
    public TagInstance Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, TagJsonContext.Default.TagInstance),
            TagJsonContext.Default.TagInstance)!;
}

/// <summary>
///     One label. <see cref="Value" /> is always a string: numbers as invariant-culture text, booleans as
///     <c>"true"</c>/<c>"false"</c>.
/// </summary>
public sealed class TagLabel
{
    public TagLabel()
    {
    }

    public TagLabel(string group, string value)
    {
        Group = group;
        Value = value;
    }

    /// <summary>The group; empty for a bare label.</summary>
    public string Group { get; set; } = "";

    public string Value { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The stamp on <see cref="TagInstance.Facts" />.</summary>
public sealed class TagFactsStamp
{
    /// <summary>The Round Facts schema the facts were computed against.</summary>
    public int Schema { get; set; }

    public DateTime? ComputedUtc { get; set; }

    /// <summary>True when the instance's start fell in no round at the last refresh, so the facts are old.</summary>
    public bool Stale { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     A world point on one floor. <see cref="LevelMinZ" /> is the floor's quantized lower Z, the annotation
///     anchor rule, never a floor index. Coordinates are what a person clicked and are never rewritten;
///     <see cref="Place" /> is derived and a refresh may improve it.
/// </summary>
public sealed class TagPosition
{
    public double X { get; set; }

    public double Y { get; set; }

    public double LevelMinZ { get; set; }

    /// <summary>The frame-clock tick the click was made at; null when the click was not time-specific.</summary>
    public int? Tick { get; set; }

    public string? Place { get; set; }

    /// <summary>
    ///     How <see cref="Place" /> was resolved: <c>zones:&lt;zonesVersion&gt;</c> (overview correction 13, not
    ///     the design's <c>zone-bake:&lt;n&gt;</c>), <c>pawn</c>, or null for unresolved.
    /// </summary>
    public string? PlaceSource { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Two positions: where something went from and to.</summary>
public sealed class TagMovement
{
    public TagPosition From { get; set; } = new();

    public TagPosition To { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     One row of <c>index.json</c>: the small projection of a sidecar that cross-demo readers filter on
///     before opening anything, the <c>HighlightCount</c> idiom.
/// </summary>
public sealed class TagIndexEntry
{
    public string Sha256 { get; set; } = "";

    public string? FileName { get; set; }

    public int InstanceCount { get; set; }

    /// <summary>Distinct codes, ordinal order.</summary>
    public List<string> Codes { get; set; } = [];

    /// <summary>Distinct values of the reserved human group <c>strat</c> (overview correction 23).</summary>
    public List<string> StratIds { get; set; } = [];

    public DateTime ModifiedUtc { get; set; }

    public int TickRate { get; set; }

    /// <summary>The projection of a document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="modifiedUtc">When its sidecar was last written.</param>
    public static TagIndexEntry From(TagDocument document, DateTime modifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new TagIndexEntry
        {
            Sha256 = document.Demo.Sha256,
            FileName = document.Demo.FileName,
            InstanceCount = document.Instances.Count,
            Codes = [.. document.Instances.Select(i => i.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            StratIds =
            [
                .. document.Instances.SelectMany(i => i.Labels)
                    .Where(l => string.Equals(l.Group, TagStore.StratGroup, StringComparison.Ordinal))
                    .Select(l => l.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            ],
            ModifiedUtc = modifiedUtc,
            TickRate = document.Clock.TickRate
        };
    }
}

/// <summary><c>index.json</c>: a version and the rows. Derived; rebuilt from <c>demos/</c> when lost.</summary>
public sealed class TagIndexFile
{
    public int Version { get; set; } = 1;

    public List<TagIndexEntry> Entries { get; set; } = [];
}

/// <summary>The spellings of <see cref="SuggestionVerdict.Verdict" />.</summary>
public static class SuggestionVerdicts
{
    /// <summary>Accepted as proposed.</summary>
    public const string Accepted = "accepted";

    /// <summary>Accepted after the tagger moved a boundary or changed a label.</summary>
    public const string Edited = "edited";

    /// <summary>Rejected: never offered again, across tuning passes.</summary>
    public const string Rejected = "rejected";
}

/// <summary>
///     One demo's Suggested Tags verdicts, <c>&lt;config&gt;/tags/verdicts/&lt;sha256&gt;.verdicts.json</c>
///     (suggested-tags.md §3.4, moved under the tags root by overview correction 2). User truth: append
///     only and never rebuilt, so a tuning pass that rebuilds every proposal cannot lose a rejection.
/// </summary>
public sealed class SuggestionVerdictDocument
{
    /// <summary>Advisory, like the tag sidecar's.</summary>
    public int SchemaVersion { get; set; } = TagStore.VerdictsSchemaVersion;

    /// <summary>Which demo. The file is named by <see cref="TagDemoHeader.Sha256" />.</summary>
    public TagDemoHeader Demo { get; set; } = new();

    /// <summary>Verdicts by proposal identity key.</summary>
    public Dictionary<string, SuggestionVerdict> Verdicts { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     What the tagger said about one proposal: the design's three fields, plus what the identity re-match
///     needs when a re-parse renumbers rounds (the detector, the side, the trigger tick, and the frame
///     count of the parse the proposal was made on).
/// </summary>
public sealed class SuggestionVerdict
{
    /// <summary>One of <see cref="SuggestionVerdicts" />. A string so an unknown value survives.</summary>
    public string Verdict { get; set; } = SuggestionVerdicts.Rejected;

    /// <summary>The instance an accept wrote; null for a rejection.</summary>
    public Guid? TagInstanceId { get; set; }

    public DateTime At { get; set; }

    public string Detector { get; set; } = "";

    public int Side { get; set; }

    /// <summary>Frame clock.</summary>
    public int TriggerTick { get; set; }

    /// <summary>The frame count of the parse the proposal was made on; 0 when unknown.</summary>
    public int FrameCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     Source-generated, like the annotation sidecar's context: the browser head cannot trim a
///     reflection-based serializer, and wasm-matrix.md asks that new stores not add to that bill.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NewLine = "\n")]
[JsonSerializable(typeof(TagDocument))]
[JsonSerializable(typeof(TagIndexFile))]
[JsonSerializable(typeof(SuggestionVerdictDocument))]
public sealed partial class TagJsonContext : JsonSerializerContext;

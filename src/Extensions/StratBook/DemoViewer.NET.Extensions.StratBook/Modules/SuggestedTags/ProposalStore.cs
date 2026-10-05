#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.RoundFactsPass;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>
///     One demo's proposals, <c>&lt;cache&gt;/suggestions/&lt;StableKey&gt;.json</c> (schema 1). Derived and
///     rebuilt wholesale when the detector-set fingerprint changes; the verdicts
///     that survive a rebuild live in the Tag Store, not here.
/// </summary>
public sealed class ProposalDocument
{
    /// <summary>The shape this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The <see cref="OccupancySource" /> spellings.</summary>
    public const string FromRoundIndex = "round-index";

    /// <inheritdoc cref="FromRoundIndex" />
    public const string FromWalk = "walk";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ProposalDemoHeader Demo { get; set; } = new();

    /// <summary>The frame clock the ticks are on: the annotation sidecar's block.</summary>
    public RoundFactsClock Clock { get; set; } = new();

    public ProposalDetectorSet DetectorSet { get; set; } = new();

    /// <summary><see cref="FromRoundIndex" /> or <see cref="FromWalk" />.</summary>
    public string OccupancySource { get; set; } = FromWalk;

    public List<StoredProposal> Proposals { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The document as written: indented, <c>\n</c> line ends.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, SuggestedTagsJsonContext.Default.ProposalDocument);

    /// <summary>A document, or null when the text is not one.</summary>
    /// <param name="json">The file's text.</param>
    public static ProposalDocument? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, SuggestedTagsJsonContext.Default.ProposalDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The proposals as the detectors made them.</summary>
    public IReadOnlyList<TagProposal> ToProposals() => [.. Proposals.Select(p => p.ToProposal())];
}

/// <summary>The <c>demo</c> header. Only the hash takes part in matching; the rest is for a person.</summary>
public sealed class ProposalDemoHeader
{
    public string? Sha256 { get; set; }

    public string? StableKey { get; set; }

    public string? FileName { get; set; }

    public long SizeBytes { get; set; }
}

/// <summary>The <c>detectorSet</c> block: the stamp a rebuild is decided on.</summary>
public sealed class ProposalDetectorSet
{
    /// <summary>See <see cref="SuggestionsFingerprint" />.</summary>
    public string Fingerprint { get; set; } = "";

    public string ProfileId { get; set; } = "";

    public long ComputedAtTicks { get; set; }

    /// <summary>Where the site regions came from: <c>shipped</c>, <c>learned:&lt;n&gt;</c>, <c>site-only</c>, maybe <c>+profile</c>.</summary>
    public string? Regions { get; set; }
}

/// <summary>One proposal on disk: <see cref="TagProposal" /> with mutable collections for the serializer.</summary>
public sealed class StoredProposal
{
    public string Id { get; set; } = "";

    public string Detector { get; set; } = "";

    public string Code { get; set; } = "";

    public int Round { get; set; }

    public int Side { get; set; }

    public int FromTick { get; set; }

    public int ToTick { get; set; }

    public int TriggerTick { get; set; }

    /// <summary>
    ///     The round's freeze end, frame clock: not part of the proposal, kept so the queue can say "second
    ///     21" and the editor can work in seconds without the round's rows.
    /// </summary>
    public int RoundStartTick { get; set; }

    public double Confidence { get; set; }

    public Dictionary<string, double> Factors { get; set; } = [];

    public Dictionary<string, string> Labels { get; set; } = [];

    public List<StoredEvidence> Evidence { get; set; } = [];

    /// <summary>The stored form of a proposal.</summary>
    /// <param name="p">The proposal.</param>
    /// <param name="roundStartTick">Its round's freeze end, frame clock.</param>
    public static StoredProposal From(TagProposal p, int roundStartTick)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new StoredProposal
        {
            Id = p.Id,
            Detector = p.Detector,
            Code = p.Code,
            Round = p.Round,
            Side = p.Side,
            FromTick = p.FromTick,
            ToTick = p.ToTick,
            TriggerTick = p.TriggerTick,
            RoundStartTick = roundStartTick,
            Confidence = p.Confidence,
            Factors = new Dictionary<string, double>(p.Factors, StringComparer.Ordinal),
            Labels = new Dictionary<string, string>(p.Labels, StringComparer.Ordinal),
            Evidence = [.. p.Evidence.Select(e => new StoredEvidence { Kind = e.Kind, Tick = e.Tick, Text = e.Text })]
        };
    }

    /// <summary>Back to the detectors' record.</summary>
    public TagProposal ToProposal() => new(
        Id, Detector, Code, Round, Side, FromTick, ToTick, TriggerTick, Confidence,
        new Dictionary<string, double>(Factors, StringComparer.Ordinal),
        new Dictionary<string, string>(Labels, StringComparer.Ordinal),
        [.. Evidence.Select(e => new ProposalEvidence(e.Kind, e.Tick, e.Text))]);
}

/// <summary>One evidence line on disk.</summary>
public sealed class StoredEvidence
{
    public string Kind { get; set; } = "";

    public int Tick { get; set; }

    public string Text { get; set; } = "";
}

/// <summary>
///     Source-generated for the browser head's trimmer, the Tag Store's reason. Property order is
///     declaration order.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NewLine = "\n")]
[JsonSerializable(typeof(ProposalDocument))]
public sealed partial class SuggestedTagsJsonContext : JsonSerializerContext;

/// <summary>
///     Each demo's proposals, kept as the Strat Book's per-demo data under one facet whose fingerprint is the
///     detector set. The stamp carries the pending count, so the badge sums the store's index without a
///     file read. A file that does not parse reads as absent and its demo is rebuilt; a proposals file is
///     derived, so losing one costs a rebuild and never a verdict. A demo that leaves the library loses it.
/// </summary>
public sealed class ProposalStore
{
    /// <summary>The facet's name in the per-demo data.</summary>
    public const string Facet = "suggestions";

    /// <summary>The stamp's schema; the detector set is the fingerprint.</summary>
    public const int Schema = 1;

    /// <param name="data">The extension's per-demo data.</param>
    public ProposalStore(IExtensionDemoData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    /// <summary>The per-demo data the facet lives in.</summary>
    public IExtensionDemoData Data { get; }

    /// <summary>False on the browser host and in tests kept in memory: proposals die with the process.</summary>
    public bool IsPersistent => Data is not Extensions.StratBook.MemoryDemoData;

    /// <summary>Raised on the UI thread when a demo's stamp moved, with its path, or null for many.</summary>
    public event Action<string?>? Changed
    {
        add => Data.Changed += value;
        remove => Data.Changed -= value;
    }

    /// <summary>A demo's stamp, or null when nothing was built or counted.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public DemoDataStamp? Stamp(string demoPath) => Data.Stamp(demoPath, Facet);

    /// <summary>Every demo's stamp.</summary>
    public IReadOnlyList<DemoDataStamp> Stamps() => Data.Stamps(Facet);

    /// <summary>Proposals with no verdict yet across every demo: the badge.</summary>
    public int PendingTotal() => Stamps().Sum(s => s.Count);

    /// <summary>Are the demo's proposals current under <paramref name="fingerprint" /> (the detector set)?</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="fingerprint">The detector-set fingerprint in force for the demo's map.</param>
    public bool IsCurrent(string demoPath, string fingerprint) => Stamp(demoPath)?.IsCurrent(Schema, fingerprint) ?? false;

    /// <summary>
    ///     Writes a demo's proposals built under <paramref name="fingerprint" /> with <paramref name="pending" />
    ///     open proposals. Throws on an I/O failure: the service leaves the stamp as it was.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="document">The proposals.</param>
    /// <param name="fingerprint">The detector-set fingerprint.</param>
    /// <param name="pending">Proposals with no verdict yet.</param>
    public void Write(string demoPath, ProposalDocument document, string fingerprint, int pending)
    {
        ArgumentNullException.ThrowIfNull(document);
        Data.Write(demoPath, new DemoDataWrite(Facet, Schema, fingerprint, System.Text.Encoding.UTF8.GetBytes(document.Serialize()))
        {
            Count = pending
        });
    }

    /// <summary>Updates the pending count alone, after a verdict; a demo with no stamp gets a pending one carrying the count.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="pending">Proposals with no verdict yet.</param>
    public void SetCount(string demoPath, int pending) => Data.SetCount(demoPath, Facet, pending);

    /// <summary>A demo's proposals, or null when there are none or they do not parse.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public ProposalDocument? TryRead(string demoPath) =>
        Data.ReadAny(demoPath, Facet) is { Schema: Schema } record
            ? ProposalDocument.TryDeserialize(System.Text.Encoding.UTF8.GetString(record.Content))
            : null;

    /// <summary>Forgets a demo's proposals.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void Delete(string demoPath) => Data.Delete(demoPath, Facet);
}

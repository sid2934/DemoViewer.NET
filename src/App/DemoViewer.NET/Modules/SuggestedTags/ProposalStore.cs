#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     One demo's proposals, <c>&lt;cache&gt;/suggestions/&lt;StableKey&gt;.json</c> (suggested-tags.md §3.4,
///     schema 1). Derived and rebuilt wholesale when the detector-set fingerprint changes; the verdicts
///     that survive a rebuild live in the Tag Store (overview correction 2), not here.
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
///     declaration order, which is the §3.4 example's order.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NewLine = "\n")]
[JsonSerializable(typeof(ProposalDocument))]
public sealed partial class SuggestedTagsJsonContext : JsonSerializerContext;

/// <summary>
///     The proposals files: one per demo under <c>&lt;config&gt;/cache/suggestions/</c>, beside
///     <c>demos/</c>, named by <see cref="DemoCacheStore.StableKey" /> (suggested-tags.md §3.4, the
///     owner's store-location decision). The stamp that says whether a file is current lives on the cache
///     record (<see cref="DemoCacheRecord.SuggestionsFingerprint" />); this store only holds the payloads.
///     <para>
///         The Round Index's sibling-directory rules, for the same reasons: atomic writes, in memory when
///         there is no cache root (the browser host, tests), a corrupt file reads as absent so its demo
///         is simply rebuilt, and a demo that leaves the index loses its file through a
///         <see cref="DemoCacheStore.Changed" /> subscriber. A proposals file is derived, so losing one
///         costs a rebuild and never a verdict.
///     </para>
/// </summary>
public sealed class ProposalStore : IDisposable
{
    /// <summary>The file suffix; the whole name is <c>&lt;StableKey&gt;.json</c>.</summary>
    public const string Suffix = ".json";

    private readonly DemoCacheStore _demoCache;
    private readonly Lock _gate = new();

    // In-memory documents keyed by stable key, used when there is no cache root. Under _gate.
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly string? _root;

    private bool _disposed;

    /// <param name="cacheRoot">The cache directory (<c>&lt;config&gt;/cache</c>), or null for an in-memory store.</param>
    /// <param name="demoCache">The index the files follow: a demo it forgets loses its file here.</param>
    public ProposalStore(string? cacheRoot, DemoCacheStore demoCache)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        _root = cacheRoot is null ? null : Path.Combine(cacheRoot, "suggestions");
        _demoCache = demoCache;
        _demoCache.Changed += OnCacheChanged;
    }

    /// <summary>False on the browser host and in tests without a root: proposals die with the process.</summary>
    public bool IsPersistent => _root is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _demoCache.Changed -= OnCacheChanged;
    }

    /// <summary>The file for a demo, or null in memory.</summary>
    /// <param name="demoPath">The demo's path as the library knows it.</param>
    public string? PathFor(string demoPath) =>
        _root is null ? null : Path.Combine(_root, DemoCacheStore.StableKey(demoPath) + Suffix);

    /// <summary>Writes a demo's proposals atomically. Throws on an I/O failure: the service leaves the stamp unset.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="document">The proposals.</param>
    public void Write(string demoPath, ProposalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string json = document.Serialize();
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memory[DemoCacheStore.StableKey(demoPath)] = json;
            }

            return;
        }

        DemoCacheStore.WriteAtomic(file, json);
    }

    /// <summary>A demo's proposals, or null when the file is missing or does not parse.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public ProposalDocument? TryRead(string demoPath)
    {
        string? json;
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                json = _memory.GetValueOrDefault(DemoCacheStore.StableKey(demoPath));
            }
        }
        else
        {
            try
            {
                json = File.Exists(file) ? File.ReadAllText(file) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                json = null;
            }
        }

        return json is null ? null : ProposalDocument.TryDeserialize(json);
    }

    /// <summary>Forgets a demo's proposals. Best effort.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void Delete(string demoPath)
    {
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memory.Remove(DemoCacheStore.StableKey(demoPath));
            }

            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An orphan costs disk and nothing else: the index no longer names its demo.
        }
    }

    // A path that changed and is no longer in the index was removed: its file goes with it.
    private void OnCacheChanged(string? path)
    {
        if (path is not null && _demoCache.TryGetIndex(path) is null)
        {
            Delete(path);
        }
    }
}

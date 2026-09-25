#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The tuning view's harness (suggested-tags.md §3.7): builds the stored table for free (file reads
///     only, the counts a team's verdicts have already recorded), and re-runs the detectors in memory
///     against a candidate profile to preview recall and precision before the profile is saved.
///     <para>
///         <b>What a re-run actually redoes.</b> A demo's occupancy and placed events do not depend on
///         the profile (only its site-region overrides and its detector thresholds do), so the first
///         preview of a session parses each scored demo once
///         (<see cref="SuggestedTagsService.BuildDetectionInputs" />) and every later parameter tweak
///         only re-runs <see cref="ProposalDetection.Detect" /> over what is already held: the "in
///         memory" the design asks for. The made/accepted/edited/rejected counts are history and are
///         never recomputed by a preview; only recall and precision move.
///     </para>
///     <para>
///         Recall and precision score fired proposals against <b>human</b> tags only
///         (<see cref="TagSources.Human" />): an accepted suggestion would otherwise match itself and
///         say nothing. §7.3's formal validation (two independent taggers, agreement, targets) is a
///         separate, one-time study; this harness is the general tool a team runs against whatever hand
///         tags already exist, which may be none.
///     </para>
/// </summary>
public sealed class SuggestedTagsTuningService
{
    private readonly Dictionary<string, CachedDemo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DemoCacheStore _demoCache;
    private readonly Lock _gate = new();
    private readonly Func<string, ParsedDemo> _parseFile;
    private readonly SiteRegionStore _regions;
    private readonly SuggestedTagsService _suggestedTags;
    private readonly TagStore? _tags;

    /// <param name="demoCache">The unified cache: the library this scores over.</param>
    /// <param name="suggestedTags">The evaluator: stored proposals, verdicts, and the detection-input builder.</param>
    /// <param name="tags">The Tag Store hand tags are scored against; null runs with no ground truth (every row's recall/precision is "no data").</param>
    /// <param name="regions">The learned site region tables, the same store the evaluator reads.</param>
    /// <param name="parseFile">The parse to run per demo; defaults to reading the file and parsing its bytes.</param>
    public SuggestedTagsTuningService(
        DemoCacheStore demoCache,
        SuggestedTagsService suggestedTags,
        TagStore? tags,
        SiteRegionStore regions,
        Func<string, ParsedDemo>? parseFile = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(suggestedTags);
        ArgumentNullException.ThrowIfNull(regions);
        _demoCache = demoCache;
        _suggestedTags = suggestedTags;
        _tags = tags;
        _regions = regions;
        _parseFile = parseFile ?? (path => DemoParser.Parse(File.ReadAllBytes(path).AsMemory()));
    }

    /// <summary>Every demo the evaluator has built proposals for, newest first: what the tables score over.</summary>
    public IReadOnlyList<string> ScoredDemoPaths() =>
    [
        .. _demoCache.Index
            .Where(e => _suggestedTags.Load(e.Path, e.Sha256).Entries.Count > 0)
            .OrderByDescending(e => e.ModifiedTicks)
            .Select(e => e.Path)
    ];

    /// <summary>
    ///     The table at the profile currently in force: verdict counts over every demo that has one, and
    ///     recall/precision over whatever hand tags already exist. No parse; every number comes from
    ///     already-written proposal and verdict files, and the Tag Store's own documents.
    /// </summary>
    public TuningReport BuildStoredReport()
    {
        List<ProposalEntry> verdictEntries = [];
        Dictionary<string, List<FiredProposal>> firedByDetector = new(StringComparer.Ordinal);
        List<string> scoredPaths = [];
        int demosWithVerdicts = 0;

        foreach (DemoCacheIndexEntry entry in _demoCache.Index)
        {
            ProposalSet set = _suggestedTags.Load(entry.Path, entry.Sha256);
            if (set.Entries.Count == 0)
            {
                continue;
            }

            scoredPaths.Add(entry.Path);
            AddFired(firedByDetector, DemoKey(entry.Path, entry.Sha256), set.Entries.Select(e => e.Proposal));
            if (set.Entries.Any(e => e.Verdict is not null))
            {
                demosWithVerdicts++;
                verdictEntries.AddRange(set.Entries);
            }
        }

        (IReadOnlyDictionary<string, IReadOnlyList<HandTagWindow>> handTags, int demosWithHandTags) =
            CollectHandTags(scoredPaths);
        return SuggestedTagsTuning.Build(verdictEntries, ToReadOnly(firedByDetector), handTags,
            demosWithVerdicts, demosWithHandTags);
    }

    /// <summary>
    ///     Re-runs detection under <paramref name="candidate" /> over <paramref name="demoPaths" /> and
    ///     returns <paramref name="baseline" /> with only <see cref="DetectorTuningRow.Recall" /> and
    ///     <see cref="DetectorTuningRow.Precision" /> replaced: the two numbers the design says a
    ///     parameter change updates before it is saved. Runs off the calling thread; a demo that fails to
    ///     parse or has no Round Facts rows is skipped, the way the evaluator skips it.
    /// </summary>
    /// <param name="candidate">The profile to preview.</param>
    /// <param name="baseline">The report to keep the verdict counts from (<see cref="BuildStoredReport" />).</param>
    /// <param name="demoPaths">The demos to score, typically <see cref="ScoredDemoPaths" />.</param>
    /// <param name="cancellationToken">Cancels a sweep the user has already moved past.</param>
    public Task<TuningReport> PreviewAsync(
        DetectorProfile candidate, TuningReport baseline, IReadOnlyList<string> demoPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(demoPaths);
        return Task.Run(() =>
        {
            Dictionary<string, List<FiredProposal>> firedByDetector = new(StringComparer.Ordinal);
            List<string> scoredPaths = [];
            foreach (string path in demoPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetOrBuildCache(path) is not { } cached)
                {
                    continue;
                }

                scoredPaths.Add(path);
                SiteRegions regions = SiteRegions.Compose(cached.Map, null, cached.Table, candidate);
                IReadOnlyList<TagProposal> proposals = ProposalDetection.Detect(
                    cached.Map, cached.Inputs.TickRate, regions, cached.Inputs.Events, cached.Inputs.Rounds, candidate);
                AddFired(firedByDetector, DemoKey(path, _demoCache.TryGetIndex(path)?.Sha256), proposals);
            }

            (IReadOnlyDictionary<string, IReadOnlyList<HandTagWindow>> handTags, int demosWithHandTags) =
                CollectHandTags(scoredPaths);
            IReadOnlyDictionary<string, IReadOnlyList<FiredProposal>> byDetector = ToReadOnly(firedByDetector);
            List<DetectorTuningRow> rows =
            [
                .. baseline.Rows.Select(row =>
                {
                    (double? recall, double? precision) = SuggestedTagsTuning.Score(
                        byDetector.GetValueOrDefault(row.Detector, []),
                        handTags.GetValueOrDefault(row.Detector, [])); // detector id spells the code it proposes
                    return row with { Recall = recall, Precision = precision };
                })
            ];
            return new TuningReport(rows, baseline.DemosWithVerdicts, demosWithHandTags);
        }, cancellationToken);
    }

    /// <summary>Forgets every cached parse, so the next preview reparses (a demo changed, a re-index ran).</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private CachedDemo? GetOrBuildCache(string path)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out CachedDemo? cached))
            {
                return cached;
            }
        }

        DemoCacheRecord? record = _demoCache.TryLoadRecord(path);
        if (record?.RoundFacts is not { Schema: DemoCacheRecord.RoundFactsSchema } facts)
        {
            return null; // no Round Facts rows: nothing to bound rounds and seat sides with
        }

        ParsedDemo parsed;
        try
        {
            parsed = _parseFile(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }

        string map = string.IsNullOrEmpty(parsed.MapName) ? record.Map ?? "" : parsed.MapName;
        SuggestedTagsService.DetectionInputs inputs = _suggestedTags.BuildDetectionInputs(path, map, parsed, facts, record);
        SiteRegionTable? table;
        try
        {
            table = string.IsNullOrWhiteSpace(map) ? null : _regions.TryLoad(map);
        }
        catch (ArgumentException)
        {
            table = null; // a map name no file can carry: site-only
        }

        CachedDemo built = new(map, inputs, table);
        lock (_gate)
        {
            _cache[path] = built;
        }

        return built;
    }

    private (IReadOnlyDictionary<string, IReadOnlyList<HandTagWindow>>, int DemosWithHandTags) CollectHandTags(
        IReadOnlyList<string> scoredPaths)
    {
        if (_tags is null)
        {
            return (new Dictionary<string, IReadOnlyList<HandTagWindow>>(), 0);
        }

        HashSet<string> shas = new(StringComparer.Ordinal);
        foreach (string path in scoredPaths)
        {
            if (_demoCache.TryGetIndex(path)?.Sha256 is { Length: > 0 } sha)
            {
                shas.Add(sha.ToLowerInvariant());
            }
        }

        Dictionary<string, List<HandTagWindow>> byCode = new(StringComparer.Ordinal);
        int demosWithHandTags = 0;
        foreach (TagDocument document in _tags.LoadDocuments(entry => shas.Contains(entry.Sha256)))
        {
            bool any = false;
            foreach (TagInstance instance in document.Instances)
            {
                if (!string.Equals(instance.Source, TagSources.Human, StringComparison.Ordinal)
                    || instance.Round is not { } round)
                {
                    continue;
                }

                if (!byCode.TryGetValue(instance.Code, out List<HandTagWindow>? list))
                {
                    byCode[instance.Code] = list = [];
                }

                string? site = instance.Labels.FirstOrDefault(l =>
                    string.Equals(l.Group, "site", StringComparison.OrdinalIgnoreCase))?.Value;
                list.Add(new HandTagWindow(DemoKey(null, document.Demo.Sha256), round, instance.FromTick,
                    instance.ToTick, string.IsNullOrEmpty(site) ? null : site));
                any = true;
            }

            if (any)
            {
                demosWithHandTags++;
            }
        }

        return (byCode.ToDictionary(kv => kv.Key, IReadOnlyList<HandTagWindow> (kv) => kv.Value, StringComparer.Ordinal),
            demosWithHandTags);
    }

    // Proposals and hand tags meet on the demo's hash, the one identity both stores share (the Tag Store
    // matches documents on it alone). A demo with no hash yet falls back to its path, which no hand tag
    // can carry, so its proposals score as unmatched rather than borrowing another demo's tags.
    private static string DemoKey(string? path, string? sha256) =>
        sha256 is { Length: > 0 } ? sha256.ToLowerInvariant() : "path:" + path;

    private static void AddFired(
        Dictionary<string, List<FiredProposal>> byDetector, string demo, IEnumerable<TagProposal> proposals)
    {
        foreach (TagProposal proposal in proposals)
        {
            if (!byDetector.TryGetValue(proposal.Detector, out List<FiredProposal>? list))
            {
                byDetector[proposal.Detector] = list = [];
            }

            list.Add(new FiredProposal(demo, proposal));
        }
    }

    private static Dictionary<string, IReadOnlyList<FiredProposal>> ToReadOnly(
        Dictionary<string, List<FiredProposal>> byDetector) =>
        byDetector.ToDictionary(kv => kv.Key, IReadOnlyList<FiredProposal> (kv) => kv.Value, StringComparer.Ordinal);

    // The profile-independent half of one demo's build, held for the session so a parameter sweep only
    // parses once (class doc). Map and the site region table travel with it because regions are the one
    // profile-dependent piece computed OUTSIDE DetectionInputs (SuggestedTagsService.BuildDetectionInputs).
    private sealed record CachedDemo(string Map, SuggestedTagsService.DetectionInputs Inputs, SiteRegionTable? Table);
}

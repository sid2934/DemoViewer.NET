#region

using System.Runtime.ExceptionServices;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>
///     The tuning view's harness: builds the stored table for free (file reads
///     only, the counts a team's verdicts have already recorded), and re-runs the detectors in memory
///     against a candidate profile to preview recall and precision before the profile is saved.
///     <para>
///         <b>What a re-run actually redoes.</b> A demo's occupancy and placed events do not depend on
///         the profile (only its site-region overrides and its detector thresholds do), so the first
///         preview of a session reads each scored demo once, as a job on the demo's visit
///         (<see cref="SuggestedTagsService.BuildDetectionInputs" />), on the shell's parse when the demo is
///         open, and every later parameter tweak only re-runs <see cref="ProposalDetection.Detect" /> over
///         what is already held in memory, rather than reparsed. The made/accepted/edited/rejected counts are history and are
///         never recomputed by a preview; only recall and precision move.
///     </para>
///     <para>
///         Recall and precision score fired proposals against <b>human</b> tags only
///         (<see cref="TagSources.Human" />): an accepted suggestion would otherwise match itself and
///         say nothing. Formal validation (two independent taggers, agreement, targets) is a
///         separate, one-time study; this harness is the general tool a team runs against whatever hand
///         tags already exist, which may be none.
///     </para>
/// </summary>
public sealed class SuggestedTagsTuningService
{
    /// <summary>How many demos' detection inputs the preview keeps, least recently used out first.</summary>
    internal const int CacheCapacity = 8;

    // One queued preview at a time: a newer one replaces a sweep still waiting for its turn.
    private const string PreviewJobKey = "stratbook:suggested-tags-preview";

    private readonly Dictionary<string, CachedDemo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _cacheOrder = [];
    private readonly IExtensionLibrary _library;
    private readonly Lock _gate = new();
    private readonly IExtensionJobs _jobs;
    private readonly SiteRegionStore _regions;
    private readonly SuggestedTagsService _suggestedTags;
    private readonly TagStore? _tags;

    /// <param name="library">The library this scores over.</param>
    /// <param name="suggestedTags">The evaluator: stored proposals, verdicts, and the detection-input builder.</param>
    /// <param name="tags">The Tag Store hand tags are scored against; null runs with no ground truth (every row's recall/precision is "no data").</param>
    /// <param name="regions">The learned site region tables, the same store the evaluator reads.</param>
    /// <param name="jobs">The processing queue a preview reads its demos and scores them on, as user-requested jobs.</param>
    public SuggestedTagsTuningService(
        IExtensionLibrary library,
        SuggestedTagsService suggestedTags,
        TagStore? tags,
        SiteRegionStore regions,
        IExtensionJobs jobs)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(suggestedTags);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(jobs);
        _library = library;
        _jobs = jobs;
        _suggestedTags = suggestedTags;
        _tags = tags;
        _regions = regions;
    }

    /// <summary>Every demo the evaluator has built proposals for, newest first: what the tables score over.</summary>
    public IReadOnlyList<string> ScoredDemoPaths() =>
    [
        .. _library.Demos
            .Where(e => _suggestedTags.Load(e.FilePath, e.Sha256).Entries.Count > 0)
            .OrderByDescending(e => e.Modified.Ticks)
            .Select(e => e.FilePath)
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

        foreach (LibraryDemo entry in _library.Demos)
        {
            ProposalSet set = _suggestedTags.Load(entry.FilePath, entry.Sha256);
            if (set.Entries.Count == 0)
            {
                continue;
            }

            scoredPaths.Add(entry.FilePath);
            AddFired(firedByDetector, DemoKey(entry.FilePath, entry.Sha256), set.Entries.Select(e => e.Proposal));
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
    ///     <see cref="DetectorTuningRow.Precision" /> replaced: the two numbers a
    ///     parameter change updates before it is saved. A demo not in the cache is read as a user-requested
    ///     job on its own visit, which scores it there; the rest are scored in one user-requested job, which a
    ///     newer preview replaces while it is still queued. A demo that fails to parse or has no Round Facts rows
    ///     is skipped, the way the evaluator skips it.
    /// </summary>
    /// <param name="candidate">The profile to preview.</param>
    /// <param name="baseline">The report to keep the verdict counts from (<see cref="BuildStoredReport" />).</param>
    /// <param name="demoPaths">The demos to score, typically <see cref="ScoredDemoPaths" />.</param>
    /// <param name="cancellationToken">Cancels a sweep the user has already moved past.</param>
    /// <exception cref="OperationCanceledException">The sweep was cancelled, replaced or removed from the queue.</exception>
    public async Task<TuningReport> PreviewAsync(
        DetectorProfile candidate, TuningReport baseline, IReadOnlyList<string> demoPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(demoPaths);
        Dictionary<string, IReadOnlyList<TagProposal>?> fired = await ReadUncachedAsync(candidate, demoPaths, cancellationToken)
            .ConfigureAwait(false);
        TuningReport? report = null;
        ExceptionDispatchInfo? failure = null;
        await _jobs.RunAsync("Suggested Tags: preview tuning", job =>
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.CancellationToken);
            try
            {
                report = Preview(candidate, baseline, demoPaths, fired, job, linked.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The queue records a failed job and completes its task normally; the caller still sees why.
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            return Task.CompletedTask;
        }, new JobOptions(BuiltInJobKinds.Compute, JobPriority.UserRequested, PreviewJobKey)).ConfigureAwait(false);
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return report ?? throw new OperationCanceledException("The preview was removed from the queue before it finished.");
    }

    // Every demo the cache does not hold is read as a job on its own visit, all queued at once so the queue can
    // order them; each scores its demo on the parse and keeps the inputs for the next tweak. Null marks a
    // demo that could not be scored.
    private async Task<Dictionary<string, IReadOnlyList<TagProposal>?>> ReadUncachedAsync(DetectorProfile candidate,
        IReadOnlyList<string> demoPaths, CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<TagProposal>?> fired = new(StringComparer.OrdinalIgnoreCase);
        List<IJobHandle> reads = [];
        foreach (string path in demoPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                if (_cache.ContainsKey(path))
                {
                    continue;
                }
            }

            if (Inputs(path) is null)
            {
                fired[path] = null;
                continue;
            }

            reads.Add(_jobs.Enqueue(JobRequest.OnDemo("Suggested Tags: preview " + Path.GetFileName(path), path, job =>
            {
                IReadOnlyList<TagProposal>? proposals = Build(path, job.Parsed) is { } built ? Detect(built, candidate) : null;
                lock (fired)
                {
                    fired[path] = proposals;
                }

                return Task.CompletedTask;
            }, new JobOptions(StratBookJobKinds.Tuning, JobPriority.UserRequested))));
        }

        await using (cancellationToken.Register(() => reads.ForEach(r => r.Cancel())))
        {
            await Task.WhenAll(reads.Select(r => r.Completion)).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return fired;
    }

    private static IReadOnlyList<TagProposal> Detect(CachedDemo cached, DetectorProfile candidate)
    {
        SiteRegions regions = SiteRegions.Compose(cached.Map, null, cached.Table, candidate);
        return ProposalDetection.Detect(cached.Map, cached.Inputs.TickRate, regions, cached.Inputs.Events,
            cached.Inputs.Rounds, candidate);
    }

    private TuningReport Preview(DetectorProfile candidate, TuningReport baseline, IReadOnlyList<string> demoPaths,
        Dictionary<string, IReadOnlyList<TagProposal>?> fired, IJobContext job, CancellationToken cancellationToken)
    {
        Dictionary<string, List<FiredProposal>> firedByDetector = new(StringComparer.Ordinal);
        List<string> scoredPaths = [];
        for (int i = 0; i < demoPaths.Count; i++)
        {
            string path = demoPaths[i];
            cancellationToken.ThrowIfCancellationRequested();
            job.Report(i, demoPaths.Count, Path.GetFileName(path));
            IReadOnlyList<TagProposal>? proposals;
            if (fired.TryGetValue(path, out IReadOnlyList<TagProposal>? read))
            {
                proposals = read;
            }
            else
            {
                proposals = Cached(path) is { } cached ? Detect(cached, candidate) : null;
            }

            if (proposals is null)
            {
                continue;
            }

            scoredPaths.Add(path);
            AddFired(firedByDetector, DemoKey(path, _library.Find(path)?.Sha256), proposals);
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
    }

    /// <summary>Forgets every cached parse, so the next preview reparses (a demo changed, a re-index ran).</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    private CachedDemo? Cached(string path)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out CachedDemo? cached))
            {
                Touch(path);
                return cached;
            }
        }

        return null;
    }

    // The row and its Round Facts rows, or null when the demo has none: nothing bounds rounds and seats sides.
    private (LibraryDemo Record, RoundFactsRows Facts)? Inputs(string path) =>
        _library.Find(path) is { } record
        && _suggestedTags.RoundFacts.TryGet(path) is { Schema: RoundFactsRecords.Schema } facts
            ? (record, facts)
            : null;

    private CachedDemo? Build(string path, ParsedDemo parsed)
    {
        if (Inputs(path) is not var (record, facts))
        {
            return null;
        }

        string map = string.IsNullOrEmpty(parsed.MapName) ? record.MapName ?? "" : parsed.MapName;
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
            Touch(path);
            while (_cache.Count > CacheCapacity && _cacheOrder.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _cacheOrder.RemoveLast();
            }
        }

        return built;
    }

    /// <summary>The cached demos, most recently used first. For a test.</summary>
    internal IReadOnlyList<string> CachedPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _cacheOrder];
            }
        }
    }

    // Under _gate.
    private void Touch(string path)
    {
        for (LinkedListNode<string>? node = _cacheOrder.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value, path, StringComparison.OrdinalIgnoreCase))
            {
                _cacheOrder.Remove(node);
                break;
            }
        }

        _cacheOrder.AddFirst(path);
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
            if (_library.Find(path)?.Sha256 is { Length: > 0 } sha)
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

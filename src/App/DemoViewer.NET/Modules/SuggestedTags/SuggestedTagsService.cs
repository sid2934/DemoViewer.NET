#region

using System.Globalization;
using System.Text.Json.Nodes;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Tags;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     What the tagger changed before accepting (suggested-tags.md §3.6, the editor). A null field keeps
///     the proposal's value. Any edit, even one that changes nothing, accepts with <c>edited: true</c>:
///     the tagger opened the editor, which is the signal the tuning view reads.
/// </summary>
/// <param name="FromTick">The claim window's start, frame clock.</param>
/// <param name="ToTick">The claim window's end, frame clock.</param>
/// <param name="Labels">The labels, replacing the proposal's.</param>
/// <param name="Note">A note for the instance.</param>
public sealed record TagInstanceEdit(
    int? FromTick = null,
    int? ToTick = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    string? Note = null);

/// <summary>
///     The Suggested Tags engine as a background evaluator (suggested-tags.md §3.5, steps 4 and 5): one
///     parse, many evaluators, registered after the Round Index so the index written in the same pass is
///     the occupancy source and the walk is the exception (overview correction 19).
///     <para>
///         Per demo: occupancy from a current <c>.dvri.json</c>, else the walk over the held parse; the
///         detonations placed by zones, then the index's place snap, then the walk's sparse cloud
///         (correction 16); the five detectors in the profile's order; the proposals file written; and the
///         record stamped with the detector-set fingerprint and the pending count, stamp last so a crash
///         leaves "not built". Everything needs Round Facts rows (correction 11); a demo without them is
///         not wanted.
///     </para>
///     <para>
///         <b>Verdicts are user truth</b> and go through the Tag Store (correction 2): an accept writes the
///         instance, with <c>source: "suggested"</c> and the provenance object, and then the verdict; a
///         reject writes only the verdict. Neither ever touches the proposals file, which a tuning pass
///         may rebuild at any time.
///     </para>
///     <para>
///         <b>Background policy</b> (correction 20): the sweep over the library is its own opt-in and off
///         by default; the open demo is always evaluated on the parse the open already paid for, and a
///         forced request (the queue's "Detect" button) always runs.
///     </para>
/// </summary>
public sealed class SuggestedTagsService : IDemoEvaluator
{
    /// <summary>The queue owner tag and the coordinator's id (overview correction 19 spells it this way).</summary>
    public const string EvaluatorId = "suggestedtags";

    /// <summary>
    ///     The feature id, a sub-feature of the 2D Playback tab: off hides the track and the queue and stops
    ///     the evaluator. A persisted key; never renamed.
    /// </summary>
    public const string FeatureId = "playback2d.suggestedtags";

    /// <summary>The <c>provenance.source</c> an accepted instance carries (suggested-tags.md §3.4).</summary>
    public const string ProvenanceSource = "suggested-tags";

    private static ILogger? _diagLog;

    private readonly Func<bool> _background;
    private readonly DemoCacheStore _demoCache;
    private readonly Func<bool> _enabled;

    // Demos whose build threw this session: not wanted again until a forced request, so one bad file is
    // not re-parsed on every pass. Session-only on purpose; a restart retries them once.
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    // Manual requests: they run whatever the opt-in says, at user priority. Under _gate.
    private readonly HashSet<string> _forcedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly RoundIndexStore? _index;
    private readonly RoundIndexPlaceSources? _indexSources;
    private readonly Func<string?> _openDemo;
    private readonly Func<string, IReadOnlyList<PlaceSummary>?>? _placesFor;
    private readonly Action<Action> _post;
    private readonly Func<DetectorProfile> _profile;
    private readonly ProposalStore _proposals;
    private readonly SiteRegionStore _regions;

    // The region table per map, read once: Wants runs over the whole library and must not open a file
    // per row. Under _gate.
    private readonly Dictionary<string, SiteRegionTable?> _tables = new(StringComparer.Ordinal);
    private readonly TagStore? _tags;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<ParsedDemo, IEnumerable<PositionSample>>? _walk;
    private readonly IZonePlaceResolverSource _zones;

    /// <param name="demoCache">The unified demo cache: the stamp and the pending count live on its records.</param>
    /// <param name="proposals">Where the proposals files go.</param>
    /// <param name="tags">The Tag Store accepts write into and verdicts live in; null runs without verdicts.</param>
    /// <param name="regions">The learned site region tables.</param>
    /// <param name="profile">The parameter profile in force.</param>
    /// <param name="enabled">The <c>playback2d.suggestedtags</c> gate: off, nothing is wanted.</param>
    /// <param name="background">The library sweep's opt-in; the open demo and forced requests ignore it.</param>
    /// <param name="index">The Round Index sidecars, the preferred occupancy source; null walks every time.</param>
    /// <param name="indexSources">The index's fingerprint per map, to tell a current sidecar from a stale one.</param>
    /// <param name="zones">The map's zones for detonation placement; none when null.</param>
    /// <param name="placesFor">The index's place summaries per map, the second detonation fallback.</param>
    /// <param name="openDemo">The demo the shell has open, which is evaluated whatever the opt-in says.</param>
    /// <param name="post">UI-thread marshal for <see cref="Changed" />; defaults to synchronous.</param>
    /// <param name="walk">The position walk to fold; null walks the parse through the engine's sampler.</param>
    /// <param name="utcNow">The clock verdicts and instances are stamped with.</param>
    public SuggestedTagsService(
        DemoCacheStore demoCache,
        ProposalStore proposals,
        TagStore? tags,
        SiteRegionStore regions,
        Func<DetectorProfile> profile,
        Func<bool> enabled,
        Func<bool> background,
        RoundIndexStore? index = null,
        RoundIndexPlaceSources? indexSources = null,
        IZonePlaceResolverSource? zones = null,
        Func<string, IReadOnlyList<PlaceSummary>?>? placesFor = null,
        Func<string?>? openDemo = null,
        Action<Action>? post = null,
        Func<ParsedDemo, IEnumerable<PositionSample>>? walk = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(background);
        _demoCache = demoCache;
        _proposals = proposals;
        _tags = tags;
        _regions = regions;
        _profile = profile;
        _enabled = enabled;
        _background = background;
        _index = index;
        _indexSources = indexSources;
        _zones = zones ?? NoZonePlaceResolverSource.Instance;
        _placesFor = placesFor;
        _openDemo = openDemo ?? (() => null);
        _post = post ?? (action => action());
        _walk = walk;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(SuggestedTagsLog.Category);

    /// <summary>The coordinator, so a forced request can ask it to reconsider. Null in tests.</summary>
    public DemoEvaluationCoordinator? Coordinator { get; set; }

    /// <summary>Whether proposals and verdicts outlive the process. False on the browser host.</summary>
    public bool IsPersistent => _proposals.IsPersistent && (_tags?.IsPersistent ?? false);

    /// <summary>True while the coordinator has a build in flight.</summary>
    public bool IsDetecting => Coordinator?.HasOutstanding(EvaluatorId) ?? false;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <summary>A demo's proposals or verdicts changed. Raised through the post delegate with its path.</summary>
    public event Action<string>? Changed;

    /// <inheritdoc />
    /// <remarks>
    ///     From the index row alone: the gate on, Round Facts rows present, the proposals missing or built
    ///     under another fingerprint than the map's current one, and the sweep on or the demo forced.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!_enabled() || !NeedsBuild(_demoCache.TryGetIndex(path)))
        {
            return false;
        }

        lock (_gate)
        {
            if (_forcedPaths.Contains(path))
            {
                return true;
            }

            if (_failed.Contains(path))
            {
                return false;
            }
        }

        return _background();
    }

    /// <inheritdoc />
    public DemoJobPriority PriorityFor(string path)
    {
        lock (_gate)
        {
            return _forcedPaths.Contains(path) ? DemoJobPriority.UserRequested : DemoJobPriority.Background;
        }
    }

    /// <inheritdoc />
    public long OrderHint(string path) => _demoCache.TryGetIndex(path)?.ModifiedTicks ?? 0;

    /// <inheritdoc />
    public void Evaluate(string path, ParsedDemo parsed) => Refresh(path, parsed);

    /// <inheritdoc />
    /// <remarks>
    ///     The open demo is built on the parse its open paid for whatever the opt-in says (correction 20);
    ///     any other demo only when it would have been wanted, so the Library's tier-2 fan-out does not
    ///     turn a sweep the user left off back on.
    /// </remarks>
    public void OnParsedOpportunistically(string path, ParsedDemo parsed)
    {
        if (Wants(path)
            || (_enabled() && IsOpen(path) && !IsFailed(path) && NeedsBuild(_demoCache.TryGetIndex(path))))
        {
            Refresh(path, parsed);
        }
    }

    /// <inheritdoc />
    public void OnFailed(string path) => ClearForced(path);

    /// <summary>
    ///     The demos that would be submitted: the coordinator's candidate universe for this evaluator,
    ///     newest first. Derived from the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
        if (!_enabled())
        {
            return [];
        }

        bool background = _background();
        HashSet<string> forced;
        HashSet<string> failed;
        lock (_gate)
        {
            forced = [.. _forcedPaths];
            failed = [.. _failed];
        }

        return
        [
            .. _demoCache.Index
                .Where(e => NeedsBuild(e)
                            && (forced.Contains(e.Path) || (background && !failed.Contains(e.Path))))
                .OrderByDescending(e => e.ModifiedTicks)
                .Select(e => e.Path)
        ];
    }

    /// <summary>
    ///     Builds one demo at user priority regardless of the opt-in and of an earlier failure: the queue's
    ///     "Detect" button. Nothing happens for a demo without Round Facts rows; <see cref="CanDetect" /> says so first.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    public void Request(string path)
    {
        lock (_gate)
        {
            _failed.Remove(path);
            _forcedPaths.Add(path);
        }

        Coordinator?.Consider(path);
    }

    /// <summary>Whether a demo has what a build needs: a parse and Round Facts rows.</summary>
    /// <param name="path">The demo's path.</param>
    public bool CanDetect(string path) => HasInputs(_demoCache.TryGetIndex(path));

    /// <summary>The detector-set fingerprint for a map's demos under the profile and regions in force.</summary>
    /// <param name="map">The map, or null.</param>
    public string FingerprintFor(string? map) => SuggestionsFingerprint.Compose(_profile(), TableFor(map));

    /// <summary>Forgets the cached region tables, so the next build reads the learned files again.</summary>
    public void ReloadRegions()
    {
        lock (_gate)
        {
            _tables.Clear();
        }
    }

    /// <summary>
    ///     A demo's proposals merged with its verdicts, in round order. Empty when the demo has no
    ///     proposals file, or its file was written for other content than the demo now holds.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="sha256">The demo's hash when the caller knows it better than the index (an open demo Content Identity has not reached).</param>
    public ProposalSet Load(string path, string? sha256 = null) => LoadCore(path, sha256).Set;

    /// <summary>
    ///     Accepts a pending proposal: the instance into the Tag Store with its provenance, then the verdict.
    ///     False when the proposal is not pending, the demo has no hash to key tags by, or a write failed.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="proposalId">The proposal's identity key.</param>
    /// <param name="edit">What the tagger changed, or null to accept as proposed.</param>
    /// <param name="sha256">The demo's hash when the caller knows it better than the index.</param>
    public bool Accept(string path, string proposalId, TagInstanceEdit? edit = null, string? sha256 = null)
    {
        bool accepted = AcceptCore(path, proposalId, edit, sha256);
        if (accepted)
        {
            AfterVerdict(path, sha256);
        }

        return accepted;
    }

    /// <summary>Rejects a pending proposal. False when it is not pending or the verdict could not be written.</summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="proposalId">The proposal's identity key.</param>
    /// <param name="sha256">The demo's hash when the caller knows it better than the index.</param>
    public bool Reject(string path, string proposalId, string? sha256 = null)
    {
        (ProposalDocument? document, ProposalSet set) = LoadCore(path, sha256);
        if (_tags is null || set.Sha256 is not { } sha || document is null
            || set.Entries.FirstOrDefault(e => e.IsPending && e.Proposal.Id == proposalId) is not { } entry)
        {
            return false;
        }

        if (!_tags.RecordVerdict(sha, proposalId, Verdict(SuggestionVerdicts.Rejected, null, entry.Proposal, document)))
        {
            return false;
        }

        AfterVerdict(path, sha256);
        return true;
    }

    /// <summary>
    ///     Accepts every pending proposal at or above <paramref name="minConfidence" /> (Ctrl+Y, after the
    ///     queue's confirm). Returns how many were accepted.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="minConfidence">The queue's confidence filter.</param>
    /// <param name="detector">The queue's detector filter, or null for every detector.</param>
    /// <param name="sha256">The demo's hash when the caller knows it better than the index.</param>
    public int AcceptAll(string path, double minConfidence, string? detector = null, string? sha256 = null)
    {
        List<string> ids =
        [
            .. Load(path, sha256).Pending
                .Where(e => e.Proposal.Confidence >= minConfidence
                            && (detector is null || e.Proposal.Detector == detector))
                .Select(e => e.Proposal.Id)
        ];
        int accepted = 0;
        foreach (string id in ids)
        {
            if (AcceptCore(path, id, null, sha256))
            {
                accepted++;
            }
        }

        if (accepted > 0)
        {
            AfterVerdict(path, sha256);
        }

        return accepted;
    }

    /// <summary>
    ///     The instance an accept writes (suggested-tags.md §3.4): the proposal's code, window and round,
    ///     <c>source: "suggested"</c>, its labels in the human namespace with the side it is about
    ///     (overview correction 10), and the free-form provenance object.
    /// </summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="edit">What the tagger changed, or null.</param>
    /// <param name="fingerprint">The detector set the proposal was made under.</param>
    /// <param name="utcNow">The creation time.</param>
    public static TagInstance InstanceFor(TagProposal proposal, TagInstanceEdit? edit, string fingerprint, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        int from = edit?.FromTick ?? proposal.FromTick;
        int to = Math.Max(from, edit?.ToTick ?? proposal.ToTick);
        IReadOnlyDictionary<string, string> labels = edit?.Labels ?? proposal.Labels;

        TagInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Code = proposal.Code,
            FromTick = from,
            ToTick = to,
            Round = proposal.Round,
            CreatedUtc = utcNow,
            ModifiedUtc = utcNow,
            Source = TagSources.Suggested,
            Note = string.IsNullOrWhiteSpace(edit?.Note) ? null : edit.Note,
            Provenance = new JsonObject
            {
                ["source"] = ProvenanceSource,
                ["detector"] = proposal.Detector,
                ["proposalId"] = proposal.Id,
                ["confidence"] = proposal.Confidence,
                ["detectorSetFingerprint"] = fingerprint,
                ["edited"] = edit is not null
            }
        };

        if (!labels.ContainsKey("side") && proposal.Side is 2 or 3)
        {
            instance.Labels.Add(new TagLabel("side", ProposalIds.SideName(proposal.Side)));
        }

        foreach ((string group, string value) in labels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            instance.Labels.Add(new TagLabel(group, value));
        }

        return instance;
    }

    private bool AcceptCore(string path, string proposalId, TagInstanceEdit? edit, string? sha256)
    {
        (ProposalDocument? document, ProposalSet set) = LoadCore(path, sha256);
        if (_tags is null || set.Sha256 is not { } sha || document is null
            || set.Entries.FirstOrDefault(e => e.IsPending && e.Proposal.Id == proposalId) is not { } entry)
        {
            return false;
        }

        DateTime now = _utcNow();
        TagInstance instance = InstanceFor(entry.Proposal, edit, document.DetectorSet.Fingerprint, now);

        // The facts a hand-made tag gets as it is made, so the Matrix can slice an accepted tag at once
        // rather than after the next rows rewrite.
        DemoCacheRecord? record = _demoCache.TryLoadRecord(path);
        if (record?.RoundFacts is { Schema: DemoCacheRecord.RoundFactsSchema } rows)
        {
            TagFactsRefresher.RefreshInstance(instance, rows.Rounds, DemoCacheRecord.RoundFactsSchema, now);
        }

        DemoIdentity demo = new(sha, document.Demo.FileName ?? Path.GetFileName(path),
            record?.Size ?? document.Demo.SizeBytes);
        if (!_tags.Append(demo, document.Clock.ToIdentity(), instance))
        {
            return false;
        }

        string verdict = edit is null ? SuggestionVerdicts.Accepted : SuggestionVerdicts.Edited;
        return _tags.RecordVerdict(sha, proposalId, Verdict(verdict, instance.Id, entry.Proposal, document));
    }

    private SuggestionVerdict Verdict(string verdict, Guid? instanceId, TagProposal proposal, ProposalDocument document) =>
        new()
        {
            Verdict = verdict,
            TagInstanceId = instanceId,
            At = _utcNow(),
            Detector = proposal.Detector,
            Side = proposal.Side,
            TriggerTick = proposal.TriggerTick,
            FrameCount = document.Clock.FrameCount
        };

    // The pending count follows every verdict, so the index can say "12 pending" without a file read.
    private void AfterVerdict(string path, string? sha256)
    {
        int pending = LoadCore(path, sha256).Set.Pending.Count;
        try
        {
            _demoCache.UpdateExisting(path, r => r.SuggestionCount = pending);
            _demoCache.SaveIndex();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The count is a mirror; the next build or verdict writes it again.
        }

        RaiseChanged(path);
    }

    private (ProposalDocument? Document, ProposalSet Set) LoadCore(string path, string? sha256)
    {
        bool persistent = IsPersistent;
        if (_proposals.TryRead(path) is not { } document)
        {
            return (null, ProposalSet.Empty(path, persistent));
        }

        string? sha = Normalize(sha256) ?? Normalize(_demoCache.TryGetIndex(path)?.Sha256)
            ?? Normalize(document.Demo.Sha256);
        if (sha is not null && Normalize(document.Demo.Sha256) is { } written
                            && !string.Equals(sha, written, StringComparison.Ordinal))
        {
            return (null, ProposalSet.Empty(path, persistent)); // built for the file's old bytes
        }

        IReadOnlyList<TagProposal> proposals =
        [
            .. document.ToProposals().OrderBy(p => p.Round).ThenBy(p => p.TriggerTick)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
        ];
        Dictionary<string, int> roundStarts = new(StringComparer.Ordinal);
        foreach (StoredProposal stored in document.Proposals)
        {
            roundStarts.TryAdd(stored.Id, stored.RoundStartTick);
        }

        IReadOnlyDictionary<string, SuggestionVerdict> verdicts = new Dictionary<string, SuggestionVerdict>();
        if (_tags is not null && sha is not null)
        {
            if (_tags.LoadVerdicts(sha) is not { } file)
            {
                // Without the verdicts nothing can be offered: a rejection would come back.
                return (document, new ProposalSet(path, sha, [], persistent, true));
            }

            verdicts = file.Verdicts;
        }

        IReadOnlyList<ProposalEntry> entries =
        [
            .. ProposalVerdictMatch.Resolve(proposals, verdicts, document.Clock.FrameCount, document.Clock.TickRate)
                .Select(e => e with { RoundStartTick = roundStarts.GetValueOrDefault(e.Proposal.Id) })
        ];
        return (document, new ProposalSet(path, sha, entries, persistent, false));
    }

    private void Refresh(string path, ParsedDemo parsed)
    {
        string fileName = Path.GetFileName(path);
        bool forced;
        lock (_gate)
        {
            forced = _forcedPaths.Contains(path);
        }

        try
        {
            DemoCacheRecord? record = _demoCache.TryLoadRecord(path);
            if (record?.RoundFacts is not { Schema: DemoCacheRecord.RoundFactsSchema } facts)
            {
                return; // nothing to bound rounds and seat sides with; Round Facts has not written this demo
            }

            string map = string.IsNullOrEmpty(parsed.MapName) ? record.Map ?? "" : parsed.MapName;
            DetectorProfile profile = _profile();
            SiteRegionTable? table = TableFor(map);
            string fingerprint = SuggestionsFingerprint.Compose(profile, table);
            if (!forced && string.Equals(record.SuggestionsFingerprint, fingerprint, StringComparison.Ordinal)
                        && _proposals.TryRead(path) is not null)
            {
                return; // a queued request the open's fan-out already satisfied
            }

            ClockIdentity clock = FrameClock.IdentityFor(parsed);
            DetectionInputs inputs = BuildDetectionInputs(path, map, parsed, facts, record);
            SiteRegions regions = SiteRegions.Compose(map, null, table, profile);
            IReadOnlyList<TagProposal> proposals =
                ProposalDetection.Detect(map, inputs.TickRate, regions, inputs.Events, inputs.Rounds, profile);
            Dictionary<int, int> roundStarts = [];
            foreach (RoundOccupancy round in inputs.Rounds)
            {
                roundStarts.TryAdd(round.Round, round.StartTick);
            }

            ProposalDocument document = new()
            {
                Demo = new ProposalDemoHeader
                {
                    Sha256 = Normalize(record.Sha256),
                    StableKey = DemoCacheStore.StableKey(path),
                    FileName = fileName,
                    SizeBytes = record.Size
                },
                Clock = RoundFactsClock.From(clock),
                DetectorSet = new ProposalDetectorSet
                {
                    Fingerprint = fingerprint,
                    ProfileId = profile.Id,
                    ComputedAtTicks = _utcNow().Ticks,
                    Regions = regions.Source
                },
                OccupancySource = inputs.Source,
                Proposals = [.. proposals.Select(p => StoredProposal.From(p, roundStarts.GetValueOrDefault(p.Round)))]
            };

            // The file first and the stamp last: a crash between them leaves "not built", never a stamp
            // with nothing behind it.
            _proposals.Write(path, document);
            int pending = LoadCore(path, null).Set.Pending.Count;
            _demoCache.UpdateExisting(path, r =>
            {
                r.SuggestionsFingerprint = fingerprint;
                r.SuggestionCount = pending;
            });
            _demoCache.SaveIndex();
            RaiseChanged(path);
        }
        catch (Exception ex)
        {
            SuggestedTagsLog.BuildFailed(Log, fileName, ex);
            lock (_gate)
            {
                _failed.Add(path);
            }
        }
        finally
        {
            ClearForced(path);
        }
    }

    /// <summary>
    ///     The profile-independent half of a build (suggested-tags.md §3.2): the tick rate, the rounds'
    ///     occupancy and the placed events. Split out from the profile-dependent half (site regions,
    ///     then <see cref="ProposalDetection.Detect" />) so the tuning view's in-memory re-run can hold
    ///     one parse's occupancy and events and try many candidate profiles against them without
    ///     re-parsing for each.
    /// </summary>
    /// <param name="TickRate">Ticks per second.</param>
    /// <param name="Rounds">One occupancy per live round.</param>
    /// <param name="Events">Every placed detonation of the demo, tick order.</param>
    /// <param name="Source">"round-index" or "walk" (<see cref="ProposalDocument.FromRoundIndex" />/<see cref="ProposalDocument.FromWalk" />).</param>
    internal readonly record struct DetectionInputs(
        int TickRate, IReadOnlyList<RoundOccupancy> Rounds, IReadOnlyList<PlacedEvent> Events, string Source);

    /// <summary>
    ///     Builds a demo's occupancy and placed events: the index when it is current for the map, else
    ///     the walk (correction 11, both give the same rows); only the walk leaves a cloud behind for
    ///     detonation placement. Internal so the tuning view's re-run harness can reuse it.
    /// </summary>
    /// <param name="path">The demo's path (the index sidecar and the zone/place lookups key by it).</param>
    /// <param name="map">The map, resolved.</param>
    /// <param name="parsed">The held parse.</param>
    /// <param name="facts">The demo's Round Facts rows.</param>
    /// <param name="record">The demo's cache record, for the Round Index's freshness stamp.</param>
    internal DetectionInputs BuildDetectionInputs(
        string path, string map, ParsedDemo parsed, RoundFactsRows facts, DemoCacheRecord record)
    {
        int tickRate = parsed.TickRate > 0 ? parsed.TickRate : 64;
        IReadOnlyList<RoundOccupancy> rounds;
        DetonationCloud? cloud = null;
        string source;
        if (_index is not null && _indexSources is not null
                               && record.IsRoundIndexCurrent(_indexSources.FingerprintFor(map))
                               && _index.TryRead(path) is { } indexDocument)
        {
            rounds = RoundOccupancyBuilder.FromIndex(indexDocument, facts, tickRate);
            source = ProposalDocument.FromRoundIndex;
        }
        else
        {
            OccupancyBuild build = RoundOccupancyBuilder.FromWalk(parsed, facts, samples: _walk?.Invoke(parsed));
            rounds = build.Rounds;
            cloud = build.Cloud;
            source = ProposalDocument.FromWalk;
        }

        DetonationPlaceResolver resolver = new(
            string.IsNullOrEmpty(map) ? null : _zones.TryGet(map),
            string.IsNullOrEmpty(map) ? null : _placesFor?.Invoke(map),
            cloud);
        List<PlacedEvent> events = resolver.Place(DetonationEvents.From(parsed));
        return new DetectionInputs(tickRate, rounds, events, source);
    }

    private bool NeedsBuild(DemoCacheIndexEntry? entry) =>
        HasInputs(entry)
        && !string.Equals(entry!.SuggestionsFingerprint, FingerprintFor(entry.Map), StringComparison.Ordinal);

    private static bool HasInputs(DemoCacheIndexEntry? entry) =>
        entry is { ParseSchema: > 0, RoundFactsSchema: > 0, RoundFactsFingerprint: not null };

    private SiteRegionTable? TableFor(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return null;
        }

        lock (_gate)
        {
            if (_tables.TryGetValue(map, out SiteRegionTable? cached))
            {
                return cached;
            }
        }

        SiteRegionTable? table;
        try
        {
            table = _regions.TryLoad(map);
        }
        catch (ArgumentException)
        {
            table = null; // a map name no file can carry: site-only
        }

        lock (_gate)
        {
            _tables[map] = table;
        }

        return table;
    }

    private bool IsOpen(string path) =>
        _openDemo() is { } open && string.Equals(open, path, StringComparison.OrdinalIgnoreCase);

    private bool IsFailed(string path)
    {
        lock (_gate)
        {
            return _failed.Contains(path);
        }
    }

    private void ClearForced(string path)
    {
        lock (_gate)
        {
            _forcedPaths.Remove(path);
        }
    }

    private void RaiseChanged(string path) => _post(() => Changed?.Invoke(path));

    private static string? Normalize(string? sha256) =>
        string.IsNullOrEmpty(sha256) ? null : sha256.ToLower(CultureInfo.InvariantCulture);
}

/// <summary>Source-generated log lines for the Suggested Tags evaluator.</summary>
internal static partial class SuggestedTagsLog
{
    /// <summary>Category (the "App" source tag) for suggested tags lines.</summary>
    public const string Category = "App.SuggestedTags";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{fileName}: suggested tags were not built")]
    public static partial void BuildFailed(ILogger logger, string fileName, Exception exception);
}

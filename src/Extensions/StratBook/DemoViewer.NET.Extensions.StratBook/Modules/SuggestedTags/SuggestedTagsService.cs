#region

using System.Globalization;
using System.Text.Json.Nodes;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     What the tagger changed before accepting, in the editor. A null field keeps
///     the proposal's value. Any edit, even one that changes nothing, accepts with <c>edited: true</c>:
///     the tagger opened the editor, which is the signal the tuning view reads.
/// </summary>
/// <param name="FromTick">The claim window's start, frame clock.</param>
/// <param name="ToTick">The claim window's end, frame clock.</param>
/// <param name="Labels">The labels, in order, replacing the proposal's; a group may repeat.</param>
/// <param name="Note">A note for the instance.</param>
/// <param name="Code">Another code than the proposal's; the verdict is then <c>recoded</c>.</param>
/// <param name="Positions">Map positions for the instance.</param>
public sealed record TagInstanceEdit(
    int? FromTick = null,
    int? ToTick = null,
    IReadOnlyList<TagLabel>? Labels = null,
    string? Note = null,
    string? Code = null,
    IReadOnlyList<TagPosition>? Positions = null);

/// <summary>
///     The Suggested Tags engine as a background evaluator: one
///     parse, many evaluators, registered after the Round Index so the index written in the same pass is
///     the occupancy source and the walk is the exception.
///     <para>
///         Per demo: occupancy from a current <c>.dvri.json</c>, else the walk over the held parse; the
///         detonations placed by zones, then the index's place snap, then the walk's sparse cloud;
///         the five detectors in the profile's order; the proposals file written; and the
///         record stamped with the detector-set fingerprint and the pending count, stamp last so a crash
///         leaves "not built". Everything needs Round Facts rows; a demo without them is
///         not wanted.
///     </para>
///     <para>
///         <b>Verdicts are user truth</b> and go through the Tag Store: an accept writes the
///         instance, with <c>source: "suggested"</c> and the provenance object, and then the verdict; a
///         reject writes only the verdict. Neither ever touches the proposals file, which a tuning pass
///         may rebuild at any time.
///     </para>
///     <para>
///         <b>Background policy</b>: the sweep over the library is its own opt-in and off
///         by default; the open demo is always evaluated on the parse the open already paid for, and a
///         forced request (the queue's "Detect" button) always runs.
///     </para>
/// </summary>
public sealed class SuggestedTagsService : IExtensionPass
{
    /// <summary>The queue owner tag and the pass id.</summary>
    public const string EvaluatorId = "suggestedtags";

    /// <summary>
    ///     The feature id, a sub-feature of the 2D Playback tab: off hides the track and the queue and stops
    ///     the evaluator. A persisted key; never renamed.
    /// </summary>
    public const string FeatureId = "playback2d.suggestedtags";

    /// <summary>The <c>provenance.source</c> an accepted instance carries.</summary>
    public const string ProvenanceSource = "suggested-tags";

    private static ILogger? _diagLog;

    private readonly Func<bool> _background;
    private readonly IExtensionLibrary _library;
    private readonly IRoundFactsSource _roundFacts;

    /// <summary>The Round Facts rows the detectors read, for the tuning preview's re-runs.</summary>
    internal IRoundFactsSource RoundFacts => _roundFacts;
    private readonly Func<bool> _enabled;

    // Demos whose build threw this session: not wanted again until a forced request, so one bad file is
    // not re-parsed on every pass. Session-only on purpose; a restart retries them once.
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    // Manual requests: they run whatever the opt-in says, at user priority. Under _gate.
    private readonly HashSet<string> _forcedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Test seam: stands in for the rollback's <see cref="TagStore.Update" />.</summary>
    internal Func<string, Action<TagDocument>, bool>? RollbackOverride { get; set; }

    // Accepts whose verdict and rollback both failed: the tag may be in the document. Under _gate.
    private readonly HashSet<(string Sha, string ProposalId)> _stuckAccepts = [];
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

    /// <param name="library">The demo library: which demos are parsed and carry Round Facts.</param>
    /// <param name="roundFacts">The Round Facts rows the detectors bound rounds and seat sides with.</param>
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
        IExtensionLibrary library,
        IRoundFactsSource roundFacts,
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
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(roundFacts);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(background);
        _library = library;
        _roundFacts = roundFacts;
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

    /// <summary>The scheduling of the extension's passes, so a forced request can ask for the demo again. Null in tests.</summary>
    public IExtensionPasses? Passes { get; set; }

    /// <summary>The proposals store, whose stamps carry each demo's pending count.</summary>
    public ProposalStore Proposals => _proposals;

    /// <summary>Whether proposals and verdicts outlive the process. False on the browser host.</summary>
    public bool IsPersistent => _proposals.IsPersistent && (_tags?.IsPersistent ?? false);

    /// <summary>True while the scheduler has a build in flight.</summary>
    public bool IsDetecting => Passes?.IsBusy(EvaluatorId) ?? false;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <inheritdoc />
    public bool ReadsUserCommands => false;

    /// <summary>A demo's proposals or verdicts changed. Raised through the post delegate with its path.</summary>
    public event Action<string>? Changed;

    /// <summary>Whether the demo needs this pass now.</summary>
    /// <remarks>
    ///     From the index row alone: the gate on, Round Facts rows present, the proposals missing or built
    ///     under another fingerprint than the map's current one, and the sweep on or the demo forced. The
    ///     open demo is wanted whatever the opt-in says: its build runs on the parse the open paid for.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!_enabled() || !NeedsBuild(_library.Find(path)))
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

        return _background() || IsOpen(path);
    }

    /// <summary>Whether the demo will need this pass once the pass it runs after has written.</summary>
    /// <remarks>
    ///     A demo without Round Facts rows yet, with the sweep on, the demo forced or the demo open: the
    ///     proposals follow the rows.
    /// </remarks>
    public bool WantsAfterUpstream(string path)
    {
        if (!_enabled() || HasInputs(_library.Find(path)))
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

        return _background() || IsOpen(path);
    }

    /// <inheritdoc />
    public JobPriority PriorityFor(string demoPath)
    {
        lock (_gate)
        {
            return _forcedPaths.Contains(demoPath) ? JobPriority.UserRequested : JobPriority.Background;
        }
    }

    /// <inheritdoc />
    public long OrderHint(string demoPath) => _library.Find(demoPath)?.Modified.Ticks ?? 0;

    /// <inheritdoc />
    public DemoInterest Interest(string demoPath) =>
        Wants(demoPath) ? DemoInterest.Yes : WantsAfterUpstream(demoPath) ? DemoInterest.AfterUpstream : DemoInterest.No;

    /// <inheritdoc />
    public void Run(IPassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Evaluate(context.DemoPath, context.Parsed);
    }

    /// <summary>Does the pass's work on <paramref name="parsed" />.</summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="parsed">The demo's parse.</param>
    public void Evaluate(string path, ParsedDemo parsed) => Refresh(path, parsed);

    /// <inheritdoc />
    public void OnFailed(string demoPath) => ClearForced(demoPath);

    /// <summary>
    ///     The demos the sweep or a force would submit, newest first, for the pending counts. Derived from
    ///     the index, never stored.
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
            .. _library.Demos
                .Where(e => NeedsBuild(e)
                            && (forced.Contains(e.FilePath) || (background && !failed.Contains(e.FilePath))))
                .OrderByDescending(e => e.Modified.Ticks)
                .Select(e => e.FilePath)
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

        Passes?.Request(path);
    }

    /// <summary>Whether a demo has what a build needs: a parse and Round Facts rows.</summary>
    /// <param name="path">The demo's path.</param>
    public bool CanDetect(string path) => HasInputs(_library.Find(path));

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

    /// <summary>Offers a dismissed proposal again. False when it is not dismissed or the verdict could not be written.</summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="proposalId">The proposal's identity key.</param>
    /// <param name="sha256">The demo's hash when the caller knows it better than the index.</param>
    public bool Restore(string path, string proposalId, string? sha256 = null)
    {
        (_, ProposalSet set) = LoadCore(path, sha256);
        if (_tags is null || set.Sha256 is not { } sha
            || set.Entries.FirstOrDefault(e => e.Proposal.Id == proposalId && e.State == GeneratedState.Dismissed) is not
                { VerdictKey: { } key }
            || !_tags.RestoreVerdict(sha, key, _utcNow()))
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
    /// <param name="round">The queue's round filter, or null for every round.</param>
    public int AcceptAll(string path, double minConfidence, string? detector = null, string? sha256 = null, int? round = null)
    {
        List<string> ids =
        [
            .. Load(path, sha256).Pending
                .Where(e => e.Proposal.Confidence >= minConfidence
                            && (detector is null || e.Proposal.Detector == detector)
                            && (round is null || e.Proposal.Round == round))
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
    ///     The instance an accept writes: the proposal's code, window and round,
    ///     <c>source: "suggested"</c>, its labels in the human namespace with the side it is about,
    ///     and the free-form provenance object.
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
        List<TagLabel> labels = edit?.Labels is { } edited
            ? [.. edited.Select(l => new TagLabel(l.Group, l.Value))]
            : [.. proposal.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new TagLabel(l.Key, l.Value))];

        TagInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Code = edit?.Code is { Length: > 0 } code ? code : proposal.Code,
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

        if (!labels.Any(l => l.Group == "side") && proposal.Side is 2 or 3)
        {
            instance.Labels.Add(new TagLabel("side", ProposalIds.SideName(proposal.Side)));
        }

        instance.Labels.AddRange(labels);
        if (edit?.Positions is { } positions)
        {
            instance.Positions.AddRange(positions);
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

        lock (_gate)
        {
            // Its tag may still be in the document: a second accept would write a copy.
            if (_stuckAccepts.Contains((sha, proposalId)))
            {
                return false;
            }
        }

        DateTime now = _utcNow();
        TagInstance instance = InstanceFor(entry.Proposal, edit, document.DetectorSet.Fingerprint, now);

        // The facts a hand-made tag gets as it is made, so the Matrix can slice an accepted tag at once
        // rather than after the next rows rewrite.
        LibraryDemo? record = _library.Find(path);
        if (record is not null && _roundFacts.TryGet(path) is { Schema: StratBookCache.RoundFactsSchema } rows)
        {
            TagFactsRefresher.RefreshInstance(instance, rows.Rounds, StratBookCache.RoundFactsSchema, now);
        }

        DemoIdentity demo = new(sha, document.Demo.FileName ?? Path.GetFileName(path),
            record?.FileSizeBytes ?? document.Demo.SizeBytes);
        if (!_tags.Append(demo, document.Clock.ToIdentity(), instance))
        {
            return false;
        }

        string verdict = edit is null ? SuggestionVerdicts.Accepted
            : edit.Code is { Length: > 0 } code && !string.Equals(code, entry.Proposal.Code, StringComparison.Ordinal)
                ? SuggestionVerdicts.Recoded
                : SuggestionVerdicts.Edited;
        if (_tags.RecordVerdict(sha, proposalId, Verdict(verdict, instance.Id, entry.Proposal, document)))
        {
            return true;
        }

        // Without its verdict the proposal stays pending, and accepting it again would write a second tag.
        if (!(RollbackOverride ?? _tags.Update)(sha, d => d.Instances.RemoveAll(i => i.Id == instance.Id)))
        {
            lock (_gate)
            {
                _stuckAccepts.Add((sha, proposalId));
            }

            if (Log.IsEnabled(LogLevel.Warning))
            {
                string fileName = Path.GetFileName(path);
                SuggestedTagsLog.AcceptRollbackFailed(Log, fileName, proposalId);
            }
        }

        return false;
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
        _proposals.SetCount(path, pending);

        RaiseChanged(path);
    }

    private (ProposalDocument? Document, ProposalSet Set) LoadCore(string path, string? sha256) =>
        _proposals.TryRead(path) is { } document ? Resolve(path, sha256, document) : (null, ProposalSet.Empty(path, IsPersistent));

    private int CountPending(string path, ProposalDocument document) => Resolve(path, null, document).Set.Pending.Count;

    // The document's proposals matched against the demo's verdicts.
    private (ProposalDocument? Document, ProposalSet Set) Resolve(string path, string? sha256, ProposalDocument document)
    {
        bool persistent = IsPersistent;
        string? sha = Normalize(sha256) ?? Normalize(_library.Find(path)?.Sha256)
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

            // A restored rejection is no verdict: the proposal is pending again and may re-match.
            verdicts = file.Verdicts.Where(v => v.Value.Verdict != SuggestionVerdicts.Restored)
                .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
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
            if (_library.Find(path) is not { } record || _roundFacts.TryGet(path) is not { Schema: StratBookCache.RoundFactsSchema } facts)
            {
                return; // nothing to bound rounds and seat sides with; Round Facts has not written this demo
            }

            string map = string.IsNullOrEmpty(parsed.MapName) ? record.MapName ?? "" : parsed.MapName;
            DetectorProfile profile = _profile();
            SiteRegionTable? table = TableFor(map);
            string fingerprint = SuggestionsFingerprint.Compose(profile, table);
            if (!forced && _proposals.IsCurrent(path, fingerprint)
                        && _proposals.TryRead(path) is not null)
            {
                return; // a queued request the open's visit already satisfied
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
                    StableKey = DemoKeys.StableKey(path),
                    FileName = fileName,
                    SizeBytes = record.FileSizeBytes
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

            // The pending count needs the verdicts matched against these proposals, so it is counted before
            // the write that stamps it.
            _proposals.Write(path, document, fingerprint, CountPending(path, document));
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
    ///     The profile-independent half of a build: the tick rate, the rounds'
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
    ///     the walk (both give the same rows); only the walk leaves a cloud behind for
    ///     detonation placement. Internal so the tuning view's re-run harness can reuse it.
    /// </summary>
    /// <param name="path">The demo's path (the index sidecar and the zone/place lookups key by it).</param>
    /// <param name="map">The map, resolved.</param>
    /// <param name="parsed">The held parse.</param>
    /// <param name="facts">The demo's Round Facts rows.</param>
    /// <param name="record">The demo's library row.</param>
    internal DetectionInputs BuildDetectionInputs(
        string path, string map, ParsedDemo parsed, RoundFactsRows facts, LibraryDemo record)
    {
        int tickRate = parsed.TickRate > 0 ? parsed.TickRate : 64;
        IReadOnlyList<RoundOccupancy> rounds;
        DetonationCloud? cloud = null;
        string source;
        if (_index is not null && _indexSources is not null
                               && _index.IsCurrent(path, _indexSources.FingerprintFor(map))
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
            string fileName = Path.GetFileName(path);
            foreach (RoundIndexDisagreement d in build.Disagreements)
            {
                SuggestedTagsLog.SampleDisagreedWithFacts(Log, fileName, d.Round, d.SideMismatches, d.AliveMismatches);
            }
        }

        DetonationPlaceResolver resolver = new(
            string.IsNullOrEmpty(map) ? null : _zones.TryGet(map),
            string.IsNullOrEmpty(map) ? null : _placesFor?.Invoke(map),
            cloud);

        // Names a thrower for the 21 to 27 percent of inferno and decoy detonations the wire leaves unresolved
        // (#56, #59); one bounded pass, since Removed samples are yielded on their own frame at any stride.
        List<ProjectileSample> projectiles = [.. ProjectileSampler.Walk(parsed, frameStride: 8).Where(s => s.Removed)];
        List<PlacedEvent> events = resolver.Place(DetonationEvents.From(parsed, projectiles));
        return new DetectionInputs(tickRate, rounds, events, source);
    }

    private bool NeedsBuild(LibraryDemo? entry) =>
        entry is not null && HasInputs(entry) && !_proposals.IsCurrent(entry.FilePath, FingerprintFor(entry.MapName));

    private static bool HasInputs(LibraryDemo? entry) =>
        entry is { State: >= LibraryDemoState.Parsed } && entry.Fact(RoundFactsRecords.FacetId) is { IsWritten: true };

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

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "{fileName}: round {round} sample alive/team disagreed with round facts ({sideMismatches} side, {aliveMismatches} alive)")]
    public static partial void SampleDisagreedWithFacts(ILogger logger, string fileName, int round, int sideMismatches, int aliveMismatches);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "{fileName}: accepting {proposalId} wrote its tag but not its verdict, and the tag could not be taken back; the proposal is held until restart")]
    public static partial void AcceptRollbackFailed(ILogger logger, string fileName, string proposalId);
}

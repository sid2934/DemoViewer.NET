#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     The highlights scanner (migrated onto the global queue —
///     docs/demo-processing-queue.md). The unified <see cref="DemoCacheStore" /> IS the work list, and the
///     backlog is DERIVED from it rather than stored (see <see cref="BacklogNewestFirst" />); the scanner
///     FEEDS that backlog into the shared <see cref="IDemoProcessingQueue" /> (which owns the workers, the
///     newest-first drain, the gate yielding, and the one-at-a-time invariant). A demo the Library already
///     submitted is
///     parsed ONCE — the queue coalesces both owners onto a single parse (this dissolves the historical
///     tier-2/backfill double-parse). Three feeders:
///     <list type="bullet">
///         <item>
///             <b>Piggyback:</b> the Library tier-2 hook hands over its already-parsed demo — a
///             stale/missing row refreshes on the already-serialized job; only when the background-scan opt-in is on.
///         </item>
///         <item>
///             <b>Backfill:</b> demos wanting a scan are submitted to the queue — auto rows only while the
///             the opt-in holds; forced (manual) rows always, at <see cref="DemoJobPriority.UserRequested" />.
///         </item>
///         <item>
///             <b>Open-demo harvest:</b> the Analysis tab's own evaluation refreshes the open demo's
///             row for free.
///         </item>
///     </list>
///     A scan failure sets ONLY <see cref="DemoCacheRecord.AnalysisState" /> to
///     <see cref="DemoAnalysisState.Failed" />. Tier 3 and tier 2 share one record now, so "never the
///     library entry" is enforced by what the write touches rather than by which file it lands in: the
///     roster, score and rounds the Library established are left exactly as they were.
/// </summary>
public sealed class HighlightScanService : IDisposable, IDemoEvaluator
{
    private readonly Func<bool> _backgroundScanEnabled;

    // Manual per-demo requests: they submit REGARDLESS of the opt-in, but ONLY these paths — a
    // retry click on one demo must never trigger a whole-library scan marathon. Cleared when the demo's
    // Evaluate/OnFailed runs. Drives PriorityFor (forced → UserRequested).
    private readonly HashSet<string> _forcedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DemoCacheStore _demoCache;
    private readonly IHighlightHarvester _harvester;
    private readonly Func<IReadOnlyList<string>> _libraryDemoPaths;

    private readonly object _lifecycle = new();
    private readonly Action<Action> _post;

    // Test seam: yields the harvested events for (path, parsed) instead of running the rules engine.
    // Null → real. Returning null means "this demo failed".
    private readonly Func<string, ParsedDemo, IReadOnlyList<HighlightFired>?>? _processorOverride;

    // RefreshStaleness serialization: one pass at a time, bursts coalesce to one queued pass.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private bool _disposed;
    private int _refreshQueued;


    /// <param name="harvester">Rules/analysis access (fake in tests).</param>
    /// <param name="libraryDemoPaths">Current library demo paths (reconciliation + backfill universe).</param>
    /// <param name="backgroundScanEnabled">The background-scan opt-in probe (live settings read).</param>
    /// <param name="post">UI-thread marshal for progress events.</param>
    /// <param name="processorOverride">Test seam replacing the rules run per path.</param>
    /// <param name="demoCache">The unified demo cache — tier 3 lives here, and nowhere else now.</param>
    public HighlightScanService(
        DemoCacheStore demoCache,
        IHighlightHarvester harvester,
        Func<IReadOnlyList<string>> libraryDemoPaths,
        Func<bool> backgroundScanEnabled,
        Action<Action>? post = null,
        Func<string, ParsedDemo, IReadOnlyList<HighlightFired>?>? processorOverride = null)
    {
        _harvester = harvester;
        _libraryDemoPaths = libraryDemoPaths;
        _backgroundScanEnabled = backgroundScanEnabled;
        _post = post ?? (action => action());
        _processorOverride = processorOverride;
        _demoCache = demoCache;

        // The coordinator owns submission + the CapacityAvailable re-feed; this service no longer touches
        // the queue directly (it is registered as an IDemoEvaluator, and Coordinator is set post-construct).
    }

    /// <summary>The evaluation coordinator that submits this evaluator's work; null → nothing is fed.</summary>
    public DemoEvaluationCoordinator? Coordinator { get; set; }

    /// <summary>Queued-demo count — the tab toolbar's "⟳ scan: N queued". DERIVED, never stored.</summary>
    public int QueueLength => BacklogNewestFirst().Count;

    /// <summary>
    ///     The demos wanting a scan, newest first. THE work list — computed from the index each time rather
    ///     than read out of a persisted <c>Pending</c> flag.
    ///     <para>
    ///         The old design stored that flag, which forced one field to mean both "queued" and "has no
    ///         tier-3 data". Deriving separates them: a demo whose rules fingerprint moved is queued while its
    ///         previous harvest stays on screen, so a rules save no longer blanks the highlight section of
    ///         every demo in the library until each is rescanned.
    ///     </para>
    ///     <para>
    ///         Costs one dictionary pass over the index (the fingerprint is mirrored onto the index row for
    ///         exactly this), plus the library paths that have no record at all.
    ///     </para>
    /// </summary>
    private List<string> BacklogNewestFirst()
    {
        string? fingerprint = TryFingerprint();
        Dictionary<string, long> wanted = new(StringComparer.OrdinalIgnoreCase);

        foreach (DemoCacheIndexEntry entry in _demoCache.Index)
        {
            if (entry.NeedsAnalysis(fingerprint))
            {
                wanted[entry.Path] = entry.ModifiedTicks;
            }
        }

        // A demo the library knows about but the cache has never seen is the most-pending case there is.
        foreach (string path in _libraryDemoPaths())
        {
            if (Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal))
            {
                continue; // AppleDouble sidecar, never a demo
            }

            if (_demoCache.TryGetIndex(path) is null)
            {
                wanted.TryAdd(path, 0);
            }
        }

        return [.. wanted.OrderByDescending(kv => kv.Value).Select(kv => kv.Key)];
    }

    // The rules fingerprint is tick-rate dependent, and the backlog spans demos of several rates. 64 is what
    // every supported CS2 demo records at, so it is the right single probe; a rate that genuinely differs is
    // caught per demo at scan time. A config that cannot load yields null, which reads as "current" — one
    // broken rule file must never mark the whole library stale.
    private string? TryFingerprint()
    {
        try
        {
            return _harvester.ComputeFingerprint(64).Fingerprint;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool NeedsScan(string path)
    {
        DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
        return entry is null || entry.NeedsAnalysis(TryFingerprint());
    }

    /// <summary>True while any highlights demo is outstanding in the shared queue (via the coordinator).</summary>
    public bool IsScanning => Coordinator?.HasOutstanding(Id) ?? false;

    /// <summary>
    ///     Opportunistic hand-off: a demo parsed elsewhere — the Library
    ///     tier-2 slot (holding the parse), or an interactive open — is handed over via the coordinator's
    ///     <see cref="DemoEvaluationCoordinator.FanOutParsed" />. Refreshes the row only when missing/stale
    ///     AND the opt-in is on — the common fresh case (and every demo when the opt-in is off) costs one
    ///     fingerprint compare. Not gated on <see cref="Wants" /> (order-independent: it refreshes a row
    ///     that may not exist yet), which is exactly what the old Library <c>Tier2DemoParsed</c> piggyback
    ///     guaranteed before this generalized it.
    /// </summary>
    public void OnParsedOpportunistically(string path, ParsedDemo parsed)
    {
        try
        {
            // Fingerprint BEFORE the analysis: a rule save mid-run then errs on the stale side.
            (string fingerprint, IReadOnlyDictionary<string, string> hashes) =
                _harvester.ComputeFingerprint(parsed.TickRate);
            DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
            if (entry is { AnalysisState: DemoAnalysisState.Indexed })
            {
                (long size, long modified) = SafeFileIdentity(path);
                if (string.Equals(entry.ConfigFingerprint, fingerprint, StringComparison.Ordinal)
                    && entry.ModifiedTicks == modified && entry.Size == size)
                {
                    return;
                }
            }

            // The full replay below is gated behind the background-scan opt-in like the backfill.
            if (!_backgroundScanEnabled())
            {
                return;
            }

            AnalysisRun run = _harvester.RunBareAnalysis(parsed);
            WriteHarvest(path, parsed, run.Highlights, fingerprint, hashes);
            RaiseProgress();
        }
        catch (Exception)
        {
            MarkFailed(path);
        }
    }

    // ── Backfill: feed Pending rows into the shared queue ──────────────────────

    // ── IDemoEvaluator ("one parse, many evaluators") ──

    /// <inheritdoc />
    public string Id => "highlights";

    /// <inheritdoc />
    /// <remarks>
    ///     Interested when the row is Pending AND either it was manually forced or the opt-in is
    ///     on — forced rows flow regardless of the opt-in, auto rows only while it holds.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!NeedsScan(path))
        {
            return false;
        }

        bool forced;
        lock (_lifecycle)
        {
            forced = _forcedPaths.Contains(path);
        }

        return forced || _backgroundScanEnabled();
    }

    /// <inheritdoc />
    public DemoJobPriority PriorityFor(string path)
    {
        lock (_lifecycle)
        {
            return _forcedPaths.Contains(path) ? DemoJobPriority.UserRequested : DemoJobPriority.Background;
        }
    }

    /// <inheritdoc />
    public long OrderHint(string path) => _demoCache.TryGetIndex(path)?.ModifiedTicks ?? 0;

    /// <inheritdoc />
    /// <remarks>
    ///     Runs in the queue's gate slot with the parse held. Re-checks the row is still Pending —
    ///     the Library piggyback may have refreshed it while this was queued (re-processing would waste
    ///     the slot). Clears the forced flag either way.
    /// </remarks>
    public void Evaluate(string path, ParsedDemo parsed)
    {
        bool processed = false;
        try
        {
            // A demo the user explicitly asked about runs the EXPENSIVE mode; the background sweep never
            // does. That split is what lets "Compute full stats" mean what it says: the scoreboard is
            // projected from snapshot vectors, which the bare run does not produce at all, so a forced scan
            // that stayed bare would deliver highlights and quietly leave the stats half of the page reading
            // "needs a full analysis pass" — with the button that supposedly fixes it having just run.
            bool forced;
            lock (_lifecycle)
            {
                forced = _forcedPaths.Contains(path);
            }

            // A FORCED request runs even when the row is no longer Pending.
            //
            // The skip below exists so a demo the Library piggyback already refreshed does not burn a queue
            // slot being re-scanned — sound while every run was equivalent, and WRONG the moment bare and
            // full stopped being the same thing. Both owners coalesce onto one parse, so the Library's
            // Evaluate fans out to OnParsedOpportunistically (a BARE run, which upserts Indexed) and can
            // easily win the race against this. The user's press would then be silently consumed: the row
            // reads Indexed, this returns early, `finally` clears the forced flag, and they get highlights
            // with no scoreboard and no indication anything was skipped.
            if (!forced && !NeedsScan(path))
            {
                return;
            }

            processed = true;

            AnalysisRun? run = null;
            IReadOnlyList<HighlightFired>? harvested;
            if (_processorOverride is not null)
            {
                harvested = _processorOverride(path, parsed);
            }
            else
            {
                run = forced ? _harvester.RunFullAnalysis(parsed) : _harvester.RunBareAnalysis(parsed);
                harvested = run.Highlights;
            }

            if (harvested is not null)
            {
                WriteHarvest(path, parsed, harvested);

                // Only when the run actually carried snapshots — a harvester that does not implement the full
                // mode falls back to the bare run, and no scoreboard is the honest outcome there.
                if (run?.Snapshots is not null)
                {
                    WriteScoreboardFromRun(path, run, parsed);
                }
            }
            else
            {
                MarkFailed(path);
            }
        }
        catch (Exception)
        {
            MarkFailed(path);
        }
        finally
        {
            lock (_lifecycle)
            {
                _forcedPaths.Remove(path);
            }

            if (processed)
            {
                _demoCache.SaveIndex(); // cheap vs a parse; the sidecar itself was already written atomically
            }

            RaiseProgress();
        }
    }

    /// <inheritdoc />
    public void OnFailed(string path)
    {
        lock (_lifecycle)
        {
            _forcedPaths.Remove(path);
        }

        MarkFailed(path);
        _demoCache.SaveIndex();
        RaiseProgress();
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
        }
        // The coordinator owns the CapacityAvailable subscription now — nothing queue-side to detach here.
    }

    /// <summary>Raised (posted) when queue length / scanning state changes.</summary>
    public event Action? ScanProgressChanged;

    /// <summary>
    ///     Re-fingerprint pass (triggers: app start, tab activation, rule save, manual rescan):
    ///     reconciles rows against the library (drops vanished files, skips AppleDouble sidecars),
    ///     creates Pending skeletons for unseen demos, and re-marks rows whose fingerprint no longer
    ///     matches the current config at their tick rate. No parse, but it stats every library file and
    ///     may compose+hash the rule config — so the pass runs on a background thread, serialized, with
    ///     bursts of library Changed events coalesced to one queued pass. Then feeds the backfill queue.
    /// </summary>
    public void RefreshStaleness()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            // A pass is already queued (not yet started) — it will observe this trigger's world.
            return;
        }

        _ = Task.Run(async () =>
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_disposed)
                {
                    RefreshStalenessCore();
                }
            }
            catch (Exception)
            {
                // A failed pass retries on the next trigger; never crash a worker thread.
            }
            finally
            {
                _refreshGate.Release();
            }
        });
    }

    private void RefreshStalenessCore()
    {
        // WRITES NOTHING. The backlog is derived (see BacklogNewestFirst), so "refreshing staleness" is now
        // just re-deriving it and asking the coordinator to reconsider — the pass that used to stamp Pending
        // across the library has no work left to do.
        //
        // AND IT MUST NOT PRUNE. The old version dropped every row whose demo was not in the current
        // library paths, which was safe when it owned a highlights-only store. Against the UNIFIED cache the
        // same call would delete whole records — taking the Library's tier-2 roster, score and rounds with
        // them — and a configured folder on a detached volume enumerates zero files, so it would fire exactly
        // when the demos are still perfectly fine. Pruning the unified cache belongs to the Library, which
        // already does it, with the reached-roots guard that makes it safe.
        RaiseProgress();
        EnsureBackfillRunning();
    }

    /// <summary>
    ///     Manual per-demo rescan (staleness badge / failed-retry click). Runs regardless of the opt-in
    ///     — but scoped to THIS path: it is submitted at <see cref="DemoJobPriority.UserRequested" />
    ///     (outranks auto, bypasses the size cap); auto rows still only flow while the opt-in holds.
    /// </summary>
    public void RequestScan(string path)
    {
        // ONE write, and only for a failure: Failed is excluded from the derived backlog on purpose (else a
        // corrupt demo is re-parsed on every pass), so an explicit retry has to lift it. It also keeps the
        // scan chip honest — leaving the record Failed would make "retry" a button that changes nothing
        // visible until the scan finishes, on a count that gates the button itself.
        try
        {
            if (_demoCache.TryGetIndex(path) is { AnalysisState: DemoAnalysisState.Failed })
            {
                _demoCache.UpdateExisting(path, r => r.AnalysisState = DemoAnalysisState.Pending);
            }
        }
        catch (Exception)
        {
            // The forced set below still carries it into the queue.
        }

        lock (_lifecycle)
        {
            _forcedPaths.Add(path);
        }

        RaiseProgress();
        EnsureBackfillRunning();
    }

    /// <summary>
    ///     Marks every row Pending and forces them all (toolbar "Rescan all" — the one whole-queue
    ///     force; an explicit user click on exactly that).
    /// </summary>
    public void RescanAll()
    {
        // Every demo the library knows about, forced — "rescan all" has to reach the demos that are
        // CURRENT too, which is precisely what the forced set is for now that the backlog is derived.
        List<string> all = [.. _libraryDemoPaths()];

        lock (_lifecycle)
        {
            _forcedPaths.UnionWith(all);
        }

        RaiseProgress();
        EnsureBackfillRunning();
    }

    /// <summary>
    ///     Open-demo harvest: the Analysis tab's completed evaluation refreshes the open demo's
    ///     row for free. The SHA/streaming work runs off-thread.
    /// </summary>
    public void OnOpenDemoEvaluated(string? path, AnalysisRun run, ParsedDemo parsed)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                WriteHarvest(path, parsed, run.Highlights);
                RaiseProgress();
            }
            catch (Exception)
            {
                MarkFailed(path);
            }
        });
    }

    /// <summary>
    ///     Pending row paths (newest first) — this evaluator's slice of the coordinator's candidate
    ///     universe (worker-readable snapshot; never enumerated off-thread against a UI collection).
    /// </summary>
    public IReadOnlyList<string> PendingPaths() => BacklogNewestFirst();

    /// <summary>
    ///     Asks the coordinator to (re-)consider the Pending rows — a call-site-compatible shim over the
    ///     old queue feeder. Per-row gating (forced set + opt-in) lives in <see cref="Wants" /> now, and
    ///     the coordinator's outstanding set makes it idempotent.
    /// </summary>
    public void EnsureBackfillRunning(bool force = false)
    {
        _ = force; // retained for call-site compat; Wants decides submission per row
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }
        }

        Coordinator?.ConsiderAll();
        RaiseProgress();
    }

    // ── Row assembly ──────────────────────────────────────────────────────────

    private void MarkFailed(string path)
    {
        // Best-effort HERE and nowhere else. This runs from the catch block that is already handling a scan
        // failure; letting a cache-write error out of it would replace the real cause with a second one and
        // escape Evaluate entirely. The SUCCESS path deliberately does not catch — a write that silently fails
        // there is the bug this whole seam exists to fix, and it must be loud.
        try
        {
            _demoCache.UpdateExisting(path, record => record.AnalysisState = DemoAnalysisState.Failed);
        }
        catch (Exception)
        {
            // Rebuildable; the demo simply stays in the backlog instead of being marked failed.
        }

        RaiseProgress();
    }

    /// <summary>
    ///     Stores the SCOREBOARD half of tier 3 from a snapshot-bearing run — the other producer being a real
    ///     interactive open, which gets its table for free. Same projection either way, so a demo's stats do
    ///     not depend on which route computed them.
    /// </summary>
    private void WriteScoreboardFromRun(string path, AnalysisRun run, ParsedDemo parsed)
    {
        if (_demoCache is null || run.Snapshots is null)
        {
            return;
        }

        MetricTable table = new PlayerGameStatsProjector
        {
            MatchId = Path.GetFileName(path)
        }.Project(run.Snapshots, parsed).Single();

        List<CachedStatRow> rows = DemoCacheAnalysisProjector.ProjectScoreboard(table);
        if (rows.Count == 0)
        {
            // Not a scoreboard. Writing an empty list would stamp the tier and let the page read FULL off a
            // record with nothing in it.
            return;
        }

        (int? ctSide, int? tSide) = DemoCacheAnalysisProjector.ComputeSideWins(table);
        int rounds = _demoCache.TryLoadRecord(path)?.Rounds.Count ?? 0;

        _demoCache.UpdateExisting(path, record =>
        {
            record.Scoreboard = rows;
            record.CtSideWins = ctSide;
            record.TSideWins = tSide;
            if (rounds > 0)
            {
                record.AnalysisRoundCount = rounds;
            }

            DemoCacheStore.StampAnalysis(record);
        });
    }

    /// <summary>
    ///     Writes a completed harvest into the demo's tier 3 — the highlights half. The scoreboard half comes
    ///     from a snapshot-bearing run (<see cref="WriteScoreboardFromRun" />) or from a real interactive open.
    ///     <para>
    ///         This is step 4's absorption finished: the scanner used to write <c>highlights.json</c> and
    ///         mirror into the unified record, which meant two sources of truth for one fact and a whole class
    ///         of "which one is stale" question. There is one store now.
    ///     </para>
    ///     <para>
    ///         <b>Does not stamp file identity.</b> <c>UpdateExisting</c> keeps whatever the Library set, and
    ///         that is the point: the two writers historically disagreed on whether <c>modified</c> meant local
    ///         or UTC ticks, and the way to end that is for one writer to own identity rather than for both to
    ///         restate it. The Library owns it.
    ///     </para>
    /// </summary>
    private void WriteHarvest(string path, ParsedDemo parsed, IReadOnlyList<HighlightFired> events,
        string? knownFingerprint = null,
        IReadOnlyDictionary<string, string>? knownHashes = null)
    {
        (string fingerprint, IReadOnlyDictionary<string, string> hashes) =
            knownFingerprint is not null && knownHashes is not null
                ? (knownFingerprint, knownHashes)
                : _harvester.ComputeFingerprint(parsed.TickRate);

        // ClipRounds is the frame-clock round authority: round_freeze_end opens a
        // round, GameTick is the tick. CS2 emits no round_start — the string-matching walk this replaced
        // produced an EMPTY list on every CS2 demo, silently disabling the clip lead-in floor.
        List<Services.DemoCache.CachedRound> rounds = ClipRounds.Derive(parsed).ToCachedRounds();

        _demoCache.UpdateExisting(path, record =>
        {
            record.AnalysisState = DemoAnalysisState.Indexed;
            record.ProfileName = RulesHighlightHarvester.GotvProfileId;
            record.ConfigFingerprint = fingerprint;
            record.HighlightHashes = new Dictionary<string, string>(hashes);
            // The surfacing policy (Hidden drop + group supersession) lives in the packaged
            // HighlightSurfacing — one implementation, so a reel and this cache row can never
            // disagree about which firings are moments.
            record.Highlights =
            [
                .. HighlightSurfacing.Surface(events).Select(e => new CachedHighlightEvent
                {
                    RulesetId = e.RulesetId,
                    HighlightId = e.HighlightId,
                    FrameIndex = e.FrameIndex,
                    Tick = e.Tick,
                    ClipStartTick = e.ClipStartTick,
                    PlayerSlot = e.PlayerSlot,
                    RoundNumber = e.RoundNumber,
                    RenderedTitle = e.RenderedTitle,
                    Score = e.Score,
                    Kind = e.Kind
                })
            ];

            // Parse-tier facts this run happens to hold, filled where the record has none — a demo
            // scanned but never library-indexed otherwise renders with no tick rate and, worse, no roster,
            // which would leave every highlight attributed to a placeholder name. Never stamps Parse: the
            // score half of that tier is the Library's to claim.
            //
            // Also REPAIRS a placeholder roster, not only a missing one. Legacy names-only cache records
            // migrate in with every player at Slot = -1 / Team = 0 (LegacyCacheMigration) — names but no slot
            // attribution. Every consumer resolves a player's name BY SLOT (HighlightSelection.RawPlayerName,
            // and through it the reel's PlayerNameToSpectate); against a -1 roster that lookup returns empty,
            // and CSVG then rejects the whole clip plan ("PlayerNameToSpectate must not be empty"). This scan
            // already holds a freshly parsed roster carrying real slots/teams/steamids, so overwrite the
            // placeholder. A usable roster (any player with Slot >= 0) is the Library's and left untouched.
            List<CachedPlayerInfo> parsedRoster =
            [
                .. parsed.Players.Values
                    .Where(pl => !pl.IsBot && pl.Name.Length > 0)
                    .Select(pl => new CachedPlayerInfo
                    {
                        Slot = pl.Slot,
                        Name = pl.Name,
                        SteamId64 = pl.SteamId64.ToString(CultureInfo.InvariantCulture),
                        Team = pl.Team
                    })
            ];

            bool rosterMissing = record.Players.Count == 0;
            // Only overwrite a NAMES-ONLY placeholder when this parse actually recovered real slots — never
            // trade a roster that at least has names for an empty one just because the parse saw no players.
            bool placeholderRepairable = record.Players.Count > 0
                                         && record.Players.All(p => p.Slot < 0)
                                         && parsedRoster.Exists(p => p.Slot >= 0);
            if (rosterMissing || placeholderRepairable)
            {
                record.Players = parsedRoster;
            }

            if (record.Rounds.Count == 0 && rounds.Count > 0)
            {
                record.Rounds = rounds;
                record.RoundCount = rounds.Count;
            }

            if (record.TickRate <= 0)
            {
                record.TickRate = parsed.TickRate;
            }

            if (record.TickCount <= 0)
            {
                record.TickCount = parsed.TickCount;
            }

            if (record.ServerStartTick == 0)
            {
                record.ServerStartTick = parsed.ServerStartTick;
            }

            if (string.IsNullOrEmpty(record.Map) && !string.IsNullOrEmpty(parsed.MapName))
            {
                record.Map = parsed.MapName;
            }

            DemoCacheStore.StampAnalysis(record);
        });
    }

    /// <summary>
    ///     File identity in the LIBRARY'S UNITS — local <c>LastWriteTime</c> ticks, which is what every record
    ///     on disk actually carries (<see cref="DemoCacheStore.UpdateExisting" /> documents why).
    ///     <para>
    ///         This used to read <c>LastWriteTimeUtc</c>, the scanner's old convention from when it owned its
    ///         own file. Nothing was corrupted by that — this value is only ever COMPARED, never written — but
    ///         for every user not on UTC the compare could not match, so the freshness early-out in
    ///         <see cref="OnParsedOpportunistically" /> never fired and a current demo was re-harvested on
    ///         every hand-off. Read-side alignment is safe precisely because it is read-side: it changes no
    ///         stored value and invalidates nothing.
    ///     </para>
    /// </summary>
    private static (long Size, long ModifiedTicks) SafeFileIdentity(string path)
    {
        try
        {
            FileInfo info = new(path);
            // A missing file reports the 1601 epoch, not zero — normalize so "unknown" is stable.
            return info.Exists ? (info.Length, info.LastWriteTime.Ticks) : (0, 0);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private void RaiseProgress() => _post(() => ScanProgressChanged?.Invoke());
}

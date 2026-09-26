#region

using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The evaluator that writes the round index: an <see cref="IDemoEvaluator" /> on the tier-2
///     fan-out, registered after <see cref="RoundFactsEvaluator" /> so it reads the rows that one wrote
///     in the same pass. Round Facts is rules-driven and has no extractor, so there is nothing to fall
///     back to: a demo is not wanted until its index row carries a Round Facts fingerprint, and the
///     index simply follows Round Facts by one pass and never races it.
///     <para>
///         Per demo the work is one position walk (1 to 3 s on top of the parse the queue already
///         paid), the positions file and the sidecar written in that order, and one record stamp, the
///         stamp last so a crash between them leaves "not indexed" and never a stamp without both
///         files. A throw stamps <see cref="RoundIndexState.Failed" />,
///         which the derived backlog excludes until the user retries; the parse itself failing is not
///         this evaluator's to mark.
///     </para>
/// </summary>
public sealed class RoundIndexEvaluator : IDemoEvaluator
{
    /// <summary>The queue owner tag and the coordinator's id for this evaluator.</summary>
    public const string EvaluatorId = "roundindex";

    private static ILogger? _diagLog;

    private readonly Func<bool> _backgroundIndex;
    private readonly DemoCacheStore _demoCache;

    // Manual per-demo requests (a Retry on the strip): they submit regardless of the opt-in, but only
    // these paths, and at user priority. Cleared when the demo's Evaluate or OnFailed runs.
    private readonly HashSet<string> _forcedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private readonly RoundIndexPlaceSources _sources;
    private readonly RoundIndexStore _store;

    // Test seam: yields the position walk for a parse instead of PositionSampler.Walk. Null is the
    // real walk; a synthetic parse carries no entity data for the tracker to replay.
    private readonly Func<ParsedDemo, IEnumerable<PositionSample>>? _walk;

    /// <param name="demoCache">The unified demo cache: the stamp lives on its records.</param>
    /// <param name="store">The sidecar store the rows go to.</param>
    /// <param name="sources">The place source and fingerprint in force per map.</param>
    /// <param name="backgroundIndex">The live <c>SituationsSettings.BackgroundIndex</c>; forced paths ignore it.</param>
    /// <param name="post">UI-thread marshal for <see cref="Indexed" />; defaults to synchronous.</param>
    /// <param name="walk">The position walk to fold; null walks the parse through the engine's sampler.</param>
    public RoundIndexEvaluator(
        DemoCacheStore demoCache,
        RoundIndexStore store,
        RoundIndexPlaceSources sources,
        Func<bool> backgroundIndex,
        Action<Action>? post = null,
        Func<ParsedDemo, IEnumerable<PositionSample>>? walk = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(backgroundIndex);
        _demoCache = demoCache;
        _store = store;
        _sources = sources;
        _backgroundIndex = backgroundIndex;
        _post = post ?? (action => action());
        _walk = walk;
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundIndexLog.Category);

    /// <summary>The coordinator, so a retry or a rebuild can ask it to reconsider. Null in tests.</summary>
    public DemoEvaluationCoordinator? Coordinator { get; set; }

    /// <summary>True while the coordinator has index work in flight.</summary>
    public bool IsIndexing => Coordinator?.HasOutstanding(EvaluatorId) ?? false;

    /// <summary>
    ///     A demo's sidecar was written, raised synchronously on the evaluator's thread before the
    ///     posted <see cref="Indexed" />. The query service merges from here so the sidecar read stays
    ///     off the UI thread.
    /// </summary>
    public event Action<RoundIndexedEvent>? Written;

    /// <summary>A demo's sidecar was written and stamped. Raised through the post delegate.</summary>
    public event Action<RoundIndexedEvent>? Indexed;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <inheritdoc />
    /// <remarks>
    ///     From the index row alone: a parsed demo with Round Facts rows whose index is missing or
    ///     stale under the fingerprint for its map, and either the background sweep is on or the demo
    ///     was forced. No sidecar is read; the row carries everything.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!NeedsIndex(_demoCache.TryGetIndex(path)))
        {
            return false;
        }

        lock (_gate)
        {
            return _forcedPaths.Contains(path) || _backgroundIndex();
        }
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
    ///     Same as <see cref="Evaluate" /> when the demo is wanted, else a no-op: an interactive open
    ///     indexes the opened demo on that parse, and the Library's tier-2 fan-out indexes a demo whose
    ///     Round Facts landed one evaluator earlier in the same pass.
    /// </remarks>
    public void OnParsedOpportunistically(string path, ParsedDemo parsed)
    {
        if (Wants(path))
        {
            Refresh(path, parsed);
        }
    }

    /// <inheritdoc />
    public void OnFailed(string path) => ClearForced(path);

    /// <summary>
    ///     The demos whose index is missing or stale and would be submitted: the coordinator's
    ///     candidate universe for this evaluator, newest first. Derived from the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
        bool background = _backgroundIndex();
        HashSet<string> forced;
        lock (_gate)
        {
            forced = [.. _forcedPaths];
        }

        return
        [
            .. _demoCache.Index
                .Where(e => NeedsIndex(e) && (background || forced.Contains(e.Path)))
                .OrderByDescending(e => e.ModifiedTicks)
                .Select(e => e.Path)
        ];
    }

    /// <summary>Every row that carries an index built under another fingerprint than its map's current one.</summary>
    public int StaleCount() =>
        _demoCache.Index.Count(e =>
            e.RoundIndexSchema > 0 && e.RoundIndexState == RoundIndexState.Indexed
            && !e.IsRoundIndexCurrent(_sources.FingerprintFor(e.Map)));

    /// <summary>
    ///     Re-queues one demo at user priority regardless of the opt-in: the strip's Retry. A failed
    ///     row is lifted back to Pending first, since Failed is excluded from the derived backlog.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    public void Request(string path)
    {
        try
        {
            if (_demoCache.TryGetIndex(path) is { RoundIndexState: RoundIndexState.Failed })
            {
                _demoCache.UpdateExisting(path, r => r.RoundIndexState = RoundIndexState.Pending);
            }
        }
        catch (Exception)
        {
            // The forced set below still carries it into the queue.
        }

        lock (_gate)
        {
            _forcedPaths.Add(path);
        }

        Coordinator?.Consider(path);
    }

    /// <summary>Re-queues every failed row: the strip's "Retry failed".</summary>
    public void RetryFailed()
    {
        List<string> failed =
        [
            .. _demoCache.Index.Where(e => e.RoundIndexState == RoundIndexState.Failed).Select(e => e.Path)
        ];
        foreach (string path in failed)
        {
            Request(path);
        }
    }

    /// <summary>
    ///     Marks every index stale and asks the coordinator to reconsider the library: the strip's
    ///     "Rebuild index". The stamp is cleared, not the sidecar, so the old rows keep answering
    ///     queries until each demo is rebuilt (the derived-backlog rule).
    /// </summary>
    public void RebuildAll()
    {
        List<string> indexed =
        [
            .. _demoCache.Index
                .Where(e => e.RoundIndexSchema > 0 || e.RoundIndexState == RoundIndexState.Failed)
                .Select(e => e.Path)
        ];
        using (_demoCache.BeginBatch())
        {
            foreach (string path in indexed)
            {
                _demoCache.UpdateExisting(path, r =>
                {
                    r.RoundIndexFingerprint = null;
                    if (r.RoundIndexState == RoundIndexState.Failed)
                    {
                        r.RoundIndexState = RoundIndexState.Pending;
                    }
                });
            }
        }

        _demoCache.SaveIndex();
        Coordinator?.ConsiderAll();
    }

    private bool NeedsIndex(DemoCacheIndexEntry? entry) =>
        entry is { ParseSchema: > 0, RoundFactsSchema: > 0, RoundFactsFingerprint: not null }
        && entry.NeedsRoundIndex(_sources.FingerprintFor(entry.Map));

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
            if (record?.RoundFacts is not { } facts || facts.Schema != DemoCacheRecord.RoundFactsSchema)
            {
                // The row said Round Facts was written (that is why Wants picked this demo) but the
                // sidecar has none: index.json was saved before a later write replaced the record. Left
                // alone, Wants stays true and the coordinator re-parses this demo forever. Re-project the
                // row from the sidecar so it stops claiming facts and Round Facts re-wants the demo.
                if (record is not null && _demoCache.TryGetIndex(path) is { RoundFactsSchema: > 0 })
                {
                    RoundIndexLog.RowClaimedMissingFacts(Log, fileName);
                    _demoCache.UpdateExisting(path, _ => { });
                    _demoCache.SaveIndex();
                }

                return; // no round windows to sample within; Round Facts has not written this demo yet
            }

            IPlaceSource source = _sources.SourceFor(parsed.MapName);
            string fingerprint = RoundIndexFingerprint.Compose(_sources.Options, source);
            if (!forced && record.IsRoundIndexCurrent(fingerprint))
            {
                return; // a queued request the Library's fan-out already satisfied
            }

            RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(parsed, facts, _sources.Options, source,
                _walk?.Invoke(parsed));
            foreach (RoundIndexDisagreement d in build.Disagreements)
            {
                RoundIndexLog.SampleDisagreedWithFacts(Log, fileName, d.Round, d.SideMismatches, d.AliveMismatches);
            }

            RoundIndexDocument document = build.Index;
            string stableKey = DemoCacheStore.StableKey(path);
            document.Demo = new RoundIndexDemo
            {
                Sha256 = record.Sha256,
                StableKey = stableKey,
                FileName = fileName,
                SizeBytes = record.Size
            };
            build.Positions.Demo = new RoundPositionsDemo
            {
                Sha256 = record.Sha256,
                StableKey = stableKey
            };

            // Positions first, sidecar second, stamp last: a crash between any two leaves "not indexed"
            // or a positions file nothing reads, never an index whose cards have nothing to draw.
            _store.WritePositions(path, build.Positions);
            _store.Write(path, document);

            long computedAt = 0;
            _demoCache.UpdateExisting(path, r =>
            {
                DemoCacheStore.StampRoundIndex(r);
                r.RoundIndexState = RoundIndexState.Indexed;
                r.RoundIndexFingerprint = fingerprint;
                r.RoundIndexRowCount = document.RowCount;
                computedAt = r.RoundIndex.ComputedAtTicks;
            });
            _demoCache.SaveIndex();

            RoundIndexedEvent indexed = new(path, document.Demo.StableKey, record.Sha256, document.Map, computedAt);
            Written?.Invoke(indexed);
            _post(() => Indexed?.Invoke(indexed));
        }
        catch (Exception ex)
        {
            RoundIndexLog.BuildFailed(Log, fileName, ex);
            try
            {
                _demoCache.UpdateExisting(path, r => r.RoundIndexState = RoundIndexState.Failed);
            }
            catch (Exception)
            {
                // The row could not be marked; the next pass will try the demo again.
            }
        }
        finally
        {
            ClearForced(path);
        }
    }

    private void ClearForced(string path)
    {
        lock (_gate)
        {
            _forcedPaths.Remove(path);
        }
    }
}

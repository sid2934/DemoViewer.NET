#region

using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The evaluator that writes the round index: an <see cref="IExtensionPass" /> on the tier-2
///     fan-out, registered after <see cref="RoundFactsEvaluator" /> so it reads the rows that one wrote
///     in the same pass. Round Facts is rules-driven and has no extractor, so there is nothing to fall
///     back to: a demo is not wanted until its index row carries a Round Facts fingerprint, and the
///     index simply follows Round Facts by one pass and never races it.
///     <para>
///         Per demo the work is one position walk (1 to 3 s on top of the parse the queue already
///         paid), the positions file and the sidecar written in that order, and one record stamp, the
///         stamp last so a crash between them leaves "not indexed" and never a stamp without both
///         files. A throw stamps <see cref="DemoAnalysisState.Failed" />,
///         which the derived backlog excludes until the user retries; the parse itself failing is not
///         this evaluator's to mark.
///     </para>
/// </summary>
public sealed class RoundIndexEvaluator : IExtensionPass
{
    /// <summary>The queue owner tag and the pass id for this evaluator.</summary>
    public const string EvaluatorId = "roundindex";

    private static ILogger? _diagLog;

    private readonly Func<bool> _backgroundIndex;
    private readonly DemoCacheStore _demoCache;
    private readonly Func<bool> _enabled;

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
    /// <param name="enabled">The owning pack's gate; off, nothing is wanted. Defaults to always-on.</param>
    public RoundIndexEvaluator(
        DemoCacheStore demoCache,
        RoundIndexStore store,
        RoundIndexPlaceSources sources,
        Func<bool> backgroundIndex,
        Action<Action>? post = null,
        Func<ParsedDemo, IEnumerable<PositionSample>>? walk = null,
        Func<bool>? enabled = null)
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
        _enabled = enabled ?? (() => true);
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundIndexLog.Category);

    /// <summary>The scheduling of the extension's passes, so a retry or a rebuild can ask for the demo again. Null in tests.</summary>
    public IExtensionPasses? Passes { get; set; }

    /// <summary>True while the scheduler has index work in flight.</summary>
    public bool IsIndexing => Passes?.IsBusy(EvaluatorId) ?? false;

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
    public bool ReadsUserCommands => false;

    /// <summary>Whether the demo needs this pass now.</summary>
    /// <remarks>
    ///     From the index row alone: the owning pack's gate on, a parsed demo with Round Facts rows whose
    ///     index is missing or stale under the fingerprint for its map, and either the background sweep is
    ///     on or the demo was forced. No sidecar is read; the row carries everything.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!_enabled() || !NeedsIndex(_demoCache.TryGetIndex(path)))
        {
            return false;
        }

        lock (_gate)
        {
            return _forcedPaths.Contains(path) || _backgroundIndex();
        }
    }

    /// <summary>Whether the demo will need this pass once the pass it runs after has written.</summary>
    /// <remarks>A demo without Round Facts rows yet, with the sweep on or the demo forced: the index follows the rows.</remarks>
    public bool WantsAfterUpstream(string path)
    {
        if (!_enabled() || _demoCache.TryGetIndex(path) is { ParseSchema: > 0 } entry && entry.HasRoundFacts())
        {
            return false;
        }

        lock (_gate)
        {
            return _forcedPaths.Contains(path) || _backgroundIndex();
        }
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
    public long OrderHint(string demoPath) => _demoCache.TryGetIndex(demoPath)?.ModifiedTicks ?? 0;

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
    ///     The demos whose index is missing or stale and would be submitted, newest first, for the strip's
    ///     counts. Derived from the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
        if (!_enabled())
        {
            return [];
        }

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
            e.RoundIndexStamp() is { Schema: > 0, State: DemoAnalysisState.Indexed }
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
            if (_demoCache.TryGetIndex(path)?.RoundIndexStamp() is { State: DemoAnalysisState.Failed })
            {
                _demoCache.UpdateExisting(path, r => r.ClearFailed(EvaluatorId));
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

        Passes?.Request(path);
    }

    /// <summary>Re-queues every failed row: the strip's "Retry failed".</summary>
    public void RetryFailed()
    {
        List<string> failed =
        [
            .. _demoCache.Index.Where(e => e.RoundIndexStamp() is { State: DemoAnalysisState.Failed }).Select(e => e.Path)
        ];
        foreach (string path in failed)
        {
            Request(path);
        }
    }

    /// <summary>
    ///     Marks every index stale and asks the scheduler to check the library again: the strip's
    ///     "Rebuild index". The stamp is cleared, not the sidecar, so the old rows keep answering
    ///     queries until each demo is rebuilt (the derived-backlog rule).
    /// </summary>
    public void RebuildAll()
    {
        List<string> indexed =
        [
            .. _demoCache.Index
                .Where(e => e.RoundIndexStamp() is { } s && (s.Schema > 0 || s.State == DemoAnalysisState.Failed))
                .Select(e => e.Path)
        ];
        using (_demoCache.BeginBatch())
        {
            foreach (string path in indexed)
            {
                _demoCache.UpdateExisting(path, r =>
                {
                    if (r.RoundIndexStamp() is { } stamp)
                    {
                        r.SetStamp(stamp with
                        {
                            Fingerprint = null,
                            State = stamp.State == DemoAnalysisState.Failed ? DemoAnalysisState.Pending : stamp.State
                        });
                    }
                });
            }
        }

        _demoCache.SaveIndex();
        Passes?.RecheckAll();
    }

    private bool NeedsIndex(DemoCacheIndexEntry? entry) =>
        entry is { ParseSchema: > 0 } && entry.HasRoundFacts()
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
            if (record is null || _demoCache.RoundFactsOf(record) is not { Schema: StratBookCache.RoundFactsSchema } facts)
            {
                // The row said Round Facts was written (that is why Wants picked this demo) but the
                // sidecar has none: index.json was saved before a later write replaced the record. Left
                // alone, Wants stays true and the scheduler re-parses this demo forever. Re-project the
                // row from the sidecar so it stops claiming facts and Round Facts re-wants the demo.
                if (record is not null && _demoCache.TryGetIndex(path)?.RoundFactsStamp() is { Schema: > 0 })
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
                return; // a queued request an earlier visit already satisfied
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

            // Stamped inside the mutate, under the store's read-modify-write lock, so the time is the persist time.
            long computedAt = 0;
            _demoCache.UpdateExisting(path, r =>
            {
                PackStamp stamp = new(EvaluatorId, StratBookCache.RoundIndexSchema, fingerprint)
                {
                    ComputedAtTicks = DateTime.UtcNow.Ticks,
                    Count = document.RowCount
                };
                r.SetStamp(stamp);
                computedAt = stamp.ComputedAtTicks;
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
                _demoCache.UpdateExisting(path, r => r.MarkFailed(EvaluatorId));
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

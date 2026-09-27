#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The per-session in-memory situation index: the only reader of the sidecars at query time.
///     Loaded once at startup off the UI thread (measured 2.0 ms per demo on real sidecars), merged
///     incrementally as the evaluator writes, and queried through inverted postings per map and per
///     side so an exact or tolerant match answers in microseconds. Scanning sidecars per query was
///     measured at 141 to 267 ms for a hundred demos and fails the one-second bar somewhere between
///     300 and 700; the live count runs a query per token drag, so it cannot scan.
///     <para>
///         Per map: a place table, a token table with each token decoded once into (placeId, count)
///         pairs, postings per side keyed by token id, the demos with their round tables (freeze end,
///         end, runs as token ids) so a two-sided query verifies overlap without re-reading, and the
///         place and transition summaries folded by addition. A demo's removal subtracts what it added,
///         so a stale sidecar keeps answering until its replacement arrives and then leaves exactly.
///     </para>
/// </summary>
public sealed class SituationIndex : ISituationIndex, IDisposable
{
    private static ILogger? _diagLog;

    private readonly DemoCacheStore _demoCache;
    private readonly RoundIndexEvaluator? _evaluator;
    private readonly IRoundFactsSource? _facts;
    private readonly object _gate = new();
    private readonly Dictionary<string, LoadedDemo> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MapIndex> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<Action> _post;
    private readonly RoundIndexPlaceSources _sources;
    private readonly RoundIndexStore _store;
    private readonly IZonePlaceResolverSource _zones;

    private bool _disposed;
    private bool _ready;

    /// <param name="demoCache">The index rows: which sidecars are worth opening, and removals.</param>
    /// <param name="store">The sidecars.</param>
    /// <param name="sources">The fingerprint in force per map, for the stale count.</param>
    /// <param name="facts">The Round Facts read API a <see cref="SituationQuery.Facts" /> filter joins through; null applies no such filter.</param>
    /// <param name="zones">Where a map's zone graph comes from; none until Zone Baking's resolver lands.</param>
    /// <param name="evaluator">The writer, whose <see cref="RoundIndexEvaluator.Written" /> merges a demo; null in a read-only host.</param>
    /// <param name="post">UI-thread marshal for the events; defaults to synchronous.</param>
    public SituationIndex(
        DemoCacheStore demoCache,
        RoundIndexStore store,
        RoundIndexPlaceSources sources,
        IRoundFactsSource? facts = null,
        IZonePlaceResolverSource? zones = null,
        RoundIndexEvaluator? evaluator = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);
        _demoCache = demoCache;
        _store = store;
        _sources = sources;
        _facts = facts;
        _zones = zones ?? NoZonePlaceResolverSource.Instance;
        _evaluator = evaluator;
        _post = post ?? (action => action());

        if (_evaluator is not null)
        {
            _evaluator.Written += OnWritten;
        }

        _demoCache.Changed += OnCacheChanged;
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundIndexLog.Category);

    /// <inheritdoc />
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    /// <inheritdoc />
    public int IndexedDemoCount
    {
        get
        {
            lock (_gate)
            {
                return _loaded.Count;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Read from the index rows, not the documents: a rebuild clears the row's fingerprint and leaves
    ///     the sidecar as it was, and that row is exactly the stale one the strip should count.
    /// </remarks>
    public int StaleDemoCount
    {
        get
        {
            List<LoadedDemo> loaded;
            lock (_gate)
            {
                loaded = [.. _loaded.Values];
            }

            return loaded.Count(d =>
                !string.Equals(_demoCache.TryGetIndex(d.Path)?.RoundIndexFingerprint,
                    _sources.FingerprintFor(d.Map.Name), StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public event Action<RoundIndexedEvent>? Indexed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_evaluator is not null)
        {
            _evaluator.Written -= OnWritten;
        }

        _demoCache.Changed -= OnCacheChanged;
    }

    /// <summary>The startup load on a worker: the composition root calls this and never awaits it on the UI thread.</summary>
    public Task StartLoadAsync() => Task.Run(Load);

    /// <summary>
    ///     Sweeps orphan sidecars and loads every sidecar whose index row is stamped Indexed at the
    ///     current schema. Stale fingerprints load too: a stale index is still the best answer available
    ///     until its demo is rebuilt, and the strip says how many there are. Synchronous; call it off
    ///     the UI thread.
    /// </summary>
    public void Load()
    {
        Stopwatch watch = Stopwatch.StartNew();
        int orphans = _store.SweepOrphans();
        foreach (DemoCacheIndexEntry entry in _demoCache.Index)
        {
            if (IsLoadable(entry))
            {
                Merge(entry, raise: false);
            }
        }

        int demos;
        int postings;
        lock (_gate)
        {
            _ready = true;
            demos = _loaded.Count;
            postings = _maps.Values.Sum(m => m.PostingCount);
        }

        RoundIndexLog.Loaded(Log, demos, postings, orphans, watch.ElapsedMilliseconds);
        _post(() => Changed?.Invoke());
    }

    /// <inheritdoc />
    public IReadOnlyList<SituationHit> Query(SituationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<SituationHit> hits = [];
        Execute(query, hits);
        return hits;
    }

    /// <inheritdoc />
    public int Count(SituationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Execute(query, null);
    }

    /// <inheritdoc />
    public IReadOnlyList<PlaceSummary> Places(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (_gate)
        {
            if (!_maps.TryGetValue(map, out MapIndex? index))
            {
                return [];
            }

            List<PlaceSummary> summaries = [];
            foreach ((string place, PlaceAggregate aggregate) in index.Places.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                List<PlaceZBucket> buckets =
                [
                    .. aggregate.Buckets
                        .OrderBy(b => b.Key)
                        .Select(b => new PlaceZBucket(b.Key, b.Value.Count, b.Value.SumX / b.Value.Count,
                            b.Value.SumY / b.Value.Count))
                ];
                summaries.Add(new PlaceSummary(place, aggregate.Count, buckets));
            }

            return summaries;
        }
    }

    /// <inheritdoc />
    public IPlaceAdjacency? Adjacency(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (_gate)
        {
            return AdjacencyLocked(map);
        }
    }

    // ── Merge and remove ──────────────────────────────────────────────────────

    // Stamped Indexed at the current schema; the fingerprint is not checked here on purpose (see Load).
    private static bool IsLoadable(DemoCacheIndexEntry? entry) =>
        entry is { RoundIndexState: RoundIndexState.Indexed, RoundIndexSchema: DemoCacheRecord.RoundIndexSchema };

    // Reads one sidecar and replaces whatever the demo contributed before. Returns the merged event, or
    // null when the file is missing, unreadable, or belongs to another demo.
    private RoundIndexedEvent? Merge(DemoCacheIndexEntry entry, bool raise)
    {
        string fileName = Path.GetFileName(entry.Path);
        RoundIndexDocument? document = _store.TryRead(entry.Path);
        if (document is null)
        {
            RoundIndexLog.SidecarIgnored(Log, fileName, "missing or unreadable");
            return null;
        }

        if (document.SchemaVersion != DemoCacheRecord.RoundIndexSchema)
        {
            string reason = $"schema {document.SchemaVersion}";
            RoundIndexLog.SidecarIgnored(Log, fileName, reason);
            return null;
        }

        if (!string.Equals(document.Clock.Kind, ClockIdentity.DvFrameClock, StringComparison.Ordinal))
        {
            string reason = $"clock {document.Clock.Kind}";
            RoundIndexLog.SidecarIgnored(Log, fileName, reason);
            return null;
        }

        // A reader that has a hash compares it and ignores a mismatching file: the sidecar belongs to a
        // different demo that happens to share the path.
        if (entry.Sha256 is { Length: > 0 } sha && document.Demo.Sha256 is { Length: > 0 } written
            && !string.Equals(sha, written, StringComparison.Ordinal))
        {
            RoundIndexLog.SidecarIgnored(Log, fileName, "content hash differs");
            return null;
        }

        if (document.CadenceTicks <= 0)
        {
            RoundIndexLog.SidecarIgnored(Log, fileName, "no cadence");
            return null;
        }

        RoundIndexedEvent indexed = new(entry.Path, DemoCacheStore.StableKey(entry.Path), entry.Sha256,
            document.Map, entry.RoundIndexComputedAtTicks);
        lock (_gate)
        {
            RemoveLocked(entry.Path);
            MapIndex map = MapFor(document.Map);
            LoadedDemo demo = map.Add(entry, document);
            _loaded[entry.Path] = demo;
        }

        if (raise)
        {
            _post(() =>
            {
                Indexed?.Invoke(indexed);
                Changed?.Invoke();
            });
        }

        return indexed;
    }

    private MapIndex MapFor(string map)
    {
        if (!_maps.TryGetValue(map, out MapIndex? index))
        {
            index = new MapIndex(map);
            _maps[map] = index;
        }

        return index;
    }

    private bool RemoveLocked(string path)
    {
        if (!_loaded.Remove(path, out LoadedDemo? demo))
        {
            return false;
        }

        demo.Map.Remove(demo);
        return true;
    }

    private void OnWritten(RoundIndexedEvent written)
    {
        if (_demoCache.TryGetIndex(written.DemoPath) is { } entry && IsLoadable(entry))
        {
            Merge(entry, raise: true);
        }
    }

    // Removals only: a demo that left the library, or one whose record lost its stamp (the file was
    // replaced and identity drift discarded every tier). New rows arrive through the evaluator's
    // Written, so no sidecar is read on the UI thread here.
    private void OnCacheChanged(string? path)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_ready)
            {
                return;
            }

            if (path is not null)
            {
                if (_loaded.ContainsKey(path) && !IsLoadable(_demoCache.TryGetIndex(path)))
                {
                    changed = RemoveLocked(path);
                }
            }
            else
            {
                foreach (string loaded in _loaded.Keys.ToList())
                {
                    if (!IsLoadable(_demoCache.TryGetIndex(loaded)))
                    {
                        changed |= RemoveLocked(loaded);
                    }
                }
            }
        }

        if (changed)
        {
            _post(() => Changed?.Invoke());
        }
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    // One code path for Query and Count: the count is the number of hits the list would hold.
    private int Execute(SituationQuery query, List<SituationHit>? hits)
    {
        List<Candidate> candidates = [];
        lock (_gate)
        {
            if (!_ready || !_maps.TryGetValue(query.Map, out MapIndex? map))
            {
                return 0;
            }

            SituationTolerance tolerance = query.Tolerance;
            IPlaceAdjacency? adjacency = null;
            if (tolerance is SituationTolerance.Adjacent or SituationTolerance.TwoHops)
            {
                // Without a graph the middle stops collapse to Exact: the plan's stated degraded form.
                adjacency = AdjacencyLocked(query.Map);
                if (adjacency is null)
                {
                    tolerance = SituationTolerance.Exact;
                }
            }

            bool[]? ctMatch = query.Ct.Count == 0 ? null : map.MatchTokens(query.Ct, tolerance, adjacency);
            bool[]? tMatch = query.T.Count == 0 ? null : map.MatchTokens(query.T, tolerance, adjacency);

            foreach ((LoadedDemo demo, int roundIndex) in map.Candidates(ctMatch, tMatch))
            {
                if (query.Demos is not null && !query.Demos.Contains(demo.StableKey))
                {
                    continue;
                }

                if (query.IndexedAfterTicks is long after && demo.ComputedAtTicks <= after)
                {
                    continue;
                }

                DecodedRound round = demo.Rounds[roundIndex];
                List<(int From, int To)> matched = [];
                foreach (DecodedRun run in demo.Runs.AsSpan(round.RunStart, round.RunCount))
                {
                    if ((ctMatch is null || ctMatch[run.CtId]) && (tMatch is null || tMatch[run.TId]))
                    {
                        matched.Add((run.FromStep, run.ToStep));
                    }
                }

                if (matched.Count > 0)
                {
                    candidates.Add(new Candidate(demo, round, matched));
                }
            }
        }

        // Newest demo first, then round number; the path breaks a modified-time tie so the order is stable.
        candidates.Sort((a, b) =>
        {
            int byTime = b.Demo.ModifiedTicks.CompareTo(a.Demo.ModifiedTicks);
            if (byTime != 0)
            {
                return byTime;
            }

            int byPath = string.CompareOrdinal(a.Demo.Path, b.Demo.Path);
            return byPath != 0 ? byPath : a.Round.Number.CompareTo(b.Round.Number);
        });

        // The Round Facts join reads a record per demo through the cache, so it runs outside the lock.
        int count = 0;
        RoundFactsRows? factsRows = null;
        string? factsPath = null;
        foreach (Candidate candidate in candidates)
        {
            int first = int.MaxValue;
            int last = int.MinValue;
            int steps = 0;
            if (query.Facts is { } filter)
            {
                if (filter.Demos is not null && !filter.Demos.Contains(candidate.Demo.Path))
                {
                    continue;
                }

                if (!string.Equals(factsPath, candidate.Demo.Path, StringComparison.OrdinalIgnoreCase))
                {
                    factsPath = candidate.Demo.Path;
                    factsRows = _facts?.TryGet(factsPath);
                }

                RoundFacts.RoundFacts? facts = factsRows?.Rounds.FirstOrDefault(r => r.Number == candidate.Round.Number);
                if (facts is null || !RoundFactsSource.Matches(candidate.Demo.Path, facts, filter))
                {
                    continue;
                }

                // A tick-anchored field is read at each matched step: the round is a hit when the
                // arrangement stood at a tick that also passes (post-plant, 3v2, late), and the match
                // window narrows to those steps so the card seeks to one of them.
                if (filter.HasTickAnchored)
                {
                    int tickRate = candidate.Demo.TickRate;
                    foreach ((int from, int to) in candidate.Matched)
                    {
                        for (int step = from; step <= to; step++)
                        {
                            int tick = candidate.Round.FreezeEndTick + step * candidate.Demo.CadenceTicks;
                            if (RoundFactsSource.MatchesAt(facts, filter, tick, tickRate))
                            {
                                first = Math.Min(first, step);
                                last = Math.Max(last, step);
                                steps++;
                            }
                        }
                    }

                    if (steps == 0)
                    {
                        continue;
                    }
                }
            }

            if (steps == 0)
            {
                foreach ((int from, int to) in candidate.Matched)
                {
                    first = Math.Min(first, from);
                    last = Math.Max(last, to);
                    steps += to - from + 1;
                }
            }

            count++;
            hits?.Add(new SituationHit(
                candidate.Demo.Path,
                candidate.Demo.StableKey,
                candidate.Demo.Sha256,
                candidate.Demo.Map.Name,
                candidate.Round.Number,
                candidate.Round.FreezeEndTick,
                candidate.Round.FreezeEndTick + first * candidate.Demo.CadenceTicks,
                candidate.Round.FreezeEndTick + last * candidate.Demo.CadenceTicks,
                steps));
        }

        return count;
    }

    /// <summary>Live tokens and token slots of a map's token table, for the shrink tests.</summary>
    internal (int Tokens, int Slots) TokenTable(string map)
    {
        lock (_gate)
        {
            return _maps.TryGetValue(map, out MapIndex? index) ? (index.TokenCount, index.TokenSlots) : (0, 0);
        }
    }

    private IPlaceAdjacency? AdjacencyLocked(string map)
    {
        if (_zones.TryGet(map) is { } resolver)
        {
            return new ZonePlaceAdjacency(resolver);
        }

        return _maps.TryGetValue(map, out MapIndex? index) && index.DemoCount > 0 ? index.Empirical() : null;
    }

    // The runs that held the arrangement, as step ranges; the hit's window is folded from them after
    // the Round Facts join, which may narrow it to the steps a tick-anchored field also passes.
    private sealed record Candidate(LoadedDemo Demo, DecodedRound Round, List<(int From, int To)> Matched);

    // ── Per-map structures ────────────────────────────────────────────────────

    private sealed class PlaceAggregate
    {
        public int Count { get; set; }

        public Dictionary<int, (int Count, double SumX, double SumY)> Buckets { get; } = [];
    }

    private readonly record struct DecodedRun(int FromStep, int ToStep, int CtId, int TId);

    // A round's runs are Runs[RunStart .. RunStart + RunCount) on its demo.
    private readonly record struct DecodedRound(int Number, int FreezeEndTick, int RunStart, int RunCount);

    private readonly record struct Posting(int DemoId, int RoundIndex);

    private readonly record struct BucketSum(int ZBucket, int Count, double SumX, double SumY);

    // What a demo folded into the place and transition summaries; removal subtracts exactly this.
    private readonly record struct PlaceContribution(string Place, int Count, BucketSum[] Buckets);

    private readonly record struct TransitionContribution(string A, string B, int Count);

    private sealed class LoadedDemo(MapIndex map, int id, DemoCacheIndexEntry entry, int tickRate, int cadenceTicks)
    {
        public MapIndex Map { get; } = map;

        public int Id { get; } = id;

        public string Path { get; } = entry.Path;

        public string StableKey { get; } = DemoCacheStore.StableKey(entry.Path);

        public string? Sha256 { get; } = entry.Sha256;

        public long ModifiedTicks { get; } = entry.ModifiedTicks;

        public long ComputedAtTicks { get; } = entry.RoundIndexComputedAtTicks;

        public int TickRate { get; } = tickRate;

        public int CadenceTicks { get; } = cadenceTicks;

        public DecodedRound[] Rounds { get; set; } = [];

        public DecodedRun[] Runs { get; set; } = [];

        /// <summary>The distinct token ids this demo posted under, per side, so removal filters those lists only.</summary>
        public int[][] Contributed { get; set; } = [[], []];

        public PlaceContribution[] Places { get; set; } = [];

        public TransitionContribution[] Transitions { get; set; } = [];
    }

    private sealed class TokenInfo(string text, int[] placeIds, int[] counts)
    {
        public string Text { get; } = text;

        public int[] PlaceIds { get; } = placeIds;

        public int[] Counts { get; } = counts;

        public int Alive { get; } = counts.Sum();

        /// <summary>(demo, side) pairs posting under this token; the slot is freed at zero.</summary>
        public int References { get; set; }
    }

    private sealed class MapIndex(string name)
    {
        private readonly List<LoadedDemo?> _demos = [];
        private readonly Stack<int> _freeIds = new();
        private readonly Stack<int> _freeTokenIds = new();
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _placeIds = new(StringComparer.Ordinal);

        // postings[side][tokenId]: side 0 = CT, 1 = T.
        private readonly List<List<Posting>>[] _postings = [[], []];
        private readonly Dictionary<string, int> _tokenIds = new(StringComparer.Ordinal);

        // A freed slot stays null until a new token takes it; it never matches and has no postings.
        private readonly List<TokenInfo?> _tokens = [];
        private EmpiricalPlaceAdjacency? _empirical;

        public string Name { get; } = name;

        public int DemoCount { get; private set; }

        public int TokenCount => _tokenIds.Count;

        public int TokenSlots => _tokens.Count;

        public Dictionary<string, PlaceAggregate> Places { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string A, string B), int> Transitions { get; } = [];

        public int PostingCount => _postings[0].Sum(p => p.Count) + _postings[1].Sum(p => p.Count);

        public LoadedDemo Add(DemoCacheIndexEntry entry, RoundIndexDocument document)
        {
            int id;
            if (_freeIds.Count > 0)
            {
                id = _freeIds.Pop();
            }
            else
            {
                id = _demos.Count;
                _demos.Add(null);
            }

            LoadedDemo demo = new(this, id, entry, document.Clock.TickRate, document.CadenceTicks);
            _demos[id] = demo;
            DemoCount++;

            HashSet<int>[] contributed = [[], []];
            DecodedRound[] rounds = new DecodedRound[document.Rounds.Count];
            DecodedRun[] runs = new DecodedRun[document.Rounds.Sum(r => r.Runs.Count)];
            int next = 0;
            for (int r = 0; r < document.Rounds.Count; r++)
            {
                RoundIndexRound round = document.Rounds[r];
                int start = next;
                foreach (RoundIndexRun run in round.Runs)
                {
                    int ctId = TokenId(run.Ct);
                    int tId = TokenId(run.T);
                    runs[next++] = new DecodedRun(run.FromStep, run.ToStep, ctId, tId);
                    Post(0, ctId, id, r, contributed[0]);
                    Post(1, tId, id, r, contributed[1]);
                }

                rounds[r] = new DecodedRound(round.Number, round.FreezeEndTick, start, next - start);
            }

            demo.Rounds = rounds;
            demo.Runs = runs;
            demo.Contributed = [[.. contributed[0]], [.. contributed[1]]];
            foreach (int[] side in demo.Contributed)
            {
                foreach (int tokenId in side)
                {
                    _tokens[tokenId]!.References++;
                }
            }

            List<PlaceContribution> places = new(document.Places.Count);
            foreach ((string text, PlaceSampleSummary summary) in document.Places)
            {
                string place = Intern(text);
                if (!Places.TryGetValue(place, out PlaceAggregate? aggregate))
                {
                    aggregate = new PlaceAggregate();
                    Places[place] = aggregate;
                }

                aggregate.Count += summary.Count;
                BucketSum[] buckets = new BucketSum[summary.Buckets.Count];
                for (int b = 0; b < buckets.Length; b++)
                {
                    PlaceZBucketSum bucket = summary.Buckets[b];
                    buckets[b] = new BucketSum(bucket.ZBucket, bucket.Count, bucket.SumX, bucket.SumY);
                    (int count, double sumX, double sumY) = aggregate.Buckets.GetValueOrDefault(bucket.ZBucket);
                    aggregate.Buckets[bucket.ZBucket] = (count + bucket.Count, sumX + bucket.SumX, sumY + bucket.SumY);
                }

                places.Add(new PlaceContribution(place, summary.Count, buckets));
            }

            TransitionContribution[] transitions = new TransitionContribution[document.Transitions.Count];
            for (int i = 0; i < transitions.Length; i++)
            {
                PlaceTransition transition = document.Transitions[i];
                (string, string) key = (Intern(transition.A), Intern(transition.B));
                Transitions[key] = Transitions.GetValueOrDefault(key) + transition.Count;
                transitions[i] = new TransitionContribution(key.Item1, key.Item2, transition.Count);
            }

            demo.Places = [.. places];
            demo.Transitions = transitions;
            _empirical = null;
            return demo;
        }

        public void Remove(LoadedDemo demo)
        {
            for (int side = 0; side < 2; side++)
            {
                foreach (int tokenId in demo.Contributed[side])
                {
                    _postings[side][tokenId].RemoveAll(p => p.DemoId == demo.Id);
                }
            }

            for (int side = 0; side < 2; side++)
            {
                foreach (int tokenId in demo.Contributed[side])
                {
                    if (_tokens[tokenId] is { } token && --token.References == 0)
                    {
                        FreeToken(tokenId);
                    }
                }
            }

            foreach (PlaceContribution contribution in demo.Places)
            {
                if (!Places.TryGetValue(contribution.Place, out PlaceAggregate? aggregate))
                {
                    continue;
                }

                aggregate.Count -= contribution.Count;
                foreach (BucketSum bucket in contribution.Buckets)
                {
                    (int count, double sumX, double sumY) = aggregate.Buckets.GetValueOrDefault(bucket.ZBucket);
                    count -= bucket.Count;
                    if (count <= 0)
                    {
                        aggregate.Buckets.Remove(bucket.ZBucket);
                    }
                    else
                    {
                        aggregate.Buckets[bucket.ZBucket] = (count, sumX - bucket.SumX, sumY - bucket.SumY);
                    }
                }

                if (aggregate.Count <= 0)
                {
                    Places.Remove(contribution.Place);
                }
            }

            foreach (TransitionContribution transition in demo.Transitions)
            {
                (string, string) key = (transition.A, transition.B);
                int remaining = Transitions.GetValueOrDefault(key) - transition.Count;
                if (remaining <= 0)
                {
                    Transitions.Remove(key);
                }
                else
                {
                    Transitions[key] = remaining;
                }
            }

            _demos[demo.Id] = null;
            _freeIds.Push(demo.Id);
            DemoCount--;
            _empirical = null;
        }

        public EmpiricalPlaceAdjacency Empirical() =>
            _empirical ??= new EmpiricalPlaceAdjacency(
                Transitions.Select(t => new PlaceTransition(t.Key.A, t.Key.B, t.Value)), DemoCount);

        /// <summary>
        ///     Which tokens of this map every queried pair matches under the tolerance. Pairs naming one
        ///     place are summed first (two tokens dropped on A ask for two in A), and the wider stops also
        ///     require the side's alive total to reach the sum of the counts: two queried places sharing a
        ///     neighbourhood would otherwise count the same players twice and let a tolerant match exceed
        ///     the AnyPlace one, which is the monotonicity the slider promises.
        /// </summary>
        public bool[] MatchTokens(IReadOnlyList<PlaceQuery> queried, SituationTolerance tolerance, IPlaceAdjacency? adjacency)
        {
            bool[] match = new bool[_tokens.Count];
            Dictionary<string, int> merged = new(StringComparer.Ordinal);
            foreach (PlaceQuery pair in queried)
            {
                merged[pair.Place] = merged.GetValueOrDefault(pair.Place) + pair.Count;
            }

            List<PlaceQuery> pairs = [.. merged.Select(p => new PlaceQuery(p.Key, p.Value))];
            int total = pairs.Sum(p => p.Count);

            // Per pair, the place ids the sum runs over: the place alone at Exact, its neighbourhood
            // at the wider stops. An unknown place has no id and can only match through neighbours.
            List<(int PlaceId, HashSet<int>? Neighbourhood, int Count)> resolved = [];
            foreach (PlaceQuery pair in pairs)
            {
                int placeId = _placeIds.GetValueOrDefault(pair.Place, -1);
                HashSet<int>? neighbourhood = null;
                if (tolerance is SituationTolerance.Adjacent or SituationTolerance.TwoHops && adjacency is not null)
                {
                    neighbourhood = [];
                    if (placeId >= 0)
                    {
                        neighbourhood.Add(placeId);
                    }

                    foreach (string neighbour in adjacency.Neighbours(pair.Place))
                    {
                        AddPlace(neighbourhood, neighbour);
                        if (tolerance == SituationTolerance.TwoHops)
                        {
                            foreach (string second in adjacency.Neighbours(neighbour))
                            {
                                AddPlace(neighbourhood, second);
                            }
                        }
                    }
                }

                resolved.Add((placeId, neighbourhood, pair.Count));
            }

            for (int t = 0; t < _tokens.Count; t++)
            {
                if (_tokens[t] is not { } token)
                {
                    continue;
                }

                match[t] = tolerance switch
                {
                    SituationTolerance.AnyPlace => token.Alive >= total,
                    SituationTolerance.Exact => resolved.All(p => p.PlaceId >= 0 && CountOf(token, p.PlaceId) == p.Count),
                    _ => token.Alive >= total && resolved.All(p => SumOver(token, p.Neighbourhood!) >= p.Count)
                };
            }

            return match;
        }

        /// <summary>The (demo, round) pairs worth verifying: every round when both sides are free, else the union of the more selective side's postings.</summary>
        public IEnumerable<(LoadedDemo Demo, int RoundIndex)> Candidates(bool[]? ctMatch, bool[]? tMatch)
        {
            if (ctMatch is null && tMatch is null)
            {
                foreach (LoadedDemo? demo in _demos)
                {
                    if (demo is null)
                    {
                        continue;
                    }

                    for (int r = 0; r < demo.Rounds.Length; r++)
                    {
                        yield return (demo, r);
                    }
                }

                yield break;
            }

            int side;
            bool[] selected;
            if (ctMatch is null)
            {
                side = 1;
                selected = tMatch!;
            }
            else if (tMatch is null)
            {
                side = 0;
                selected = ctMatch;
            }
            else
            {
                long ctPostings = PostingsFor(0, ctMatch);
                long tPostings = PostingsFor(1, tMatch);
                side = ctPostings <= tPostings ? 0 : 1;
                selected = side == 0 ? ctMatch : tMatch;
            }

            HashSet<(int DemoId, int RoundIndex)> seen = [];
            List<List<Posting>> postings = _postings[side];
            for (int t = 0; t < selected.Length; t++)
            {
                if (!selected[t])
                {
                    continue;
                }

                foreach (Posting posting in postings[t])
                {
                    if (seen.Add((posting.DemoId, posting.RoundIndex)) && _demos[posting.DemoId] is { } demo)
                    {
                        yield return (demo, posting.RoundIndex);
                    }
                }
            }
        }

        private long PostingsFor(int side, bool[] match)
        {
            long total = 0;
            for (int t = 0; t < match.Length; t++)
            {
                if (match[t])
                {
                    total += _postings[side][t].Count;
                }
            }

            return total;
        }

        private void AddPlace(HashSet<int> set, string place)
        {
            if (_placeIds.TryGetValue(place, out int id))
            {
                set.Add(id);
            }
        }

        private static int CountOf(TokenInfo token, int placeId)
        {
            for (int i = 0; i < token.PlaceIds.Length; i++)
            {
                if (token.PlaceIds[i] == placeId)
                {
                    return token.Counts[i];
                }
            }

            return 0;
        }

        private static int SumOver(TokenInfo token, HashSet<int> places)
        {
            int sum = 0;
            for (int i = 0; i < token.PlaceIds.Length; i++)
            {
                if (places.Contains(token.PlaceIds[i]))
                {
                    sum += token.Counts[i];
                }
            }

            return sum;
        }

        private void Post(int side, int tokenId, int demoId, int roundIndex, HashSet<int> contributed)
        {
            List<Posting> list = _postings[side][tokenId];
            // Consecutive runs of one round often repeat a side's token; one posting per (round, token) is enough.
            if (list.Count == 0 || list[^1].DemoId != demoId || list[^1].RoundIndex != roundIndex)
            {
                list.Add(new Posting(demoId, roundIndex));
            }

            contributed.Add(tokenId);
        }

        // Decodes a token once; a token that will not parse (a hand-edited sidecar) is kept as a
        // token nobody can match rather than failing the whole demo.
        private int TokenId(string text)
        {
            if (_tokenIds.TryGetValue(text, out int id))
            {
                return id;
            }

            int[] placeIds;
            int[] counts;
            try
            {
                IReadOnlyList<(string Place, int Count)> pairs = PlaceCountToken.Decode(text);
                placeIds = new int[pairs.Count];
                counts = new int[pairs.Count];
                for (int i = 0; i < pairs.Count; i++)
                {
                    placeIds[i] = PlaceId(pairs[i].Place);
                    counts[i] = pairs[i].Count;
                }
            }
            catch (FormatException)
            {
                placeIds = [];
                counts = [];
            }

            TokenInfo token = new(text, placeIds, counts);
            id = -1;
            while (_freeTokenIds.Count > 0)
            {
                int free = _freeTokenIds.Pop();
                // A slot past a truncated tail is stale.
                if (free < _tokens.Count && _tokens[free] is null)
                {
                    id = free;
                    break;
                }
            }

            if (id < 0)
            {
                id = _tokens.Count;
                _tokens.Add(token);
                _postings[0].Add([]);
                _postings[1].Add([]);
            }
            else
            {
                _tokens[id] = token;
            }

            _tokenIds[text] = id;
            return id;
        }

        private void FreeToken(int tokenId)
        {
            _tokenIds.Remove(_tokens[tokenId]!.Text);
            _tokens[tokenId] = null;
            _postings[0][tokenId].TrimExcess();
            _postings[1][tokenId].TrimExcess();
            _freeTokenIds.Push(tokenId);
            int last = _tokens.Count;
            while (last > 0 && _tokens[last - 1] is null)
            {
                last--;
            }

            if (last < _tokens.Count)
            {
                int drop = _tokens.Count - last;
                _tokens.RemoveRange(last, drop);
                _postings[0].RemoveRange(last, drop);
                _postings[1].RemoveRange(last, drop);
            }
        }

        private int PlaceId(string place)
        {
            if (!_placeIds.TryGetValue(place, out int id))
            {
                id = _placeIds.Count;
                _placeIds[place] = id;
            }

            return id;
        }

        private string Intern(string name)
        {
            if (_names.TryGetValue(name, out string? interned))
            {
                return interned;
            }

            _names[name] = name;
            return name;
        }
    }
}

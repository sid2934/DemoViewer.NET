namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>One detected floor slice: a contiguous Z band players are assigned to.</summary>
public readonly record struct FloorSlice(double MinZ, double MaxZ)
{
    /// <summary>The slice's mid Z (for ordering / display).</summary>
    public double MidZ => (MinZ + MaxZ) / 2;

    /// <summary>True when <paramref name="z" /> falls within this slice's band (inclusive).</summary>
    public bool Contains(double z) => z >= MinZ && z <= MaxZ;
}

/// <summary>
///     Heuristic multi-floor (Z) splitter. Buckets observed player Z values into a running
///     histogram and finds clusters separated by an empty-bucket gap ≥ G (a player can't span floors within
///     one tick). Each cluster is a <see cref="FloorSlice" />, ordered low→high. A single cluster ⇒ one
///     slice (the common case). Metadata-free: when per-map Z thresholds arrive they replace the
///     heuristic boundaries, same slice abstraction.
///     <para>
///         Pure / deterministic (no Avalonia). The histogram is accumulated over observed ticks so the
///         split stabilises as more of the map is seen; <see cref="Reset" /> clears it (e.g. on demo unload).
///     </para>
/// </summary>
public sealed class FloorSplitter
{
    // 3.4e38 ≈ float.MaxValue is the engine's "unused section" sentinel; anything at/above this (and 0 for
    // unused trailing slots) is not a real boundary.
    private const double SectionSentinel = 3.0e38;

    // Peak-and-valley tuning (#1). A floor is a DWELL PEAK holding ≥ this fraction of all observed Z samples
    // (filters transient catwalk/box-jump noise). Nuke's lightly-used upper floor is ~5% of samples, so this
    // is deliberately low; a single-floor map still has only one such peak.
    private const double MinPeakFraction = 0.04;

    // Two adjacent peaks are SEPARATE floors when the valley between them drops below this fraction of the
    // SMALLER of the two peaks. A LOCAL test: global-peak comparison buries a low-occupancy upper floor.
    // The valley persists under accumulation (floor peaks grow faster than stair traffic fills the valley),
    // so once split it holds. Nuke settles at 2 floors, Dust2 at 1 (FloorSplitterMultiFloorTests).
    private const double ValleyDepthFraction = 0.25;

    // ComputeSlices' working set, hoisted to fields. It runs on EVERY frame the histogram moved, which is
    // every frame with a live player, and a fresh List + int[] + two more Lists there measured 552 B/frame
    // against a zero-allocation budget.
    private readonly List<int> _boundaries = new(8);

    // Sparse running histogram: bucket index → observed count. Sparse because Z spans can be large.
    //
    // Deliberately NOT a SortedDictionary. Nothing needs the keys in order (ComputeSlices scatters them
    // into an indexed array and reads that in order), and enumerating a SortedDictionary allocates a
    // Stack<Node> inside the tree walker EVERY time: 72 B measured on each recompute, once a frame.
    private readonly Dictionary<int, int> _buckets = new(64);
    private readonly List<int> _peaks = new(8);
    private readonly List<FloorSlice> _scratch = new(4);

    // Authoritative floor bands supplied by a baked map-asset bundle (nav-mesh-derived, checked against
    // observed player-Z by ZFloorValidationProbe). When set they OVERRIDE the Z-histogram AND its sticky
    // hysteresis entirely: map-intrinsic and stable from frame 1, so none of the heuristic's warmup or
    // hysteresis machinery applies. Cleared by Reset() (demo unload) so a single-floor map loaded after
    // Nuke does not inherit its bands.
    private List<FloorSlice>? _authoritativeFloors;
    private int[] _counts = [];
    private bool _dirty = true;

    // The last list handed to SetSectionHeights, for the reference-identity short circuit above. Held
    // only to compare against, never read.
    private IReadOnlyList<double>? _lastSuppliedHeights;

    // The histogram's extent, tracked as it is filled. Read every recompute; `_buckets.Keys.Min()` and
    // `.Max()` are the obvious spelling and both allocate an enumerator on the way through
    // IEnumerable<int>, for two numbers Observe already knows.
    private int _maxBucket = int.MinValue;
    private int _minBucket = int.MaxValue;

    // The map's REAL networked Z-floor boundaries, when present:
    // CCSGameRulesProxy.m_pGameRules.m_MinimapVerticalSectionHeights[0..N]. These are exact for
    // Nuke/Vertigo instead of histogram-guessed. When set, they OVERRIDE the histogram split entirely; when
    // null/empty (absent or all-sentinel) the histogram heuristic remains. The values are the LOWER edge of
    // each section (ascending), e.g. [1.81, 51.54, 287.0, 376.0]: section i spans [heights[i], heights[i+1]).
    private double[]? _sectionHeights;

    // Cached slices, recomputed when the histogram changes. Hysteresis keeps a player from flickering
    // across a boundary on a ramp: assignment uses the LAST slices unless Z clearly enters a new band.
    private List<FloorSlice> _slices = new();

    public FloorSplitter(double bucketWidth = 64, double gapThreshold = 180)
    {
        BucketWidth = bucketWidth > 0 ? bucketWidth : 64;
        GapThreshold = gapThreshold > 0 ? gapThreshold : 180;
    }

    /// <summary>Histogram bucket width in world units (fixed, e.g. 64u).</summary>
    public double BucketWidth { get; }

    /// <summary>Minimum empty-Z gap (world units) that separates two floors (e.g. ~180u).</summary>
    public double GapThreshold { get; }

    /// <summary>True when authoritative (bundle-supplied) floor bands are in force (histogram bypassed).</summary>
    public bool HasAuthoritativeFloors => _authoritativeFloors is { Count: > 0 };

    /// <summary>True when networked section heights have been supplied (stored; not currently adopted).</summary>
    public bool HasSectionHeights => _sectionHeights is { Length: > 0 };

    /// <summary>
    ///     The current floor slices, ordered low→high. Recomputed lazily from the histogram. Empty only
    ///     before any Z is observed; otherwise at least one slice.
    /// </summary>
    public IReadOnlyList<FloorSlice> Slices
    {
        get
        {
            // Authoritative bundle floors bypass the histogram AND the sticky-count hysteresis; see
            // _authoritativeFloors.
            if (_authoritativeFloors is { Count: > 0 } auth)
            {
                return auth;
            }

            if (_dirty)
            {
                ComputeSlices(_scratch);
                _dirty = false;

                // Sticky floor count (count hysteresis). Once a map has REVEALED N floors, keep at least N.
                // A floor that is momentarily empty, or whose relative dwell-mass dilutes as the histogram
                // keeps growing on the other floor, must NOT make its viewport vanish. The count only grows
                // or refines its boundaries; it never drops. Reset() clears it for a new demo.
                //
                // The published list is REPLACED, never refilled in place, and only when the bands actually
                // moved. MapSpaceFactory.SameBands short-circuits on ReferenceEquals, so mutating _slices
                // would hide a real band change from the rebuild, and a fresh copy every frame is a
                // per-frame allocation.
                if (_scratch.Count >= _slices.Count && !SameSlices(_scratch, _slices))
                {
                    _slices = [.. _scratch];
                }
            }

            return _slices;
        }
    }

    /// <summary>Clears the accumulated histogram AND any networked section heights (e.g. on demo unload).</summary>
    public void Reset()
    {
        _buckets.Clear();
        _minBucket = int.MaxValue;
        _maxBucket = int.MinValue;
        _slices = new List<FloorSlice>();
        _scratch.Clear();
        _sectionHeights = null;
        _lastSuppliedHeights = null;
        _authoritativeFloors = null;
        _dirty = true;
    }

    /// <summary>
    ///     Adopts authoritative, map-intrinsic floor bands from a baked, nav-mesh-derived map-asset bundle.
    ///     When non-empty these REPLACE the Z-histogram split entirely (see <see cref="Slices" />). A null /
    ///     empty list clears the override and falls back to the histogram heuristic. Idempotent-ish: always
    ///     marks dirty so the next <see cref="Slices" /> read reflects the change.
    /// </summary>
    public void SetAuthoritativeFloors(IReadOnlyList<FloorSlice>? floors)
    {
        _authoritativeFloors = floors is { Count: > 0 } ? floors.ToList() : null;
        _dirty = true;
    }

    /// <summary>
    ///     Stores the map's networked Z section boundaries (<c>m_MinimapVerticalSectionHeights</c>) so they
    ///     can be surfaced (<c>VM.SectionHeights</c>) and re-enabled later. They are currently NOT adopted
    ///     as the floor split; see the note in <c>ComputeSlices</c>. The histogram heuristic owns the split
    ///     until a genuine multi-floor demo is available to validate adoption. Idempotent: re-supplying an
    ///     equal set is a no-op. A null / empty / all-sentinel array clears them.
    /// </summary>
    public void SetSectionHeights(IReadOnlyList<double>? heights)
    {
        // Reference identity first. The scene calls this once per PUSH with the same list instance the
        // frame has been publishing since the map was read, and CleanSectionHeights allocates a List plus
        // an array every time it runs: a per-frame allocation for data that is constant for the demo.
        if (ReferenceEquals(_lastSuppliedHeights, heights))
        {
            return;
        }

        _lastSuppliedHeights = heights;

        double[]? cleaned = CleanSectionHeights(heights);

        if (SameHeights(_sectionHeights, cleaned))
        {
            return;
        }

        _sectionHeights = cleaned;
        _dirty = true;
    }

    // Keeps the ascending real boundaries, drops the 3.4e38 sentinel and any 0 trailing-unused slots, and
    // returns null when nothing usable remains (so the caller falls back to the histogram).
    private static double[]? CleanSectionHeights(IReadOnlyList<double>? heights)
    {
        if (heights is null || heights.Count == 0)
        {
            return null;
        }

        List<double> kept = new(heights.Count);
        foreach (double h in heights)
        {
            // The first slot is the real floor (≈1.81 on the verified demo) so 0 is meaningful only there;
            // trailing 0 / sentinel slots are "unused section". Stop at the first non-ascending / sentinel.
            if (h >= SectionSentinel)
            {
                break;
            }

            if (kept.Count > 0 && h <= kept[^1])
            {
                break; // not strictly ascending → trailing unused (0) slot; stop here.
            }

            kept.Add(h);
        }

        // A single boundary describes one section (one floor); nothing to split, so stay histogram-eligible.
        return kept.Count >= 2 ? kept.ToArray() : null;
    }

    private static bool SameHeights(double[]? a, double[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 1e-3)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Folds one observed Z value into the running histogram. Non-finite samples are ignored.</summary>
    public void Observe(double z)
    {
        // (int)Math.Floor(NaN / w) is 0 under .NET's saturating conversions, so an unfiltered bad sample
        // does not throw. It invents a phantom dwell band at Z ∈ [0, BucketWidth) and can split a
        // single-floor map in two.
        if (!double.IsFinite(z))
        {
            return;
        }

        int bucket = (int)Math.Floor(z / BucketWidth);
        _buckets.TryGetValue(bucket, out int count);
        _buckets[bucket] = count + 1;

        if (bucket < _minBucket)
        {
            _minBucket = bucket;
        }

        if (bucket > _maxBucket)
        {
            _maxBucket = bucket;
        }

        _dirty = true;
    }

    /// <summary>Folds a batch of observed Z values (one push of player positions).</summary>
    public void Observe(IEnumerable<double> zs)
    {
        foreach (double z in zs)
        {
            Observe(z);
        }
    }

    /// <summary>
    ///     Assigns a Z value to a slice index (0 = lowest floor). Returns the nearest slice when Z falls in
    ///     a gap (e.g. a player on a ramp between floors) so a player is always drawn somewhere
    ///     (hysteresis intent: no flicker into a phantom slice).
    /// </summary>
    public int SliceIndexFor(double z)
    {
        IReadOnlyList<FloorSlice> slices = Slices;
        if (slices.Count == 0)
        {
            return 0;
        }

        for (int i = 0; i < slices.Count; i++)
        {
            if (slices[i].Contains(z))
            {
                return i;
            }
        }

        // In a gap: snap to the nearest slice by mid-Z distance.
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < slices.Count; i++)
        {
            double d = Math.Abs(z - slices[i].MidZ);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        return nearest;
    }

    // Content equality over two band lists, to the same 1e-3 tolerance MapSpaceFactory.SameBands uses. A
    // different tolerance means a rebuild that fires in one place and not the other.
    private static bool SameSlices(List<FloorSlice> a, List<FloorSlice> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].MinZ - b[i].MinZ) > 1e-3 || Math.Abs(a[i].MaxZ - b[i].MaxZ) > 1e-3)
            {
                return false;
            }
        }

        return true;
    }

    // Fills `result` with the bands the histogram currently describes.
    private void ComputeSlices(List<FloorSlice> result)
    {
        // DEFERRED: m_MinimapVerticalSectionHeights is read + stored (SetSectionHeights /
        // VM.SectionHeights) but NOT adopted as the floor split. The schema's "radar floor-switching"
        // sections are render sub-divisions, not storeys: on the demo that resolves them the boundaries
        // (-456,-416,-352) cut THROUGH a continuous single-floor player-Z span [-416..-111]. Every
        // adoption variant either fragments one floor into mostly-empty bands ([0,10,0]) or flickers
        // adoption on/off as the histogram accumulates (Playback2DFloorThresholdProbeTests). Re-enable
        // only with a Nuke/Vertigo demo that publishes real multi-floor heights.
        result.Clear();
        if (_buckets.Count == 0)
        {
            return;
        }

        // Density-valley clustering. An empty-gap heuristic does NOT survive Nuke: the two floors are only
        // ~90-160u apart and stair/ramp traffic fills the inter-floor buckets as the histogram accumulates,
        // closing the gap and merging 2 floors into 1. Floors are where players DWELL (tall histogram
        // mass); stairs/ramps are transient (shallow). A valley, a run of buckets below ValleyFraction of
        // the global peak, separates two floors and PERSISTS under accumulation because the floor peaks
        // grow faster than the valley fills. Each retained cluster must hold ≥ DwellFraction of all
        // observations, so a lone catwalk passer never spawns a phantom band and a single floor with a
        // shallow internal dip never false-splits.
        int lo = _minBucket;
        int hi = _maxBucket;
        int n = hi - lo + 1;

        // Grown, never shrunk, and cleared to n rather than reallocated: the extent only widens as the
        // demo is watched, so this settles after the first few frames and then costs one Array.Clear.
        if (_counts.Length < n)
        {
            _counts = new int[n];
        }

        int[] counts = _counts;
        Array.Clear(counts, 0, n);

        long total = 0;
        int peak = 0;
        foreach (KeyValuePair<int, int> kv in _buckets)
        {
            int c = kv.Value;
            counts[kv.Key - lo] = c;
            total += c;
            if (c > peak)
            {
                peak = c;
            }
        }

        double minPeakMass = MinPeakFraction * total;

        // 1. Significant peaks: local maxima holding ≥ minPeakMass (the dwell bands; one per floor).
        List<int> peaks = _peaks;
        peaks.Clear();
        for (int b = 0; b < n; b++)
        {
            if (counts[b] < minPeakMass)
            {
                continue;
            }

            bool geLeft = b == 0 || counts[b] >= counts[b - 1];
            bool gtLeft = b == 0 || counts[b] > counts[b - 1];
            bool geRight = b == n - 1 || counts[b] >= counts[b + 1];
            bool gtRight = b == n - 1 || counts[b] > counts[b + 1];
            if (geLeft && gtRight || gtLeft && geRight)
            {
                peaks.Add(b);
            }
        }

        if (peaks.Count <= 1)
        {
            // One dwell peak (or none significant) → a single slice over the whole observed Z range.
            result.Add(SliceFromBuckets(lo, hi));
            return;
        }

        // 2. Merge adjacent peaks into one floor unless the valley between them is deep vs the SMALLER peak;
        //    a deep valley → a real floor boundary. Boundaries are the valley-minimum bucket indices.
        List<int> boundaries = _boundaries;
        boundaries.Clear();
        int lastFloorPeak = peaks[0];
        for (int i = 1; i < peaks.Count; i++)
        {
            int a = lastFloorPeak, c2 = peaks[i];
            int valleyMin = counts[a];
            for (int j = a; j <= c2; j++)
            {
                if (counts[j] < valleyMin)
                {
                    valleyMin = counts[j];
                }
            }

            int smaller = Math.Min(counts[a], counts[c2]);
            if (valleyMin < ValleyDepthFraction * smaller)
            {
                // Deep valley → real floor boundary. Place it at the MIDPOINT between the two peaks (robust
                // for a wide flat valley, where the first valley-min bucket would mis-place the cut).
                boundaries.Add((a + c2) / 2);
                lastFloorPeak = c2;
            }
            else if (counts[c2] > counts[lastFloorPeak])
            {
                lastFloorPeak = c2; // shallow valley → same floor; track the taller peak as representative
            }
        }

        if (boundaries.Count == 0)
        {
            result.Add(SliceFromBuckets(lo, hi));
            return;
        }

        // 3. Floor f spans [valley below .. valley above]; lowest extends to lo, highest to hi. Contiguous.
        for (int f = 0; f <= boundaries.Count; f++)
        {
            int loIdx = f == 0 ? 0 : boundaries[f - 1];
            int hiIdx = f == boundaries.Count ? n : boundaries[f];
            result.Add(new FloorSlice((lo + loIdx) * BucketWidth, (lo + hiIdx) * BucketWidth));
        }
    }

    private FloorSlice SliceFromBuckets(int firstBucket, int lastBucket) =>
        new(firstBucket * BucketWidth, (lastBucket + 1) * BucketWidth);
}

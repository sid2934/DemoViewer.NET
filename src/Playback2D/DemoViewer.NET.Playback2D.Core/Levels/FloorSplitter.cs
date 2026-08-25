namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>One detected floor slice — a contiguous Z band players are assigned to.</summary>
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
///     slice (the common case). Metadata-free — when per-map Z thresholds arrive they simply replace
///     the heuristic boundaries (same slice abstraction).
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
    // SMALLER of the two peaks (a LOCAL test — global-peak comparison buries a low-occupancy upper floor).
    // The valley persists under accumulation (floor peaks grow faster than stair traffic fills the valley),
    // so once split it holds. Validated: Nuke → 2 stable floors, Dust2 → 1 (FloorSplitterMultiFloorTests).
    private const double ValleyDepthFraction = 0.25;

    // Sparse running histogram: bucket index → observed count. Sparse because Z spans can be large.
    private readonly SortedDictionary<int, int> _buckets = new();

    // Authoritative floor bands supplied by a baked map-asset bundle (nav-mesh-derived; validated against
    // observed player-Z — ZFloorValidationProbe). When set they OVERRIDE the Z-histogram AND its sticky
    // hysteresis entirely: they are map-intrinsic, stable from frame 1, and known-correct — none of the
    // heuristic's warmup/hysteresis machinery applies. Cleared by Reset() (demo unload) so a single-floor
    // map loaded after Nuke does not inherit its bands.
    private List<FloorSlice>? _authoritativeFloors;
    private bool _dirty = true;

    // The map's REAL networked Z-floor boundaries, when present:
    // CCSGameRulesProxy.m_pGameRules.m_MinimapVerticalSectionHeights[0..N] (#1 bonus). These are exact for
    // Nuke/Vertigo instead of histogram-guessed. When set, they OVERRIDE the histogram split entirely; when
    // null/empty (absent or all-sentinel) the histogram heuristic remains. The values are the LOWER edge of
    // each section (ascending), e.g. [1.81, 51.54, 287.0, 376.0]: section i spans [heights[i], heights[i+1]).
    private double[]? _sectionHeights;

    // The last list handed to SetSectionHeights, for the reference-identity short circuit above. Held
    // only to compare against, never read.
    private IReadOnlyList<double>? _lastSuppliedHeights;

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
            // Authoritative bundle floors bypass the histogram AND the sticky-count hysteresis (they are
            // known-correct and map-intrinsic — no warmup, no flicker guard needed).
            if (_authoritativeFloors is { Count: > 0 } auth)
            {
                return auth;
            }

            if (_dirty)
            {
                List<FloorSlice> fresh = ComputeSlices();

                // Sticky floor count (count hysteresis). Once a map has REVEALED N floors, keep at least N:
                // a floor that is momentarily empty — or whose relative dwell-mass dilutes as the histogram
                // keeps growing on the other floor — must NOT make its viewport vanish (jarring). The count
                // only ever grows or refines its boundaries; it never drops. Reset() clears it for a new demo.
                _slices = fresh.Count >= _slices.Count ? fresh : _slices;
                _dirty = false;
            }

            return _slices;
        }
    }

    /// <summary>Clears the accumulated histogram AND any networked section heights (e.g. on demo unload).</summary>
    public void Reset()
    {
        _buckets.Clear();
        _slices = new List<FloorSlice>();
        _sectionHeights = null;
        _lastSuppliedHeights = null;
        _authoritativeFloors = null;
        _dirty = true;
    }

    /// <summary>
    ///     Adopts authoritative, map-intrinsic floor bands (from a baked map-asset bundle — nav-mesh-derived).
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
    ///     Stores the map's networked Z section boundaries (<c>m_MinimapVerticalSectionHeights</c>, #1 bonus)
    ///     so they can be surfaced (<c>VM.SectionHeights</c>) and re-enabled later. They are currently NOT
    ///     adopted as the floor split — see the note in <c>ComputeSlices</c>: the schema's "radar floor-
    ///     switching" sections are render sub-divisions, not real storeys, and naive adoption fragments a
    ///     single floor / flickers (Playback2DFloorThresholdProbeTests). The histogram heuristic owns the
    ///     split until a genuine multi-floor demo is available to validate adoption. Idempotent — re-supplying
    ///     an equal set is a no-op. A null / empty / all-sentinel array clears them.
    /// </summary>
    public void SetSectionHeights(IReadOnlyList<double>? heights)
    {
        // Reference identity first. The scene calls this once per PUSH with the same list instance the
        // frame has been publishing since the map was read, and CleanSectionHeights allocates a List
        // plus an array every time it runs — a per-frame allocation for data that is constant for the
        // whole demo, and one the §6 zero-allocation budget catches immediately.
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

        // A single boundary describes one section (one floor) — no point splitting; treat as histogram-eligible.
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

    /// <summary>Folds one observed Z value into the running histogram.</summary>
    public void Observe(double z)
    {
        int bucket = (int)Math.Floor(z / BucketWidth);
        _buckets.TryGetValue(bucket, out int count);
        _buckets[bucket] = count + 1;
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
    ///     (hysteresis intent — no flicker into a phantom slice).
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

    private List<FloorSlice> ComputeSlices()
    {
        // NOTE (#1 bonus, DEFERRED): m_MinimapVerticalSectionHeights is read + stored (SetSectionHeights /
        // VM.SectionHeights) but NOT adopted as the floor split. Empirically the schema's "radar floor-
        // switching feature" turned out to be render sub-sections, not real storeys: on the resolving demo
        // the boundaries (-456,-416,-352) cut THROUGH a continuous single-floor player-Z span [-416..-111],
        // and every adoption variant either fragments one floor into mostly-empty bands ([0,10,0]) or flickers
        // adoption on/off as the histogram accumulates (see Playback2DFloorThresholdProbeTests). With no real
        // multi-floor demo to validate the good case, the stable + testable behavior is histogram-only.
        // Re-enable adoption only when a genuine Nuke/Vertigo demo is available to validate the split.
        List<FloorSlice> result = new();
        if (_buckets.Count == 0)
        {
            return result;
        }

        // Density-valley clustering (replaces the original empty-gap heuristic, which COLLAPSED on Nuke:
        // the two floors are only ~90-160u apart and stair/ramp traffic FILLS the inter-floor buckets as
        // the histogram accumulates, closing any empty gap → 2 floors merged to 1 "after a short time").
        // Floors are where players DWELL (tall histogram mass); stairs/ramps are transient (shallow). A
        // valley — a run of buckets below ValleyFraction of the global peak — separates two floors and
        // PERSISTS under accumulation (the floor peaks grow faster than the valley fills). Each retained
        // cluster must hold ≥ DwellFraction of all observations, so a lone catwalk passer / box-jump never
        // spawns a phantom band and a single floor with a shallow internal dip never false-splits.
        int lo = _buckets.Keys.First();
        int hi = _buckets.Keys.Last();
        int n = hi - lo + 1;
        int[] counts = new int[n];
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
        List<int> peaks = new();
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
            return result;
        }

        // 2. Merge adjacent peaks into one floor unless the valley between them is deep vs the SMALLER peak;
        //    a deep valley → a real floor boundary. Boundaries are the valley-minimum bucket indices.
        List<int> boundaries = new();
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
            return result;
        }

        // 3. Floor f spans [valley below .. valley above]; lowest extends to lo, highest to hi. Contiguous.
        for (int f = 0; f <= boundaries.Count; f++)
        {
            int loIdx = f == 0 ? 0 : boundaries[f - 1];
            int hiIdx = f == boundaries.Count ? n : boundaries[f];
            result.Add(new FloorSlice((lo + loIdx) * BucketWidth, (lo + hiIdx) * BucketWidth));
        }

        return result;
    }

    private FloorSlice SliceFromBuckets(int firstBucket, int lastBucket) =>
        new(firstBucket * BucketWidth, (lastBucket + 1) * BucketWidth);
}

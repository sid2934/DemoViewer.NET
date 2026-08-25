#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     The resolved set of floors for the current map, and the one authority on "which floor is this
///     world Z on".
///     <para>
///         <b>Assignment is a parity clone of <see cref="FloorSplitter.SliceIndexFor" /></b> (B1
///         decision D-15): contains-first, then nearest by band centre. That fallback is load-bearing —
///         a player on a ramp between bands, or standing above the highest observed band, must still be
///         drawn <i>somewhere</i>, and the pre-v2 control's "nearest" answer is what the goldens
///         contain. <c>MapSpaceTests</c> pins the two implementations against each other over a Z table.
///     </para>
///     <para>
///         <b>Identity is minted, then CARRIED</b> (design risk 5). A quantized lower Z mints the id of
///         a genuinely new band; every rebuild after that matches bands to levels by <i>overlap</i>, so
///         a boundary drifting one or two buckets — which is what the density-valley histogram does all
///         demo long — keeps every identity intact. See <see cref="Rebuild" /> and
///         <see cref="MapLevelId" />.
///     </para>
/// </summary>
public sealed class MapSpace
{
    /// <summary>
    ///     Z quantum for id minting, equal to <see cref="FloorSplitter" />'s default bucket width. Every
    ///     histogram-derived boundary is already an exact multiple of it
    ///     (<c>FloorSplitter.ComputeSlices</c>), so quantization is the identity function on the common
    ///     path and only snaps the arbitrary-double authoritative nav bands.
    /// </summary>
    public const double LevelQuantum = 64.0;

    /// <summary>
    ///     How much of the thinner of two bands must be shared before a rebuild treats them as the same
    ///     floor. A boundary drifting by one or two buckets moves the score by under 0.05 on any real
    ///     band, so identity survives drift; a genuine 1→2 split scores below this on at least one side,
    ///     so the new floor is <c>Added</c> rather than welded onto its neighbour (plan D2, risk R1).
    /// </summary>
    public const double MatchThreshold = 0.50;

    private static readonly string[] _names =
        ["L0", "L1", "L2", "L3", "L4", "L5", "L6", "L7"];

    private readonly List<MapLevel> _levels = [];
    private readonly List<MapLevelId> _scratchAdded = [];
    private readonly List<MapLevelId> _scratchRemoved = [];
    private readonly List<MapLevelId> _scratchRetained = [];

    // Every key this space has EVER minted, so a level that is removed and a band that later reappears
    // in the same place are two identities, not one wearing the other's camera and annotations.
    private readonly HashSet<int> _usedKeys = [];

    /// <summary>The current levels, lowest band first. Empty before the first rebuild.</summary>
    public IReadOnlyList<MapLevel> Levels => _levels;

    /// <summary>How the radar images were matched to <see cref="Levels" />.</summary>
    public RadarBindingQuality RadarBinding { get; private set; } = RadarBindingQuality.None;

    /// <summary>The last rebuild's outcome. <see cref="LevelSetChange.None" /> before the first.</summary>
    public LevelSetChange LastChange { get; private set; } = LevelSetChange.None;

    /// <summary>
    ///     Bumped on every rebuild that actually changed the set. Cheaper than comparing band lists, and
    ///     it is what <c>PaneSet.Reconcile</c> early-outs on so a steady-state frame allocates nothing.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Raised once per rebuild that changed the set. Never raised by an idempotent rebuild.</summary>
    public event Action? LevelSetChanged;

    /// <summary>
    ///     Quantizes a world Z to the id grid. <b>Half-up, not banker's</b>: CS2 maps sit at negative Z
    ///     routinely, and <c>Math.Round</c>'s round-half-to-even would make the rule asymmetric about
    ///     zero — a silent identity change at exactly the boundary values (plan D1).
    /// </summary>
    /// <param name="z">World Z.</param>
    public static double QuantizeZ(double z) => Math.Floor(z / LevelQuantum + 0.5) * LevelQuantum;

    /// <summary>Mints the id a genuinely new band with this lower Z would carry.</summary>
    /// <param name="zMin">The band's lower world Z.</param>
    public static MapLevelId IdForZMin(double zMin) => new((int)Math.Floor(zMin / LevelQuantum + 0.5));

    /// <summary>
    ///     The level a world Z belongs on. Never null once the space has levels; returns null only
    ///     before the first rebuild.
    /// </summary>
    /// <param name="worldZ">World Z.</param>
    public MapLevel? LevelFor(double worldZ)
    {
        int index = LevelIndexFor(worldZ);
        return index >= 0 && index < _levels.Count ? _levels[index] : null;
    }

    /// <summary>
    ///     The level index a world Z belongs on, or 0 when the space is empty. <b>Behaviourally
    ///     identical to <see cref="FloorSplitter.SliceIndexFor" />.</b>
    /// </summary>
    /// <param name="worldZ">World Z.</param>
    public int LevelIndexFor(double worldZ)
    {
        int count = _levels.Count;
        if (count == 0)
        {
            return 0;
        }

        for (int i = 0; i < count; i++)
        {
            if (_levels[i].Contains(worldZ))
            {
                return i;
            }
        }

        // In a gap (or beyond the observed range): snap to the nearest band centre, exactly as the
        // pre-v2 splitter does, so nothing ever vanishes for want of a band.
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            double d = Math.Abs(worldZ - _levels[i].MidZ);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        return nearest;
    }

    /// <summary>
    ///     The <b>sticky</b> answer given the caller's previous one: keeps <paramref name="previous" />
    ///     until <paramref name="worldZ" /> has cleared that band by at least
    ///     <see cref="LevelHysteresis.SpatialBand" />.
    ///     <para>
    ///         This is the <i>spatial</i> half of the hysteresis and carries no dwell — an entity must
    ///         never lag its own level (plan D4). The temporal half lives in
    ///         <see cref="LevelHysteresis" />, which is what AutoFollow's <i>view</i> decision uses.
    ///     </para>
    ///     <para>
    ///         <b>Drawing does not go through here.</b> <c>SceneRenderContext.BelongsHere</c> uses the
    ///         stateless <see cref="LevelIndexFor" />, because a pane filter that depended on call order
    ///         would make a golden depend on how many frames preceded it.
    ///     </para>
    /// </summary>
    /// <param name="worldZ">World Z.</param>
    /// <param name="previous">The level this caller was last assigned, or null.</param>
    public MapLevel? LevelFor(double worldZ, MapLevelId? previous)
    {
        MapLevel? resolved = LevelFor(worldZ);
        if (resolved is null || previous is not { } previousId || previousId.IsNone ||
            previousId == resolved.Id)
        {
            return resolved;
        }

        MapLevel? held = ById(previousId);
        if (held is null)
        {
            return resolved;
        }

        double band = LevelHysteresis.SpatialBand(held, resolved, LevelHysteresisOptions.Default);
        return DistanceOutside(held, worldZ) <= band ? held : resolved;
    }

    /// <summary>How far <paramref name="worldZ" /> lies outside a band; 0 when inside it.</summary>
    /// <param name="level">The band.</param>
    /// <param name="worldZ">World Z.</param>
    public static double DistanceOutside(MapLevel level, double worldZ)
    {
        ArgumentNullException.ThrowIfNull(level);

        if (worldZ > level.ZMax)
        {
            return worldZ - level.ZMax;
        }

        return worldZ < level.ZMin ? level.ZMin - worldZ : 0;
    }

    /// <summary>The level with this id, or null.</summary>
    /// <param name="id">A level id.</param>
    public MapLevel? ById(MapLevelId id)
    {
        for (int i = 0; i < _levels.Count; i++)
        {
            if (_levels[i].Id == id)
            {
                return _levels[i];
            }
        }

        return null;
    }

    /// <summary>The index of the level with this id, or -1.</summary>
    /// <param name="id">A level id.</param>
    public int IndexOf(MapLevelId id)
    {
        for (int i = 0; i < _levels.Count; i++)
        {
            if (_levels[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Re-derives the level set from a band list, <b>carrying</b> each surviving level's identity by
    ///     band overlap and minting an id only for a genuinely new band.
    ///     <para>
    ///         <b>Idempotent</b>: an unchanged band list (same Z to within a thousandth, same radar
    ///         binding) returns <see cref="LevelSetChange.None" /> and raises nothing, which is what lets
    ///         the caller run this every frame — and it does, because the map bundle can arrive late.
    ///     </para>
    ///     <para>
    ///         <see cref="LastChange" /> is assigned <i>before</i> <see cref="LevelSetChanged" /> is
    ///         raised, so a handler can read the change off the property.
    ///     </para>
    /// </summary>
    /// <param name="bands">The floor bands, lowest first.</param>
    /// <param name="radarByLevel">Radar image per band, positionally aligned; null for none.</param>
    /// <param name="quality">How confidently the radar images were matched.</param>
    /// <param name="radarNamesByLevel">
    ///     Bundle file names for <paramref name="radarByLevel" />, positionally aligned. Optional —
    ///     <see cref="MapLevel.RadarImageName" /> is diagnostics, fixtures and radar placement.
    /// </param>
    public LevelSetChange Rebuild(IReadOnlyList<FloorSlice> bands,
        IReadOnlyList<SKImage?>? radarByLevel = null,
        RadarBindingQuality quality = RadarBindingQuality.None,
        IReadOnlyList<string?>? radarNamesByLevel = null)
    {
        ArgumentNullException.ThrowIfNull(bands);

        if (IsUnchanged(bands, radarByLevel, quality))
        {
            LastChange = LevelSetChange.None;
            return LastChange;
        }

        int oldCount = _levels.Count;
        int newCount = bands.Count;

        // 1. Normalise. A degenerate band can only come from a malformed authoritative bundle; widening
        //    it by one quantum keeps every downstream Span > 0 rather than dividing by zero later.
        double[] minZ = new double[newCount];
        double[] maxZ = new double[newCount];
        for (int i = 0; i < newCount; i++)
        {
            minZ[i] = bands[i].MinZ;
            maxZ[i] = bands[i].MaxZ > bands[i].MinZ ? bands[i].MaxZ : bands[i].MinZ + LevelQuantum;
        }

        // 2-3. Score every (old, new) pair by shared fraction of the thinner band, then match greedily
        //      in descending score while the pair still clears MatchThreshold. Both lists are ≤ ~4.
        int[] oldForNew = new int[newCount];
        int[] newForOld = new int[oldCount];
        Array.Fill(oldForNew, -1);
        Array.Fill(newForOld, -1);

        while (true)
        {
            double best = -1;
            int bestOld = -1;
            int bestNew = -1;

            for (int o = 0; o < oldCount; o++)
            {
                if (newForOld[o] >= 0)
                {
                    continue;
                }

                for (int n = 0; n < newCount; n++)
                {
                    if (oldForNew[n] >= 0)
                    {
                        continue;
                    }

                    double score = OverlapScore(_levels[o].ZMin, _levels[o].ZMax, minZ[n], maxZ[n]);
                    if (score >= MatchThreshold && score > best)
                    {
                        best = score;
                        bestOld = o;
                        bestNew = n;
                    }
                }
            }

            if (bestOld < 0)
            {
                break;
            }

            oldForNew[bestNew] = bestOld;
            newForOld[bestOld] = bestNew;
        }

        _scratchAdded.Clear();
        _scratchRemoved.Clear();
        _scratchRetained.Clear();

        Dictionary<MapLevelId, MapLevelId> remapped = new(oldCount);
        (MapLevelId Id, double ZMin)[] before = new (MapLevelId, double)[oldCount];
        for (int o = 0; o < oldCount; o++)
        {
            before[o] = (_levels[o].Id, _levels[o].ZMin);
            if (newForOld[o] < 0)
            {
                _scratchRemoved.Add(_levels[o].Id);
            }
        }

        // 4-6. Carry, mint, name.
        List<MapLevel> next = new(newCount);
        for (int i = 0; i < newCount; i++)
        {
            MapLevelId id;
            if (oldForNew[i] >= 0)
            {
                id = _levels[oldForNew[i]].Id;
                remapped[id] = id;
                _scratchRetained.Add(id);
            }
            else
            {
                id = Mint(minZ[i], next);
                _scratchAdded.Add(id);
            }

            _usedKeys.Add(id.Key);
            next.Add(new MapLevel
            {
                Id = id,
                Name = NameFor(i),
                ZMin = minZ[i],
                ZMax = maxZ[i],
                Radar = radarByLevel is not null && i < radarByLevel.Count ? radarByLevel[i] : null,
                RadarImageName = radarNamesByLevel is not null && i < radarNamesByLevel.Count
                    ? radarNamesByLevel[i]
                    : null
            });
        }

        _levels.Clear();
        _levels.AddRange(next);
        RadarBinding = quality;
        Version++;

        LastChange = new LevelSetChange(true, _scratchAdded.ToArray(), _scratchRemoved.ToArray(),
            _scratchRetained.ToArray())
        {
            Remapped = remapped,
            LevelsAfter = next,
            LevelsBefore = before
        };
        LevelSetChanged?.Invoke();
        return LastChange;
    }

    /// <summary>Clears every level. For a demo unload; the next rebuild starts from nothing.</summary>
    public void Reset()
    {
        if (_levels.Count == 0 && RadarBinding == RadarBindingQuality.None && _usedKeys.Count == 0)
        {
            return;
        }

        _levels.Clear();
        _usedKeys.Clear();
        RadarBinding = RadarBindingQuality.None;
        LastChange = LevelSetChange.None;
        Version++;
        LevelSetChanged?.Invoke();
    }

    /// <summary>
    ///     The shared fraction of the thinner of two bands, in [0, 1]. 0 when they do not overlap or
    ///     either is degenerate.
    /// </summary>
    /// <param name="aMin">First band's lower Z.</param>
    /// <param name="aMax">First band's upper Z.</param>
    /// <param name="bMin">Second band's lower Z.</param>
    /// <param name="bMax">Second band's upper Z.</param>
    public static double OverlapScore(double aMin, double aMax, double bMin, double bMax)
    {
        double overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
        if (overlap <= 0)
        {
            return 0;
        }

        double thinner = Math.Min(aMax - aMin, bMax - bMin);
        return thinner > 0 ? Math.Min(1.0, overlap / thinner) : 0;
    }

    // The minting rule plus its collision bump: a key already live, or ever minted by this space, walks
    // upward until it is free. Without the "ever minted" half, removing a level and later re-observing
    // the same band would hand the newcomer the departed level's identity — and with it whatever camera
    // or annotation still remembered that id.
    private MapLevelId Mint(double zMin, List<MapLevel> staged)
    {
        int key = IdForZMin(zMin).Key;
        while (_usedKeys.Contains(key) || IsStaged(staged, key))
        {
            key++;
        }

        return new MapLevelId(key);
    }

    private static bool IsStaged(List<MapLevel> staged, int key)
    {
        for (int i = 0; i < staged.Count; i++)
        {
            if (staged[i].Id.Key == key)
            {
                return true;
            }
        }

        return false;
    }

    // Display only, by ascending-ZMin ordinal. Names re-order freely across a rebuild; ids never do.
    private static string NameFor(int index) =>
        index < _names.Length
            ? _names[index]
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"L{index}");

    private bool IsUnchanged(IReadOnlyList<FloorSlice> bands, IReadOnlyList<SKImage?>? radarByLevel,
        RadarBindingQuality quality)
    {
        if (_levels.Count != bands.Count || RadarBinding != quality)
        {
            return false;
        }

        for (int i = 0; i < bands.Count; i++)
        {
            MapLevel level = _levels[i];
            FloorSlice band = bands[i];
            if (Math.Abs(level.ZMin - band.MinZ) > 1e-3 || Math.Abs(level.ZMax - band.MaxZ) > 1e-3)
            {
                return false;
            }

            SKImage? radar = radarByLevel is not null && i < radarByLevel.Count ? radarByLevel[i] : null;
            if (!ReferenceEquals(level.Radar, radar))
            {
                return false;
            }
        }

        return true;
    }
}

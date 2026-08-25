#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     The resolved set of floors for the current map, and the one authority on "which floor is this
///     world Z on".
///     <para>
///         <b>Assignment is a parity clone of <see cref="FloorSplitter.SliceIndexFor" /></b> (plan
///         decision D-15): contains-first, then nearest by band centre. That fallback is load-bearing —
///         a player on a ramp between bands, or standing above the highest observed band, must still be
///         drawn <i>somewhere</i>, and the pre-v2 control's "nearest" answer is what the goldens
///         contain. <c>MapSpaceTests</c> pins the two implementations against each other over a Z table.
///     </para>
///     <para>
///         <b>Identity is minted, not indexed</b> (design risk 5). See <see cref="MapLevelId" />.
///     </para>
/// </summary>
public sealed class MapSpace
{
    /// <summary>
    ///     Z quantum for id minting, equal to <see cref="FloorSplitter" />'s default bucket width. Two
    ///     rebuilds whose bands differ by less than this describe the same floor and keep the same id.
    /// </summary>
    public const double LevelQuantum = 64.0;

    private readonly List<MapLevel> _levels = [];
    private readonly List<MapLevelId> _scratchAdded = [];
    private readonly List<MapLevelId> _scratchRemoved = [];
    private readonly List<MapLevelId> _scratchRetained = [];

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

    /// <summary>Quantizes a world Z to the id grid.</summary>
    /// <param name="z">World Z.</param>
    public static double QuantizeZ(double z) => Math.Round(z / LevelQuantum) * LevelQuantum;

    /// <summary>Mints the id a band with this lower Z would carry.</summary>
    /// <param name="zMin">The band's lower world Z.</param>
    public static MapLevelId IdForZMin(double zMin) => new((int)Math.Round(zMin / LevelQuantum));

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
    ///     The sticky answer given the caller's previous one. B1 ships the stateless result so level
    ///     assignment cannot regress here; <b>B3 fills in the hysteresis band</b> (a player must clear
    ///     the boundary by a margin before their marker jumps floors). The signature is fixed now so B3
    ///     changes a body, not every call site.
    /// </summary>
    /// <param name="worldZ">World Z.</param>
    /// <param name="previous">The level this caller was last assigned, or null.</param>
    public MapLevel? LevelFor(double worldZ, MapLevelId? previous) => LevelFor(worldZ);

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
    ///     Replaces the level set from a band list, minting ids from each band's quantized lower Z.
    ///     <b>Idempotent</b>: an unchanged band list (same ids, same Z to within a thousandth, same radar
    ///     binding) returns <see cref="LevelSetChange.None" /> and raises nothing, which is what lets the
    ///     caller run this every frame.
    ///     <para>
    ///         B3 replaces the minting with overlap-carry matching so a band that drifts across a
    ///         quantum boundary keeps its identity. The signature is frozen here.
    ///     </para>
    /// </summary>
    /// <param name="bands">The floor bands, lowest first.</param>
    /// <param name="radarByLevel">Radar image per band, positionally aligned; null for none.</param>
    /// <param name="quality">How confidently the radar images were matched.</param>
    /// <param name="radarNamesByLevel">
    ///     Bundle file names for <paramref name="radarByLevel" />, positionally aligned. Optional —
    ///     <see cref="MapLevel.RadarImageName" /> is diagnostics and fixtures only.
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

        _scratchAdded.Clear();
        _scratchRemoved.Clear();
        _scratchRetained.Clear();

        for (int i = 0; i < _levels.Count; i++)
        {
            MapLevelId id = _levels[i].Id;
            bool survives = false;
            for (int b = 0; b < bands.Count; b++)
            {
                if (IdForZMin(bands[b].MinZ) == id)
                {
                    survives = true;
                    break;
                }
            }

            (survives ? _scratchRetained : _scratchRemoved).Add(id);
        }

        List<MapLevel> next = new(bands.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            FloorSlice band = bands[i];
            MapLevelId id = IdForZMin(band.MinZ);
            if (IndexOf(id) < 0)
            {
                _scratchAdded.Add(id);
            }

            next.Add(new MapLevel
            {
                Id = id,
                Name = NameFor(i, band),
                ZMin = band.MinZ,
                ZMax = band.MaxZ,
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
            _scratchRetained.ToArray());
        LevelSetChanged?.Invoke();
        return LastChange;
    }

    /// <summary>Clears every level. For a demo unload; the next rebuild starts from nothing.</summary>
    public void Reset()
    {
        if (_levels.Count == 0 && RadarBinding == RadarBindingQuality.None)
        {
            return;
        }

        _levels.Clear();
        RadarBinding = RadarBindingQuality.None;
        LastChange = LevelSetChange.None;
        Version++;
        LevelSetChanged?.Invoke();
    }

    // The label the pre-v2 floor band printed: "floor {index}  z[{min}..{max}]" is assembled by the
    // FloorLabelLayer from the index and the band, so the NAME here is the short human one.
    private static string NameFor(int index, FloorSlice band) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"floor {index}");

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

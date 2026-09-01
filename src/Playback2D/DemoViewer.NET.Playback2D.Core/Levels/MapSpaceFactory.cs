#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     Binds radar images to floor bands. The rules live in Pipeline (they read the baked bundle's
///     radar layers), so Core states the seam and <c>MapRadarBinder</c> implements it. The same
///     Core-declares/Pipeline-solves split as <c>IVisionSolver</c>.
/// </summary>
public interface ILevelRadarBinder
{
    /// <summary>Binds one image (or none) per band.</summary>
    /// <param name="bands">The floor bands, lowest first.</param>
    /// <param name="images">Destination for the per-band image, cleared first.</param>
    /// <param name="names">Destination for the per-band bundle file name, cleared first.</param>
    /// <returns>How confidently the match was made.</returns>
    RadarBindingQuality Bind(IReadOnlyList<FloorSlice> bands, List<SKImage?> images, List<string?> names);
}

/// <summary>
///     Owns the <see cref="FloorSplitter" /> and turns its bands into a <see cref="MapSpace" />.
///     <para>
///         The precedence chain is unchanged from the pre-v2 viewport: authoritative nav floors from a
///         baked bundle override everything; otherwise the Z histogram decides; networked section
///         heights are <b>stored, not adopted</b>; see <c>FloorSplitter.ComputeSlices</c>' note.
///     </para>
///     <para>
///         The frame deliberately carries floor inputs rather than resolved levels, so this is where the
///         derivation happens: once per push, rebuilding the space only when the band list moved.
///     </para>
/// </summary>
public sealed class MapSpaceFactory
{
    private readonly List<SKImage?> _radarImages = [];
    private readonly List<string?> _radarNames = [];
    private ILevelRadarBinder? _binder;
    private bool _forceRebind;
    private IReadOnlyList<FloorSlice> _lastBands = [];

    /// <summary>Creates a factory over a fresh or supplied space.</summary>
    /// <param name="space">The space to fill; a new one when null.</param>
    public MapSpaceFactory(MapSpace? space = null) => Space = space ?? new MapSpace();

    /// <summary>The space this factory maintains.</summary>
    public MapSpace Space { get; }

    /// <summary>The splitter deriving bands from observed player Z. Exposed for parity tests.</summary>
    public FloorSplitter Splitter { get; } = new();

    /// <summary>
    ///     The binder consulted once per rebuild. Setting it re-binds on the next
    ///     <see cref="Update" />, so a late-arriving map bundle reaches the levels without a
    ///     re-activation, as the pre-v2 per-push <c>AuthoritativeFloors</c> pull did.
    /// </summary>
    public ILevelRadarBinder? RadarBinder
    {
        get => _binder;
        set
        {
            if (ReferenceEquals(_binder, value))
            {
                return;
            }

            _binder = value;
            _forceRebind = true;
        }
    }

    /// <summary>
    ///     Adopts nav-derived floor bands from a baked bundle. These override the histogram entirely;
    ///     null or empty falls back to it.
    /// </summary>
    /// <param name="floors">The bundle's bands, or null.</param>
    public void SetAuthoritativeFloors(IReadOnlyList<FloorSlice>? floors) =>
        Splitter.SetAuthoritativeFloors(floors);

    /// <summary>
    ///     Folds one frame into the splitter and rebuilds the space when the bands moved.
    ///     <para>
    ///         Called once per push on the UI thread. In the steady state (bands unchanged) it does an
    ///         indexed pass over the markers and returns false, allocating nothing.
    ///     </para>
    ///     <para>
    ///         <b>Allocates nothing on either branch.</b> With a baked bundle the splitter short-circuits
    ///         to the authoritative list; without one, <c>FloorSplitter</c> recomputes out of reusable
    ///         buffers and republishes the band list only when the bands moved.
    ///     </para>
    /// </summary>
    /// <param name="frame">The frame being advanced to.</param>
    /// <returns>True when the level set changed.</returns>
    public bool Update(Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        Splitter.SetSectionHeights(frame.Map.SectionHeights);

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            // Always fold Z in, even with section heights present: the splitter needs the observed
            // distribution to validate that the heights separate real floor clusters.
            Splitter.Observe(markers[i].WorldZ);
        }

        IReadOnlyList<FloorSlice> bands = Splitter.Slices;
        if (!_forceRebind && SameBands(bands, _lastBands))
        {
            return false;
        }

        _forceRebind = false;
        _lastBands = bands;

        RadarBindingQuality quality = RadarBindingQuality.None;
        _radarImages.Clear();
        _radarNames.Clear();
        if (_binder is not null)
        {
            quality = _binder.Bind(bands, _radarImages, _radarNames);
        }

        LevelSetChange change = Space.Rebuild(bands, _radarImages, quality, _radarNames);
        return change.Changed;
    }

    /// <summary>Clears the histogram, the bundle floors and the space. For a demo unload.</summary>
    public void Reset()
    {
        Splitter.Reset();
        Space.Reset();
        _lastBands = [];
        _radarImages.Clear();
        _radarNames.Clear();
        _forceRebind = true;
    }

    // Reference equality first: FloorSplitter.Slices hands back the SAME list instance while the
    // histogram is unchanged, so the common frame costs one comparison.
    private static bool SameBands(IReadOnlyList<FloorSlice> a, IReadOnlyList<FloorSlice> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

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
}

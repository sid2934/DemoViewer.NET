#region

using System.Collections;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Vision;

/// <summary>
///     Tuning for the line-of-sight overlay, shared by the layer and the Pipeline solver so the drawn
///     cone and the solved rays can never disagree about the field of view. Values are the pre-v2
///     <c>Playback2DViewport</c> constants.
/// </summary>
/// <param name="ConeRays">Rays across the horizontal FOV fan.</param>
/// <param name="ConeHalfFovDeg">Half the horizontal FOV, in degrees.</param>
/// <param name="ConeMaxRange">Ray length cap, in world units.</param>
/// <param name="SightlineHalfFovHDeg">Horizontal half-FOV for the could-see test.</param>
/// <param name="SightlineHalfFovVDeg">Vertical half-FOV for the could-see test.</param>
public sealed record VisionOptions(
    int ConeRays = 26,
    float ConeHalfFovDeg = 53f,
    float ConeMaxRange = 3200f,
    float SightlineHalfFovHDeg = 53f,
    float SightlineHalfFovVDeg = 37f)
{
    /// <summary>The pre-v2 values.</summary>
    public static readonly VisionOptions Default = new();
}

/// <summary>
///     One player's solved FOV footprint: an apex and a fan of clipped ray ends, all in world space.
///     <para>
///         Pooled and reused by <see cref="VisionSolution" />; valid only until the next
///         <see cref="VisionSolution.Clear" />. The ray ends live in a flat float array (x, y per ray)
///         rather than a list of points, because this is rebuilt ten times per frame and a point list
///         would put the §6 zero-allocation budget out of reach on its own.
///     </para>
/// </summary>
public sealed class ConePolygon
{
    private float[] _rayEnds = [];

    /// <summary>The viewing player's roster slot.</summary>
    public int Slot { get; internal set; }

    /// <summary>The viewing player's team (2 = T, 3 = CT).</summary>
    public int Team { get; internal set; }

    /// <summary>Apex world X — the smoothed marker position, so the cone stays glued to the dot.</summary>
    public float ApexX { get; internal set; }

    /// <summary>Apex world Y.</summary>
    public float ApexY { get; internal set; }

    /// <summary>
    ///     The player's <b>feet</b> Z, not the eye Z. This is what the level filter compares, and the
    ///     pre-v2 filter used <c>m.WorldZ</c>; the eye height is a detail of the solve and never leaves
    ///     the solver.
    /// </summary>
    public float ApexZ { get; internal set; }

    /// <summary>How many rays are live in <see cref="RayEndsXY" />.</summary>
    public int RayCount { get; internal set; }

    /// <summary>The clipped ray ends, 2 floats per ray, ordered by angle so the fan fills as one polygon.</summary>
    public ReadOnlySpan<float> RayEndsXY => _rayEnds.AsSpan(0, RayCount * 2);

    /// <summary>The same buffer, writable. For the solver only.</summary>
    public Span<float> RayEndsWritable => _rayEnds.AsSpan(0, RayCount * 2);

    internal void Reserve(int rayCount)
    {
        RayCount = rayCount;
        if (_rayEnds.Length < rayCount * 2)
        {
            _rayEnds = new float[rayCount * 2];
        }
    }
}

/// <summary>
///     A could-see relationship between two players. <b>Endpoints are normally absent</b>: the pre-v2
///     overlay drew the line between the two <i>smoothed</i> marker dots (lines 998-1002) so the line
///     meets the players it describes, and the smoothed positions are not known until render time. A
///     live <see cref="IVisionSolver" /> therefore names slots and lets the layer resolve them.
///     <para>
///         <b>The four endpoint fields exist for geometry that was solved somewhere else</b> (D6 round 3):
///         <c>SceneVision.Sightline</c>, the shape a serialized <see cref="Scene2DFrame" /> carries,
///         holds world endpoints and no target slot at all, because whoever solved it had already
///         resolved both ends. Left <see cref="float.NaN" /> — which is what the five-argument form
///         gives — the layer resolves slots exactly as it always did, so the solver path is unchanged.
///     </para>
/// </summary>
/// <param name="ViewerSlot">The seeing player's roster slot.</param>
/// <param name="ViewerTeam">The seeing player's team, which colours the line.</param>
/// <param name="ViewerZ">The viewer's feet Z, for the level filter.</param>
/// <param name="TargetSlot">The seen player's roster slot, or -1 when only endpoints are known.</param>
/// <param name="TargetZ">The target's feet Z, for the level filter.</param>
/// <param name="ViewerX">Pre-resolved viewer world X, or <see cref="float.NaN" /> to resolve the slot.</param>
/// <param name="ViewerY">Pre-resolved viewer world Y.</param>
/// <param name="TargetX">Pre-resolved target world X, or <see cref="float.NaN" /> to resolve the slot.</param>
/// <param name="TargetY">Pre-resolved target world Y.</param>
public readonly record struct SightlineSegment(
    int ViewerSlot,
    int ViewerTeam,
    float ViewerZ,
    int TargetSlot,
    float TargetZ,
    float ViewerX = float.NaN,
    float ViewerY = float.NaN,
    float TargetX = float.NaN,
    float TargetY = float.NaN)
{
    /// <summary>
    ///     True when both ends were resolved upstream and the layer must draw them as given rather than
    ///     re-deriving them from the frame's markers. Both ends together: half a pre-resolved segment and
    ///     half a smoothed one would be a line neither source ever computed.
    /// </summary>
    public bool HasWorldEndpoints => !float.IsNaN(ViewerX) && !float.IsNaN(TargetX);
}

/// <summary>
///     One frame's solved line-of-sight geometry, in world space. Buffers are pooled and reused, so a
///     solution is valid only until the next <see cref="Clear" />.
/// </summary>
public sealed class VisionSolution
{
    private readonly List<ConePolygon> _conePool = new(12);
    private readonly ConeView _cones;
    private readonly List<SightlineSegment> _sightlines = new(32);
    private int _coneCount;

    /// <summary>Creates an empty solution.</summary>
    public VisionSolution() => _cones = new ConeView(this);

    /// <summary>Whether a solver has produced anything for this frame.</summary>
    public bool IsAvailable { get; set; }

    /// <summary>The solved cones. Indexed access is allocation-free; do not <c>foreach</c> the interface.</summary>
    public IReadOnlyList<ConePolygon> Cones => _cones;

    /// <summary>The solved could-see segments.</summary>
    public IReadOnlyList<SightlineSegment> Sightlines => _sightlines;

    /// <summary>Empties the solution without releasing its pooled buffers.</summary>
    public void Clear()
    {
        _coneCount = 0;
        _sightlines.Clear();
        IsAvailable = false;
    }

    /// <summary>
    ///     Appends a cone and returns it for the caller to fill. The returned instance is pooled — write
    ///     its rays through <see cref="ConePolygon.RayEndsWritable" /> and do not retain it.
    /// </summary>
    /// <param name="slot">Viewer slot.</param>
    /// <param name="team">Viewer team.</param>
    /// <param name="apexX">Apex world X.</param>
    /// <param name="apexY">Apex world Y.</param>
    /// <param name="apexZ">Viewer feet Z (the level filter's input).</param>
    /// <param name="rayCount">How many rays this cone will carry.</param>
    public ConePolygon AddCone(int slot, int team, float apexX, float apexY, float apexZ, int rayCount)
    {
        if (_coneCount == _conePool.Count)
        {
            _conePool.Add(new ConePolygon());
        }

        ConePolygon cone = _conePool[_coneCount++];
        cone.Slot = slot;
        cone.Team = team;
        cone.ApexX = apexX;
        cone.ApexY = apexY;
        cone.ApexZ = apexZ;
        cone.Reserve(rayCount);
        return cone;
    }

    /// <summary>Appends a could-see segment.</summary>
    /// <param name="segment">The segment.</param>
    public void AddSightline(in SightlineSegment segment) => _sightlines.Add(segment);

    // A window over the pooled list's live prefix. A List<T>.GetRange would allocate; exposing the pool
    // directly would leak stale cones from a previous, busier frame.
    private sealed class ConeView : IReadOnlyList<ConePolygon>
    {
        private readonly VisionSolution _owner;

        public ConeView(VisionSolution owner) => _owner = owner;

        public ConePolygon this[int index] => index >= 0 && index < _owner._coneCount
            ? _owner._conePool[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

        public int Count => _owner._coneCount;

        public IEnumerator<ConePolygon> GetEnumerator()
        {
            for (int i = 0; i < _owner._coneCount; i++)
            {
                yield return _owner._conePool[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
///     Where the line-of-sight solve happens. Core declares it and draws the answer; Pipeline's
///     <c>VisibilityEngineSolver</c> implements it, because the visibility engine is a CS2DemoKit type
///     and Core references SkiaSharp only (plan decision D-2).
///     <para>
///         This is also the escape hatch for §6's budget risk: if the solve is too slow on baseline
///         hardware, a <c>DeferredVisionSolver</c> wraps this interface to compute into the <i>next</i>
///         frame's solution off the UI thread. B1 deliberately does not build that — the seam is the
///         deliverable.
///     </para>
/// </summary>
public interface IVisionSolver
{
    /// <summary>False when no engine is loaded for this map — "no data", not "nothing seen".</summary>
    bool IsReady { get; }

    /// <summary>Solves one frame into a reusable solution.</summary>
    /// <param name="frame">The frame being advanced to.</param>
    /// <param name="into">The solution to fill; cleared by the solver.</param>
    void Solve(Scene2DFrame frame, VisionSolution into);
}

#region

using System.Numerics;
using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Vision;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Vision;

/// <summary>
///     The one <see cref="IVisionSolver" /> B1 ships. Port of <c>RebuildSightlines</c> (viewport lines
///     933-984) and the 26 raycasts inside <c>DrawOneCone</c> (1037-1046), both verbatim.
///     <para>
///         It lives in Pipeline because <see cref="VisibilityEngine" /> and
///         <see cref="VisibilityAnalyzer" /> are CS2DemoKit types and Core references SkiaSharp only
///         (plan decision D-2). Core owns the seam and draws the answer.
///     </para>
///     <para>
///         <b>Could-see uses the same <c>VisibilityAnalyzer.EvaluatePair</c> the statistic uses</b>, so
///         the overlay and the number can never disagree about who saw whom — that is the whole reason
///         the pre-v2 code called into the analyzer rather than writing its own frustum test, and it is
///         preserved here.
///     </para>
/// </summary>
public sealed class VisibilityEngineSolver : IVisionSolver
{
    private readonly Func<VisibilityEngine?> _engine;
    private readonly VisionOptions _options;
    private readonly List<Vector4> _smokeScratch = new(4);
    private readonly ISmoothedPositionSource? _smoothed;
    private readonly List<(PlayerMarker Marker, VisibilityAnalyzer.Vantage Vantage)> _vantages = new(12);

    /// <summary>Creates a solver.</summary>
    /// <param name="engine">
    ///     Resolves the current map's engine. A delegate rather than an instance because the BVH is
    ///     built off-thread after the map loads, and the layer outlives every one of them.
    /// </param>
    /// <param name="smoothed">Smoothed marker positions for the cone apexes, or null for raw.</param>
    /// <param name="options">FOV tuning; the pre-v2 constants when null.</param>
    public VisibilityEngineSolver(Func<VisibilityEngine?> engine, ISmoothedPositionSource? smoothed = null,
        VisionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _smoothed = smoothed;
        _options = options ?? VisionOptions.Default;
    }

    /// <inheritdoc />
    public bool IsReady => _engine() is not null;

    /// <inheritdoc />
    public void Solve(Scene2DFrame frame, VisionSolution into)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (_engine() is not { } engine)
        {
            return;
        }

        BuildVantages(frame);
        SolveSightlines(engine, frame, into);
        SolveCones(engine, into);
        into.IsAvailable = true;
    }

    private void BuildVantages(Scene2DFrame frame)
    {
        _vantages.Clear();
        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker m = markers[i];
            if (!m.IsAlive)
            {
                continue;
            }

            Vector3 feet = new(m.WorldX, m.WorldY, m.WorldZ);
            Vector3 eye = PlayerVantage.Eye(feet, m.DuckAmount);
            Vector3 forward = PlayerVantage.Forward(m.PitchDegrees, m.YawDegrees);
            _vantages.Add((m, new VisibilityAnalyzer.Vantage(m.Slot, m.Team, feet, eye, forward, true, m.DuckAmount)));
        }
    }

    private void SolveSightlines(VisibilityEngine engine, Scene2DFrame frame, VisionSolution into)
    {
        // Active smoke occludes could-see, matching the statistic. Sourced from the already-computed
        // area effects so the overlay and the drawn clouds cannot disagree about where smoke is.
        _smokeScratch.Clear();
        IReadOnlyList<AreaEffect> effects = frame.AreaEffects;
        for (int i = 0; i < effects.Count; i++)
        {
            AreaEffect fx = effects[i];
            if (fx.Kind == AreaEffectKind.Smoke)
            {
                _smokeScratch.Add(new Vector4(fx.WorldX, fx.WorldY, fx.WorldZ, fx.WorldRadius));
            }
        }

        ReadOnlySpan<Vector4> smoke = CollectionsMarshal.AsSpan(_smokeScratch);
        for (int i = 0; i < _vantages.Count; i++)
        {
            for (int j = 0; j < _vantages.Count; j++)
            {
                if (i == j || !VisibilityAnalyzer.AreEnemies(_vantages[i].Vantage, _vantages[j].Vantage))
                {
                    continue;
                }

                (_, bool couldSee) = VisibilityAnalyzer.EvaluatePair(engine,
                    _vantages[i].Vantage, _vantages[j].Vantage,
                    _options.SightlineHalfFovHDeg, _options.SightlineHalfFovVDeg, smoke);
                if (!couldSee)
                {
                    continue;
                }

                PlayerMarker viewer = _vantages[i].Marker;
                PlayerMarker target = _vantages[j].Marker;
                into.AddSightline(new SightlineSegment(viewer.Slot, viewer.Team, viewer.WorldZ,
                    target.Slot, target.WorldZ));
            }
        }
    }

    private void SolveCones(VisibilityEngine engine, VisionSolution into)
    {
        int rays = _options.ConeRays;
        for (int v = 0; v < _vantages.Count; v++)
        {
            PlayerMarker m = _vantages[v].Marker;

            // Apex on the SMOOTHED dot so the cone stays glued to the marker; eye height from the RAW
            // Z, exactly as line 1030-1031.
            float px = m.WorldX, py = m.WorldY;
            _smoothed?.TryGetSmoothed(m.Slot, out px, out py);
            float eyeZ = PlayerVantage.Eye(new Vector3(px, py, m.WorldZ), m.DuckAmount).Z;
            Vector3 eye = new(px, py, eyeZ);

            // ApexZ is the FEET Z: it is what the level filter compares, and the pre-v2 filter used
            // m.WorldZ. The eye height never leaves this method.
            ConePolygon cone = into.AddCone(m.Slot, m.Team, px, py, m.WorldZ, rays);
            Span<float> ends = cone.RayEndsWritable;

            for (int i = 0; i < rays; i++)
            {
                float deg = m.YawDegrees - _options.ConeHalfFovDeg
                                         + 2f * _options.ConeHalfFovDeg * i / (rays - 1);
                float rad = deg * (MathF.PI / 180f);
                float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
                float dist = engine.Raycast(eye, new Vector3(cos, sin, 0f), _options.ConeMaxRange, out float t)
                    ? t
                    : _options.ConeMaxRange;
                ends[i * 2] = px + cos * dist;
                ends[i * 2 + 1] = py + sin * dist;
            }
        }
    }
}

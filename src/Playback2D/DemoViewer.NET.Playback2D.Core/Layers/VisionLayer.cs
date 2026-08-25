#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Vision;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The line-of-sight overlay: per-player FOV cones filled first, then the could-see sightlines over
///     them, so the marker discs above stay readable. Port of <c>DrawViewCones</c> / <c>DrawOneCone</c>
///     / <c>DrawSightlines</c> (viewport lines 987-1057).
///     <para>
///         <b>The raycasts moved from Render to Advance</b> (plan decision D-13). The pre-v2 code ran 26
///         raycasts per player <i>inside</i> <c>Control.Render</c> — once per pane, so a two-floor Nuke
///         paid for them twice — and called into the visibility engine from the render thread. Solving
///         once in Advance is pixel-identical (same rays, same eye, same range), strictly cheaper, and
///         the only way the Advance/Render purity split can hold.
///     </para>
///     <para>
///         The solve itself lives in Pipeline (decision D-2): <c>VisibilityEngine</c> is a CS2DemoKit
///         type and Core references SkiaSharp only.
///     </para>
/// </summary>
public sealed class VisionLayer : ISceneLayer
{
    private readonly SKPaint _cone;
    private readonly SKPath _conePath = new();
    private readonly SKPaint _sightline;
    private readonly MarkerSmoother? _smoother;
    private readonly IVisionSolver? _solver;

    /// <summary>Creates the layer.</summary>
    /// <param name="solver">The solve seam; null draws nothing.</param>
    /// <param name="smoother">
    ///     The shared marker smoothing, so cone apexes and sightline endpoints sit on the drawn dots
    ///     rather than the raw samples. <b>Read, never advanced</b> — <c>MarkerLayer</c> owns that, and
    ///     the compositor advances it second (draw order 40 against this layer's 30), so a cone apex
    ///     trails its dot by one frame while a glide is in progress. Sightline endpoints are resolved at
    ///     Render and are always current. Null falls back to raw positions.
    /// </param>
    public VisionLayer(IVisionSolver? solver, MarkerSmoother? smoother = null)
    {
        _solver = solver;
        _smoother = smoother;
        _cone = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        _sightline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
    }

    /// <summary>The last solved geometry. Test hook, and what B4's HUD reads for a "seen by" count.</summary>
    public VisionSolution Solution { get; } = new();

    /// <summary>Could-see segments solved for the last advance — the pre-v2 <c>SightlineCount</c> hook.</summary>
    public int SightlineCount => Solution.Sightlines.Count;

    /// <inheritdoc />
    public string Id => SceneLayerIds.Vision;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.World;

    /// <inheritdoc />
    public int Order => 30;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; }

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        if (_solver is null)
        {
            Solution.Clear();
            return false;
        }

        _solver.Solve(frame, Solution);
        return false; // vision is frame-driven, not animated — it never keeps the loop armed on its own
    }

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (!Solution.IsAvailable)
        {
            return;
        }

        DrawCones(canvas, in ctx);
        DrawSightlines(canvas, in ctx);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _conePath.Dispose();
        _cone.Dispose();
        _sightline.Dispose();
    }

    private void DrawCones(SKCanvas canvas, in SceneRenderContext ctx)
    {
        IReadOnlyList<ConePolygon> cones = Solution.Cones;
        for (int c = 0; c < cones.Count; c++)
        {
            ConePolygon cone = cones[c];
            if (!ctx.BelongsHere(cone.ApexZ))
            {
                continue;
            }

            ReadOnlySpan<float> rays = cone.RayEndsXY;
            if (rays.Length < 4)
            {
                continue;
            }

            (double apexX, double apexY) = ctx.Transform.WorldToScreen(cone.ApexX, cone.ApexY);
            _conePath.Reset();
            _conePath.MoveTo((float)apexX, (float)apexY);
            for (int i = 0; i < rays.Length; i += 2)
            {
                (double ex, double ey) = ctx.Transform.WorldToScreen(rays[i], rays[i + 1]);
                _conePath.LineTo((float)ex, (float)ey);
            }

            _conePath.Close();

            _cone.Color = cone.Team switch
            {
                2 => ctx.Palette.ConeT,
                3 => ctx.Palette.ConeCt,
                _ => ctx.Palette.ConeNeutral
            };
            canvas.DrawPath(_conePath, _cone);
        }
    }

    // A sightline draws on a band if EITHER endpoint is on it, mirroring the trail rule (parity
    // invariant 5), and connects the SMOOTHED dots so the line meets the players it describes.
    private void DrawSightlines(SKCanvas canvas, in SceneRenderContext ctx)
    {
        IReadOnlyList<SightlineSegment> lines = Solution.Sightlines;
        _sightline.StrokeWidth = ctx.Palette.Strokes.Sightline;

        for (int i = 0; i < lines.Count; i++)
        {
            SightlineSegment line = lines[i];
            if (!ctx.BelongsHere(line.ViewerZ) && !ctx.BelongsHere(line.TargetZ))
            {
                continue;
            }

            if (!TryResolve(ctx.Frame, line.ViewerSlot, out float vx, out float vy) ||
                !TryResolve(ctx.Frame, line.TargetSlot, out float tx, out float ty))
            {
                continue;
            }

            (double sx0, double sy0) = ctx.Transform.WorldToScreen(vx, vy);
            (double sx1, double sy1) = ctx.Transform.WorldToScreen(tx, ty);
            _sightline.Color = line.ViewerTeam == 3 ? ctx.Palette.SightlineCt : ctx.Palette.SightlineT;
            canvas.DrawLine((float)sx0, (float)sy0, (float)sx1, (float)sy1, _sightline);
        }
    }

    private bool TryResolve(Scene2DFrame frame, int slot, out float x, out float y)
    {
        if (_smoother is not null && _smoother.TryGetSmoothed(slot, out x, out y))
        {
            return true;
        }

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Slot == slot)
            {
                x = markers[i].WorldX;
                y = markers[i].WorldY;
                return true;
            }
        }

        x = 0;
        y = 0;
        return false;
    }
}

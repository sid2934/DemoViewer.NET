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
///         <b>The raycasts moved from Render to Advance.</b> The pre-v2 code ran 26 raycasts per player
///         inside <c>Control.Render</c> — once per pane, so a two-floor Nuke paid for them twice — and
///         called into the visibility engine from the render thread. Solving once in Advance is
///         pixel-identical (same rays, same eye, same range), strictly cheaper, and the only way the
///         Advance/Render purity split can hold. The solve itself lives in Pipeline:
///         <c>VisibilityEngine</c> is a CS2DemoKit type and Core references SkiaSharp only.
///     </para>
///     <para>
///         <b>Two sources, and it draws whichever one has data.</b> An <see cref="IVisionSolver" /> is the
///         live path the app takes, and it wins whenever it produces a solution. A
///         <see cref="Scene2DFrame" /> can also arrive with <see cref="SceneVision" /> already solved —
///         the shape a serialized fixture carries. It is a fallback rather than a merge because two
///         sources drawn at once would double every cone on the one frame that carried both.
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
    /// <param name="solver">
    ///     The solve seam. Null is not "draw nothing" — the layer then draws whatever the frame carries
    ///     pre-solved in <see cref="Scene2DFrame.Vision" />. A headless fixture render supplies its cones
    ///     this way.
    /// </param>
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

    /// <summary>The last solved geometry. Test hook, and what the HUD reads for a "seen by" count.</summary>
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
    /// <remarks>
    ///     <b>Defaults on, like the other eleven layers.</b> The default only matters before
    ///     <c>SetEnabled</c> is called: in the app, <c>SyncFromViewModel</c> pushes <c>vm.ShowVision</c>
    ///     through it on every frame, so the toggle, not this default, decides there.
    /// </remarks>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _solver?.Solve(frame, Solution);

        // A solver that produced nothing has not necessarily decided there is nothing to see: with no
        // engine loaded for this map it clears and leaves IsAvailable false, which is the same state as
        // having no solver at all. Either way the frame's own pre-solved geometry is the next-best
        // source, so the test is on the RESULT rather than on `_solver is null`.
        if (_solver is null || !Solution.IsAvailable)
        {
            Project(frame.Vision, Solution);
        }

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

    /// <summary>
    ///     Copies a frame's already-solved <see cref="SceneVision" /> into the solution shape the renderer
    ///     reads. The two shapes differ on purpose and neither is wrong: a live solver names slots and
    ///     defers the endpoints to render-time smoothing, while a persisted frame carries the world
    ///     coordinates because whoever wrote it had already resolved them.
    ///     <para>
    ///         <b>Allocation-free after the first frame.</b> <c>AddCone</c> hands back a pooled
    ///         <see cref="ConePolygon" /> whose ray buffer only ever grows, and the sightline list keeps
    ///         its capacity across <c>Clear</c> — which it must, because <c>duel-mirage-b</c> is the
    ///         fixture CI's 0 B/frame allocation gate benches and it is one of the three that carry
    ///         vision.
    ///     </para>
    /// </summary>
    private static void Project(SceneVision vision, VisionSolution into)
    {
        into.Clear();
        if (!vision.IsAvailable)
        {
            return;
        }

        IReadOnlyList<VisionCone> cones = vision.Cones;
        for (int c = 0; c < cones.Count; c++)
        {
            VisionCone source = cones[c];
            IReadOnlyList<ConePoint> fan = source.Fan;
            ConePolygon target = into.AddCone(source.Slot, source.Team, source.ApexX, source.ApexY,
                source.ApexZ, fan.Count);

            Span<float> rays = target.RayEndsWritable;
            for (int i = 0; i < fan.Count; i++)
            {
                rays[i * 2] = fan[i].X;
                rays[i * 2 + 1] = fan[i].Y;
            }
        }

        IReadOnlyList<Sightline> lines = vision.Sightlines;
        for (int i = 0; i < lines.Count; i++)
        {
            Sightline line = lines[i];

            // TargetSlot -1: the persisted shape does not carry one, and inventing a slot by matching
            // an endpoint against a marker would be a guess that silently re-points the line the day two
            // players share a coordinate. The endpoints make the slot unnecessary here.
            into.AddSightline(new SightlineSegment(line.ViewerSlot, line.ViewerTeam, line.Z0, -1, line.Z1,
                line.X0, line.Y0, line.X1, line.Y1));
        }

        into.IsAvailable = true;
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
    // invariant 5), and connects the SMOOTHED dots so the line meets the players it describes — unless
    // the segment arrived with both ends already resolved, in which case re-deriving them would throw
    // away the answer its author computed.
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

            float vx, vy, tx, ty;
            if (line.HasWorldEndpoints)
            {
                (vx, vy, tx, ty) = (line.ViewerX, line.ViewerY, line.TargetX, line.TargetY);
            }
            else if (!TryResolve(ctx.Frame, line.ViewerSlot, out vx, out vy) ||
                     !TryResolve(ctx.Frame, line.TargetSlot, out tx, out ty))
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

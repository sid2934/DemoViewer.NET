#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     Grenade flight trails: a fading polyline per projectile plus a brighter head dot at the live
///     position. Port of <c>DrawTrajectory</c> (viewport lines 1237-1290).
///     <para>
///         Each <b>segment</b> is level-assigned by its own endpoints, not by the trail's current tip,
///         so an arc that crosses floors draws each portion on the right band and the crossing segment
///         bridges both (parity invariant 4). The head dot draws only on the tip's level (invariant 4,
///         second half).
///     </para>
/// </summary>
public sealed class TrailLayer : ISceneLayer
{
    private readonly SKPaint _head;
    private readonly SKPath _path = new();
    private readonly List<(int Start, int End)> _runs = new(8);
    private readonly SKPaint _stroke;

    /// <summary>Creates the layer.</summary>
    public TrailLayer()
    {
        _stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        _head = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.Trails;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.World;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        IReadOnlyList<GrenadeTrail> trails = ctx.Frame.Trails;
        for (int i = 0; i < trails.Count; i++)
        {
            DrawTrail(canvas, trails[i], in ctx);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _path.Dispose();
        _stroke.Dispose();
        _head.Dispose();
    }

    private void DrawTrail(SKCanvas canvas, GrenadeTrail trail, in SceneRenderContext ctx)
    {
        List<GrenadeTrailPoint> points = trail.Points;
        if (points.Count < 2)
        {
            return;
        }

        SKColor colour = ColourFor(trail.Kind, ctx.Palette);
        double alpha = Math.Clamp(trail.Alpha, 0, 1);

        TrailGeometry.FloorSegmentRuns(points, in ctx, _runs);

        _path.Reset();
        for (int r = 0; r < _runs.Count; r++)
        {
            (int start, int end) = _runs[r];
            (double sx, double sy) = ctx.Transform.WorldToScreen(points[start].X, points[start].Y);
            _path.MoveTo((float)sx, (float)sy);
            for (int i = start + 1; i <= end; i++)
            {
                (double ex, double ey) = ctx.Transform.WorldToScreen(points[i].X, points[i].Y);
                _path.LineTo((float)ex, (float)ey);
            }
        }

        if (!_path.IsEmpty)
        {
            _stroke.Color = colour.WithAlpha((byte)Math.Clamp(alpha * 200, 0, 255));
            canvas.DrawPath(_path, _stroke);
        }

        GrenadeTrailPoint head = points[^1];
        if (!ctx.BelongsHere(head.Z))
        {
            return;
        }

        (double hx, double hy) = ctx.Transform.WorldToScreen(head.X, head.Y);
        _head.Color = colour.WithAlpha((byte)Math.Clamp(alpha * 240, 0, 255));
        canvas.DrawCircle((float)hx, (float)hy, 2.5f, _head);
    }

    private static SKColor ColourFor(GrenadeKind kind, ScenePalette palette) => kind switch
    {
        GrenadeKind.He => palette.TrailHe,
        GrenadeKind.Flash => palette.TrailFlash,
        GrenadeKind.Smoke => palette.TrailSmoke,
        GrenadeKind.Molotov => palette.TrailMolotov,
        GrenadeKind.Decoy => palette.TrailDecoy,
        _ => palette.TrailSmoke
    };
}

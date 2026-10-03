#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;

/// <summary>
///     The strat canvas's editing guides (<see cref="SceneGuides" />, read from the host on each paint): the selected step's destination pins and
///     via marks, and while a token is dragged its hollow start, the dashed route the drop would store and the place under
///     the pointer. Under the markers, so the dragged token stays on top. Draws nothing on a frame without guides, which is
///     every host but the strat canvas.
/// </summary>
public sealed class GuideLayer : ISceneLayer
{
    private const float PinRadius = SceneDefaults.MarkerRadius + 2f;
    private const float ViaMarkSize = 4f;

    private readonly SKPaint _dashed;
    private readonly SKPaint _fill;
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([7f, 5f], 0);
    private readonly SKPath _path = new();
    private readonly SKPaint _solid;
    private readonly Func<SceneGuides> _source;

    /// <summary>Creates the layer over a source of guides.</summary>
    /// <param name="source">The current guides; read on the render thread, so it hands out an immutable snapshot.</param>
    public GuideLayer(Func<SceneGuides> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _solid = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
        _dashed = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, PathEffect = _dash
        };
        _fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.Guides;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    /// <remarks>Just under the markers (40).</remarks>
    public int Order => 38;

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
        SceneGuides guides = _source();
        if (guides.IsEmpty)
        {
            return;
        }

        if (guides.DropOutline.Count > 0 && ctx.BelongsHere(guides.DropOutlineZ))
        {
            _path.Reset();
            foreach ((System.Numerics.Vector2 a, System.Numerics.Vector2 b) in guides.DropOutline)
            {
                (double ax, double ay) = ctx.Transform.WorldToScreen(a.X, a.Y);
                (double bx, double by) = ctx.Transform.WorldToScreen(b.X, b.Y);
                _path.MoveTo((float)ax, (float)ay);
                _path.LineTo((float)bx, (float)by);
            }

            _dashed.Color = ctx.Palette.DropTarget;
            canvas.DrawPath(_path, _dashed);
        }

        foreach (GuidePin pin in guides.Pins)
        {
            SKColor colour = ctx.Palette.TeamFill(pin.At.Team);
            _solid.Color = ctx.Palette.RouteGhost(pin.At.Team);
            Line(canvas, pin.Route, _solid, in ctx);
            if (ctx.BelongsHere(pin.At.Z))
            {
                (double x, double y) = ctx.Transform.WorldToScreen(pin.At.X, pin.At.Y);
                _solid.Color = pin.Earlier ? colour.WithAlpha(0x66) : colour;
                canvas.DrawCircle((float)x, (float)y, PinRadius, _solid);
            }
        }

        foreach (GrenadeTrailPoint mark in guides.ViaMarks)
        {
            if (!ctx.BelongsHere(mark.Z))
            {
                continue;
            }

            (double x, double y) = ctx.Transform.WorldToScreen(mark.X, mark.Y);
            _fill.Color = ctx.Palette.DropTarget;
            _path.Reset();
            _path.MoveTo((float)x, (float)y - ViaMarkSize);
            _path.LineTo((float)x + ViaMarkSize, (float)y);
            _path.LineTo((float)x, (float)y + ViaMarkSize);
            _path.LineTo((float)x - ViaMarkSize, (float)y);
            _path.Close();
            canvas.DrawPath(_path, _fill);
        }

        if (guides.GhostRoute.Count > 1)
        {
            _dashed.Color = ctx.Palette.RouteGhost(guides.GhostTeam);
            Line(canvas, guides.GhostRoute, _dashed, in ctx);
        }

        if (guides.Ghost is { } ghost && ctx.BelongsHere(ghost.Z))
        {
            (double x, double y) = ctx.Transform.WorldToScreen(ghost.X, ghost.Y);
            _dashed.Color = ctx.Palette.TeamFill(ghost.Team);
            canvas.DrawCircle((float)x, (float)y, SceneDefaults.MarkerRadius, _dashed);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _solid.Dispose();
        _dashed.Dispose();
        _fill.Dispose();
        _dash.Dispose();
        _path.Dispose();
    }

    // Only the legs on this pane: a route across floors is drawn in the pane of each leg's start.
    private void Line(SKCanvas canvas, IReadOnlyList<GrenadeTrailPoint> points, SKPaint paint, in SceneRenderContext ctx)
    {
        _path.Reset();
        bool open = false;
        for (int i = 0; i < points.Count; i++)
        {
            (double x, double y) = ctx.Transform.WorldToScreen(points[i].X, points[i].Y);
            bool here = ctx.BelongsHere(points[i].Z) || (i > 0 && ctx.BelongsHere(points[i - 1].Z));
            if (!here)
            {
                open = false;
                continue;
            }

            if (open)
            {
                _path.LineTo((float)x, (float)y);
            }
            else
            {
                _path.MoveTo((float)x, (float)y);
                open = true;
            }
        }

        canvas.DrawPath(_path, paint);
    }
}

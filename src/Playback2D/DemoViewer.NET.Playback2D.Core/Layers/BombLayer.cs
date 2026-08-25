#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The planted-C4: a red diamond, a dim ring track, a detonation arc depleting clockwise from 12
///     o'clock, and — during a defuse — an inner arc depleting alongside it, which is the
///     defuse-vs-detonation race made spatial. Port of <c>DrawBomb</c> / <c>DrawArc</c> /
///     <c>PointOnCircle</c> (viewport lines 1338-1396).
/// </summary>
public sealed class BombLayer : ISceneLayer
{
    private const float IconRadius = 6f;
    private const float DetonateRadius = 16f;
    private const float DefuseRadius = 11f;

    private readonly SKPath _arc = new();
    private readonly SKPath _diamond = new();
    private readonly SKPaint _fill;
    private readonly SKPaint _stroke;

    /// <summary>Creates the layer.</summary>
    public BombLayer()
    {
        _fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        _stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.Bomb;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order => 50;

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

        if (ctx.Frame.Bomb is not { } bomb || !ctx.BelongsHere(bomb.WorldZ))
        {
            return;
        }

        (double sx, double sy) = ctx.Transform.WorldToScreen(bomb.WorldX, bomb.WorldY);
        float cx = (float)sx, cy = (float)sy;

        _diamond.Reset();
        _diamond.MoveTo(cx, cy - IconRadius);
        _diamond.LineTo(cx + IconRadius, cy);
        _diamond.LineTo(cx, cy + IconRadius);
        _diamond.LineTo(cx - IconRadius, cy);
        _diamond.Close();
        _fill.Color = ctx.Palette.Bomb;
        canvas.DrawPath(_diamond, _fill);

        _stroke.Color = ctx.Palette.BombTrack;
        _stroke.StrokeWidth = ctx.Palette.Strokes.BombTrack;
        canvas.DrawCircle(cx, cy, DetonateRadius, _stroke);

        _stroke.Color = ctx.Palette.BombDetonation;
        _stroke.StrokeWidth = ctx.Palette.Strokes.BombDetonation;
        DrawArc(canvas, cx, cy, DetonateRadius, bomb.DetonationFraction);

        if (!bomb.BeingDefused)
        {
            return;
        }

        _stroke.Color = ctx.Palette.BombTrack;
        _stroke.StrokeWidth = ctx.Palette.Strokes.BombTrack;
        canvas.DrawCircle(cx, cy, DefuseRadius, _stroke);

        _stroke.Color = ctx.Palette.BombDefuse;
        _stroke.StrokeWidth = ctx.Palette.Strokes.BombDefuse;
        DrawArc(canvas, cx, cy, DefuseRadius, bomb.DefuseFraction);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _arc.Dispose();
        _diamond.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
    }

    // Strokes `fraction` of a circle clockwise from 12 o'clock. The two clamps are pixel-visible
    // (parity invariant 8): below half a degree the arc collapses to a dot and is skipped, and a full
    // 360 makes start == end, which Skia draws as nothing at all — the track ring behind it already
    // reads as "full", so 359.99 is both correct and what the pre-v2 code drew.
    private void DrawArc(SKCanvas canvas, float cx, float cy, float radius, double fraction)
    {
        double sweep = Math.Clamp(fraction, 0, 1) * 360.0;
        if (sweep <= 0.5)
        {
            return;
        }

        sweep = Math.Min(sweep, 359.99);
        (float startX, float startY) = PointOnCircle(cx, cy, radius, -90);
        (float endX, float endY) = PointOnCircle(cx, cy, radius, -90 + sweep);

        _arc.Reset();
        _arc.MoveTo(startX, startY);
        _arc.ArcTo(new SKPoint(radius, radius), 0,
            sweep > 180 ? SKPathArcSize.Large : SKPathArcSize.Small,
            SKPathDirection.Clockwise, new SKPoint(endX, endY));
        canvas.DrawPath(_arc, _stroke);
    }

    private static (float X, float Y) PointOnCircle(float cx, float cy, float radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return (cx + (float)(radius * Math.Cos(rad)), cy + (float)(radius * Math.Sin(rad)));
    }
}

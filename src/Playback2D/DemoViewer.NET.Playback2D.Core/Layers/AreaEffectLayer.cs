#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     Smoke clouds and burning inferno cells as translucent world-radius discs. Port of
///     <c>DrawAreaEffect</c> plus its level filter.
///     <para>
///         The pre-v2 draw was one <c>DrawEllipse(fill, pen, …)</c>, which fills AND strokes. Skia needs
///         two passes for that, and the order matters: fill first, then the outline over it, or the
///         outline's inner half is painted over.
///     </para>
/// </summary>
public sealed class AreaEffectLayer : ISceneLayer
{
    private const float DecoyMinRadius = 5f;

    private readonly SKPaint _fill;
    private readonly SKPaint _stroke;

    /// <summary>Creates the layer.</summary>
    public AreaEffectLayer()
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
    public string Id => SceneLayerIds.AreaEffects;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.World;

    /// <inheritdoc />
    public int Order => 20;

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

        IReadOnlyList<AreaEffect> effects = ctx.Frame.AreaEffects;
        for (int i = 0; i < effects.Count; i++)
        {
            AreaEffect fx = effects[i];
            if (!ctx.BelongsHere(fx.WorldZ))
            {
                continue;
            }

            (double sx, double sy) = ctx.Transform.WorldToScreen(fx.WorldX, fx.WorldY);
            // A floor of 2 px: zoomed out, a real 28-unit fire cell is sub-pixel, and a cluster of
            // invisible cells reads as "the fire went out".
            float r = (float)Math.Max(2, fx.WorldRadius * ctx.Transform.EffectiveScale);

            float alpha = Math.Clamp(fx.Alpha, 0f, 1f);
            switch (fx.Kind)
            {
                case AreaEffectKind.Smoke:
                    _fill.Color = Faded(ctx.Palette.Smoke, alpha);
                    canvas.DrawCircle((float)sx, (float)sy, r, _fill);
                    _stroke.Color = Faded(ctx.Palette.SmokeStroke, alpha);
                    _stroke.StrokeWidth = ctx.Palette.Strokes.SmokeStroke;
                    canvas.DrawCircle((float)sx, (float)sy, r, _stroke);
                    break;
                case AreaEffectKind.Flash:
                    DrawPop(canvas, (float)sx, (float)sy, r, ctx.Palette.TrailFlash, alpha);
                    break;
                case AreaEffectKind.Explosion:
                    DrawPop(canvas, (float)sx, (float)sy, r, ctx.Palette.TrailHe, alpha);
                    break;
                case AreaEffectKind.Decoy:
                    // A decoy's world size is a pixel at the map's fit, so the ring keeps a readable floor.
                    DrawPop(canvas, (float)sx, (float)sy, Math.Max(DecoyMinRadius, r), ctx.Palette.TrailDecoy, alpha);
                    break;
                default:
                    _fill.Color = Faded(ctx.Palette.Fire, alpha);
                    canvas.DrawCircle((float)sx, (float)sy, r, _fill);
                    break;
            }
        }
    }

    private void DrawPop(SKCanvas canvas, float x, float y, float r, SKColor colour, float alpha)
    {
        _fill.Color = Faded(colour.WithAlpha(0x70), alpha);
        canvas.DrawCircle(x, y, r, _fill);
        _stroke.Color = Faded(colour, alpha);
        _stroke.StrokeWidth = 2f;
        canvas.DrawCircle(x, y, r, _stroke);
    }

    private static SKColor Faded(SKColor colour, float alpha) =>
        alpha >= 1f ? colour : colour.WithAlpha((byte)Math.Round(colour.Alpha * alpha));

    /// <inheritdoc />
    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
    }
}

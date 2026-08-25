#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     Smoke clouds and burning inferno cells as translucent world-radius discs. Port of
///     <c>DrawAreaEffect</c> (viewport lines 1216-1230) plus the level filter at line 892.
///     <para>
///         The pre-v2 draw was one <c>DrawEllipse(fill, pen, …)</c>, which fills <i>and</i> strokes.
///         Skia needs two passes for that, and the order matters — fill first, then the outline over
///         it — or the outline's inner half is painted over.
///     </para>
/// </summary>
public sealed class AreaEffectLayer : ISceneLayer
{
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

            if (fx.Kind == AreaEffectKind.Smoke)
            {
                _fill.Color = ctx.Palette.Smoke;
                canvas.DrawCircle((float)sx, (float)sy, r, _fill);
                _stroke.Color = ctx.Palette.SmokeStroke;
                _stroke.StrokeWidth = ctx.Palette.Strokes.SmokeStroke;
                canvas.DrawCircle((float)sx, (float)sy, r, _stroke);
            }
            else
            {
                _fill.Color = ctx.Palette.Fire;
                canvas.DrawCircle((float)sx, (float)sy, r, _fill);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
    }
}

#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The per-band caption — <c>floor 1  z[-352..-128]</c> — in the pane's top-left corner. Port of
///     the label built at viewport line 588 and drawn at 927.
///     <para>
///         Renders only on a multi-level layout: the pre-v2 single-floor path passed a null label
///         (line 577), and a lone band showing "floor 0" over the whole map is noise.
///     </para>
/// </summary>
public sealed class FloorLabelLayer : ISceneLayer
{
    private const float MarginX = 8f;
    private const float MarginY = 6f;

    private readonly Dictionary<LabelKey, string> _captions = new(4);
    private readonly SKPaint _paint;
    private readonly TextBlobCache _text;
    private readonly bool _ownsText;

    /// <summary>Creates the layer.</summary>
    /// <param name="text">The shared blob cache. A private one when null, disposed with the layer.</param>
    public FloorLabelLayer(TextBlobCache? text = null)
    {
        _ownsText = text is null;
        _text = text ?? new TextBlobCache();
        _paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.FloorLabel;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Hud;

    /// <inheritdoc />
    public int Order => 60;

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

        if (ctx.IsSingleLevel || ctx.Pane.Level is not { } level)
        {
            return;
        }

        string caption = CaptionFor(ctx.LevelIndex, level);
        if (_text.Get(caption, SceneDefaults.FloorLabelSize) is not { } shaped)
        {
            return;
        }

        _paint.Color = ctx.Palette.Label;
        (float x, float y) = shaped.OriginForTopLeft(MarginX, MarginY);
        canvas.DrawText(shaped.Blob, x, y, _paint);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _paint.Dispose();
        if (_ownsText)
        {
            _text.Dispose();
        }
    }

    // Cached per (level id, index, band): the string is constant for the whole map, and formatting it
    // per band per frame is the exact allocation the text cache exists to remove one level up.
    private string CaptionFor(int levelIndex, MapLevel level)
    {
        LabelKey key = new(level.Id, levelIndex, level.ZMin, level.ZMax);
        if (_captions.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string caption = string.Create(CultureInfo.InvariantCulture,
            $"floor {levelIndex}  z[{level.ZMin:F0}..{level.ZMax:F0}]");
        _captions[key] = caption;
        return caption;
    }

    private readonly record struct LabelKey(MapLevelId Id, int Index, double ZMin, double ZMax);
}

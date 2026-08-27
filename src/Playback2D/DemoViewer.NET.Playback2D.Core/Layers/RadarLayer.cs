#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The pane's background: the baked radar image for this level, or the synthetic grid when there is
///     none. Port of <c>TryDrawRadar</c> (viewport lines 1066-1091) and <c>DrawGrid</c> (1118-1153),
///     preserving the radar-else-grid structure of line 868 exactly.
///     <para>
///         <b>Cached <c>PerCamera</c>, both halves in one picture.</b> The radar is a single
///         <c>DrawImage</c> and the grid is up to 800 <c>DrawLine</c>s; splitting them so the image could
///         be <c>Static</c> would be contortion for no gain, because the grid dominates and only ever
///         draws when the image is absent.
///     </para>
/// </summary>
public sealed class RadarLayer : ISceneLayer
{
    /// <summary>Above this edge length the resample is an upscale, and caching it would waste memory.</summary>
    private const int MaxScaledEdge = 8192;

    private readonly SKPaint _image;
    private readonly SKPaint _major;
    private readonly SKPaint _minor;
    private readonly SKPaint _resample;

    // How the resample intermediate is obtained. A field rather than a direct SKSurface.Create call so
    // the failure branch in ScaledFor is REACHABLE from a test: Skia decides on its own when an
    // allocation is too large, and a suite that cannot make it say no cannot prove what happens next.
    private Func<SKImageInfo, SKSurface?> _surfaceFactory = static info => SKSurface.Create(info);

    private SKImage? _scaled;
    private SKImage? _scaledFrom;
    private int _scaledHeight;
    private int _scaledWidth;
    private bool _useRadarImage = true;

    /// <summary>Creates the layer.</summary>
    public RadarLayer()
    {
        _image = new SKPaint
        {
            // The pre-v2 draw was PushOpacity(0.9) + DrawImage. In Skia that is a white paint at the
            // same alpha, because DrawImage multiplies the image by the paint's colour.
            Color = new SKColor(255, 255, 255, (byte)(SceneDefaults.RadarOpacity * 255)),
            // SkiaSharp 2.88.9 predates SKSamplingOptions — sampling is a paint property here. High is
            // not a default-by-habit: measured against the pre-v2 golden, it is the closest match of the
            // four (93.1% of pixels within ±1, versus 78.9% for Medium/Low and 76.5% for None), matching
            // how Avalonia's DrawImage resamples. Changing it re-baselines every radar golden.
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true
        };

        // The resample into the cached image. Opaque white and full alpha, because the opacity in _image
        // must be applied ONCE, at the final draw — baking it in here and multiplying again there would
        // render the radar at 0.81 opacity instead of 0.9.
        _resample = new SKPaint
        {
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true
        };

        _minor = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        _major = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
    }

    /// <summary>
    ///     Whether to draw the baked radar image. False falls back to the grid — the pre-v2
    ///     <c>ShowRadar</c> toggle, which was never "hide the underlay" but always "picture or grid",
    ///     which is why it is a property here rather than <see cref="IsEnabled" />.
    /// </summary>
    public bool UseRadarImage
    {
        get => _useRadarImage;
        set
        {
            if (_useRadarImage == value)
            {
                return;
            }

            _useRadarImage = value;
            ContentVersion++;
        }
    }

    /// <summary>
    ///     World rectangle the radar images span, when the frame does not carry it (a fixture with no
    ///     radar metadata). Normally resolved from <c>frame.Map.Radars</c>.
    /// </summary>
    public WorldBounds? RadarBoundsOverride { get; set; }

    /// <summary>
    ///     Whether the resampled radar is cached at its on-screen size instead of being re-resampled on
    ///     every draw. Off by default; <c>SceneExportSession</c> turns it on.
    /// </summary>
    /// <remarks>
    ///     The cached path resamples into a whole-pixel intermediate and then blits, where the direct path
    ///     resamples once into a fractional rectangle — mathematically close but not identical, so an
    ///     interactive frame (which has budget to spare and must not move a golden) leaves it off, while an
    ///     export (which renders thousands of frames back to back) turns it on.
    ///     <para>
    ///         Exists because <c>LayerCacheHint.PerCamera</c> caches the picture, not its pixels: replaying
    ///         it re-runs the bicubic resample every frame, and on a ~2 000 px bundle layer at
    ///         <see cref="SKFilterQuality.High" /> that one <c>DrawImage</c> was five sixths of the export
    ///         frame budget. Caching the resample instead costs one image per pane per camera epoch.
    ///     </para>
    /// </remarks>
    public bool CacheScaledImage { get; set; }

    /// <summary>
    ///     Test seam: how <see cref="ScaledFor" /> obtains its resample intermediate. Returning null (or
    ///     throwing) exercises its fault path — see that method's own doc.
    /// </summary>
    /// <param name="factory">The replacement factory. Null restores <c>SKSurface.Create</c>.</param>
    internal void SetSurfaceFactoryForTest(Func<SKImageInfo, SKSurface?>? factory) =>
        _surfaceFactory = factory ?? (static info => SKSurface.Create(info));

    /// <summary>The size the live resample cache describes, or (0,0) when it holds nothing. Test hook.</summary>
    internal (int Width, int Height) ScaledCacheSizeForTest => _scaled is null ? (0, 0)
        : (_scaledWidth, _scaledHeight);

    /// <inheritdoc />
    public string Id => SceneLayerIds.Radar;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Underlay;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.PerCamera;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion { get; private set; }

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (_useRadarImage && TryDrawRadar(canvas, ctx))
        {
            return;
        }

        DrawGrid(canvas, ctx);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _image.Dispose();
        _minor.Dispose();
        _major.Dispose();
        _resample.Dispose();
        DropScaled();
    }

    // Placed via the bundle's world bounds through the shared transform. The overview txt's rotate and
    // zoom are in-game minimap-widget hints and are deliberately NOT applied — verified that dust2
    // (rotate=1, zoom=1.1) aligns correctly with pos/scale alone (parity invariant 9).
    private bool TryDrawRadar(SKCanvas canvas, in SceneRenderContext ctx)
    {
        if (ctx.Pane.Level?.Radar is not { } image)
        {
            return false;
        }

        if (ResolveBounds(ctx) is not { } bounds)
        {
            return false;
        }

        // Top-left pixel is world (MinX, MaxY); bottom-right is (MaxX, MinY) — Y is inverted by the
        // transform. Computed in SCREEN space, exactly as line 1081, so the image is never sampled
        // under a world matrix.
        (double x0, double y0) = ctx.Transform.WorldToScreen(bounds.MinX, bounds.MaxY);
        (double x1, double y1) = ctx.Transform.WorldToScreen(bounds.MaxX, bounds.MinY);
        SKRect destination = new((float)x0, (float)y0, (float)x1, (float)y1);

        if (!CacheScaledImage)
        {
            canvas.DrawImage(image, destination, _image);
            return true;
        }

        SKImage scaled = ScaledFor(image, destination);
        if (scaled.Width == (int)MathF.Round(destination.Width) &&
            scaled.Height == (int)MathF.Round(destination.Height))
        {
            // 1:1. Drawing by ORIGIN rather than into a rectangle is what makes it a blit: a destination
            // rectangle whose width is 1234.7 against a 1235 px image is still a resample, and a resample
            // is the whole cost this cache exists to remove.
            canvas.DrawImage(scaled, destination.Left, destination.Top, _image);
            return true;
        }

        canvas.DrawImage(scaled, destination, _image);
        return true;
    }

    /// <summary>
    ///     The radar, already resampled to <paramref name="destination" />'s size. The subsequent draw is
    ///     then a 1:1 blit, which <see cref="SKFilterQuality.High" /> short-circuits.
    ///     <para>
    ///         Keyed on the source image identity and the destination size rounded to whole pixels: a
    ///         camera that has not moved re-uses the resample, and one that has pays for it once. Sizes
    ///         are quantised because a sub-pixel change in the destination is invisible and re-resampling
    ///         for it would defeat the cache during a pan.
    ///     </para>
    ///     <para>
    ///         <b>The cache is forgotten before the replacement is attempted, not after it succeeds.</b>
    ///         Disposing <c>_scaled</c> while <c>_scaledFrom</c>/<c>_scaledWidth</c>/<c>_scaledHeight</c>
    ///         still described it would leave the hit branch above trusting a dead handle if anything
    ///         between dispose and reassignment failed — a null from <c>SKSurface.Create</c> or a throw
    ///         out of the resample. Handing a disposed <see cref="SKImage" /> to <c>DrawImage</c> is an
    ///         access violation inside Skia, not an exception the frame loop can catch. Falling back to
    ///         <paramref name="source" /> draws the right pixels by the un-cached route; there is no
    ///         wrong-pixels branch here.
    ///     </para>
    /// </summary>
    private SKImage ScaledFor(SKImage source, SKRect destination)
    {
        int width = Math.Max(1, (int)MathF.Round(destination.Width));
        int height = Math.Max(1, (int)MathF.Round(destination.Height));

        if (_scaled is not null && ReferenceEquals(_scaledFrom, source) &&
            _scaledWidth == width && _scaledHeight == height)
        {
            return _scaled;
        }

        // An oversized target would cost more than it saves: at that point the source is already smaller
        // than the destination and the resample is an upscale Skia does cheaply.
        if (width > MaxScaledEdge || height > MaxScaledEdge)
        {
            return source;
        }

        DropScaled();

        using SKSurface? surface = _surfaceFactory(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return source; // no intermediate this frame — the direct resample is correct, only slower.
        }

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(source, new SKRect(0, 0, width, height), _resample);

        if (surface.Snapshot() is not { } snapshot)
        {
            return source;
        }

        _scaled = snapshot;
        _scaledFrom = source;
        _scaledWidth = width;
        _scaledHeight = height;
        return _scaled;
    }

    // Disposes the cached resample and forgets everything that describes it, in that order and with no
    // step between them that can fail. Every field the hit branch reads is cleared, not just the handle.
    private void DropScaled()
    {
        _scaled?.Dispose();
        _scaled = null;
        _scaledFrom = null;
        _scaledWidth = 0;
        _scaledHeight = 0;
    }

    private WorldBounds? ResolveBounds(in SceneRenderContext ctx)
    {
        IReadOnlyList<MapRadarImage> radars = ctx.Frame.Map.Radars;
        string? name = ctx.Pane.Level?.RadarImageName;

        for (int i = 0; i < radars.Count; i++)
        {
            if (name is not null && string.Equals(radars[i].Name, name, StringComparison.Ordinal))
            {
                return radars[i].Bounds;
            }
        }

        // Every layer of one bundle shares the same world rectangle, so an unmatched name still gets
        // the right placement from any entry.
        return radars.Count > 0 ? radars[0].Bounds : RadarBoundsOverride;
    }

    private void DrawGrid(SKCanvas canvas, in SceneRenderContext ctx)
    {
        ViewportTransform t = ctx.Transform;
        SKRect bounds = ctx.PaneBounds;

        (double wx0, double wy1) = t.ScreenToWorld(0, 0);
        (double wx1, double wy0) = t.ScreenToWorld(bounds.Width, bounds.Height);

        const double step = SceneDefaults.GridStepWorld;
        double startX = Math.Floor(wx0 / step) * step;
        double endX = wx1;
        double startY = Math.Floor(wy0 / step) * step;
        double endY = wy1;

        // Guard against an absurd line count when zoomed all the way out. A negative count means an
        // inverted or degenerate transform, which is equally a bail-out.
        int countX = (int)((endX - startX) / step);
        int countY = (int)((endY - startY) / step);
        if (countX > SceneDefaults.MaxGridLines || countY > SceneDefaults.MaxGridLines ||
            countX < 0 || countY < 0)
        {
            return;
        }

        _minor.Color = ctx.Palette.MinorGrid;
        _minor.StrokeWidth = ctx.Palette.Strokes.MinorGrid;
        _major.Color = ctx.Palette.MajorGrid;
        _major.StrokeWidth = ctx.Palette.Strokes.MajorGrid;

        for (double wx = startX; wx <= endX; wx += step)
        {
            (double sx, _) = t.WorldToScreen(wx, 0);
            bool major = Math.Abs(wx) < 1e-3; // the major line is the AXIS, not every nth line
            canvas.DrawLine((float)sx, 0, (float)sx, bounds.Height, major ? _major : _minor);
        }

        for (double wy = startY; wy <= endY; wy += step)
        {
            (_, double sy) = t.WorldToScreen(0, wy);
            bool major = Math.Abs(wy) < 1e-3;
            canvas.DrawLine(0, (float)sy, bounds.Width, (float)sy, major ? _major : _minor);
        }
    }
}

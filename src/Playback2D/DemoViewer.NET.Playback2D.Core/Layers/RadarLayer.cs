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
///         <b>Cached <c>PerCamera</c>, both halves in one picture</b> (plan decision D-5). The radar is
///         a single <c>DrawImage</c> and the grid is up to 800 <c>DrawLine</c>s; splitting them so the
///         image could be <c>Static</c> would be contortion for no gain, because the grid dominates and
///         only ever draws when the image is absent.
///     </para>
/// </summary>
public sealed class RadarLayer : ISceneLayer
{
    private readonly SKPaint _image;
    private readonly SKPaint _major;
    private readonly SKPaint _minor;
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
            // four (93.1% of pixels within ±1, versus 78.9% for Medium/Low and 76.5% for None), which
            // says Avalonia's DrawImage resamples the same way. Changing it re-baselines every radar
            // golden — see docs/playback2d-v2/plans/B1-text-metrics-review.md.
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
        // under a world matrix (risk R4 never arises).
        (double x0, double y0) = ctx.Transform.WorldToScreen(bounds.MinX, bounds.MaxY);
        (double x1, double y1) = ctx.Transform.WorldToScreen(bounds.MaxX, bounds.MinY);
        canvas.DrawImage(image, new SKRect((float)x0, (float)y0, (float)x1, (float)y1), _image);
        return true;
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

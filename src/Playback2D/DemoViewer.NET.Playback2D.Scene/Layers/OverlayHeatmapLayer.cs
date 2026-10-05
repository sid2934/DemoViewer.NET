#region

using System.Runtime.InteropServices;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The Overlay View: every point of an <see cref="OverlayDocument" /> splatted onto the pane as a
///     density wash, CT-tinted where CT stood, T-tinted where T stood, and blended where both did.
///     A pane draws only the points whose Z belongs on its floor, so a stacked layout shows each
///     storey's own heat and a lower Ramp never bleeds through the site above it.
///     <para>
///         <b>Rasterised, not stroked.</b> A result set of forty rounds is about twenty thousand
///         points; twenty thousand blurred discs per pane per frame is not a budget. The layer bins
///         the points into a grid of <see cref="CellPx" />-pixel cells with a Gaussian kernel, turns the
///         grid into one small bitmap and draws that once, linearly sampled up to the pane. The
///         kernel radius is in world units, so a spot is the same size on the map at every zoom, and
///         the wash is normalised to the pane's own peak: the hottest cell on each floor is fully
///         opaque and everything else is relative to it, which is what "where do they stand most"
///         asks.
///     </para>
///     <para>
///         <b>Deterministic.</b> The grid is accumulated in document order with the same kernel every
///         time, so one document under one camera is one set of bytes: a fixture's golden holds.
///         <see cref="LayerCacheHint.PerCamera" /> means that work is paid once per camera or content
///         change and replayed from the compositor's picture cache otherwise.
///     </para>
///     <para>
///         <b>No text.</b> The counts belong to the results header, which is Avalonia text and never in
///         a golden, so an overlay fixture is judged at the unrelaxed pixel gate.
///     </para>
/// </summary>
public sealed class OverlayHeatmapLayer : ISceneLayer
{
    /// <summary>Kernel radius in world units: about three player widths, the spot a stationary player warms.</summary>
    public const float KernelRadiusWorld = 96f;

    /// <summary>The kernel never shrinks below this on screen, so a whole-map overview still shows blobs rather than specks.</summary>
    public const float MinKernelRadiusPx = 4f;

    /// <summary>Nor grows past this, so a close zoom does not turn one player into a pane-wide fog.</summary>
    public const float MaxKernelRadiusPx = 48f;

    /// <summary>Pane pixels per grid cell. Two keeps a 1080p pane at half a million cells and the wash smooth after sampling.</summary>
    public const int CellPx = 2;

    /// <summary>The alpha of the pane's hottest cell.</summary>
    public const float PeakAlpha = 0.85f;

    // The exponent that lifts the low end: with a linear ramp a cell at a tenth of the peak is nearly
    // invisible, and a tenth of forty rounds is still a position worth seeing.
    private const float RampExponent = 0.6f;

    // Below this alpha a cell is not written at all, so the kernel's Gaussian tail does not tint the
    // whole map a faint colour around every point.
    private const float FloorAlpha = 0.02f;

    private readonly OverlayDocument _document;
    private readonly SKPaint _paint;
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>Creates the layer over a document. The document is shared with its filler and not owned.</summary>
    /// <param name="document">The points to draw.</param>
    public OverlayHeatmapLayer(OverlayDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _paint = new SKPaint
        {
            IsAntialias = false
        };
    }

    /// <summary>The document being drawn.</summary>
    public OverlayDocument Document => _document;

    /// <inheritdoc />
    public string Id => SceneLayerIds.Overlay;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    /// <remarks>Above the ink (100) and below the query tokens (120): a token the user is dragging must sit over the heat it is being placed on.</remarks>
    public int Order => 110;

    /// <inheritdoc />
    /// <remarks>The wash is a function of the camera and the document and nothing else; recorded once per change and replayed.</remarks>
    public LayerCacheHint Cache => LayerCacheHint.PerCamera;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => _document.Version;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        IReadOnlyList<OverlayPoint> points = _document.Points;
        if (points.Count == 0)
        {
            return;
        }

        float width = ctx.PaneBounds.Width;
        float height = ctx.PaneBounds.Height;
        if (width < 1 || height < 1)
        {
            return;
        }

        int gridWidth = (int)Math.Ceiling(width / CellPx);
        int gridHeight = (int)Math.Ceiling(height / CellPx);
        float radiusPx = Math.Clamp((float)(KernelRadiusWorld * ctx.Transform.EffectiveScale),
            MinKernelRadiusPx, MaxKernelRadiusPx);
        float radius = radiusPx / CellPx;
        int reach = (int)Math.Ceiling(radius);
        // Two sigma at the radius: the kernel is near zero at its edge rather than cut off there.
        float sigma = radius / 2f;
        float twoSigmaSquared = 2f * sigma * sigma;

        float[] ct = new float[gridWidth * gridHeight];
        float[] t = new float[gridWidth * gridHeight];
        bool any = false;
        for (int i = 0; i < points.Count; i++)
        {
            OverlayPoint point = points[i];
            if (!ctx.BelongsHere(point.WorldZ))
            {
                continue;
            }

            (double sx, double sy) = ctx.Transform.WorldToScreen(point.WorldX, point.WorldY);
            float gx = (float)(sx / CellPx);
            float gy = (float)(sy / CellPx);
            if (gx < -reach || gy < -reach || gx > gridWidth + reach || gy > gridHeight + reach)
            {
                continue; // wholly off the pane, kernel included
            }

            Splat(point.Side == QuerySide.Ct ? ct : t, gridWidth, gridHeight, gx, gy, reach, twoSigmaSquared);
            any = true;
        }

        if (!any)
        {
            return;
        }

        float peak = 0f;
        for (int i = 0; i < ct.Length; i++)
        {
            peak = Math.Max(peak, ct[i] + t[i]);
        }

        if (peak <= 0f)
        {
            return;
        }

        SKColor ctColour = ctx.Palette.TeamCt;
        SKColor tColour = ctx.Palette.TeamT;
        byte[] pixels = new byte[gridWidth * gridHeight * 4];
        for (int i = 0; i < ct.Length; i++)
        {
            float total = ct[i] + t[i];
            if (total <= 0f)
            {
                continue;
            }

            float alpha = MathF.Pow(total / peak, RampExponent) * PeakAlpha;
            if (alpha < FloorAlpha)
            {
                continue;
            }

            // The tint is the two sides' share of the cell: a CT-only cell is the CT colour, a T-only
            // cell the T colour, and a contested cell sits between them.
            float share = ct[i] / total;
            int at = i * 4;
            pixels[at] = Lerp(tColour.Red, ctColour.Red, share);
            pixels[at + 1] = Lerp(tColour.Green, ctColour.Green, share);
            pixels[at + 2] = Lerp(tColour.Blue, ctColour.Blue, share);
            pixels[at + 3] = (byte)Math.Round(alpha * 255f);
        }

        SKImageInfo info = new(gridWidth, gridHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap bitmap = new(info);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        // The image is a copy the recording owns from here; the compositor may replay the picture
        // long after this bitmap is gone.
        using SKImage image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(0, 0, gridWidth * CellPx, gridHeight * CellPx), _sampling, _paint);
    }

    /// <inheritdoc />
    public void Dispose() => _paint.Dispose();

    // Adds one Gaussian kernel centred on a grid position, clipped to the grid.
    private static void Splat(float[] grid, int gridWidth, int gridHeight, float gx, float gy, int reach,
        float twoSigmaSquared)
    {
        int minX = Math.Max(0, (int)MathF.Floor(gx) - reach);
        int maxX = Math.Min(gridWidth - 1, (int)MathF.Ceiling(gx) + reach);
        int minY = Math.Max(0, (int)MathF.Floor(gy) - reach);
        int maxY = Math.Min(gridHeight - 1, (int)MathF.Ceiling(gy) + reach);
        float reachSquared = (float)reach * reach;

        for (int y = minY; y <= maxY; y++)
        {
            float dy = y + 0.5f - gy;
            int row = y * gridWidth;
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x + 0.5f - gx;
                float d2 = dx * dx + dy * dy;
                if (d2 > reachSquared)
                {
                    continue;
                }

                grid[row + x] += MathF.Exp(-d2 / twoSigmaSquared);
            }
        }
    }

    private static byte Lerp(byte from, byte to, float share) =>
        (byte)Math.Round(from + (to - from) * share);
}

#region

using System.Numerics;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     Draws, per pane, the boundary edges of every place on that pane's floor and a label at each
///     place's area-weighted centroid. Opt-in and off by default, the same class as the ink.
///     <para>
///         <b>Two halves, like the ink layer.</b> The outline geometry is a pure function of
///         <c>(EffectiveVersion, floorKey)</c>, so it is recorded once per floor into a WORLD-space
///         <see cref="SKPicture" /> and replayed under the pane's camera; the labels are screen-space
///         text drawn per frame, because a glyph recorded in world space would scale and flip with the
///         camera. The compositor hint is therefore <see cref="LayerCacheHint.Dynamic" />: the static
///         half is cached here, keyed exactly as the design asks, and the compositor's own picture cache
///         is not asked to record text.
///     </para>
///     <para>
///         Custom zones draw in a distinct style (the T-side colour, dashed) so an author can check a
///         polygon they wrote against the radar; baked places draw hairline in the label colour.
///     </para>
/// </summary>
public sealed class ZoneOutlineLayer : ISceneLayer
{
    private const float LabelSize = SceneDefaults.FloorLabelSize;

    // World-unit dash so the custom style survives a zoom: at fit scale on a CS2 map (about 0.15 px per
    // unit) this is a 6 px dash and a 3 px gap.
    private static readonly float[] _customDash = [40f, 20f];
    private static readonly SKRect _worldCull = new(-32768, -32768, 32768, 32768);

    private readonly SKPaint _baked;
    private readonly SKPaint _custom;
    private readonly SKPaint _label;
    private readonly bool _ownsText;
    private readonly TextBlobCache _text;
    private readonly Dictionary<double, SKPicture> _pictures = new();
    private readonly Lock _pictureLock = new();

    private bool _disposed;
    private PlaceResolver? _resolver;

    /// <summary>Creates the layer.</summary>
    /// <param name="resolver">The resolver to draw, or null to draw nothing until one is set.</param>
    /// <param name="text">The shared blob cache. A private one when null, disposed with the layer.</param>
    public ZoneOutlineLayer(PlaceResolver? resolver = null, TextBlobCache? text = null)
    {
        _resolver = resolver;
        _ownsText = text is null;
        _text = text ?? new TextBlobCache();

        // Hairline: width 0 draws one device pixel under any matrix, which is what an outline replayed
        // under the camera needs. A world-unit width would thicken with every zoom step.
        _baked = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0,
            IsAntialias = true
        };
        _custom = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(_customDash, 0)
        };
        _label = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <summary>
    ///     The resolver whose set is drawn. Setting a different one drops every cached picture and
    ///     bumps <see cref="ContentVersion" />; the same instance is a no-op. Null draws nothing.
    /// </summary>
    public PlaceResolver? Resolver
    {
        get => _resolver;
        set
        {
            if (ReferenceEquals(_resolver, value))
            {
                return;
            }

            _resolver = value;
            ClearPictures();
            ContentVersion++;
        }
    }

    /// <summary>Whether labels are drawn. Off leaves the outlines alone; a golden attribution hook.</summary>
    public bool DrawLabels { get; set; } = true;

    /// <summary>How many world-space pictures have been recorded. Test hook.</summary>
    public int PictureRecordCount { get; private set; }

    /// <inheritdoc />
    public string Id => SceneLayerIds.Zones;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order => 90;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

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

        if (_disposed || _resolver is not { } resolver)
        {
            return;
        }

        _baked.Color = ctx.Palette.Label.WithAlpha(0xA0);
        _custom.Color = ctx.Palette.TeamT;

        SKMatrix matrix = ViewportMatrix.From(ctx.Transform);
        foreach (double floorKey in FloorsFor(in ctx, resolver))
        {
            IReadOnlyList<PlaceOutline> outlines = resolver.OutlinesFor(floorKey);
            if (outlines.Count == 0)
            {
                continue;
            }

            canvas.DrawPicture(PictureFor(floorKey, outlines), in matrix);

            if (DrawLabels)
            {
                DrawLabelsFor(canvas, in ctx, outlines);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPictures();
        _baked.Dispose();
        _custom.Dispose();
        _label.Dispose();
        if (_ownsText)
        {
            _text.Dispose();
        }
    }

    // A multi-level pane draws its own floor; a pane showing every level at once (a fixture render with
    // no level set, or the single-pane layout) draws them all. The key is the pane's band lower bound
    // quantized exactly as the baker keyed the areas, so the two agree by construction.
    private static double[] FloorsFor(in SceneRenderContext ctx, PlaceResolver resolver)
    {
        if (!ctx.IsSingleLevel && ctx.Pane.Level is { } level)
        {
            return [MapSpace.QuantizeZ(level.ZMin)];
        }

        double[] keys = new double[resolver.Zones.Floors.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = resolver.Zones.Floors[i].Key;
        }

        return keys;
    }

    private SKPicture PictureFor(double floorKey, IReadOnlyList<PlaceOutline> outlines)
    {
        lock (_pictureLock)
        {
            if (_pictures.TryGetValue(floorKey, out SKPicture? cached))
            {
                return cached;
            }

            using SKPictureRecorder recorder = new();
            SKCanvas recording = recorder.BeginRecording(_worldCull);
            using SKPath path = new();

            foreach (PlaceOutline outline in outlines)
            {
                path.Reset();
                foreach ((Vector2 a, Vector2 b) in outline.Edges)
                {
                    path.MoveTo(a.X, a.Y);
                    path.LineTo(b.X, b.Y);
                }

                recording.DrawPath(path, outline.Origin == PlaceOrigin.Custom ? _custom : _baked);
            }

            SKPicture picture = recorder.EndRecording();
            _pictures[floorKey] = picture;
            PictureRecordCount++;
            return picture;
        }
    }

    private void DrawLabelsFor(SKCanvas canvas, in SceneRenderContext ctx, IReadOnlyList<PlaceOutline> outlines)
    {
        SKRect bounds = ctx.PaneBounds;
        foreach (PlaceOutline outline in outlines)
        {
            if (outline.Name.Length == 0 || outline.Edges.Count == 0)
            {
                continue;
            }

            (double sx, double sy) = ctx.Transform.WorldToScreen(outline.LabelAt.X, outline.LabelAt.Y);
            if (sx < bounds.Left || sx > bounds.Right || sy < bounds.Top || sy > bounds.Bottom)
            {
                continue;
            }

            if (_text.Get(outline.Name, LabelSize) is not { } shaped)
            {
                continue;
            }

            _label.Color = outline.Origin == PlaceOrigin.Custom ? ctx.Palette.TeamT : ctx.Palette.Label;
            (float ox, float oy) = shaped.OriginForCentre((float)sx, (float)sy);
            canvas.DrawText(shaped.Blob, ox, oy, _label);
        }
    }

    private void ClearPictures()
    {
        lock (_pictureLock)
        {
            foreach (SKPicture picture in _pictures.Values)
            {
                picture.Dispose();
            }

            _pictures.Clear();
        }
    }
}

#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     Owns the layer stack, its draw order, and the picture caches. Layers are kept sorted by
///     <c>(Slot, Order, Id)</c> — <c>Id</c> is the final tiebreaker rather than insertion order so the
///     sequence is a pure function of the registered set, and a golden image cannot silently depend on
///     registration timing.
///     <para>
///         <b>Two render entry points, one code path.</b> <see cref="Render(SKCanvas, in SceneRenderContext)" />
///         draws one pane the caller has already framed; <see cref="Render(SKCanvas, in SceneSubmission)" />
///         draws a whole multi-pane submission including the background fill and the band dividers.
///         Both funnel into the same per-layer cache logic, so an offscreen golden and an on-screen frame
///         cannot diverge.
///     </para>
/// </summary>
public sealed class SceneCompositor : IDisposable
{
    private readonly SKPaint _background;
    private readonly LayerPictureCache _cache;
    private readonly SKPaint _divider;
    private readonly List<ISceneLayer> _layers = [];
    private readonly SceneCompositorOptions _options;
    private bool _disposed;
    private int _layersRendered;
    private int _panesRendered;

    /// <summary>Creates a compositor.</summary>
    /// <param name="options">Caching policy; the defaults when null.</param>
    public SceneCompositor(SceneCompositorOptions? options = null)
    {
        _options = options ?? new SceneCompositorOptions();
        _cache = new LayerPictureCache(_options.MaxCachedPictures);

        // Owned and mutated in place rather than constructed per frame. An SKPaint is a managed
        // wrapper over native state, so one per frame is both a heap allocation and a native
        // allocation — and the §6 budget is zero bytes.
        _background = new SKPaint
        {
            Style = SKPaintStyle.Fill
        };
        _divider = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
    }

    /// <summary>The registered layers in draw order.</summary>
    public IReadOnlyList<ISceneLayer> Layers => _layers;

    /// <summary>
    ///     The host's render gate, when there is one. Left null by single-threaded consumers (export,
    ///     the CLI, tests); set by <c>Scene2DHost</c>, and then every cache mutation debug-asserts that
    ///     the caller holds it (plan §5.8).
    /// </summary>
    public SceneRenderGate? Gate { get; set; }

    /// <summary>Counters from the last completed render. Diagnostics and the bench harness.</summary>
    public SceneCompositorStats Stats { get; private set; }

    /// <summary>Disposes every registered layer and every cached picture. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Dispose();
        _background.Dispose();
        _divider.Dispose();
        foreach (ISceneLayer layer in _layers)
        {
            layer.Dispose();
        }

        _layers.Clear();
    }

    /// <summary>Registers a layer and re-sorts the stack.</summary>
    /// <param name="layer">The layer to add.</param>
    /// <exception cref="ArgumentException">A layer with the same <see cref="ISceneLayer.Id" /> is registered.</exception>
    public void Add(ISceneLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

        if (Find(layer.Id) is not null)
        {
            throw new ArgumentException($"A layer with id '{layer.Id}' is already registered.", nameof(layer));
        }

        _layers.Add(layer);
        _layers.Sort(CompareLayers);
    }

    /// <summary>Removes and disposes the layer with this id. Returns false when it was not registered.</summary>
    /// <param name="layerId">The layer's stable id.</param>
    public bool Remove(string layerId)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            if (!string.Equals(_layers[i].Id, layerId, StringComparison.Ordinal))
            {
                continue;
            }

            ISceneLayer removed = _layers[i];
            _layers.RemoveAt(i);
            removed.Dispose();
            InvalidateCaches();
            return true;
        }

        return false;
    }

    /// <summary>The layer with this id, or null.</summary>
    /// <param name="layerId">The layer's stable id.</param>
    public ISceneLayer? Find(string layerId)
    {
        foreach (ISceneLayer layer in _layers)
        {
            if (string.Equals(layer.Id, layerId, StringComparison.Ordinal))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>Enables or disables one layer. A no-op when the id is not registered.</summary>
    /// <param name="layerId">The layer's stable id.</param>
    /// <param name="enabled">The new enabled state.</param>
    public void SetEnabled(string layerId, bool enabled)
    {
        if (Find(layerId) is { } layer)
        {
            layer.IsEnabled = enabled;
        }
    }

    /// <summary>
    ///     Advances every enabled layer. Returns the OR of their results — true means at least one layer
    ///     is still animating, so the caller keeps the self-terminating render loop armed. Every layer is
    ///     advanced even once one has returned true, because Advance is where they mutate.
    /// </summary>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="frame">The frame being advanced to.</param>
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        if (_disposed)
        {
            return false;
        }

        bool keepArmed = false;
        foreach (ISceneLayer layer in _layers)
        {
            if (layer.IsEnabled)
            {
                keepArmed |= layer.Advance(in time, frame);
            }
        }

        return keepArmed;
    }

    /// <summary>
    ///     Draws every enabled layer into one already-framed pane. The caller owns the clip, the
    ///     translation and the background.
    /// </summary>
    /// <param name="canvas">The pane's canvas.</param>
    /// <param name="ctx">The pane's render context.</param>
    public void Render(SKCanvas canvas, in SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        _layersRendered = 0;
        _panesRendered = 0;

        if (_disposed)
        {
            PublishStats();
            return;
        }

        RenderPane(canvas, in ctx);
        _panesRendered = 1;
        PublishStats();
    }

    /// <summary>
    ///     Draws a whole submission: background fill → each pane (clipped and translated) → band
    ///     dividers. This is the on-screen and export path, and the only one that reproduces the pre-v2
    ///     multi-band layout.
    /// </summary>
    /// <param name="canvas">The host canvas, in host coordinates.</param>
    /// <param name="submission">Everything captured on the UI thread for this frame.</param>
    public void Render(SKCanvas canvas, in SceneSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        _layersRendered = 0;
        _panesRendered = 0;

        // A frame already queued on the render thread when the host tore down arrives here AFTER
        // Dispose. The gate serializes the two but does not order them, and _background/_divider are
        // compositor-owned SKPaints whose native handles are gone by then — writing through one is an
        // access violation, not an exception. Drop the frame instead; there is nothing left to draw.
        if (_disposed)
        {
            PublishStats();
            return;
        }

        // 1. Background. The pre-v2 control filled the whole control before laying out bands, so a
        //    fractional-pixel seam between bands shows the background rather than stale pixels.
        _background.Color = submission.Palette.Background;
        canvas.DrawRect(submission.HostBounds, _background);

        IReadOnlyList<LevelPaneSnapshot> panes = submission.Panes;
        if (panes.Count == 0 || submission.HostBounds.Width < 1 || submission.HostBounds.Height < 1)
        {
            PublishStats();
            return;
        }

        // "One pane" and "one level" were the same statement until B3: the pre-v2 control drew every
        // player regardless of Z when there was a single band, and that is parity invariant 1. Under
        // SingleLayout a single pane shows ONE of several levels, and drawing the other floor's players
        // into it would be the very confusion the mode exists to remove — so the sentinel is now
        // "a lone pane over a map that has no other floor".
        bool single = panes.Count == 1 && (submission.Levels?.Levels.Count ?? 1) <= 1;

        // 2. Panes.
        for (int i = 0; i < panes.Count; i++)
        {
            LevelPaneSnapshot pane = panes[i];
            SKRect rect = pane.ViewportRect;

            int save = canvas.Save();
            canvas.ClipRect(rect);
            canvas.Translate(rect.Left, rect.Top);

            SceneRenderContext ctx = new(
                submission.Frame,
                submission.Time,
                pane.Transform,
                new SKRect(0, 0, rect.Width, rect.Height),
                single ? -1 : pane.LevelIndex, // parity invariant 1: -1 is a sentinel, not an index
                pane.Level.ZMin,
                pane.Level.ZMax,
                submission.Purpose,
                submission.Palette,
                submission.RenderScaling)
            {
                Pane = pane,
                Levels = submission.Levels
            };

            RenderPane(canvas, in ctx);
            canvas.RestoreToCount(save);
            _panesRendered++;
        }

        // 3. Band dividers, in HOST coordinates — chrome between panes, not a layer (plan §3.1). The
        //    pre-v2 rule is "every band except the topmost", i.e. every band whose top edge is not the
        //    control's own top edge.
        if (!single)
        {
            _divider.Color = submission.Palette.MajorGrid;
            _divider.StrokeWidth = submission.Palette.Strokes.MajorGrid;

            for (int i = 0; i < panes.Count; i++)
            {
                float top = panes[i].ViewportRect.Top;
                if (top <= submission.HostBounds.Top + 0.001f)
                {
                    continue;
                }

                canvas.DrawLine(submission.HostBounds.Left, top, submission.HostBounds.Right, top, _divider);
            }
        }

        PublishStats();
    }

    /// <summary>Drops every cached picture. For a theme change, a resize, or a layer swap.</summary>
    public void InvalidateCaches()
    {
        Debug.Assert(Gate is null || Gate.IsHeld, "compositor cache mutated outside the render gate");
        _cache.Clear();
    }

    /// <summary>Drops the cached pictures recorded for one level.</summary>
    /// <param name="levelId">The vanished (or rebuilt) level.</param>
    public void InvalidatePaneCaches(MapLevelId levelId)
    {
        Debug.Assert(Gate is null || Gate.IsHeld, "compositor cache mutated outside the render gate");
        _cache.InvalidatePane(levelId);
    }

    private void RenderPane(SKCanvas canvas, in SceneRenderContext ctx)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            ISceneLayer layer = _layers[i];
            if (!layer.IsEnabled)
            {
                continue;
            }

            RenderLayer(canvas, layer, in ctx);
            _layersRendered++;
        }
    }

    private void RenderLayer(SKCanvas canvas, ISceneLayer layer, in SceneRenderContext ctx)
    {
        if (!_options.EnablePictureCaching || layer.Cache == LayerCacheHint.Dynamic)
        {
            layer.Render(canvas, ctx);
            return;
        }

        bool perCamera = layer.Cache == LayerCacheHint.PerCamera;
        LayerPictureCache.Key key = new(
            ctx.Pane.LevelId,
            layer.Id,
            layer.ContentVersion,
            perCamera ? ctx.Pane.CameraEpoch : 0);

        SKPicture? picture = _cache.Get(in key);
        if (picture is null)
        {
            Debug.Assert(Gate is null || Gate.IsHeld, "compositor cache mutated outside the render gate");

            using SKPictureRecorder recorder = new();
            // A PerCamera recording is in PANE-LOCAL SCREEN space, so its cull rect is the pane. A
            // Static recording is in WORLD space, and its extent is not knowable up front — Skia treats
            // an oversized cull rect as a hint, so a generous one is correct rather than wasteful.
            SKRect cull = perCamera ? ctx.PaneBounds : WorldCullRect;
            SKCanvas recording = recorder.BeginRecording(cull);
            layer.Render(recording, ctx);
            picture = recorder.EndRecording();
            _cache.Put(in key, picture);
        }

        if (perCamera)
        {
            canvas.DrawPicture(picture);
            return;
        }

        SKMatrix matrix = ViewportMatrix.From(ctx.Transform);
        canvas.DrawPicture(picture, ref matrix);
    }

    // Generous world-space cull for Static recordings: CS2 maps live well inside ±32768 world units.
    private static readonly SKRect WorldCullRect = new(-32768, -32768, 32768, 32768);

    private void PublishStats() =>
        Stats = new SceneCompositorStats(_layersRendered, _cache.Recorded, _cache.Replayed, _panesRendered);

    private static int CompareLayers(ISceneLayer a, ISceneLayer b)
    {
        int bySlot = ((int)a.Slot).CompareTo((int)b.Slot);
        if (bySlot != 0)
        {
            return bySlot;
        }

        int byOrder = a.Order.CompareTo(b.Order);
        return byOrder != 0 ? byOrder : string.CompareOrdinal(a.Id, b.Id);
    }
}

/// <summary>Caching policy for a <see cref="SceneCompositor" />.</summary>
/// <param name="EnablePictureCaching">
///     When false every layer draws directly, whatever its hint says. The escape hatch for bisecting a
///     "is this a caching bug or a drawing bug" question, and what the determinism test flips.
/// </param>
/// <param name="MaxCachedPictures">Hard cap on live pictures before LRU eviction.</param>
public sealed record SceneCompositorOptions(bool EnablePictureCaching = true, int MaxCachedPictures = 64);

/// <summary>Counters from the last completed render.</summary>
/// <param name="LayersRendered">Layer draws issued, summed over panes.</param>
/// <param name="PicturesRecorded">Cumulative picture recordings since construction.</param>
/// <param name="PicturesReplayed">Cumulative cache hits since construction.</param>
/// <param name="PanesRendered">Panes drawn this frame.</param>
public readonly record struct SceneCompositorStats(
    int LayersRendered,
    int PicturesRecorded,
    int PicturesReplayed,
    int PanesRendered);

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
    private readonly List<IDisposable> _owned = [];
    private readonly SceneCompositorOptions _options;
    private bool _disposed;
    private int _layersRendered;
    private int _panesRendered;

    // The palette the live pictures were recorded under, and whether one has been seen at all. See
    // EnsurePalette.
    private ScenePalette _cachedPalette;
    private bool _paletteSeen;

    // Set for the duration of a single-pane Render whose caller framed no pane. See that overload.
    private bool _bypassCache;

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

    /// <summary>
    ///     Optional per-layer measurement (plan <c>P1-perf-instrumentation</c> §3.1). Null on the default
    ///     path — the whole mechanism is then one field read and one predicted branch per layer per
    ///     phase, no clock and no allocation. See <see cref="ISceneProfiler" /> for why the timestamping
    ///     lives on the other side of the interface rather than here.
    /// </summary>
    public ISceneProfiler? Profiler { get; set; }

    /// <summary>
    ///     Disposes every registered layer, everything handed to <see cref="AddOwned" />, and every
    ///     cached picture. Idempotent.
    /// </summary>
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

        // After the layers, never before: a shared resource is shared precisely because a layer is
        // still using it right up to its own Dispose.
        foreach (IDisposable resource in _owned)
        {
            resource.Dispose();
        }

        _owned.Clear();
    }

    /// <summary>
    ///     Registers a resource the compositor should dispose along with its layers — a
    ///     <see cref="TextBlobCache" /> several layers share, and nothing else so far.
    ///     <para>
    ///         <b>Why this exists.</b> A shared resource cannot be owned by one of the layers sharing it:
    ///         <see cref="Remove" /> disposes the layer it drops, which would take the font out from
    ///         under everyone else still drawing with it. Hosts that build their own stack
    ///         (<c>Scene2DHost</c>, the test stage) hold such resources in a field and dispose them after
    ///         the compositor; a factory that hands back only a compositor has nowhere else to put them.
    ///     </para>
    /// </summary>
    /// <param name="resource">The resource to dispose at teardown. Disposed after every layer.</param>
    public void AddOwned(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _owned.Add(resource);
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

        // Hoisted once rather than re-read per layer: with no profiler attached this is a single field
        // read for the whole loop and a predicted null branch per layer.
        ISceneProfiler? profiler = Profiler;

        bool keepArmed = false;
        for (int i = 0; i < _layers.Count; i++)
        {
            ISceneLayer layer = _layers[i];
            if (!layer.IsEnabled)
            {
                continue;
            }

            profiler?.BeginLayer(i, layer.Id, LayerPhase.Advance);
            keepArmed |= layer.Advance(in time, frame);
            profiler?.EndLayer(i, LayerPhase.Advance);
        }

        return keepArmed;
    }

    /// <summary>
    ///     Draws every enabled layer into one already-framed pane. The caller owns the clip, the
    ///     translation and the background.
    ///     <para>
    ///         <b>A caller that leaves <c>ctx.Pane</c> at its default gets no picture caching.</b> The
    ///         cache key's camera component is <c>Pane.CameraEpoch</c> and its pane component is
    ///         <c>Pane.LevelId</c> — both zero on a default snapshot — so every <c>PerCamera</c> layer
    ///         would key to the same entry whatever the camera is doing, and the first frame's
    ///         pane-local pixels would replay for the life of the compositor (D6 finding 17). Drawing
    ///         directly costs a re-record per frame on a path with no production caller; a frozen radar
    ///         under a moving camera costs the picture.
    ///     </para>
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

        EnsurePalette(ctx.Palette);

        // Computed once per render rather than per layer: a caller that framed a pane (the export HUD
        // suite, a golden that supplies one) keeps its cache.
        _bypassCache = ctx.Pane == default;
        try
        {
            RenderPane(canvas, in ctx);
        }
        finally
        {
            _bypassCache = false;
        }

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

        EnsurePalette(submission.Palette);

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
                // The ONLY production read of Purpose in the repo, and it is a copy: it is handed to
                // every layer and no layer branches on it, so Export and Interactive render the same
                // pixels and Thumbnail is never submitted at all. Reserved, on purpose and on record —
                // RenderPurpose's own doc carries the reasoning, and RenderPurposeTests pins the
                // equality so this cannot quietly become half-true.
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

    /// <summary>
    ///     Drops every cached picture when the palette this frame draws with is not the palette they
    ///     were recorded under.
    ///     <para>
    ///         <b>Why not a fifth component on the cache key.</b> A recorded picture bakes in whatever
    ///         colours the layer read out of <c>ctx.Palette</c> — <c>RadarLayer</c>, the only production
    ///         <c>PerCamera</c> layer, records the grid with <c>MinorGrid</c>/<c>MajorGrid</c> in it —
    ///         so a palette swap really does invalidate them all. But the palette is <i>compositor</i>
    ///         state, not frame state (<see cref="ScenePalette" />'s own words: "the theme changes on a
    ///         variant switch, not on a tick"), and putting thirty-two colours into a key that is hashed
    ///         once per layer per pane per frame would pay for a per-frame lookup to catch an event that
    ///         happens twice a session. One equality test per <i>render</i> catches the same event, and
    ///         it catches it for every entry point rather than only for the one property whose doc
    ///         promised it — <c>HeadlessSceneRenderer.Palette</c> claimed to invalidate and was a plain
    ///         auto-property, while <c>Scene2DHost.RefreshPalette</c> was invalidating by hand, which is
    ///         the only reason the stale grid never shipped (D6 finding 16).
    ///     </para>
    /// </summary>
    /// <param name="palette">The palette this render draws with.</param>
    private void EnsurePalette(in ScenePalette palette)
    {
        if (_paletteSeen && _cachedPalette == palette)
        {
            return;
        }

        _cachedPalette = palette;
        if (_paletteSeen)
        {
            InvalidateCaches();
        }

        _paletteSeen = true;
    }

    private void RenderPane(SKCanvas canvas, in SceneRenderContext ctx)
    {
        ISceneProfiler? profiler = Profiler;

        for (int i = 0; i < _layers.Count; i++)
        {
            ISceneLayer layer = _layers[i];
            if (!layer.IsEnabled)
            {
                continue;
            }

            profiler?.BeginLayer(i, layer.Id, LayerPhase.Render);
            RenderLayer(canvas, layer, i, profiler, in ctx);
            profiler?.EndLayer(i, LayerPhase.Render);
            _layersRendered++;
        }
    }

    private void RenderLayer(SKCanvas canvas, ISceneLayer layer, int index, ISceneProfiler? profiler,
        in SceneRenderContext ctx)
    {
        if (!_options.EnablePictureCaching || _bypassCache || layer.Cache == LayerCacheHint.Dynamic)
        {
            // Not a miss: there is no cache in this path at all, and counting it as one would make a
            // Dynamic layer look like a permanent cache failure.
            profiler?.RecordPicture(index, PictureCacheOutcome.Uncached);
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

        // The hit/miss decision the compositor was already making; reporting it computes nothing new.
        profiler?.RecordPicture(index,
            picture is null ? PictureCacheOutcome.Recorded : PictureCacheOutcome.Replayed);

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

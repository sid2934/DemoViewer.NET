#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     Everything <c>Scene2DHost</c> does per frame, minus Avalonia: derive the level set, reconcile
///     the panes, advance the cameras and the layers, and draw one multi-pane submission into an
///     offscreen surface.
///     <para>
///         <b>One headless render entry point.</b> Goldens, the frame-budget benchmark and (from C1) the
///         <c>dv2d</c> commands all go through this, so an offscreen picture and an on-screen one cannot
///         diverge through a second, subtly different loop. It is a facade over Core's
///         <c>SceneCompositor</c>, never a competing renderer.
///     </para>
///     <para>
///         The surface is created once for a given <see cref="Size" /> and reused, because the frame
///         budget is measured on this class and allocating a full-frame surface per iteration would be
///         measuring the allocator.
///     </para>
///     <para>
///         <b>C1 merge:</b> the CLI's single-pane facade of the same name was withdrawn into this class
///         (merge note 1). C1 deviation (1) predicted exactly that — "B1 adds the layout/map parameters
///         when it lands those types" — so the convenience members <c>dv2d</c> was written against
///         (<see cref="Camera" />, <see cref="RenderPng" />, <see cref="RenderInto" />, the
///         <see cref="Backend" /> passthrough) sit here as thin wrappers over the pane pipeline rather
///         than as a second, level-blind render path.
///     </para>
/// </summary>
public sealed class HeadlessSceneRenderer : IDisposable
{
    private readonly SceneCompositor _compositor;
    private readonly List<LevelPaneSnapshot> _snapshots = new(4);
    private readonly IRenderSurfaceProvider _surfaces;
    private bool _disposed;
    private SKSizeI _size;
    private long _submissionId;
    private SKSurface? _surface;

    /// <summary>Creates a renderer over a layer stack and a surface provider.</summary>
    /// <param name="compositor">The layer stack. Not owned — the caller disposes it.</param>
    /// <param name="surfaces">Where surfaces come from. Not owned.</param>
    /// <param name="layout">Pane layout policy.</param>
    /// <param name="palette">Resolved colours.</param>
    public HeadlessSceneRenderer(SceneCompositor compositor, IRenderSurfaceProvider surfaces,
        ILevelLayoutPolicy layout, ScenePalette palette)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(layout);

        _compositor = compositor;
        _surfaces = surfaces;
        Panes = new PaneSet(layout);
        Palette = palette;
    }

    /// <summary>
    ///     Convenience constructor for a headless consumer that has no opinion about pane layout: the
    ///     pre-v2 stacked bands and the dark palette. What <c>dv2d</c> builds.
    /// </summary>
    /// <param name="surfaces">Where surfaces come from. Not owned.</param>
    /// <param name="compositor">The layer stack. Not owned.</param>
    public HeadlessSceneRenderer(IRenderSurfaceProvider surfaces, SceneCompositor compositor)
        : this(compositor, surfaces, new StackedLayout(), ScenePalette.Dark)
    {
    }

    /// <summary>The level derivation. Set its <c>RadarBinder</c> to draw baked radar images.</summary>
    public MapSpaceFactory Levels { get; } = new();

    /// <summary>The layer stack being drawn. Not owned.</summary>
    public SceneCompositor Compositor => _compositor;

    /// <summary>The backend the underlying provider hands out.</summary>
    public RenderBackend Backend => _surfaces.Backend;

    /// <summary>
    ///     A camera to pin every pane to, re-applied after each <see cref="Advance" />.
    ///     <para>
    ///         Null — the default — leaves the panes wherever <see cref="SetAllCameras" /> or
    ///         <see cref="FitAll" /> put them. Setting it is how a caller that renders a scene <i>once</i>
    ///         (a golden, a <c>dv2d render</c>) gets a camera that survives the pane reconciliation the
    ///         first advance performs, without the advance-set-advance dance a stateful caller does.
    ///     </para>
    /// </summary>
    public ViewportTransform? Camera { get; set; }

    /// <summary>
    ///     Which entities changed floor this frame. Updated inside <see cref="Advance" />, after the
    ///     level set is current and before the layers advance, so anything holding per-entity temporal
    ///     state can drop it in the same frame the crossing happened. Wire it to a
    ///     <c>MarkerSmoother.LevelCrossings</c> to get the snap.
    /// </summary>
    public LevelCrossingTracker Crossings { get; } = new();

    /// <summary>The arranged panes.</summary>
    public PaneSet Panes { get; }

    /// <summary>Resolved colours. Swapping it invalidates the compositor's caches.</summary>
    public ScenePalette Palette { get; set; }

    /// <summary>How levels are laid out. B1 only ever uses <see cref="LevelDisplayMode.Stacked" />.</summary>
    public LevelDisplayMode DisplayMode { get; set; } = LevelDisplayMode.Stacked;

    /// <summary>Why the scene is being rendered; reaches every layer through the context.</summary>
    public RenderPurpose Purpose { get; set; } = RenderPurpose.Export;

    /// <summary>
    ///     Whether cameras follow their rigs. Off — the default — leaves them exactly where
    ///     <see cref="SetAllCameras" /> or the initial fit put them, which is what a golden needs: a
    ///     camera that lerps is a picture that depends on how many frames you rendered.
    /// </summary>
    public bool AdvanceCameras { get; set; }

    /// <summary>Output size in pixels. Changing it reallocates the surface.</summary>
    public SKSizeI Size
    {
        get => _size;
        set
        {
            if (_size == value)
            {
                return;
            }

            _size = value;
            _surface?.Dispose();
            _surface = null;
        }
    }

    /// <summary>The submission built by the last <see cref="Advance" />.</summary>
    public SceneSubmission LastSubmission { get; private set; }

    /// <summary>Releases the cached surface. The compositor and provider belong to the caller.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface?.Dispose();
        _surface = null;
    }

    /// <summary>Points every pane's camera at one transform, re-viewported to its band.</summary>
    /// <param name="transform">The camera to apply.</param>
    public void SetAllCameras(ViewportTransform transform)
    {
        IReadOnlyList<LevelPane> panes = Panes.Panes;
        for (int i = 0; i < panes.Count; i++)
        {
            LevelPane pane = panes[i];
            pane.Camera.Current = transform.WithViewport(pane.ViewportRect.Width, pane.ViewportRect.Height);
            pane.Camera.ManualOverride = true; // hold it: a golden's camera is data, not a target
            pane.SyncCameraEpoch();
        }
    }

    /// <summary>
    ///     The UI-thread half of a frame: level derivation, pane reconciliation, camera and layer
    ///     advance. Returns true while anything is still animating.
    /// </summary>
    /// <param name="frame">The frame to advance to.</param>
    /// <param name="time">The injected clock.</param>
    public bool Advance(Scene2DFrame frame, in SceneTime time)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (Levels.Update(frame))
        {
            _compositor.InvalidateCaches();

            // Every cached assignment describes bands that no longer exist. Re-resolving from scratch
            // is also what stops a rebuild from reporting a phantom crossing for an entity that merely
            // got re-keyed (B3 remap algorithm, step 10).
            Crossings.Reset();
        }

        if (time.IsDiscontinuity)
        {
            Crossings.Reset();
        }

        SKSize host = new(_size.Width, _size.Height);
        Panes.Reconcile(Levels.Space, DisplayMode, host, frame.Map.ObservedBounds);

        UpdateCrossings(frame);

        bool keepArmed = false;
        if (AdvanceCameras)
        {
            keepArmed = Core.Cameras.CameraAdvancer.Advance(Panes, frame, in time);
        }

        // After reconciliation, so a pinned camera survives the pane set being (re)built by this very
        // call. Idempotent — SetAllCameras only writes Current/ManualOverride/epoch.
        if (Camera is { } pinned)
        {
            SetAllCameras(pinned);
        }

        Panes.SyncCameraEpochs();
        keepArmed |= _compositor.Advance(in time, frame);

        // A crossing is true for exactly one frame, and every layer that cares has now advanced.
        Crossings.EndFrame();

        Panes.CopySnapshots(_snapshots);
        LastSubmission = new SceneSubmission(
            ++_submissionId,
            frame,
            time,
            _snapshots,
            Palette,
            Purpose,
            new SKRect(0, 0, _size.Width, _size.Height),
            1f, // offscreen is always 1 device pixel per DIP
            Levels.Space);

        return keepArmed;
    }

    /// <summary>Fits every pane to the frame's observed extent. The offscreen equivalent of the host's one-shot fit.</summary>
    /// <param name="frame">The frame whose extent to frame.</param>
    public void FitAll(Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Panes.FitAll(frame.Map.ObservedBounds);
    }

    /// <summary>Draws the last submission into the cached surface. Call <see cref="Advance" /> first.</summary>
    public void Render()
    {
        SKSurface surface = EnsureSurface();
        surface.Canvas.Clear(Palette.Background);
        _compositor.Render(surface.Canvas, LastSubmission);
        _surfaces.Flush(surface);
    }

    /// <summary>Advances, renders and snapshots in one call. The caller owns the returned image.</summary>
    /// <param name="frame">The frame to render.</param>
    /// <param name="time">The injected clock.</param>
    public SKImage RenderFrame(Scene2DFrame frame, in SceneTime time)
    {
        Advance(frame, in time);
        Render();
        return EnsureSurface().Snapshot();
    }

    /// <summary>Encodes the current surface as PNG bytes.</summary>
    public byte[] SnapshotPng()
    {
        using SKImage image = EnsureSurface().Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    ///     Draws the last submission into a surface the <b>caller</b> owns, rather than the cached one.
    ///     What a benchmark that wants to hold its own surface across a run uses; the pixels are the
    ///     same ones <see cref="Render()" /> produces.
    /// </summary>
    /// <param name="surface">The destination surface.</param>
    public void Render(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.Canvas.Clear(Palette.Background);
        _compositor.Render(surface.Canvas, LastSubmission);
        _surfaces.Flush(surface);
    }

    /// <summary>
    ///     Advance + render into a caller-owned surface, against an <b>explicit</b> clock.
    ///     <para>
    ///         The injected clock is the whole determinism contract (design §5.1): motion is a function of
    ///         <see cref="SceneTime" />, never of a wall clock, and on the demo path the frame's own
    ///         <c>Time</c> and the source's <c>TimeAt</c> are not the same value —
    ///         <c>TrackerFrameSource.TimeAt</c> derives <c>DeltaSeconds</c> from fps/speed and authors
    ///         <c>IsDiscontinuity</c>. A render stamped from the frame would silently discard what the
    ///         caller injected (C1 deviation 18).
    ///     </para>
    /// </summary>
    /// <param name="surface">The destination surface.</param>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public void RenderInto(SKSurface surface, Scene2DFrame frame, in SceneTime time,
        RenderPurpose purpose = RenderPurpose.Export)
    {
        ArgumentNullException.ThrowIfNull(surface);

        // The caller's surface is the host: without this the pane layout would be arranged over a 0x0
        // host and draw nothing into a perfectly valid surface.
        SKRectI clip = surface.Canvas.DeviceClipBounds;
        Size = new SKSizeI(clip.Width, clip.Height);
        Purpose = purpose;
        Advance(frame, in time);
        Render(surface);
    }

    /// <summary>Advance + render at a given size; the caller disposes the returned image.</summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="size">Pixel size of the output.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public SKImage Render(Scene2DFrame frame, in SceneTime time, SKSizeI size,
        RenderPurpose purpose = RenderPurpose.Export)
    {
        Size = size;
        Purpose = purpose;
        return RenderFrame(frame, in time);
    }

    /// <summary>Advance + render at a given size, encoded as PNG.</summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="size">Pixel size of the output.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public byte[] RenderPng(Scene2DFrame frame, in SceneTime time, SKSizeI size,
        RenderPurpose purpose = RenderPurpose.Export)
    {
        Size = size;
        Purpose = purpose;
        Advance(frame, in time);
        Render();
        return SnapshotPng();
    }

    // Indexed, allocation-free: a dictionary write over an existing key and one level resolution per
    // marker. The §6 budget is zero bytes per steady-state frame and this runs inside it.
    private void UpdateCrossings(Scene2DFrame frame)
    {
        MapSpace space = Levels.Space;
        if (space.Levels.Count < 2)
        {
            return;
        }

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            Crossings.Update(markers[i].Slot, markers[i].WorldZ, space);
        }
    }

    private SKSurface EnsureSurface() => _surface ??= _surfaces.CreateSurface(_size);
}

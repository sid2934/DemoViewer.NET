#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     One-shot headless render of a single <see cref="Scene2DFrame" />. The single render entry point
///     for every non-Avalonia consumer: <c>dv2d render</c>, <c>dv2d bench</c>, <c>dv2d golden</c>,
///     export, and tests.
///     <para>
///         <b>A facade over Core's <see cref="SceneRenderer" />, never a second render path</b> (C1
///         correction 2b). <see cref="Render(Scene2DFrame,in SceneTime,SKSizeI,RenderPurpose)" />
///         delegates to it outright. The split
///         <see cref="Advance" />/<see cref="Render(SKSurface,Scene2DFrame,RenderPurpose)" /> pair exists
///         only because the bench command must time the two phases separately and reuse one surface
///         across thousands of frames; it is the body of <c>SceneRenderer.Render</c> minus the surface
///         creation, and <c>HeadlessSceneRendererTests.Render_MatchesRenderInto</c> pins the two together
///         so they cannot drift.
///     </para>
/// </summary>
public sealed class HeadlessSceneRenderer : IDisposable
{
    private readonly bool _ownsCompositor;
    private readonly SceneRenderer _renderer;
    private readonly IRenderSurfaceProvider _surfaces;
    private bool _disposed;

    /// <summary>Creates a renderer over a provider and a layer stack.</summary>
    /// <param name="surfaces">Where surfaces come from. Not owned unless <paramref name="ownsProvider" />.</param>
    /// <param name="compositor">The layer stack. Not owned unless <paramref name="ownsCompositor" />.</param>
    /// <param name="ownsCompositor">Dispose <paramref name="compositor" /> with this renderer.</param>
    /// <param name="ownsProvider">Dispose <paramref name="surfaces" /> with this renderer.</param>
    public HeadlessSceneRenderer(IRenderSurfaceProvider surfaces, SceneCompositor compositor,
        bool ownsCompositor = false, bool ownsProvider = false)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(compositor);

        _surfaces = surfaces;
        Compositor = compositor;
        _ownsCompositor = ownsCompositor;
        OwnsProvider = ownsProvider;
        _renderer = new SceneRenderer(surfaces);
    }

    /// <summary>The layer stack being drawn.</summary>
    public SceneCompositor Compositor { get; }

    /// <summary>The backend the underlying provider hands out.</summary>
    public RenderBackend Backend => _surfaces.Backend;

    /// <summary>World → pane-local screen. Set before rendering; the CLI resolves it from `--camera`.</summary>
    public ViewportTransform Camera { get; set; }

    /// <summary>The colours and stroke widths every layer draws with.</summary>
    public ScenePalette Palette { get; set; } = ScenePalette.Dark;

    private bool OwnsProvider { get; }

    /// <summary>Disposes what this renderer was told it owns. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsCompositor)
        {
            Compositor.Dispose();
        }

        if (OwnsProvider)
        {
            _surfaces.Dispose();
        }
    }

    /// <summary>Advance + Render into a surface the caller owns (bench reuses one surface).</summary>
    /// <param name="surface">The destination surface.</param>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public void RenderInto(SKSurface surface, Scene2DFrame frame, in SceneTime time,
        RenderPurpose purpose = RenderPurpose.Thumbnail)
    {
        Advance(in time, frame);
        Render(surface, frame, in time, purpose);
    }

    /// <summary>Advance + Render into a fresh provider surface; the caller disposes the image.</summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="size">Pixel size of the output.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public SKImage Render(Scene2DFrame frame, in SceneTime time, SKSizeI size,
        RenderPurpose purpose = RenderPurpose.Thumbnail)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _renderer.Render(Compositor, frame, in time, ContextFor(frame, in time, size, purpose), size);
    }

    /// <summary>Convenience: <see cref="Render(Scene2DFrame,in SceneTime,SKSizeI,RenderPurpose)" /> as PNG.</summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="size">Pixel size of the output.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public byte[] RenderPng(Scene2DFrame frame, in SceneTime time, SKSizeI size,
        RenderPurpose purpose = RenderPurpose.Thumbnail)
    {
        using SKImage image = Render(frame, in time, size, purpose);
        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);
        return stream.ToArray();
    }

    /// <summary>The mutating half, measured on its own by <c>dv2d bench</c>.</summary>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="frame">The frame being advanced to.</param>
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Compositor.Advance(in time, frame);
    }

    /// <summary>
    ///     The pure half, measured on its own by <c>dv2d bench</c>. The frame's own <c>Time</c> stamps the
    ///     context; prefer the overload taking an explicit clock whenever the caller has one.
    /// </summary>
    /// <param name="surface">The destination surface.</param>
    /// <param name="frame">The frame to draw. Its own <c>Time</c> is used for the context.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public void Render(SKSurface surface, Scene2DFrame frame, RenderPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(frame);
        SceneTime time = frame.Time;
        Render(surface, frame, in time, purpose);
    }

    /// <summary>
    ///     The pure half, drawn against an <b>explicit</b> clock.
    ///     <para>
    ///         The injected clock is the whole determinism contract (design §5.1): motion is a function of
    ///         <see cref="SceneTime" />, never of a wall clock. On the demo path the frame's own <c>Time</c>
    ///         and the source's <c>TimeAt</c> are <i>not</i> the same value —
    ///         <c>TrackerFrameSource.TimeAt</c> derives <c>DeltaSeconds</c> from fps/speed and authors
    ///         <c>IsDiscontinuity</c> — so a render context stamped from the frame silently discards what
    ///         the caller injected, and <c>bench</c> and <c>golden</c> would draw different scenes from
    ///         the same input the moment a layer reads <c>ctx.Time</c>.
    ///     </para>
    /// </summary>
    /// <param name="surface">The destination surface.</param>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="time">The frame's injected clock; stamped onto the render context.</param>
    /// <param name="purpose">Why this scene is being rendered.</param>
    public void Render(SKSurface surface, Scene2DFrame frame, in SceneTime time, RenderPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(frame);

        SKSizeI size = new(surface.Canvas.DeviceClipBounds.Width, surface.Canvas.DeviceClipBounds.Height);
        SceneRenderContext ctx = ContextFor(frame, in time, size, purpose);

        surface.Canvas.Clear(ctx.Palette.Background);
        Compositor.Render(surface.Canvas, in ctx);
        _surfaces.Flush(surface);
    }

    private SceneRenderContext ContextFor(Scene2DFrame frame, in SceneTime time, SKSizeI size,
        RenderPurpose purpose) =>
        new(
            frame,
            time,
            Camera,
            SKRect.Create(size.Width, size.Height),
            -1, // single pane showing every level; B3 supplies the per-level bands
            0,
            0,
            purpose,
            Palette,
            1f); // offscreen is always 1 device pixel per DIP (design §5.8)
}

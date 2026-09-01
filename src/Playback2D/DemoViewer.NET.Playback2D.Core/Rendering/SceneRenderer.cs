#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     Drives one offscreen render: obtain a surface, advance the compositor, draw it, flush, snapshot.
///     C1's <c>HeadlessSceneRenderer</c> is a Pipeline facade over this, never a second render path.
/// </summary>
public sealed class SceneRenderer
{
    private readonly IRenderSurfaceProvider _surfaces;

    /// <summary>Creates a renderer over a surface provider.</summary>
    /// <param name="surfaces">Where surfaces come from. Not owned: the caller disposes it.</param>
    public SceneRenderer(IRenderSurfaceProvider surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        _surfaces = surfaces;
    }

    /// <summary>The backend the underlying provider hands out.</summary>
    public RenderBackend Backend => _surfaces.Backend;

    /// <summary>
    ///     Renders one pane and returns the snapshot. The caller owns the returned image. B1 adds a
    ///     pane-list overload rather than changing this signature (decision D9).
    /// </summary>
    /// <param name="compositor">The layer stack to advance and draw.</param>
    /// <param name="frame">The frame to render.</param>
    /// <param name="time">The frame's injected clock, passed to <c>Advance</c>.</param>
    /// <param name="ctx">The pane's render context. Its <c>Frame</c>/<c>Time</c> should match the arguments.</param>
    /// <param name="size">Pixel size of the output.</param>
    public SKImage Render(SceneCompositor compositor, Scene2DFrame frame, in SceneTime time,
        in SceneRenderContext ctx, SKSizeI size)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        using SKSurface surface = _surfaces.CreateSurface(size);
        compositor.Advance(in time, frame);
        surface.Canvas.Clear(ctx.Palette.Background);
        compositor.Render(surface.Canvas, in ctx);
        _surfaces.Flush(surface);
        return surface.Snapshot();
    }

    /// <summary>Encodes an image as PNG into a stream.</summary>
    /// <param name="image">The image to encode.</param>
    /// <param name="destination">The stream written to. Not closed.</param>
    public static void WritePng(SKImage image, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(destination);

        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(destination);
    }

    /// <summary>Encodes an image as PNG to a file path, creating the directory if needed.</summary>
    /// <param name="image">The image to encode.</param>
    /// <param name="path">The destination file path.</param>
    public static void WritePng(SKImage image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        WritePng(image, stream);
    }
}

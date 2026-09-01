#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>Render contexts for the direct-execution suite.</summary>
internal static class TestContexts
{
    /// <summary>A single-level 16×16 pane over the empty frame.</summary>
    public static readonly SceneRenderContext Default = For(Scene2DFrame.Empty, default, 16, 16);

    /// <summary>A single-level pane of the given size, framed on the fixture's camera.</summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="camera">World → screen transform.</param>
    /// <param name="width">Pane width in pixels.</param>
    /// <param name="height">Pane height in pixels.</param>
    public static SceneRenderContext For(Scene2DFrame frame, ViewportTransform camera, int width, int height) =>
        new(
            frame,
            frame.Time,
            camera,
            SKRect.Create(width, height),
            -1,
            0,
            0,
            RenderPurpose.Interactive,
            ScenePalette.Dark,
            1f);
}

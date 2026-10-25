#region

using DemoViewer.NET.Playback2D.Core;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     The single-frame framings a headless render can aim: fit the map, fit the living players, or
///     re-fit the camera a fixture carries. Whoever decides WHICH of them a request means (a flag, a
///     lane convention) does that elsewhere; this is the arithmetic.
/// </summary>
internal static class SceneCameraFit
{
    /// <summary>
    ///     The framing for a fixture that named no camera of its own: its captured camera, re-aimed at
    ///     the requested viewport so a different output size reframes rather than crops. A fixture with
    ///     no usable camera falls back to <see cref="FitMap" />.
    /// </summary>
    /// <param name="frame">The frame being framed.</param>
    /// <param name="size">The output size.</param>
    /// <param name="fixtureCamera">The camera the fixture carries.</param>
    public static ViewportTransform ForFixture(Scene2DFrame frame, SKSizeI size,
        ViewportTransform fixtureCamera)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return fixtureCamera.BaseScale > 0 ? Refit(fixtureCamera, size) : FitMap(frame, size);
    }

    /// <summary>The whole map in view, from the networked bounds when it has them.</summary>
    /// <param name="frame">The frame being framed.</param>
    /// <param name="size">The output size.</param>
    public static ViewportTransform FitMap(Scene2DFrame frame, SKSizeI size)
    {
        ArgumentNullException.ThrowIfNull(frame);

        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;
        return ViewportTransform.Fit(size.Width, size.Height, bounds.MinX, bounds.MinY,
            bounds.MaxX, bounds.MaxY);
    }

    /// <summary>The living players in view, with a 256-unit margin.</summary>
    /// <param name="frame">The frame being framed.</param>
    /// <param name="size">The output size.</param>
    public static ViewportTransform FitAlive(Scene2DFrame frame, SKSizeI size)
    {
        ArgumentNullException.ThrowIfNull(frame);

        bool any = false;
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (PlayerMarker marker in frame.Markers)
        {
            if (!marker.IsAlive)
            {
                continue;
            }

            any = true;
            minX = Math.Min(minX, marker.WorldX);
            minY = Math.Min(minY, marker.WorldY);
            maxX = Math.Max(maxX, marker.WorldX);
            maxY = Math.Max(maxY, marker.WorldY);
        }

        // No living player is a legitimate scene (a post-round fixture); fall back to the map rather
        // than emitting a degenerate transform.
        return any
            ? ViewportTransform.Fit(size.Width, size.Height, minX - 256, minY - 256, maxX + 256, maxY + 256)
            : FitMap(frame, size);
    }

    private static ViewportTransform Refit(ViewportTransform camera, SKSizeI size)
    {
        // Keep the world centre and the user zoom; re-solve base scale for the new viewport so the same
        // world span still fits. Pan is dropped: it is measured in the OLD viewport's pixels.
        double ratio = camera.ViewWidth > 0 && camera.ViewHeight > 0
            ? Math.Min(size.Width / camera.ViewWidth, size.Height / camera.ViewHeight)
            : 1.0;
        return new ViewportTransform(size.Width, size.Height, camera.CenterX, camera.CenterY,
            camera.BaseScale * ratio, camera.Zoom, 0, 0);
    }
}

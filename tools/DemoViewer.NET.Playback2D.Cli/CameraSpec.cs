#region

using DemoViewer.NET.Playback2D.Core;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Resolves <c>--camera</c> into a <see cref="ViewportTransform" />. Deliberately a CLI concern:
///     B4's <c>CameraScript</c> is an export-time animation over several frames, while this is the
///     single-frame framing a designer types.
/// </summary>
internal static class CameraSpec
{
    /// <summary>The spec accepted by <c>--camera</c>, for the usage text.</summary>
    public const string Syntax = "fit-map|fit-alive|follow:<steamId>|fixed:<x>,<y>,<zoom>";

    /// <summary>
    ///     Resolves the transform. With no <c>--camera</c>, the fixture's own camera is used, re-fitted to
    ///     the requested viewport so <c>--size</c> reframes rather than crops.
    /// </summary>
    /// <param name="spec">The raw <c>--camera</c> value, or null.</param>
    /// <param name="frame">The frame being framed.</param>
    /// <param name="size">The output size.</param>
    /// <param name="fixtureCamera">The camera the fixture carries.</param>
    /// <exception cref="CliUsageException">The spec is malformed or names an unknown mode.</exception>
    public static ViewportTransform Resolve(string? spec, Scene2DFrame frame, SKSizeI size,
        ViewportTransform fixtureCamera)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (spec is null)
        {
            // A fixture captured at 900x900 rendered at 640x360 must still frame the same world, so the
            // viewport is re-aimed and the base scale re-solved rather than reused verbatim.
            return fixtureCamera.BaseScale > 0
                ? Refit(fixtureCamera, size)
                : FitMap(frame, size);
        }

        if (string.Equals(spec, "fit-map", StringComparison.OrdinalIgnoreCase))
        {
            return FitMap(frame, size);
        }

        if (string.Equals(spec, "fit-alive", StringComparison.OrdinalIgnoreCase))
        {
            return FitAlive(frame, size);
        }

        if (spec.StartsWith("follow:", StringComparison.OrdinalIgnoreCase))
        {
            string raw = spec["follow:".Length..];
            if (!ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong steamId))
            {
                throw new CliUsageException($"--camera follow: expects a SteamID, got '{raw}'.");
            }

            foreach (PlayerMarker marker in frame.Markers)
            {
                if (marker.SteamId == steamId)
                {
                    ViewportTransform baseline = FitMap(frame, size);
                    return new ViewportTransform(size.Width, size.Height, marker.WorldX, marker.WorldY,
                        baseline.BaseScale, 2.5, 0, 0);
                }
            }

            throw new CliUsageException($"--camera follow:{steamId} — no marker with that SteamID in the scene.");
        }

        if (spec.StartsWith("fixed:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = spec["fixed:".Length..].Split(',');
            if (parts.Length != 3 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                throw new CliUsageException("--camera fixed: expects <x>,<y>,<zoom>.");
            }

            ViewportTransform baseline = FitMap(frame, size);
            return new ViewportTransform(size.Width, size.Height, x, y, baseline.BaseScale, zoom, 0, 0);
        }

        throw new CliUsageException($"--camera expects {Syntax}, got '{spec}'.");
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

    private static ViewportTransform FitMap(Scene2DFrame frame, SKSizeI size)
    {
        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;
        return ViewportTransform.Fit(size.Width, size.Height, bounds.MinX, bounds.MinY,
            bounds.MaxX, bounds.MaxY);
    }

    private static ViewportTransform FitAlive(Scene2DFrame frame, SKSizeI size)
    {
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
}

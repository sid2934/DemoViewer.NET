#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Parses <c>--camera</c> and dispatches to the framing it names. Deliberately a CLI concern:
///     <c>CameraScript</c> is an export-time animation over several frames, while this is the
///     single-frame framing a designer types. The arithmetic behind each mode is
///     <see cref="SceneCameraFit" />.
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
            return SceneCameraFit.ForFixture(frame, size, fixtureCamera);
        }

        if (string.Equals(spec, "fit-map", StringComparison.OrdinalIgnoreCase))
        {
            return SceneCameraFit.FitMap(frame, size);
        }

        if (string.Equals(spec, "fit-alive", StringComparison.OrdinalIgnoreCase))
        {
            return SceneCameraFit.FitAlive(frame, size);
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
                    ViewportTransform baseline = SceneCameraFit.FitMap(frame, size);
                    return new ViewportTransform(size.Width, size.Height, marker.WorldX, marker.WorldY,
                        baseline.BaseScale, 2.5, 0, 0);
                }
            }

            throw new CliUsageException(
                $"--camera follow:{steamId} names no marker with that SteamID in the scene.");
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

            ViewportTransform baseline = SceneCameraFit.FitMap(frame, size);
            return new ViewportTransform(size.Width, size.Height, x, y, baseline.BaseScale, zoom, 0, 0);
        }

        throw new CliUsageException($"--camera expects {Syntax}, got '{spec}'.");
    }
}

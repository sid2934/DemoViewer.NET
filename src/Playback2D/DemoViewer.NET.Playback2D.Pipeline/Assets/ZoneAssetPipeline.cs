#region

using System.IO.Compression;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>What one zones load produced. <see cref="Resolver" /> is null when the map has no zones file.</summary>
/// <param name="Resolver">The resolver over the effective set, or null.</param>
/// <param name="Baked">The baked set before the overlay, or null.</param>
/// <param name="OverlayPath">The overlay file that was looked for, whether or not it existed.</param>
/// <param name="Diagnostics">Everything the overlay loader skipped or flagged.</param>
public sealed record ZoneLoadResult(
    PlaceResolver? Resolver,
    ZoneSet? Baked,
    string? OverlayPath,
    IReadOnlyList<ZoneDiagnostic> Diagnostics)
{
    /// <summary>No zones file: every consumer takes its no-zones path.</summary>
    public static readonly ZoneLoadResult Empty = new(null, null, null, []);

    /// <summary>True when an overlay file was read and applied, cleanly or not.</summary>
    public bool OverlayApplied => Resolver?.Zones.HasOverlay == true;
}

/// <summary>
///     Loads a map's <c>zones.json</c> from its bundle directory, applies the user overlay for that map,
///     and builds the <see cref="PlaceResolver" /> over the effective set. The sibling of
///     <see cref="MapAssetPipeline" /> for the zone file.
///     <para>
///         <b>Never throws, reads nothing from <c>bundle.json</c>.</b> The file is located by path
///         (decision D1: a <c>--zones</c> top-up cannot write a bundle reference, so nothing reads one),
///         and a missing, unreadable or malformed file is null. The overlay is the other way round:
///         it can never shadow the bundle when it fails, so a malformed overlay yields the baked set plus
///         a diagnostic, and a malformed entry inside it yields the rest of the overlay plus a diagnostic.
///     </para>
/// </summary>
public static class ZoneAssetPipeline
{
    /// <summary>The plain file the baker writes beside <c>bundle.json</c>.</summary>
    public const string FileName = "zones.json";

    /// <summary>The gzipped spelling; the reader accepts both (decision D3).</summary>
    public const string GzipFileName = "zones.json.gz";

    /// <summary>The overlay's name is <c>&lt;map&gt;</c> plus this.</summary>
    public const string OverlaySuffix = ".zones.json";

    /// <summary>The resolver for a map, or null when it has no zones file. The one-call form.</summary>
    /// <param name="bundleDir">The map's bundle directory.</param>
    /// <param name="overlayDir">The user's <c>zones/</c> directory, or null for the baked set alone.</param>
    public static PlaceResolver? TryLoad(string? bundleDir, string? overlayDir) => Load(bundleDir, overlayDir).Resolver;

    /// <summary>The full answer: resolver, baked set, overlay path and diagnostics.</summary>
    /// <param name="bundleDir">The map's bundle directory.</param>
    /// <param name="overlayDir">The user's <c>zones/</c> directory, or null for the baked set alone.</param>
    public static ZoneLoadResult Load(string? bundleDir, string? overlayDir)
    {
        if (TryReadBaked(bundleDir) is not { } baked)
        {
            return ZoneLoadResult.Empty;
        }

        return Apply(baked, OverlayPathFor(overlayDir, baked.MapName));
    }

    /// <summary>
    ///     The same load with one named overlay file instead of a directory: what
    ///     <c>dv2d render --zones-overlay</c> and the corpus convention feed.
    /// </summary>
    /// <param name="bundleDir">The map's bundle directory.</param>
    /// <param name="overlayFile">The overlay file, or null for the baked set alone.</param>
    public static ZoneLoadResult LoadWithOverlayFile(string? bundleDir, string? overlayFile)
    {
        if (TryReadBaked(bundleDir) is not { } baked)
        {
            return ZoneLoadResult.Empty;
        }

        return Apply(baked, string.IsNullOrWhiteSpace(overlayFile) ? null : overlayFile);
    }

    /// <summary>Where a map's overlay lives under a <c>zones/</c> directory, or null without one.</summary>
    /// <param name="overlayDir">The user's <c>zones/</c> directory.</param>
    /// <param name="mapName">The map, e.g. <c>de_mirage</c>.</param>
    public static string? OverlayPathFor(string? overlayDir, string? mapName) =>
        string.IsNullOrWhiteSpace(overlayDir) || string.IsNullOrWhiteSpace(mapName)
            ? null
            : Path.Combine(overlayDir, mapName + OverlaySuffix);

    /// <summary>
    ///     The baked set alone: <c>zones.json</c>, then <c>zones.json.gz</c>, in the bundle directory.
    ///     Null when neither exists or neither parses.
    /// </summary>
    /// <param name="bundleDir">The map's bundle directory.</param>
    public static ZoneSet? TryReadBaked(string? bundleDir)
    {
        if (string.IsNullOrWhiteSpace(bundleDir) || !Directory.Exists(bundleDir))
        {
            return null;
        }

        string plain = Path.Combine(bundleDir, FileName);
        if (File.Exists(plain))
        {
            return TryParse(plain, false);
        }

        string gz = Path.Combine(bundleDir, GzipFileName);
        return File.Exists(gz) ? TryParse(gz, true) : null;
    }

    private static ZoneSet? TryParse(string path, bool gzip)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (gzip)
            {
                using MemoryStream input = new(bytes);
                using GZipStream inflate = new(input, CompressionMode.Decompress);
                using MemoryStream output = new();
                inflate.CopyTo(output);
                bytes = output.ToArray();
            }

            return ZoneSetReader.Read(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidDataException)
        {
            return null; // no zones: the same answer a map with no file gets
        }
    }

    private static ZoneLoadResult Apply(ZoneSet baked, string? overlayPath)
    {
        if (overlayPath is null || !File.Exists(overlayPath))
        {
            return new ZoneLoadResult(new PlaceResolver(baked), baked, overlayPath, []);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(overlayPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ZoneLoadResult(new PlaceResolver(baked), baked, overlayPath,
            [
                new ZoneDiagnostic("zones.overlay.unreadable",
                    $"could not read {overlayPath}: {e.Message}; the baked set is in use")
            ]);
        }

        ZoneOverlayDocument overlay;
        try
        {
            overlay = ZoneOverlayReader.Read(bytes);
        }
        catch (JsonException e)
        {
            return new ZoneLoadResult(new PlaceResolver(baked), baked, overlayPath,
            [
                new ZoneDiagnostic("zones.overlay.malformed",
                    $"{overlayPath} is not a zones overlay: {e.Message}; the baked set is in use")
            ]);
        }

        ZoneOverlayResult applied = ZoneOverlayApplier.Apply(baked, overlay, bytes, overlayPath);
        return new ZoneLoadResult(new PlaceResolver(applied.Effective), baked, overlayPath, applied.Diagnostics);
    }
}

#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core;
using SkiaSharp;

// The bundle's bounds DTO and the fixture serializer's own WorldBoundsDto share a simple name in two
// namespaces this file would otherwise see at once. Aliased rather than fully qualified so the one
// conversion below still reads as a conversion.
using BundleWorldBounds = CS2DemoKit.Analysis.Visibility.WorldBoundsDto;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>
///     A baked map-asset bundle decoded for the scene pipeline: the radar layers as
///     <see cref="MapRadarImage" /> (SkiaSharp, not Avalonia), plus the identity a golden is keyed on.
/// </summary>
public sealed class LoadedMapAssets : IDisposable
{
    private bool _disposed;

    /// <summary>The map this bundle describes.</summary>
    public required string MapName { get; init; }

    /// <summary>The bundle's <c>mapVersion</c> CRC32 hex, or null on a bundle baked before version keying.</summary>
    public required string? MapVersion { get; init; }

    /// <summary>The directory the bundle was read from.</summary>
    public required string Directory { get; init; }

    /// <summary>The decoded radar layers, lowest band first — ready for <c>SceneMapInfo.Radars</c>.</summary>
    public required IReadOnlyList<MapRadarImage> Radars { get; init; }

    /// <summary>The bundle's nav-derived floor bands, low → high.</summary>
    public required IReadOnlyList<(double MinZ, double MaxZ)> Floors { get; init; }

    /// <summary>The bundle's world X/Y extent.</summary>
    public required WorldBounds Bounds { get; init; }

    /// <summary>
    ///     Releases the decoded radar images. Worth an <see cref="IDisposable" /> for the same reason the
    ///     App's <c>LoadedMapAsset</c> is: a 1024×1024 RGBA radar is ~4 MB of unmanaged pixels the GC has
    ///     no pressure signal for, and a CLI that renders a corpus loads one per map.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (MapRadarImage radar in Radars)
        {
            radar.Image?.Dispose();
        }
    }
}

/// <summary>
///     Reads the baked <c>assets/&lt;map&gt;/</c> output of <c>tools/DemoViewer.NET.AssetBaker</c> from
///     an <b>explicit</b> root, and decodes its radar PNGs to <see cref="SKImage" />.
///     <para>
///         Explicit-root is the whole point: <c>MapAssetBundleReader.FindBundleDirectory</c> walks up
///         from <see cref="AppContext.BaseDirectory" />, which is the wrong answer for a tool that was
///         told where the art lives (<c>--assets</c>). The bundle parse itself is BCL-only and lives in
///         the package; what is added here is the part that cannot — the Skia decode.
///     </para>
///     <para><b>Never throws.</b> A missing or malformed bundle reads as null so the caller degrades.</para>
/// </summary>
public static class MapAssetPipeline
{
    /// <summary>Loads a map's bundle from an explicit assets root. Null when there is none to load.</summary>
    /// <param name="assetsRoot">The directory holding one subdirectory per map.</param>
    /// <param name="mapName">The map, e.g. <c>de_nuke</c>.</param>
    public static LoadedMapAssets? TryLoad(string? assetsRoot, string? mapName)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        string dir = Path.Combine(assetsRoot, mapName);
        if (MapAssetBundleReader.TryRead(dir) is not { } bundle)
        {
            return null;
        }

        List<MapRadarImage> radars = [];
        try
        {
            foreach (RadarLayerDto layer in bundle.RadarLayers ?? [])
            {
                if (string.IsNullOrEmpty(layer.Image))
                {
                    continue;
                }

                radars.Add(new MapRadarImage
                {
                    Name = layer.Image,
                    Image = TryDecode(Path.Combine(dir, layer.Image)),
                    Bounds = ToWorldBounds(bundle.Bounds),
                    MinZ = layer.MinZ,
                    MaxZ = layer.MaxZ
                });
            }

            // Lowest band first, matching SceneMapInfo.Radars' documented order. Ordinal name is the
            // tiebreaker so the list is a pure function of the bundle, not of dictionary order.
            radars.Sort(static (a, b) => a.MinZ != b.MinZ
                ? a.MinZ.CompareTo(b.MinZ)
                : string.CompareOrdinal(a.Name, b.Name));

            List<(double MinZ, double MaxZ)> floors = [];
            foreach (FloorBandDto floor in bundle.Floors ?? [])
            {
                floors.Add((floor.MinZ, floor.MaxZ));
            }

            return new LoadedMapAssets
            {
                MapName = bundle.MapName ?? mapName,
                MapVersion = bundle.MapVersion,
                Directory = dir,
                Radars = radars,
                Floors = floors,
                Bounds = ToWorldBounds(bundle.Bounds)
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (MapRadarImage radar in radars)
            {
                radar.Image?.Dispose();
            }

            return null;
        }
    }

    /// <summary>
    ///     Reads just the bundle's <c>mapVersion</c> — the cheap call a fixture capture and a
    ///     <c>golden verify</c> staleness check both need. Null when there is no readable bundle.
    /// </summary>
    /// <param name="assetsRoot">The directory holding one subdirectory per map.</param>
    /// <param name="mapName">The map, e.g. <c>de_nuke</c>.</param>
    public static string? TryReadMapVersion(string? assetsRoot, string? mapName)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        return MapAssetBundleReader.TryRead(Path.Combine(assetsRoot, mapName))?.MapVersion;
    }

    /// <summary>
    ///     The walk-up fallback: the first <c>assets/</c> directory at or above the process base
    ///     directory. Only used when neither <c>--assets</c> nor <c>DV2D_ASSETS</c> was given.
    /// </summary>
    public static string? TryLocateAssetsRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "assets");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static WorldBounds ToWorldBounds(BundleWorldBounds? dto) =>
        dto is null ? WorldBounds.Default : new WorldBounds(dto.MinX, dto.MinY, dto.MaxX, dto.MaxY);

    private static SKImage? TryDecode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using SKData data = SKData.Create(path);
            return data is null ? null : SKImage.FromEncodedData(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

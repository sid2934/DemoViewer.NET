#region

using Avalonia.Media.Imaging;
using Cs2DemoKit.Analysis.Visibility;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

// The bitmap half of map-asset loading. Locating the bundle directory, and parsing bundle.json into
// the MapAssetBundle DTOs, live in Cs2DemoKit.Analysis.Visibility (MapAssetBundleReader) — that half
// is BCL-only and ships in the package. What stays here is the part that cannot: decoding the radar
// images into Avalonia Bitmaps. The APP is VRF-free either way — it consumes baked, ready-to-use
// artifacts (PNG + JSON), never the CS2 assets directly (docs/asset-pipeline/design.md).

/// <summary>
///     A loaded map-asset bundle: the parsed <see cref="MapAssetBundle" /> plus the decoded radar
///     <see cref="Bitmap" />s (by image name). Exposes the nav floor bands as <see cref="FloorSlice" />s
///     for the viewport's <c>FloorSplitter</c>.
/// </summary>
public sealed class LoadedMapAsset : IDisposable
{
    private bool _disposed;

    public required MapAssetBundle Bundle { get; init; }

    /// <summary>Decoded radar bitmaps by file name (best-effort — empty if the platform can't decode).</summary>
    public required IReadOnlyDictionary<string, Bitmap> RadarBitmaps { get; init; }

    /// <summary>The directory the bundle was loaded from (holds sibling artifacts like <c>collision.tris</c>).</summary>
    public required string BakedDir { get; init; }

    /// <summary>The bundle's nav-derived floor bands as <see cref="FloorSlice" />s (low→high).</summary>
    public IReadOnlyList<FloorSlice> Floors =>
        Bundle.Floors.Select(f => new FloorSlice(f.MinZ, f.MaxZ)).ToList();

    /// <summary>Absolute path to the baked collision.tris blob, or null if the bundle has no collision mesh.</summary>
    public string? CollisionTrisPath =>
        Bundle.CollisionMesh is { } cm ? Path.Combine(BakedDir, cm.File) : null;

    /// <summary>
    ///     Releases the decoded radar bitmaps. This is one of the few places in the app where
    ///     <see cref="IDisposable" /> genuinely buys something: an Avalonia <see cref="Bitmap" /> is
    ///     Skia-backed, so its pixel buffer is UNMANAGED memory — invisible to <c>gc-heap-size</c>, and
    ///     reclaimed only via the finalizer queue, which the GC has little pressure signal to run. A CS2
    ///     radar is 1024×1024 RGBA ≈ 4 MB decoded, one or two per map, and a map swap used to orphan the
    ///     previous set without disposing it.
    ///     <para>
    ///         Idempotent: <see cref="Playback2DTabViewModel" /> disposes on map change and on unload, and
    ///         those can overlap.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Bitmap bitmap in RadarBitmaps.Values)
        {
            bitmap.Dispose();
        }
    }
}

/// <summary>
///     Loads a baked map-asset bundle for a map — <see cref="MapAssetBundleReader" /> for the
///     directory walk-up and the JSON, this type for the radar bitmaps. <b>Never throws</b>: a
///     missing/malformed bundle returns null so the module degrades to its grid + Z-histogram
///     fallback.
/// </summary>
public static class MapAssetLoader
{
    public static LoadedMapAsset? TryLoad(string? mapName)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(mapName);
        return dir is null ? null : TryLoadFromDirectory(dir);
    }

    /// <summary>
    ///     Loads JUST the map's primary radar image as a lightweight thumbnail (decoded to
    ///     <paramref name="width" /> px wide), for the library card background. Null when the map has no baked
    ///     bundle / radar (dev cache miss) or decoding fails — callers fall back to the accent card. Far cheaper
    ///     than <see cref="TryLoad" />: one downscaled decode, no full-res bitmaps, floors or collision.
    /// </summary>
    public static Bitmap? TryLoadRadarThumbnail(string? mapName, int width)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(mapName);
        if (dir is null || MapAssetBundleReader.TryRead(dir) is not { } bundle)
        {
            return null;
        }

        // primary (upper) radar — good enough for a card
        if (bundle.RadarImages is not { Count: > 0 } images || string.IsNullOrEmpty(images[0]))
        {
            return null;
        }

        string path = Path.Combine(dir, images[0]);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, width); // downscale on decode → small memory per card map
        }
        catch (Exception)
        {
            return null; // undecodable / headless without imaging → accent fallback
        }
    }

    /// <summary>Loads a bundle from an explicit directory (used by tests). Null-graceful.</summary>
    internal static LoadedMapAsset? TryLoadFromDirectory(string dir)
    {
        if (MapAssetBundleReader.TryRead(dir) is not { } bundle)
        {
            return null; // missing / malformed / unreadable → fall back
        }

        // Radar bitmaps are best-effort: decode each independently so a decode failure (or a headless env
        // without an imaging platform) still yields the floor metadata the FloorSplitter needs.
        Dictionary<string, Bitmap> bitmaps = new();
        foreach (string img in bundle.RadarImages)
        {
            string path = Path.Combine(dir, img);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                bitmaps[img] = new Bitmap(path);
            }
            catch (Exception)
            {
                // ignore — floors/transform still usable without the picture
            }
        }

        return new LoadedMapAsset
        {
            Bundle = bundle,
            RadarBitmaps = bitmaps,
            BakedDir = dir
        };
    }
}

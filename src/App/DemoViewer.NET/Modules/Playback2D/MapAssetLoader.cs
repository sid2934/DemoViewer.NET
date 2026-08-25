#region

using Avalonia.Media.Imaging;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

// What is left of map-asset loading in the App after B1's T5. Locating the bundle directory and
// parsing bundle.json live in CS2DemoKit.Analysis.Visibility (MapAssetBundleReader); decoding the
// radar layers into SKImages moved to DemoViewer.NET.Playback2D.Pipeline.Assets.MapAssetPipeline,
// which is what makes the scene loadable without a windowing system.
//
// Two Avalonia-shaped jobs remain, both deliberate (plan decision D-16):
//   * the library card thumbnail, which needs Bitmap.DecodeToWidth's downscale-on-decode and has no
//     SKImage analogue;
//   * radar bitmaps for the LEGACY Playback2DViewport, which draws through a DrawingContext and
//     cannot consume an SKImage. That cache is deleted with the legacy control in B5.

/// <summary>
///     Loads the lightweight radar thumbnail the library card uses as a background.
///     <b>Never throws</b>: a missing bundle, an undecodable image or a headless environment all
///     return null and the caller falls back to the accent card.
/// </summary>
public static class MapAssetLoader
{
    /// <summary>
    ///     Loads JUST the map's primary radar image, decoded to <paramref name="width" /> px wide. Far
    ///     cheaper than a full bundle load: one downscaled decode, no full-res images, no floors, no
    ///     collision mesh.
    /// </summary>
    /// <param name="mapName">e.g. <c>de_mirage</c>.</param>
    /// <param name="width">Target width in pixels.</param>
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
}

/// <summary>
///     Avalonia <see cref="Bitmap" />s for the legacy <see cref="Playback2DViewport" />, decoded lazily
///     from a loaded bundle's directory.
///     <para>
///         <b>Why this exists rather than a second decode in the bundle.</b> The scene path owns
///         <c>SKImage</c>s; a <c>DrawingContext</c> cannot draw one. Rather than have every map load pay
///         for two full-resolution decodes (~4 MB each) so that a temporary escape hatch can render, the
///         legacy control decodes its own copy on first use and only if the toggle is actually set.
///         Deleted with the legacy control in B5.
///     </para>
/// </summary>
internal sealed class LegacyRadarBitmapCache
{
    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.Ordinal);
    private string? _bakedDir;

    /// <summary>
    ///     The decoded bitmap for one of the asset's radar images, or null when it cannot be decoded.
    ///     Switching to a different bundle directory drops the previous map's bitmaps.
    /// </summary>
    /// <param name="asset">The loaded bundle the image belongs to.</param>
    /// <param name="imageName">The bundle-relative image file name.</param>
    public Bitmap? Get(LoadedMapAsset asset, string imageName)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (!string.Equals(_bakedDir, asset.BakedDir, StringComparison.Ordinal))
        {
            Clear();
            _bakedDir = asset.BakedDir;
        }

        if (_bitmaps.TryGetValue(imageName, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap? decoded = null;
        string path = Path.Combine(asset.BakedDir, imageName);
        if (File.Exists(path))
        {
            try
            {
                decoded = new Bitmap(path);
            }
            catch (Exception)
            {
                decoded = null; // headless without imaging, or a corrupt PNG — draw the grid instead
            }
        }

        // A null is cached too: a failed decode must not be retried once per band per frame.
        _bitmaps[imageName] = decoded;
        return decoded;
    }

    /// <summary>Releases every decoded bitmap. Called when the control detaches from the visual tree.</summary>
    public void Clear()
    {
        foreach (Bitmap? bitmap in _bitmaps.Values)
        {
            bitmap?.Dispose();
        }

        _bitmaps.Clear();
        _bakedDir = null;
    }
}

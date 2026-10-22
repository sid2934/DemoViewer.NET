#region

using Avalonia.Media.Imaging;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

// What is left of map-asset loading in the App. Locating the bundle directory and
// parsing bundle.json live in CS2DemoKit.Analysis.Visibility (MapAssetBundleReader); decoding the
// radar layers into SKImages moved to DemoViewer.NET.Playback2D.Pipeline.Assets.MapAssetPipeline,
// which is what makes the scene loadable without a windowing system.
//
// One Avalonia-shaped job remains: the library card thumbnail, which needs Bitmap.DecodeToWidth's
// downscale-on-decode and has no SKImage analogue.

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

        // primary (upper) radar, good enough for a card
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

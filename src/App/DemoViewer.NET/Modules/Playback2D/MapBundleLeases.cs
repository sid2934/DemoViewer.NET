#region

using Avalonia.Threading;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     Map bundles held by the map views on screen, one decode per map however many views show it. The last
///     release disposes the bundle one dispatcher hop later: the render thread may still be replaying a
///     picture that draws its radar images. UI thread only.
/// </summary>
internal static class MapBundleLeases
{
    private static readonly Dictionary<string, (LoadedMapAsset Asset, int Holders)> _held = new(StringComparer.Ordinal);

    /// <summary>The map's bundle, loaded on the first acquire; null when the map has no bundle.</summary>
    /// <param name="map">The map name.</param>
    public static LoadedMapAsset? Acquire(string map)
    {
        ArgumentException.ThrowIfNullOrEmpty(map);
        if (_held.TryGetValue(map, out (LoadedMapAsset Asset, int Holders) entry))
        {
            _held[map] = (entry.Asset, entry.Holders + 1);
            return entry.Asset;
        }

        if (MapAssetPipeline.TryLoad(map) is not { } asset)
        {
            return null;
        }

        _held[map] = (asset, 1);
        return asset;
    }

    /// <summary>Gives back a bundle <see cref="Acquire" /> handed out.</summary>
    /// <param name="map">The name it was acquired under.</param>
    /// <param name="asset">The bundle.</param>
    public static void Release(string map, LoadedMapAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!_held.TryGetValue(map, out (LoadedMapAsset Asset, int Holders) entry) || !ReferenceEquals(entry.Asset, asset))
        {
            return;
        }

        if (entry.Holders > 1)
        {
            _held[map] = (entry.Asset, entry.Holders - 1);
            return;
        }

        _held.Remove(map);
        Dispatcher.UIThread.Post(asset.Dispose, DispatcherPriority.Background);
    }

    /// <summary>How many views hold a map's bundle. For tests.</summary>
    internal static int HoldersOf(string map) => _held.TryGetValue(map, out (LoadedMapAsset Asset, int Holders) entry) ? entry.Holders : 0;
}

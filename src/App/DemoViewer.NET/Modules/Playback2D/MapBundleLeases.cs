#region

using Avalonia.Threading;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Zones;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The one map-bundle registry in the process: one decode per map however many tabs, views and
///     extensions hold it. Callable from any thread. The last release disposes the bundle one dispatcher hop
///     later, because the render thread may still be replaying a picture that draws its radar images.
///     <para>
///         An entry is dropped from the registry as its last hold ends, not when the deferred dispose runs,
///         so a map re-acquired in that window decodes afresh rather than resurrecting a bundle on its way
///         out.
///     </para>
/// </summary>
internal static class MapBundleLeases
{
    private static readonly Dictionary<string, MapBundleEntry> _held = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();
    private static int _decodes;

    /// <summary>Bundles decoded since the process started. For tests: the count "one decode per map" means.</summary>
    internal static int Decodes => Volatile.Read(ref _decodes);

    /// <summary>A hold on the map's bundle, decoded on the first one; null when the map has no bundle.</summary>
    /// <param name="map">The map name.</param>
    public static MapBundleLease? Acquire(string map)
    {
        ArgumentException.ThrowIfNullOrEmpty(map);

        MapBundleEntry entry;
        lock (_gate)
        {
            if (!_held.TryGetValue(map, out MapBundleEntry? found))
            {
                found = new MapBundleEntry(map, Decode);
                _held[map] = found;
            }

            entry = found;
            entry.Holders++;
        }

        // Decoded outside the registry lock: a radar decode is tens of milliseconds, and a pool thread
        // decoding one map must not stall the UI thread acquiring another. The Lazy still decodes once.
        LoadedMapAsset? bundle;
        try
        {
            bundle = entry.Bundle.Value;
        }
        catch
        {
            Retire(entry);
            throw;
        }

        if (bundle is null)
        {
            Retire(entry);
            return null;
        }

        // The overlay lives under the config root, which only the App knows, and the bundle reads zones
        // lazily. The same value every time in the app, so this is write-once in practice.
        bundle.ZoneOverlayDirectory = AppPaths.ZonesDirectory;
        return new MapBundleLease(entry, bundle);
    }

    /// <summary>How many holds are out on a map's bundle. For tests.</summary>
    internal static int HoldersOf(string map)
    {
        lock (_gate)
        {
            return _held.TryGetValue(map, out MapBundleEntry? entry) ? entry.Holders : 0;
        }
    }

    // One hold given back; the lease makes this run at most once per lease.
    internal static void Retire(MapBundleEntry entry)
    {
        LoadedMapAsset? bundle;
        lock (_gate)
        {
            if (--entry.Holders > 0)
            {
                return;
            }

            if (_held.TryGetValue(entry.Map, out MapBundleEntry? held) && ReferenceEquals(held, entry))
            {
                _held.Remove(entry.Map);
            }

            // IsValueCreated is true only after a decode that returned: a factory that threw leaves
            // nothing to release, and a map with no bundle decoded to null.
            bundle = entry.Bundle.IsValueCreated ? entry.Bundle.Value : null;
        }

        if (bundle is not null)
        {
            Dispatcher.UIThread.Post(bundle.Dispose, DispatcherPriority.Background);
        }
    }

    private static LoadedMapAsset? Decode(string map)
    {
        if (MapAssetPipeline.TryLoad(map) is not { } bundle)
        {
            return null;
        }

        Interlocked.Increment(ref _decodes);
        return bundle;
    }
}

// One map's decode and the holds out on it. Holders is read and written under the registry's gate only.
internal sealed class MapBundleEntry(string map, Func<string, LoadedMapAsset?> decode)
{
    public string Map { get; } = map;

    public Lazy<LoadedMapAsset?> Bundle { get; } =
        new(() => decode(map), LazyThreadSafetyMode.ExecutionAndPublication);

    public int Holders;
}

/// <summary>
///     One hold on a map's bundle from <see cref="MapBundleLeases" />. Reads as the bundle, so it goes
///     anywhere an <see cref="IMapAsset" /> does; disposing gives the hold back and never ends another
///     holder's decode.
/// </summary>
internal sealed class MapBundleLease(MapBundleEntry entry, LoadedMapAsset bundle) : IMapAssetLease
{
    private int _released;

    /// <summary>The decoded bundle, for the first-party members that are not on <see cref="IMapAsset" />.</summary>
    internal LoadedMapAsset Bundle { get; } = bundle;

    /// <inheritdoc />
    public string MapName => entry.Map;

    /// <inheritdoc />
    public IReadOnlyList<FloorSlice> Floors => Bundle.Floors;

    /// <inheritdoc />
    public WorldBounds RadarBounds => ((IMapAsset)Bundle).RadarBounds;

    /// <inheritdoc />
    public PlaceResolver? Zones => Bundle.Zones;

    /// <inheritdoc />
    public IReadOnlyList<MapRadarImage> DescribeRadars() => ((IMapAsset)Bundle).DescribeRadars();

    /// <inheritdoc />
    public ILevelRadarBinder CreateRadarBinder() => ((IMapAsset)Bundle).CreateRadarBinder();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            MapBundleLeases.Retire(entry);
        }
    }
}

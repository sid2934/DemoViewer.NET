#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Services.Zones;

/// <summary>
///     The app's zone resolver source: the map's baked <c>zones.json</c> plus the user overlay under
///     <c>&lt;config&gt;/zones/</c>, through <see cref="ZoneAssetPipeline" />, one load per map.
///     <para>
///         A map with no bundle directory or no zones file answers null, and that answer is cached too,
///         so the Query Canvas and the Tolerance Slider take their empirical path without a probe per
///         call. The browser host lands there for every map: no asset directory, no config root.
///     </para>
///     <para>
///         The cache is keyed on the overlay file's stamp as well as the map. An author who saves an
///         edited overlay gets the new names and graph on the next query without a restart, the same
///         way the Playback tab's "Reload zones" picks it up; the baked file is a shipped asset and is
///         not watched.
///     </para>
/// </summary>
public sealed class AssetZonePlaceResolverSource : IZonePlaceResolverSource
{
    private readonly Func<string, string?> _bundleDirFor;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?> _overlayDir;

    /// <summary>The composition root's source: bundles beside the executable, the overlay under the config root.</summary>
    public AssetZonePlaceResolverSource()
        : this(MapAssetBundleReader.FindBundleDirectory, () => AppPaths.ZonesDirectory)
    {
    }

    /// <param name="bundleDirFor">The map's bundle directory, or null when it has none.</param>
    /// <param name="overlayDir">The user's <c>zones/</c> directory, read per call so a config move is seen; null for none.</param>
    public AssetZonePlaceResolverSource(Func<string, string?> bundleDirFor, Func<string?> overlayDir)
    {
        ArgumentNullException.ThrowIfNull(bundleDirFor);
        ArgumentNullException.ThrowIfNull(overlayDir);
        _bundleDirFor = bundleDirFor;
        _overlayDir = overlayDir;
    }

    /// <inheritdoc />
    public IZonePlaceResolver? TryGet(string map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return null;
        }

        string? overlayDir = _overlayDir();
        OverlayStamp stamp = OverlayStamp.Of(ZoneAssetPipeline.OverlayPathFor(overlayDir, map));
        lock (_lock)
        {
            if (_maps.TryGetValue(map, out Entry? cached) && cached.Stamp == stamp
                && string.Equals(cached.OverlayDir, overlayDir, StringComparison.Ordinal))
            {
                return cached.Resolver;
            }

            // The pipeline never throws: a missing, unreadable or malformed zones file is null, and a
            // broken overlay is the baked set plus diagnostics the Playback tab already reports.
            PlaceResolver? loaded = ZoneAssetPipeline.TryLoad(_bundleDirFor(map), overlayDir);
            ZonePlaceResolverAdapter? resolver = loaded is null ? null : new ZonePlaceResolverAdapter(loaded);
            _maps[map] = new Entry(overlayDir, stamp, resolver);
            return resolver;
        }
    }

    private sealed record Entry(string? OverlayDir, OverlayStamp Stamp, ZonePlaceResolverAdapter? Resolver);

    // Existence, length and write time: enough to see a save, cheap enough to check on every call. No
    // path: the map key is case-insensitive, and the directory is compared on the entry.
    private readonly record struct OverlayStamp(bool Exists, long Length, DateTime WrittenUtc)
    {
        public static OverlayStamp Of(string? path)
        {
            if (path is null)
            {
                return default;
            }

            try
            {
                FileInfo file = new(path);
                return file.Exists
                    ? new OverlayStamp(true, file.Length, file.LastWriteTimeUtc)
                    : default;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return default;
            }
        }
    }
}

/// <summary>
///     Zone Baking's <see cref="PlaceResolver" /> in the shape the Round Index asks for: raw place names
///     in and out, the effective version as the stamp. The graph is folded to names once, since the
///     widening asks for neighbours per token on every count.
/// </summary>
public sealed class ZonePlaceResolverAdapter : IZonePlaceResolver
{
    private static readonly IReadOnlySet<string> _none = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _adjacent;

    /// <param name="resolver">The map's resolver over its effective zone set.</param>
    public ZonePlaceResolverAdapter(PlaceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Resolver = resolver;
        IReadOnlyList<ZonePlace> places = resolver.Zones.Places;
        _adjacent = new Dictionary<string, IReadOnlySet<string>>(places.Count, StringComparer.Ordinal);
        foreach (ZonePlace place in places)
        {
            _adjacent[place.Name] = resolver.Adjacent(place.Id)
                .Where(id => id >= 0 && id < places.Count)
                .Select(id => places[id].Name)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The wrapped resolver.</summary>
    public PlaceResolver Resolver { get; }

    /// <inheritdoc />
    public string ZonesVersion => Resolver.Zones.EffectiveVersion;

    /// <inheritdoc />
    public string? Resolve(Vector3 world) => Resolver.Resolve(world).Name;

    /// <inheritdoc />
    public string? ResolveOnFloor(double x, double y, double floorKey) => Resolver.ResolveOnFloor(x, y, floorKey).Name;

    /// <inheritdoc />
    public IReadOnlySet<string> Adjacent(string place) =>
        place is not null && _adjacent.TryGetValue(place, out IReadOnlySet<string>? names) ? names : _none;
}

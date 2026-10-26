#region

using System.Numerics;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D.Zones;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Zones;

/// <summary>
///     The app's zone resolver source: a map's places off one held <see cref="IMapAssetLease" /> per map,
///     which is its baked <c>zones.json</c> plus the user overlay under <c>&lt;config&gt;/zones/</c>.
///     <para>
///         A map with no art or no zones file answers null, and that answer is cached too, so the Query
///         Canvas and the Tolerance Slider take their empirical path without a probe per call. The browser
///         host lands there for every map: no asset directory, no config root.
///     </para>
///     <para>
///         Holding the lease is what makes a saved overlay visible without a restart:
///         <see cref="IMapAssetLease.RefreshZones" /> hands back the same resolver until the file's stamp
///         moves, and only then is the adjacency graph folded again. The baked file is a shipped asset and
///         is not watched.
///     </para>
///     <para>
///         <b>Dispose gives every hold back.</b> The bundle registry is process-wide, so a source left
///         undisposed pins one bundle per map it was asked about for the life of the process.
///     </para>
/// </summary>
public sealed class AssetZonePlaceResolverSource : IZonePlaceResolverSource, IExtensionResident, IDisposable
{
    private readonly Func<string, IMapAssetLease?> _acquire;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _maps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's own map art, through the one bundle registry.</summary>
    public AssetZonePlaceResolverSource()
        : this(MapAssets.Acquire)
    {
    }

    /// <param name="acquire">Takes a hold on a map's art, or answers null when the app ships none for it.</param>
    public AssetZonePlaceResolverSource(Func<string, IMapAssetLease?> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        _acquire = acquire;
    }

    /// <inheritdoc />
    public void Dispose() => Release();

    /// <inheritdoc />
    public IZonePlaceResolver? TryGet(string map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return null;
        }

        if (Cached(map) is { } hit)
        {
            return hit.Lease is null ? null : Refresh(map, hit);
        }

        // Nothing here throws: a map with no art answers null, and a missing or malformed zones file or
        // overlay is null or the baked set plus diagnostics the Playback tab already reports.
        IMapAssetLease? lease = _acquire(map);
        if (lease?.RefreshZones() is not { } loaded)
        {
            lease?.Dispose();
            lock (_lock)
            {
                _maps.TryAdd(map, new Entry(null, null, null));
                return null;
            }
        }

        // Folded outside the lock, so one map's graph never holds up another's lookup.
        ZonePlaceResolverAdapter adapter = new(loaded);
        lock (_lock)
        {
            if (_maps.TryGetValue(map, out Entry? raced))
            {
                lease.Dispose(); // a racing acquire loses to the published one, and its hold goes back
                return raced.Resolver;
            }

            _maps[map] = new Entry(lease, loaded, adapter);
            return adapter;
        }
    }

    /// <inheritdoc />
    /// <remarks>Nothing to subscribe to: a map's graph is read on first use.</remarks>
    public void Attach()
    {
    }

    /// <inheritdoc />
    /// <remarks>Every graph and every hold goes; only the Strat Book reads them, so a pack off has no reader.</remarks>
    public void Release()
    {
        Entry[] dropped;
        lock (_lock)
        {
            dropped = [.. _maps.Values];
            _maps.Clear();
        }

        foreach (Entry entry in dropped)
        {
            entry.Lease?.Dispose();
        }
    }

    // A map already held. One file stat; a resolver that comes back by reference means the graph folded
    // out of it is still the right one.
    private ZonePlaceResolverAdapter? Refresh(string map, Entry entry)
    {
        if (entry.Lease!.RefreshZones() is not { } fresh || ReferenceEquals(fresh, entry.Zones))
        {
            return entry.Resolver;
        }

        ZonePlaceResolverAdapter adapter = new(fresh);
        lock (_lock)
        {
            if (!_maps.TryGetValue(map, out Entry? current))
            {
                return adapter; // released under us: the caller still gets the graph it asked for
            }

            if (!ReferenceEquals(current.Lease, entry.Lease))
            {
                return current.Resolver;
            }

            _maps[map] = current with { Zones = fresh, Resolver = adapter };
            return adapter;
        }
    }

    private Entry? Cached(string map)
    {
        lock (_lock)
        {
            return _maps.GetValueOrDefault(map);
        }
    }

    // Lease and Zones are null together, and that pair is the cached "this map has no places" answer.
    private sealed record Entry(IMapAssetLease? Lease, PlaceResolver? Zones, ZonePlaceResolverAdapter? Resolver);
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
    private readonly StratPlaceCentres _centres;

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

        _centres = StratPlaceCentres.From(resolver.Zones);
        Paths = new NavPathResolver(resolver.Zones);
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

    /// <inheritdoc />
    public (double X, double Y)? PlaceCentre(string place, double floorKey) => _centres.Centre(place, floorKey);

    /// <inheritdoc />
    public (double X, double Y, double FloorKey)? PlaceArrival(string place, double floorKey) => _centres.Arrival(place, floorKey);

    /// <inheritdoc />
    public IReadOnlyList<string> PlaceNames => [.. Resolver.Zones.Places.Select(p => p.Name)];

    /// <inheritdoc />
    public IReadOnlyList<(Vector2 A, Vector2 B)>? OutlineOf(string place, double floorKey) =>
        Resolver.OutlinesFor(floorKey).FirstOrDefault(o => string.Equals(o.Name, place, StringComparison.Ordinal))?.Edges;

    /// <summary>
    ///     The nav graph over the effective zones, built with this adapter inside the queued zones read. A new overlay
    ///     makes a new adapter, so the graph always matches <see cref="ZonesVersion" />.
    /// </summary>
    public PathResolver Paths { get; }
}

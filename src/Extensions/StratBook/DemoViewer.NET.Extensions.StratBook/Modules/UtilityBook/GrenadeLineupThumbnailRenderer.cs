#region

using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Layers;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     Draws a Lineup Card's "landing point on the radar": the map's baked radar art with one marker
///     where the grenade went off, through the same headless path <see cref="Situations.SituationThumbnailRenderer" />
///     draws a Result Card's mini-radar (<c>dv2d render</c>'s stack, the CPU provider, the dark
///     palette). No players; the marker takes the thrower's side colour when the row read one, an
///     unteamed colour otherwise.
///     <para>
///         A map with no baked bundle on this host draws nothing (<see langword="null" />): the card
///         then shows the landing point as text, the same "never load-bearing" rule the library card's
///         radar background follows (<c>MapRadarConverter</c>).
///     </para>
///     <para>
///         One instance serves a batch of cards on the Utility Book tab: one hold per map is taken
///         and given back at <see cref="Dispose" />. Not thread-safe; the tab VM drives it from
///         one worker.
///     </para>
/// </summary>
public sealed class GrenadeLineupThumbnailRenderer : IDisposable
{
    /// <summary>The card's radar size: small and square, enough to read the spot at a glance.</summary>
    public static readonly SKSizeI Size = new(160, 160);

    private readonly Dictionary<string, IMapAssetLease?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IMapAssetLease?> _loadMapAsset;
    private bool _disposed;

    /// <param name="loadMapAsset">Takes a hold on a map's baked bundle; the app's lease registry, a stub in a test.</param>
    public GrenadeLineupThumbnailRenderer(Func<string, IMapAssetLease?>? loadMapAsset = null) =>
        _loadMapAsset = loadMapAsset ?? MapAssets.Acquire;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IMapAssetLease? lease in _assets.Values)
        {
            lease?.Dispose();
        }

        _assets.Clear();
    }

    /// <summary>The PNG for a landing point, or null when the map has no baked bundle on this host.</summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="landing">Where the grenade went off.</param>
    /// <param name="throwerTeam">2 = T, 3 = CT; anything else draws an unteamed marker.</param>
    public byte[]? Render(string map, WorldPoint landing, int throwerTeam)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (AssetFor(map) is not { } asset)
        {
            return null;
        }

        WorldBounds bounds = asset.RadarBounds;
        PlayerMarker marker = new(0, throwerTeam is 2 or 3 ? throwerTeam : 0, landing.X, landing.Y, landing.Z,
            0, RingState.Team, 1.0, "", true);
        SceneTime time = new(0, 0, 0, 1.0 / 64, true);
        Scene2DFrame frame = new()
        {
            Time = time,
            Markers = [marker],
            Map = new SceneMapInfo
            {
                MapName = map,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = asset.DescribeRadars()
            }
        };

        return MapAssets.RenderPng(frame, asset, [SceneLayerIds.Radar, SceneLayerIds.Markers],
            ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY), Size);
    }

    private IMapAssetLease? AssetFor(string map)
    {
        if (!_assets.TryGetValue(map, out IMapAssetLease? asset))
        {
            asset = _loadMapAsset(map);
            _assets[map] = asset;
        }

        return asset;
    }
}

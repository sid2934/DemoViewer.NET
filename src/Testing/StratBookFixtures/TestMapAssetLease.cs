#region

using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Zones;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A hold the test owns, over a bundle the test built: the lease shape without the app's registry, so a
///     test can hand a view model or a renderer synthetic map art and decide for itself when it goes away.
/// </summary>
/// <param name="bundle">The bundle to read.</param>
/// <param name="mapName">The map the hold reads as.</param>
public sealed class TestMapAssetLease(LoadedMapAsset bundle, string mapName) : IMapAssetLease
{
    /// <summary>How many times this hold has been given back.</summary>
    public int Releases { get; private set; }

    /// <inheritdoc />
    public string MapName => mapName;

    /// <inheritdoc />
    public IReadOnlyList<FloorSlice> Floors => bundle.Floors;

    /// <inheritdoc />
    public WorldBounds RadarBounds => ((IMapAsset)bundle).RadarBounds;

    /// <inheritdoc />
    public PlaceResolver? Zones => bundle.Zones;

    /// <inheritdoc />
    public IReadOnlyList<MapRadarImage> DescribeRadars() => ((IMapAsset)bundle).DescribeRadars();

    /// <inheritdoc />
    public ILevelRadarBinder CreateRadarBinder() => ((IMapAsset)bundle).CreateRadarBinder();

    /// <inheritdoc />
    public void Dispose()
    {
        Releases++;
        bundle.Dispose();
    }
}

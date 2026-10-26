#region

using DemoViewer.NET.Playback2D.Zones;

#endregion

namespace DemoViewer.NET.Playback2D.Levels;

/// <summary>
///     A map's baked art, loaded for drawing: its floors, its radar images and the world rectangle they cover,
///     and its places. Not disposable: one decode is shared by every view of the map, so its lifetime belongs
///     to whoever handed it out and a reader must not end it.
/// </summary>
public interface IMapAsset
{
    /// <summary>The map's floor bands, lowest first.</summary>
    IReadOnlyList<FloorSlice> Floors { get; }

    /// <summary>The world rectangle the radar images cover.</summary>
    WorldBounds RadarBounds { get; }

    /// <summary>The map's places, or null when the bundle has no zones file. Read on first use.</summary>
    PlaceResolver? Zones { get; }

    /// <summary>The radar images with the heights each covers, as a frame's <see cref="SceneMapInfo.Radars" /> takes them.</summary>
    IReadOnlyList<MapRadarImage> DescribeRadars();

    /// <summary>A binder that gives each floor band its radar image.</summary>
    ILevelRadarBinder CreateRadarBinder();
}

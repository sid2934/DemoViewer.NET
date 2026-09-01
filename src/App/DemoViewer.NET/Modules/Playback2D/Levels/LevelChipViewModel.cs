#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Levels;

/// <summary>
///     One button in the level strip: a floor's short name, its Z band, whether the map baked a radar
///     picture for it, and whether it is the one being shown.
/// </summary>
public sealed partial class LevelChipViewModel : ObservableObject
{
    /// <summary>The glyph shown next to a level the map has no baked radar for.</summary>
    public const string NoRadarGlyph = "⌀";

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Creates a chip for one level.</summary>
    /// <param name="level">The level it selects.</param>
    /// <param name="isActive">Whether it is the level currently shown.</param>
    public LevelChipViewModel(MapLevel level, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(level);

        Id = level.Id;
        Label = level.Name;
        HasRadar = level.HasRadar;

        // Same format as the pre-v2 band caption, so the strip and the on-canvas floor label read as
        // one thing rather than two spellings of the same number.
        ZRange = string.Create(CultureInfo.InvariantCulture, $"z[{level.ZMin:F0}..{level.ZMax:F0}]");
        _isActive = isActive;
    }

    /// <summary>The level's stable identity.</summary>
    public MapLevelId Id { get; }

    /// <summary>Short display name, e.g. <c>L1</c>.</summary>
    public string Label { get; }

    /// <summary>The band, e.g. <c>z[-416..-111]</c>.</summary>
    public string ZRange { get; }

    /// <summary>Whether the map bundle bound a radar image to this level.</summary>
    public bool HasRadar { get; }

    /// <summary>True when there is no baked radar. Drives the glyph's visibility.</summary>
    public bool HasNoRadar => !HasRadar;

    /// <summary>
    ///     Tooltip. The no-radar case says so explicitly: the canvas falls back to the grid, and a user
    ///     who is not told will read that as a broken map rather than a missing asset.
    /// </summary>
    public string Tooltip => HasRadar
        ? $"{Label}  {ZRange}"
        : $"{Label}  {ZRange} — no baked radar for this level (the grid shows through)";
}

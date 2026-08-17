#region

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.ViewModels.Library;

/// <summary>
///     Maps a demo's raw map name (e.g. <c>de_dust2</c>) to its baked radar thumbnail for use as the library
///     card background. Decoded ONCE per distinct map and cached (a small downscaled bitmap), so a large card
///     grid never re-decodes on scroll. Returns null when the map has no baked bundle (the dev
///     <c>cs2-assets/baked</c> cache only covers a few maps) — the card then falls back to its accent
///     background, so the radar is a progressive enhancement, never load-bearing.
/// </summary>
public sealed class MapRadarConverter : IValueConverter
{
    // A card is ~196px wide; 256 keeps it crisp on hi-DPI while staying ~16× smaller in memory than the
    // full 1024² radar. Cache null too, so a map without a baked bundle isn't probed on every re-filter.
    private const int ThumbWidth = 256;

    /// <summary>Shared singleton for XAML <c>{x:Static}</c> use.</summary>
    public static readonly MapRadarConverter Instance = new();

    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string map || string.IsNullOrWhiteSpace(map))
        {
            return null;
        }

        return _cache.GetOrAdd(map, m => MapAssetLoader.TryLoadRadarThumbnail(m, ThumbWidth));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

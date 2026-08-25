#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     One decoded radar layer for the current map: the image plus the world rectangle it spans and
///     the Z band it depicts. Replaces the Avalonia <c>Bitmap</c> the old viewport held, so the radar
///     draw is renderer-agnostic.
/// </summary>
public sealed class MapRadarImage
{
    /// <summary>The bundle's image file name — the stable key, and what a fixture round-trips.</summary>
    public required string Name { get; init; }

    /// <summary>The decoded image, or null when undecoded or unavailable (fixtures never carry pixels).</summary>
    public SKImage? Image { get; init; }

    /// <summary>The world rectangle this image spans, from the bundle's radar transform.</summary>
    public required WorldBounds Bounds { get; init; }

    /// <summary>Lower world Z of the band this layer depicts.</summary>
    public double MinZ { get; init; }

    /// <summary>Upper world Z of the band this layer depicts.</summary>
    public double MaxZ { get; init; }
}

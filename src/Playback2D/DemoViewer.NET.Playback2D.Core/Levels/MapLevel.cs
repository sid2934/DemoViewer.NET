#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     One rendered floor of a map: a Z band, a display name, and the radar image bound to it.
///     <para>
///         A <b>class</b>, not a record struct, because <see cref="Radar" /> is rebound in place when a
///         map's assets finish decoding; a value type would hand every holder a stale copy. Everything
///         else is <c>init</c>-only: a level's band is replaced by a rebuild, never edited underneath a
///         pane.
///     </para>
/// </summary>
public sealed class MapLevel
{
    /// <summary>Stable identity across rebuilds.</summary>
    public required MapLevelId Id { get; init; }

    /// <summary>Display name. May reorder across rebuilds; never key anything on it.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Lower world Z of the band, exactly as <see cref="FloorSplitter" /> emitted it.
    ///     <para>
    ///         <b>Not quantized.</b> Quantization mints <see cref="Id" />; the band itself stays raw so
    ///         <see cref="MapSpace.LevelIndexFor" /> keeps answering exactly what
    ///         <see cref="FloorSplitter.SliceIndexFor" /> answers.
    ///     </para>
    /// </summary>
    public required double ZMin { get; init; }

    /// <summary>Upper world Z of the band; always greater than <see cref="ZMin" />.</summary>
    public required double ZMax { get; init; }

    /// <summary>The radar image bound to this level, or null when the map has none for it.</summary>
    public SKImage? Radar { get; internal set; }

    /// <summary>The bundle file name of <see cref="Radar" />, for diagnostics and fixtures.</summary>
    public string? RadarImageName { get; internal set; }

    /// <summary>Whether a radar image is bound. The UI says "no radar for this level" when false.</summary>
    public bool HasRadar => Radar is not null;

    /// <summary>Band height in world units.</summary>
    public double Span => ZMax - ZMin;

    /// <summary>Band centre: the tiebreaker when a Z falls in a gap between bands.</summary>
    public double MidZ => (ZMin + ZMax) / 2;

    /// <summary>
    ///     Whether <paramref name="z" /> falls inside this band. <b>Inclusive at both ends</b>, matching
    ///     <c>FloorSlice.Contains</c>. The pre-v2 assignment is contains-first, and a half-open band would
    ///     move every player standing on a boundary to a different floor.
    /// </summary>
    /// <param name="z">World Z.</param>
    public bool Contains(double z) => z >= ZMin && z <= ZMax;
}

/// <summary>How confidently radar images were matched to levels.</summary>
public enum RadarBindingQuality
{
    /// <summary>No radar layers at all, or none bound.</summary>
    None,

    /// <summary>Every level bound a radar image by Z-band overlap.</summary>
    Exact,

    /// <summary>
    ///     Some level could not be bound by overlap, or several levels share one image because the
    ///     bundle publishes no per-layer Z metadata. The UI should say so.
    /// </summary>
    Degraded
}

#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     A level's stable identity, minted from its quantized lower Z (<see cref="MapSpace.QuantizeZ" />).
///     <para>
///         <b>Never a level INDEX.</b> Design risk 5 is the confusion between "the third band from the
///         bottom" and "the band that is Nuke's lower floor": insert a basement and every index shifts
///         while every identity holds. Panes, cameras, picture caches and (from B2) annotations are all
///         keyed on this, so making it a distinct struct means the compiler rejects the mix-up that
///         would otherwise silently repaint one floor with another's camera.
///     </para>
/// </summary>
/// <param name="Key">The quantized lower Z. Opaque — compare it, never do arithmetic on it.</param>
public readonly record struct MapLevelId(int Key)
{
    /// <summary>The "no level" sentinel. Distinct from every real id, including a level at Z 0.</summary>
    public static MapLevelId None => new(int.MinValue);

    /// <summary>Whether this is <see cref="None" />.</summary>
    public bool IsNone => Key == int.MinValue;
}

/// <summary>
///     One rendered floor of a map: a Z band, a display name, and the radar image bound to it.
///     <para>
///         A <b>class</b>, not a record struct, because <see cref="Radar" /> is rebound in place when a
///         map's assets finish decoding — a value type would hand every holder a stale copy. Everything
///         else is <c>init</c>-only: a level's band is replaced by a rebuild, never edited underneath a
///         pane.
///     </para>
/// </summary>
public sealed class MapLevel
{
    /// <summary>Stable identity across rebuilds.</summary>
    public required MapLevelId Id { get; init; }

    /// <summary>Display name. May reorder across rebuilds — never key anything on it.</summary>
    public required string Name { get; init; }

    /// <summary>Lower world Z of the band.</summary>
    public required double ZMin { get; init; }

    /// <summary>Upper world Z of the band.</summary>
    public required double ZMax { get; init; }

    /// <summary>The radar image bound to this level, or null when the map has none for it.</summary>
    public SKImage? Radar { get; internal set; }

    /// <summary>The bundle file name of <see cref="Radar" />, for diagnostics and fixtures.</summary>
    public string? RadarImageName { get; internal set; }

    /// <summary>Whether a radar image is bound. The UI says "no radar for this level" when false.</summary>
    public bool HasRadar => Radar is not null;

    /// <summary>Band height in world units.</summary>
    public double Span => ZMax - ZMin;

    /// <summary>Band centre — the tiebreaker when a Z falls in a gap between bands.</summary>
    public double MidZ => (ZMin + ZMax) / 2;

    /// <summary>
    ///     Whether <paramref name="z" /> falls inside this band. <b>Inclusive at both ends</b>, matching
    ///     <c>FloorSlice.Contains</c> exactly — the pre-v2 assignment is contains-first, and a half-open
    ///     band would move every player standing on a boundary to a different floor.
    /// </summary>
    /// <param name="z">World Z.</param>
    public bool Contains(double z) => z >= ZMin && z <= ZMax;
}

/// <summary>How confidently radar images were matched to levels (plan §4 T5's three rules).</summary>
public enum RadarBindingQuality
{
    /// <summary>No radar layers at all, or none bound.</summary>
    None,

    /// <summary>One radar layer per level, matched by ascending Z.</summary>
    Exact,

    /// <summary>Counts disagreed — every level shows the highest-altitude image. The UI should say so.</summary>
    Degraded
}

/// <summary>
///     What one <see cref="MapSpace.Rebuild" /> did to the level set. <c>PaneSet</c> reconciles against
///     it, and B3's hysteresis and level-crossing buffers key off the added/removed ids.
/// </summary>
/// <param name="Changed">False when the rebuild was a no-op (an identical band list).</param>
/// <param name="Added">Ids present after the rebuild but not before.</param>
/// <param name="Removed">Ids present before but not after.</param>
/// <param name="Retained">Ids present in both — the ones whose panes keep their camera.</param>
public sealed record LevelSetChange(
    bool Changed,
    IReadOnlyList<MapLevelId> Added,
    IReadOnlyList<MapLevelId> Removed,
    IReadOnlyList<MapLevelId> Retained)
{
    /// <summary>The "nothing happened" result. Shared, so an idempotent rebuild allocates nothing.</summary>
    public static readonly LevelSetChange None = new(false, [], [], []);
}

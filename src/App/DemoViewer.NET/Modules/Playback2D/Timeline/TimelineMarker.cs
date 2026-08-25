namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     A point event on the timeline. ARGB 0 = "use the track/kind default" (the host themes it) — a track
///     never reaches for a brush, which is what keeps this contract renderer-independent.
///     <para>
///         <see cref="FrameIndex" /> is the layout axis; <see cref="Tick" /> is carried alongside so a
///         consumer can show it or seek by it without re-resolving.
///     </para>
/// </summary>
public readonly record struct TimelineMarker(
    string TrackId,
    int FrameIndex,
    int Tick,
    TimelineMarkerKind Kind,
    string Glyph,
    string Tooltip,
    uint Argb);

/// <summary>
///     An inclusive frame range on the timeline (rounds today; segments later). ARGB 0 = track default,
///     as for <see cref="TimelineMarker" />.
/// </summary>
public readonly record struct TimelineBand(
    string TrackId,
    int StartFrameIndex,
    int EndFrameIndex,
    string Label,
    string Tooltip,
    uint Argb);

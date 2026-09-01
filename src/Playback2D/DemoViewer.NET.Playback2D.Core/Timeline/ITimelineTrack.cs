namespace DemoViewer.NET.Playback2D.Core.Timeline;

/// <summary>
///     What a <see cref="TimelineMarker" /> represents. The host maps a kind to a theme token, so a track
///     never names a colour: it may hand back ARGB 0 and let the kind decide.
/// </summary>
public enum TimelineMarkerKind
{
    Round,
    Kill,
    BombPlant,
    BombDefuse,
    BombExplode,
    Annotation,
    Custom
}

/// <summary>
///     One contributor of timeline content. Registration order is display order within its row.
///     Implementations are stateless w.r.t. the demo: everything comes from <see cref="ITimelineData" />.
/// </summary>
public interface ITimelineTrack
{
    /// <summary>Stable key: feature gates, settings, track toggles. Never renamed once shipped.</summary>
    string Id { get; }

    /// <summary>Human-readable name for the track-toggle chrome.</summary>
    string DisplayName { get; }

    /// <summary>False when this demo carries none of the events the track needs.</summary>
    bool IsAvailable(ITimelineData data);

    /// <summary>Point markers, ascending by frame index. Empty for band-only tracks.</summary>
    IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data);

    /// <summary>Range bands, ascending and non-overlapping. Empty for point-only tracks.</summary>
    IReadOnlyList<TimelineBand> BuildBands(ITimelineData data);

    /// <summary>Raised when the track's content changed and the host must re-query it.</summary>
    event Action? MarkersChanged;
}

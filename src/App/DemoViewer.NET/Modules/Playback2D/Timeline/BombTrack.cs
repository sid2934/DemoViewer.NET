namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     Plant / defuse / explode markers. Each of the three events is optional and contributes independently,
///     so a demo that only ever saw a plant shows only plants (no empty sub-row, no phantom markers).
/// </summary>
public sealed class BombTrack : ITimelineTrack
{
    /// <summary>The plant event.</summary>
    public const string PlantedEvent = "bomb_planted";

    /// <summary>The defuse event.</summary>
    public const string DefusedEvent = "bomb_defused";

    /// <summary>The detonation event.</summary>
    public const string ExplodedEvent = "bomb_exploded";

    private const string PlantGlyph = "◆";
    private const string DefuseGlyph = "✂";
    private const string ExplodeGlyph = "✸";

    /// <inheritdoc />
    public string Id => "bomb";

    /// <inheritdoc />
    public string DisplayName => "Bomb";

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data) =>
        data is not null
        && (data.HasEvent(PlantedEvent) || data.HasEvent(DefusedEvent) || data.HasEvent(ExplodedEvent));

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data)
    {
        if (data is null)
        {
            return Array.Empty<TimelineMarker>();
        }

        List<TimelineMarker> markers = new();
        Collect(data, PlantedEvent, TimelineMarkerKind.BombPlant, PlantGlyph, "Bomb planted", markers);
        Collect(data, DefusedEvent, TimelineMarkerKind.BombDefuse, DefuseGlyph, "Bomb defused", markers);
        Collect(data, ExplodedEvent, TimelineMarkerKind.BombExplode, ExplodeGlyph, "Bomb exploded", markers);

        if (markers.Count == 0)
        {
            return Array.Empty<TimelineMarker>();
        }

        markers.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return markers;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => Array.Empty<TimelineBand>();

    /// <inheritdoc />
    // Declared but never raised in A1 — see RoundTrack.MarkersChanged.
#pragma warning disable CS0067
    public event Action? MarkersChanged;
#pragma warning restore CS0067

    private void Collect(ITimelineData data, string eventName, TimelineMarkerKind kind, string glyph,
        string label, List<TimelineMarker> into)
    {
        if (!data.HasEvent(eventName))
        {
            return;
        }

        foreach (TimelineEventRecord record in data.EventsOfType(eventName))
        {
            string tooltip = record.Fields.TryGetValue(TimelineEventKeys.Site, out string? site) && site.Length > 0
                ? $"{label} ({site})"
                : label;

            into.Add(new TimelineMarker(Id, record.FrameIndex, record.Tick, kind, glyph, tooltip, 0));
        }
    }
}

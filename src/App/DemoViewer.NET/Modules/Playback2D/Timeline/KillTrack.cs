namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     One marker per <c>player_death</c>. Stateless: it allocates only inside
///     <see cref="BuildMarkers" />, which the host calls once per demo.
/// </summary>
public sealed class KillTrack : ITimelineTrack
{
    /// <summary>The event this track is built from.</summary>
    public const string DeathEvent = "player_death";

    private const string KillGlyph = "×";

    /// <inheritdoc />
    public string Id => "kill";

    /// <inheritdoc />
    public string DisplayName => "Kills";

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data) => data is not null && data.HasEvent(DeathEvent);

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data)
    {
        if (data is null)
        {
            return Array.Empty<TimelineMarker>();
        }

        IReadOnlyList<TimelineEventRecord> records = data.EventsOfType(DeathEvent);
        if (records.Count == 0)
        {
            return Array.Empty<TimelineMarker>();
        }

        List<TimelineMarker> markers = new(records.Count);
        foreach (TimelineEventRecord record in records)
        {
            markers.Add(new TimelineMarker(
                Id,
                record.FrameIndex,
                record.Tick,
                TimelineMarkerKind.Kill,
                KillGlyph,
                Describe(record),
                0));
        }

        return markers;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => Array.Empty<TimelineBand>();

    /// <inheritdoc />
    // Declared but never raised in A1 — see RoundTrack.MarkersChanged.
#pragma warning disable CS0067
    public event Action? MarkersChanged;
#pragma warning restore CS0067

    private static string Describe(TimelineEventRecord record)
    {
        string attacker = Field(record, TimelineEventKeys.Attacker, "world");
        string victim = Field(record, TimelineEventKeys.Victim, "world");
        string weapon = Field(record, TimelineEventKeys.Weapon, "");
        bool headshot = string.Equals(Field(record, TimelineEventKeys.Headshot, ""), "1", StringComparison.Ordinal);

        string body = weapon.Length > 0 ? $"{attacker} → {victim} ({weapon})" : $"{attacker} → {victim}";
        return headshot ? $"{body} HS" : body;
    }

    private static string Field(TimelineEventRecord record, string key, string fallback) =>
        record.Fields.TryGetValue(key, out string? value) && value.Length > 0 ? value : fallback;
}

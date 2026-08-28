#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

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

    // The crediting side's colour, mirroring RoundTrack.ApplyWinnerTints: ARGB, not a brush. 0 means
    // "host, use the kind default", where an unknown side lands.
    //
    // FULL alpha, not RoundTrack's 0x38 wash. The wash exists because a band is a 300 px rectangle behind
    // a label, where a fifth of an alpha still reads as a side; a marker is an eight-pixel glyph, and the
    // same wash on it reads as "nothing was drawn here" rather than as T.
    private const uint TintTeamT = 0xFFE0A030;
    private const uint TintTeamCt = 0xFF4A90D9;
    private const uint TintUnknown = 0;

    private const int TeamT = 2;
    private const int TeamCt = 3;

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
                TintFor(record)));
        }

        return markers;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => Array.Empty<TimelineBand>();

    /// <inheritdoc />
    // Never raised; see RoundTrack.MarkersChanged.
#pragma warning disable CS0067
    public event Action? MarkersChanged;
#pragma warning restore CS0067

    // Colours the marker by WHO GOT THE KILL, so a run of one colour reads as a side winning fights.
    // Every miss lands on TintUnknown: a demo with no player_team, a world/suicide death, a side number
    // outside the two the game has. A kill never loses its marker over an unresolvable side.
    private static uint TintFor(TimelineEventRecord record)
    {
        if (!record.Fields.TryGetValue(TimelineEventKeys.Team, out string? value)
            || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int team))
        {
            return TintUnknown;
        }

        return team switch
        {
            TeamT => TintTeamT,
            TeamCt => TintTeamCt,
            _ => TintUnknown
        };
    }

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

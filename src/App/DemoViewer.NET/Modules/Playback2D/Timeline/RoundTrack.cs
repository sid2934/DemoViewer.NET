#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     The rounds band. CS2 rounds OPEN at <c>round_freeze_end</c> (not <c>round_start</c>), so band
///     <c>i</c> spans <c>[freeze[i], freeze[i+1] - 1]</c> and the last band runs to the end of the demo;
///     anything before the first freeze-end is one <c>warmup</c> band. Round numbers are 1-based ordinals
///     over the freeze-end list — the track deliberately does NOT read <c>m_totalRoundsPlayed</c>, which
///     would put a per-frame entity read into chrome.
/// </summary>
public sealed class RoundTrack : ITimelineTrack
{
    /// <summary>The event that opens a round. The whole band layout keys off it.</summary>
    public const string FreezeEndEvent = "round_freeze_end";

    /// <summary>The event carrying the winning team, used only for the band tint.</summary>
    public const string RoundEndEvent = "round_end";

    // Low-alpha team washes for the won-by tint, mirroring the Pb2dTeamT / Pb2dTeamCt HUD tokens. A track
    // may not reach for a brush (this folder is renderer-independent), so it hands back ARGB and the host
    // themes everything else; 0 means "use the host's neutral band default".
    private const uint TintNeutral = 0;
    private const uint TintTeamT = 0x38E0A030;
    private const uint TintTeamCt = 0x384A90D9;

    private const int TeamT = 2;
    private const int TeamCt = 3;

    /// <inheritdoc />
    public string Id => "round";

    /// <inheritdoc />
    public string DisplayName => "Rounds";

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data) => data is not null && data.HasEvent(FreezeEndEvent);

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data)
    {
        if (data is null)
        {
            return Array.Empty<TimelineBand>();
        }

        int total = data.TotalFrames;
        IReadOnlyList<int> freeze = data.FramesForEvent(FreezeEndEvent);
        if (total <= 0 || freeze.Count == 0)
        {
            return Array.Empty<TimelineBand>();
        }

        List<TimelineBand> bands = new(freeze.Count + 1);
        int last = total - 1;

        if (freeze[0] > 0)
        {
            bands.Add(new TimelineBand(Id, 0, Math.Min(freeze[0] - 1, last), "wu", "Warmup", TintNeutral));
        }

        for (int i = 0; i < freeze.Count; i++)
        {
            int start = freeze[i];
            if (start > last)
            {
                break;
            }

            int end = i + 1 < freeze.Count ? Math.Min(freeze[i + 1] - 1, last) : last;
            if (end < start)
            {
                continue;
            }

            string label = (i + 1).ToString(CultureInfo.InvariantCulture);
            bands.Add(new TimelineBand(Id, start, end, label, $"Round {label}", TintNeutral));
        }

        ApplyWinnerTints(data, bands);
        return bands;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => Array.Empty<TimelineMarker>();

    /// <inheritdoc />
    // Declared but never raised in A1: round data is fixed after parse. The member exists so B2's
    // AnnotationTrack — whose content DOES change — implements the same interface.
#pragma warning disable CS0067
    public event Action? MarkersChanged;
#pragma warning restore CS0067

    // Matches each round_end to the band containing it and repaints that band with the winner's wash. A
    // demo without round_end (truncated / warmup-only) simply keeps every band neutral.
    private static void ApplyWinnerTints(ITimelineData data, List<TimelineBand> bands)
    {
        foreach (TimelineEventRecord record in data.EventsOfType(RoundEndEvent))
        {
            int index = IndexOfBandContaining(bands, record.FrameIndex);
            if (index < 0)
            {
                continue;
            }

            if (!record.Fields.TryGetValue(TimelineEventKeys.Winner, out string? winner)
                || !int.TryParse(winner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int team))
            {
                continue;
            }

            uint tint = team switch
            {
                TeamT => TintTeamT,
                TeamCt => TintTeamCt,
                _ => TintNeutral
            };

            if (tint == TintNeutral)
            {
                continue;
            }

            TimelineBand band = bands[index];
            bands[index] = band with
            {
                Argb = tint,
                Tooltip = $"{band.Tooltip} · won by {(team == TeamT ? "T" : "CT")}"
            };
        }
    }

    private static int IndexOfBandContaining(List<TimelineBand> bands, int frameIndex)
    {
        for (int i = 0; i < bands.Count; i++)
        {
            if (frameIndex >= bands[i].StartFrameIndex && frameIndex <= bands[i].EndFrameIndex)
            {
                return i;
            }
        }

        return -1;
    }
}

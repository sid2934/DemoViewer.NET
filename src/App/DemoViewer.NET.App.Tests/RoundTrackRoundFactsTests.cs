#region

using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round band's winner tint reads Round Facts when rows exist (owner decision 6): a Valve
///     matchmaking demo carries no <c>round_end</c>, so the tint had never appeared on one. Without rows
///     the track behaves exactly as it did, which <see cref="TimelineTrackTests" /> pins.
/// </summary>
public class RoundTrackRoundFactsTests
{
    private static RoundFacts Round(int number, int winner) => new()
    {
        Number = number,
        IsLive = true,
        WinnerSide = winner
    };

    [Test]
    public async Task WithRows_BandsTintByRoundNumber_AndRoundEndIsNotConsulted()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [50, 100, 200];
        // A round_end that would paint round 1 for T; the facts say CT, and the facts win.
        data.Events["round_end"] = [new TimelineEventRecord(90, 90, new Dictionary<string, string> { ["winner"] = "2" })];
        RoundTrack track = new()
        {
            Facts = [Round(1, 3), Round(2, 0), Round(3, 2)]
        };

        IReadOnlyList<TimelineBand> bands = track.BuildBands(data);

        using (Assert.Multiple())
        {
            await Assert.That(bands.Count).IsEqualTo(4).Because("warmup plus three rounds; the layout is untouched");
            await Assert.That(bands[0].Label).IsEqualTo("wu");
            await Assert.That(bands[0].Argb).IsEqualTo(0u);
            await Assert.That(bands[1].Tooltip).Contains("won by CT");
            await Assert.That(bands[2].Argb).IsEqualTo(0u).Because("an undecided round keeps the neutral band");
            await Assert.That(bands[3].Tooltip).Contains("won by T");
            await Assert.That(bands[1].Argb).IsNotEqualTo(bands[3].Argb);
        }
    }

    [Test]
    public async Task WithoutRows_TheRoundEndTintIsUnchanged()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [0, 100];
        data.Events["round_end"] = [new TimelineEventRecord(90, 90, new Dictionary<string, string> { ["winner"] = "2" })];
        RoundTrack track = new()
        {
            Facts = []
        };

        IReadOnlyList<TimelineBand> bands = track.BuildBands(data);

        using (Assert.Multiple())
        {
            await Assert.That(track.Facts).IsNull().Because("an empty list is no rows");
            await Assert.That(bands[0].Tooltip).Contains("won by T");
            await Assert.That(bands[1].Argb).IsEqualTo(0u);
        }
    }

    [Test]
    public async Task RefreshTints_AsksTheTimelineToReQuery()
    {
        RoundTrack track = new();
        int raised = 0;
        track.MarkersChanged += () => raised++;

        track.Facts = [Round(1, 3)];
        track.RefreshTints();

        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task ARoundBeyondTheBands_IsIgnored()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [0, 100];
        RoundTrack track = new()
        {
            Facts = [Round(1, 2), Round(7, 3)]
        };

        IReadOnlyList<TimelineBand> bands = track.BuildBands(data);

        using (Assert.Multiple())
        {
            await Assert.That(bands.Count).IsEqualTo(2);
            await Assert.That(bands[0].Tooltip).Contains("won by T");
            await Assert.That(bands[1].Argb).IsEqualTo(0u);
        }
    }
}

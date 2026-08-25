#region

using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="RoundTrack" /> / <see cref="KillTrack" /> / <see cref="BombTrack" /> against a hand-rolled
///     <see cref="ITimelineData" />. No host, no Avalonia, no demo — which is the point of the contract
///     being defined over primitives: these are the tests that must keep passing verbatim after B1 moves the
///     types to Playback2D.Core.
/// </summary>
public class TimelineTrackTests
{
    [Test]
    public async Task RoundTrack_BuildsOneBandPerFreezeEnd()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [0, 100, 200];

        IReadOnlyList<TimelineBand> bands = new RoundTrack().BuildBands(data);

        await Assert.That(bands.Count).IsEqualTo(3);
        await Assert.That(bands[0].Label).IsEqualTo("1");
        await Assert.That(bands[0].StartFrameIndex).IsEqualTo(0);
        await Assert.That(bands[0].EndFrameIndex).IsEqualTo(99);
        await Assert.That(bands[1].StartFrameIndex).IsEqualTo(100);
        await Assert.That(bands[2].Label).IsEqualTo("3");
    }

    [Test]
    public async Task RoundTrack_PrependsWarmupBandWhenFirstFreezeEndIsNotFrameZero()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [50, 200];

        IReadOnlyList<TimelineBand> bands = new RoundTrack().BuildBands(data);

        await Assert.That(bands.Count).IsEqualTo(3);
        await Assert.That(bands[0].Label).IsEqualTo("wu");
        await Assert.That(bands[0].StartFrameIndex).IsEqualTo(0);
        await Assert.That(bands[0].EndFrameIndex).IsEqualTo(49);

        // Round numbering is a 1-based ordinal over the freeze-end list — the warmup band is not round 1.
        await Assert.That(bands[1].Label).IsEqualTo("1");
    }

    [Test]
    public async Task RoundTrack_LastBandEndsAtLastFrame()
    {
        FakeTimelineData data = new(500);
        data.EventFrames["round_freeze_end"] = [0, 100];

        IReadOnlyList<TimelineBand> bands = new RoundTrack().BuildBands(data);

        await Assert.That(bands[^1].EndFrameIndex).IsEqualTo(499);
    }

    [Test]
    public async Task RoundTrack_TintsBandFromRoundEndWinner()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [0, 100];
        data.Events["round_end"] =
        [
            Record(90, 90, ("winner", "2")),
            Record(290, 290, ("winner", "3"))
        ];

        IReadOnlyList<TimelineBand> bands = new RoundTrack().BuildBands(data);

        await Assert.That(bands[0].Argb).IsNotEqualTo(0u);
        await Assert.That(bands[1].Argb).IsNotEqualTo(0u);
        await Assert.That(bands[0].Argb).IsNotEqualTo(bands[1].Argb);
        await Assert.That(bands[0].Tooltip).Contains("won by T");
        await Assert.That(bands[1].Tooltip).Contains("won by CT");
    }

    [Test]
    public async Task RoundTrack_UnavailableWhenDemoHasNoFreezeEnd()
    {
        FakeTimelineData data = new(300);
        RoundTrack track = new();

        await Assert.That(track.IsAvailable(data)).IsFalse();
        await Assert.That(track.BuildBands(data).Count).IsEqualTo(0);
    }

    [Test]
    public async Task KillTrack_MarkerPerDeath_SortedByFrame()
    {
        FakeTimelineData data = new(1000);
        data.Events["player_death"] =
        [
            Record(64, 10),
            Record(640, 100),
            Record(1280, 200)
        ];

        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        await Assert.That(markers.Count).IsEqualTo(3);
        await Assert.That(markers[0].FrameIndex).IsEqualTo(10);
        await Assert.That(markers[2].FrameIndex).IsEqualTo(200);
        await Assert.That(markers[1].Kind).IsEqualTo(TimelineMarkerKind.Kill);
        await Assert.That(markers[1].TrackId).IsEqualTo("kill");
    }

    [Test]
    public async Task KillTrack_TooltipCarriesAttackerVictimWeapon()
    {
        FakeTimelineData data = new(1000);
        data.Events["player_death"] =
        [
            Record(64, 10, ("attacker", "s1mple"), ("victim", "device"), ("weapon", "ak47"),
                ("headshot", "1"))
        ];

        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        await Assert.That(markers[0].Tooltip).Contains("s1mple");
        await Assert.That(markers[0].Tooltip).Contains("device");
        await Assert.That(markers[0].Tooltip).Contains("ak47");
        await Assert.That(markers[0].Tooltip).Contains("HS");
    }

    [Test]
    public async Task KillTrack_DropsEventsPastEndOfFrameList()
    {
        // The DROP happens in the adapter (an unresolvable tick yields no record at all) — the track then
        // simply never sees it, which is what keeps a -1 frame index out of the layout.
        FakeTimelineData data = new(100);
        data.Events["player_death"] = [Record(64, 10)];
        data.Ticks[9999] = -1;

        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        await Assert.That(markers.Count).IsEqualTo(1);
        await Assert.That(markers.All(m => m.FrameIndex >= 0)).IsTrue();
    }

    [Test]
    public async Task BombTrack_ProducesPlantDefuseExplodeKinds()
    {
        FakeTimelineData data = new(1000);
        data.Events["bomb_planted"] = [Record(100, 20, ("site", "A"))];
        data.Events["bomb_defused"] = [Record(200, 40)];
        data.Events["bomb_exploded"] = [Record(300, 60)];

        IReadOnlyList<TimelineMarker> markers = new BombTrack().BuildMarkers(data);

        await Assert.That(markers.Count).IsEqualTo(3);
        await Assert.That(markers[0].Kind).IsEqualTo(TimelineMarkerKind.BombPlant);
        await Assert.That(markers[0].Tooltip).Contains("A");
        await Assert.That(markers[1].Kind).IsEqualTo(TimelineMarkerKind.BombDefuse);
        await Assert.That(markers[2].Kind).IsEqualTo(TimelineMarkerKind.BombExplode);
    }

    [Test]
    public async Task BombTrack_UnavailableWithoutBombEvents()
    {
        FakeTimelineData data = new(1000);
        data.Events["player_death"] = [Record(64, 10)];

        await Assert.That(new BombTrack().IsAvailable(data)).IsFalse();
    }

    internal static TimelineEventRecord Record(int tick, int frameIndex, params (string Key, string Value)[] fields)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in fields)
        {
            map[key] = value;
        }

        return new TimelineEventRecord(tick, frameIndex, map);
    }
}

/// <summary>Hand-rolled <see cref="ITimelineData" />: dictionaries in, primitives out.</summary>
internal sealed class FakeTimelineData : ITimelineData
{
    public FakeTimelineData(int totalFrames) => TotalFrames = totalFrames;

    public Dictionary<string, int[]> EventFrames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TimelineEventRecord[]> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, int> Ticks { get; } = new();

    public int TotalFrames { get; }
    public int TickRate => 64;

    public int FrameIndexAtTick(int tick) => Ticks.TryGetValue(tick, out int frame) ? frame : tick / 2;

    public IReadOnlyList<int> FramesForEvent(string eventName) =>
        EventFrames.TryGetValue(eventName, out int[]? frames) ? frames : Array.Empty<int>();

    public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName) =>
        Events.TryGetValue(eventName, out TimelineEventRecord[]? records)
            ? records
            : Array.Empty<TimelineEventRecord>();

    public bool HasEvent(string eventName) =>
        EventFrames.ContainsKey(eventName) || Events.ContainsKey(eventName);
}

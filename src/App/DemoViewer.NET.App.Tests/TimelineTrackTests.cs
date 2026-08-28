#region

using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="RoundTrack" /> / <see cref="KillTrack" /> / <see cref="BombTrack" /> against a hand-rolled
///     <see cref="ITimelineData" />. No host, no Avalonia, no demo — which is the point of the contract
///     being defined over primitives: these are the tests that must keep passing verbatim regardless of
///     which namespace the types live in.
/// </summary>
public class TimelineTrackTests
{
    // The two side tints, spelled out here because a test that only says "different" is not a test of
    // WHICH. They are KillTrack's own private constants and RoundTrack's at full alpha; the shared value
    // is asserted below, so a re-theme that moves one has to move both or say why.
    private const uint TintTeamT = 0xFFE0A030; // amber
    private const uint TintTeamCt = 0xFF4A90D9; // blue

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

    /// <summary>
    ///     Every kill used to be the same red, because <see cref="KillTrack" /> handed back <c>Argb = 0</c>
    ///     ("host, use the kind default") for all of them. A coach reads the bar for momentum, which needs
    ///     the two sides to be two colours.
    ///     <para>
    ///         <b>The MAPPING, not two colours.</b> This case used to assert only that the
    ///         two were non-zero, different and opaque — under which swapping <c>TintTeamT</c> and
    ///         <c>TintTeamCt</c> stayed green and every T kill on the bar rendered in the CT blue. The
    ///         entire user-visible claim is "the kill colour reads at a glance", and a colour that reads as
    ///         the wrong side is worse than one colour for both.
    ///     </para>
    /// </summary>
    [Test]
    public async Task KillTrack_ColoursEachMarkerByTheAttackersSide()
    {
        FakeTimelineData data = new(1000);
        data.Events["player_death"] =
        [
            Record(64, 10, ("attacker", "s1mple"), ("team", "2")),
            Record(640, 100, ("attacker", "device"), ("team", "3"))
        ];

        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        // team 2 is T and team 3 is CT — the game's own numbering, which is what the demo carries.
        await Assert.That(markers[0].Argb).IsEqualTo(TintTeamT)
            .Because("a T kill is amber; swapping the two constants must fail here, not pass on 'different'");
        await Assert.That(markers[1].Argb).IsEqualTo(TintTeamCt)
            .Because("a CT kill is blue");

        // Opaque, unlike RoundTrack's 0x38 band washes: a marker is an eight-pixel glyph, and a wash on
        // one reads as nothing drawn at all.
        await Assert.That(markers[0].Argb >> 24).IsEqualTo(0xFFu);
        await Assert.That(markers[1].Argb >> 24).IsEqualTo(0xFFu);
    }

    /// <summary>
    ///     The two tracks agree about which side is which colour — the same RGB, at the two alphas their
    ///     shapes need. A band and the markers inside it disagreeing about who is amber is the one way
    ///     "read the bar for momentum" can be actively misleading rather than merely dull.
    /// </summary>
    [Test]
    public async Task RoundAndKillTracks_UseTheSameRgbForEachSide()
    {
        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [0, 100];
        data.Events["round_end"] = [Record(90, 90, ("winner", "2")), Record(290, 290, ("winner", "3"))];
        data.Events["player_death"] = [Record(64, 10, ("team", "2")), Record(640, 100, ("team", "3"))];

        IReadOnlyList<TimelineBand> bands = new RoundTrack().BuildBands(data);
        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        await Assert.That(bands[0].Argb & 0x00FFFFFF).IsEqualTo(TintTeamT & 0x00FFFFFF);
        await Assert.That(bands[1].Argb & 0x00FFFFFF).IsEqualTo(TintTeamCt & 0x00FFFFFF);
        await Assert.That(markers[0].Argb & 0x00FFFFFF).IsEqualTo(TintTeamT & 0x00FFFFFF);
        await Assert.That(markers[1].Argb & 0x00FFFFFF).IsEqualTo(TintTeamCt & 0x00FFFFFF);

        // Different alphas on purpose: a 300 px band behind a label reads at 0x38; an 8 px glyph does not.
        await Assert.That(bands[0].Argb >> 24).IsEqualTo(0x38u);
        await Assert.That(markers[0].Argb >> 24).IsEqualTo(0xFFu);
    }

    /// <summary>
    ///     The fallback is the WHOLE contract: a demo that cannot say who was on which side (no
    ///     <c>player_team</c> at all, a world death, a spectator slot) must keep every marker it had. A
    ///     kill that disappears because its side is unknown is a worse bug than a kill that is the wrong
    ///     colour.
    /// </summary>
    [Test]
    public async Task KillTrack_UnknownSide_FallsBackToTheHostsKindDefault()
    {
        FakeTimelineData data = new(1000);
        data.Events["player_death"] =
        [
            Record(64, 10, ("attacker", "s1mple")),
            Record(128, 20, ("attacker", "world"), ("team", "")),
            Record(192, 30, ("attacker", "world"), ("team", "0")),
            Record(256, 40, ("attacker", "spectator"), ("team", "1"))
        ];

        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        await Assert.That(markers.Count).IsEqualTo(4);
        await Assert.That(markers.All(m => m.Argb == 0u)).IsTrue()
            .Because("0 is what BrushForMarker reads as 'use the Kill default', i.e. today's red");
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

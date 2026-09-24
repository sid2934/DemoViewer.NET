#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Views.Playback2D;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Tag Track (tag-store.md §3.11) over a <see cref="TagSession" /> fixture and a stub
///     <see cref="ITimelineData" />: merged non-overlapping bands in one lane, one marker per instance,
///     starts past the parse dropped, a re-query on every version bump, and in the tab a lane band that
///     seeks to its first frame.
/// </summary>
[NotInParallel]
public class TagTrackTests
{
    private const string DemoPath = "/d/match.dem";

    // Frame = tick / 2, the fakes' mapping; a tick past the last frame resolves to -1 like the host's.
    private sealed class HalfTickData(int totalFrames) : ITimelineData
    {
        public int TotalFrames { get; } = totalFrames;
        public int TickRate => 64;

        public int FrameIndexAtTick(int tick)
        {
            int frame = tick / 2;
            return frame >= TotalFrames ? -1 : frame;
        }

        public IReadOnlyList<int> FramesForEvent(string eventName) => [];
        public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName) => [];
        public bool HasEvent(string eventName) => false;
    }

    private static readonly int[] MarkerFrames = [50, 90, 700];
    private static readonly string[] RoundOnly = ["round"];

    private static readonly List<CachedRound> _rounds =
    [
        new() { Number = 1, StartTickFrameClock = 0 },
        new() { Number = 2, StartTickFrameClock = 1_000 }
    ];

    private static async Task<TagSession> Attached(params TagInstance[] instances)
    {
        TagSession session = new(null, _ => _rounds, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        foreach (TagInstance instance in instances)
        {
            session.Apply(new TagDelta.Add(instance));
        }

        return session;
    }

    private static TagTrack Track(TagSession session, List<Action>? queue = null) =>
        new(session, queue is null ? static action => action() : queue.Add);

    [Test]
    public async Task Bands_MergeOverlapsIntoAscendingNonOverlappingRuns()
    {
        using TagSession session = await Attached(
            Instance("B retake", 400, 500),
            Instance("A execute", 100, 200),
            Instance("A split", 180, 260), // overlaps the execute
            Instance("mid take", 262, 300), // starts one frame after the run ends: its own band
            Instance("eco", 200, 220)); // inside the first run
        using TagTrack track = Track(session);

        IReadOnlyList<TimelineBand> bands = track.BuildBands(new HalfTickData(1000));

        await Assert.That(bands.Count).IsEqualTo(3);
        for (int i = 1; i < bands.Count; i++)
        {
            await Assert.That(bands[i].StartFrameIndex).IsGreaterThan(bands[i - 1].EndFrameIndex)
                .Because("the interface contract: ascending and non-overlapping");
        }

        using (Assert.Multiple())
        {
            await Assert.That(bands[0].StartFrameIndex).IsEqualTo(50);
            await Assert.That(bands[0].EndFrameIndex).IsEqualTo(130);
            await Assert.That(bands[0].Label).IsEqualTo("3").Because("a run of several is labelled with its count");
            await Assert.That(bands[0].Argb).IsEqualTo(0u).Because("a mixed run takes the host's neutral colour");
            await Assert.That(bands[0].Tooltip).StartsWith("3 tags");

            await Assert.That(bands[1].StartFrameIndex).IsEqualTo(131);
            await Assert.That(bands[1].Label).IsEqualTo("mid take");

            await Assert.That(bands[2].Label).IsEqualTo("B retake").Because("a run of one is labelled with its code");
            await Assert.That(bands[2].Argb).IsEqualTo(TagTrack.DefaultColour("B retake"));
            await Assert.That(bands.All(b => b.TrackId == TagTrack.TrackId)).IsTrue();
        }
    }

    [Test]
    public async Task Markers_OnePerInstance_AscendingWithCodeColourAndTooltip()
    {
        using TagSession session = await Attached(
            Instance("B retake", 1_400, 1_500, ("outcome", "won"), ("", "clutch")),
            Instance("A execute", 100, 200),
            Instance("A split", 180, 260));
        using TagTrack track = Track(session);

        IReadOnlyList<TimelineMarker> markers = track.BuildMarkers(new HalfTickData(1000));

        await Assert.That(markers.Count).IsEqualTo(3).Because("the merge must hide nothing");
        await Assert.That(markers.Select(m => m.FrameIndex)).IsEquivalentTo(MarkerFrames);

        using (Assert.Multiple())
        {
            await Assert.That(markers.All(m => m.Kind == TimelineMarkerKind.Custom)).IsTrue();
            await Assert.That(markers.All(m => m.Glyph == TagTrack.Glyph)).IsTrue();
            await Assert.That(markers[2].Tick).IsEqualTo(1_400);
            await Assert.That(markers[2].Tooltip).IsEqualTo("B retake · outcome: won, clutch · r2");
            await Assert.That(markers[0].Tooltip).IsEqualTo("A execute · r1");
            await Assert.That(markers[0].Argb).IsEqualTo(TagTrack.DefaultColour("A execute"));
        }
    }

    [Test]
    public async Task AStartPastTheParse_IsDropped_AndAnEndPastItIsClamped()
    {
        using TagSession session = await Attached(
            Instance("late", 5_000, 5_100), // frame 2500 of 1000
            Instance("runs off", 1_900, 4_000)); // starts at 950, ends past the last frame
        using TagTrack track = Track(session);
        HalfTickData data = new(1000);

        IReadOnlyList<TimelineBand> bands = track.BuildBands(data);
        IReadOnlyList<TimelineMarker> markers = track.BuildMarkers(data);

        await Assert.That(track.IsAvailable(data)).IsTrue();
        await Assert.That(bands.Count).IsEqualTo(1);
        await Assert.That(bands[0].Label).IsEqualTo("runs off");
        await Assert.That(bands[0].EndFrameIndex).IsEqualTo(999);
        await Assert.That(markers.Count).IsEqualTo(1);

        using TagSession lateOnly = await Attached(Instance("late", 5_000, 5_100));
        using TagTrack lateTrack = Track(lateOnly);
        await Assert.That(lateTrack.IsAvailable(data)).IsFalse()
            .Because("available means at least one instance whose start resolves to a frame");
    }

    [Test]
    public async Task NoDocument_IsUnavailable_AndBuildsNothing()
    {
        using TagSession session = new(null, null, () => false, () => Created);
        using TagTrack track = Track(session);
        HalfTickData data = new(1000);

        await Assert.That(track.IsAvailable(data)).IsFalse();
        await Assert.That(track.BuildBands(data)).IsEmpty();
        await Assert.That(track.BuildMarkers(data)).IsEmpty();
    }

    [Test]
    public async Task MarkersChanged_OnEveryVersionBump_CoalescedPerPost()
    {
        using TagSession session = await Attached();
        List<Action> queue = [];
        using TagTrack track = Track(session, queue);
        int raised = 0;
        track.MarkersChanged += () => raised++;

        session.Apply(new TagDelta.Add(Instance("A execute", 100, 200)));
        session.Apply(new TagDelta.Add(Instance("A split", 300, 400)));

        await Assert.That(queue.Count).IsEqualTo(1).Because("two bumps before the UI thread runs are one re-query");
        queue[0]();
        await Assert.That(raised).IsEqualTo(1);

        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(queue.Count).IsEqualTo(2);
        queue[1]();
        await Assert.That(raised).IsEqualTo(2).Because("an undo is a version bump too");

        session.Detach();
        queue[^1]();
        await Assert.That(raised).IsEqualTo(3).Because("a detach empties the lane");
    }

    [Test]
    public async Task CodeColour_OverridesTheDefault_ForTheCodesItNames()
    {
        using TagSession session = await Attached(Instance("A execute", 100, 200), Instance("eco", 600, 700));
        using TagTrack track = Track(session);
        track.CodeColour = code => code == "eco" ? 0xFF112233 : null;

        IReadOnlyList<TimelineBand> bands = track.BuildBands(new HalfTickData(1000));

        await Assert.That(bands[0].Argb).IsEqualTo(TagTrack.DefaultColour("A execute"));
        await Assert.That(bands[1].Argb).IsEqualTo(0xFF112233);
    }

    [Test]
    public async Task DefaultColour_IsStableAndOpaque()
    {
        // Pinned values: the colour must not depend on the process's string hash seed.
        await Assert.That(TagTrack.DefaultColour("A execute")).IsEqualTo(TagTrack.DefaultColour("A execute"));
        await Assert.That(TagTrack.DefaultColour("") >> 24).IsEqualTo(0xFFu);
        await Assert.That(TagTrack.DefaultColour("A execute")).IsNotEqualTo(TagTrack.DefaultColour("B retake"));
    }

    [Test]
    public async Task Timeline_PutsTheLaneTracksBandsInTheLane_AndLeavesTheRoundsRowAlone()
    {
        using TagSession session = await Attached(Instance("A execute", 100, 1_300));
        using TagTrack track = Track(session);
        FakeTimelineData data = new(1000);
        data.EventFrames["round_freeze_end"] = [0, 500];

        using Playback2DTimelineViewModel timeline = new() { PixelWidth = 1000 };
        timeline.RegisterTrack(new RoundTrack());
        timeline.RegisterTrack(track, TimelineBandRow.Lane);
        timeline.Rebuild(data);
        timeline.UpdatePlayhead(600, 1_200);

        using (Assert.Multiple())
        {
            await Assert.That(timeline.Bands.Select(b => b.TrackId).Distinct()).IsEquivalentTo(RoundOnly);
            await Assert.That(timeline.LaneBands.Count).IsEqualTo(1);
            await Assert.That(timeline.LaneBands[0].StartFrameIndex).IsEqualTo(50);
            await Assert.That(timeline.HasLaneBands).IsTrue();
            await Assert.That(timeline.CurrentRoundLabel).IsEqualTo("2")
                .Because("a tag spanning the playhead must not become the round label");
            await Assert.That(timeline.Markers.Count(m => m.TrackId == TagTrack.TrackId)).IsEqualTo(1);
        }

        session.Apply(new TagDelta.Remove(session.Document!.Instances[0].Id));

        await Assert.That(timeline.LaneBands).IsEmpty();
        await Assert.That(timeline.HasLaneBands).IsFalse();
        await Assert.That(timeline.Tracks.Single(t => t.Id == TagTrack.TrackId).IsAvailable).IsFalse();
    }

    [Test]
    [Category("Render")]
    public async Task InTheTab_AnInstanceAppearsInTheLane_AndAClickSeeksToItsStart()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            TimelineControl control = Playback2DTimelineHarness.Timeline(view);
            ItemsControl lane = control.FindControl<ItemsControl>("TagLane")
                                ?? throw new InvalidOperationException("tag lane not found");
            double heightWithoutTags = control.Bounds.Height;

            await Assert.That(vm.Timeline.HasLaneBands).IsFalse();
            await Assert.That(lane.IsVisible).IsFalse();

            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            vm.Tags.Apply(new TagDelta.Add(Instance("A execute", 800, 1_200)));
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.Timeline.LaneBands.Count).IsEqualTo(1);
            await Assert.That(lane.IsVisible).IsTrue();
            await Assert.That(control.Bounds.Height).IsGreaterThan(heightWithoutTags);

            TimelineBandViewModel band = vm.Timeline.LaneBands[0];
            await Assert.That(band.StartFrameIndex).IsEqualTo(400);

            // Past the band's first pixel, like the round-band test: the band seeks to its own start.
            Point inside = Playback2DTimelineHarness.ToWindow(lane, window,
                band.X + Math.Min(20, band.Width / 2), lane.Bounds.Height / 2);
            window.MouseDown(inside, MouseButton.Left);
            window.MouseUp(inside, MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[tag-lane-click] band=[{band.StartFrameIndex}..{band.EndFrameIndex}] "
                              + $"x={band.X:F0} w={band.Width:F0} seeks={string.Join(",", ctx.SeekFrames)}");
            await Assert.That(ctx.SeekFrames).Contains(band.StartFrameIndex);

            vm.OnDeactivated();
            vm.Dispose();
        });
    }
}

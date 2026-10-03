#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The 2D tab's lane hosting (item 18) over fake tracks and behaviours: a lane registers its track in the
///     lane row and disposing unregisters it; the timeline dispatches a band's press and menu to the lane that
///     made it, a label request to the editable lane and a handle drag to the lane with the span; a lane
///     hides for a mode without touching the user's toggle; a contributed mode toggle is listed, its keymap
///     action flips it while it is available or on, and the panels bound to it follow; and the demo-change
///     signal reaches a handler on activation and on a reset until it is removed.
/// </summary>
public class Playback2DLaneHostingTests
{
    private static readonly string[] CoreTracks = ["round", "kill", "bomb", "annotation"];

    [Test]
    public async Task AddLane_RegistersTheTrackInTheLaneRow_AndDisposeUnregistersIt()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        FakeLaneTrack a = new("lane-a", (100, 200));
        FakeLaneTrack b = new("lane-b", (300, 400));
        int saves = 0;
        vm.Timeline.TrackVisibilityChanged += () => saves++;

        ILaneHandle laneA = vm.Surface.AddLane(a, TimelineBandRow.Lane);
        ILaneHandle laneB = vm.Surface.AddLane(b, TimelineBandRow.Lane);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.Tracks.Select(t => t.Id)).IsEquivalentTo([.. CoreTracks, "lane-a", "lane-b"]);
            await Assert.That(vm.Timeline.Lanes.Select(l => l.Track.Id)).IsEquivalentTo(["lane-a", "lane-b"]);
            await Assert.That(laneA.Track).IsSameReferenceAs(a);
            await Assert.That(vm.Timeline.LaneBands.Select(band => band.TrackId)).IsEquivalentTo(["lane-a", "lane-b"])
                .Because("a lane registered after the build shows without a re-query");
            await Assert.That(vm.Timeline.Bands.Select(band => band.TrackId).Distinct()).IsEquivalentTo(["round"]);
            await Assert.That(vm.Surface.AddLane(a, TimelineBandRow.Lane)).IsSameReferenceAs(laneA)
                .Because("an id already registered is one lane");
        }

        laneA.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.Tracks.Select(t => t.Id)).IsEquivalentTo([.. CoreTracks, "lane-b"]);
            await Assert.That(vm.Timeline.Lanes.Select(l => l.Track.Id)).IsEquivalentTo(["lane-b"]);
            await Assert.That(vm.Timeline.LaneBands.Select(band => band.TrackId)).IsEquivalentTo(["lane-b"]);
            await Assert.That(a.Subscribers).IsEqualTo(0).Because("the MarkersChanged subscription left with the track");
            await Assert.That(saves).IsEqualTo(0);
        }

        laneB.Dispose();
        laneB.Dispose();
        await Assert.That(vm.Timeline.LaneBands).IsEmpty();
        vm.Dispose();
    }

    [Test]
    public async Task PressesAndMenus_GoToTheBandsOwnLane_ThenTheContributors()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
        FakeBehaviour behaviourA = new("a");
        FakeBehaviour behaviourB = new("b");
        vm.Surface.AddLane(new FakeLaneTrack("lane-a", (100, 200)), TimelineBandRow.Lane, behaviourA);
        vm.Surface.AddLane(new FakeLaneTrack("lane-b", (300, 400)), TimelineBandRow.Lane, behaviourB);
        using IDisposable contributor = vm.Surface.AddBandMenu(_ => [new MenuEntry("Any band", () => { })]);
        TimelineBandViewModel bandA = vm.Timeline.LaneBands.Single(band => band.TrackId == "lane-a");
        TimelineBandViewModel round = vm.Timeline.Bands[0];

        vm.Timeline.PressBand(bandA);
        using (Assert.Multiple())
        {
            await Assert.That(behaviourA.Pressed).IsEquivalentTo([100]);
            await Assert.That(behaviourB.Pressed).IsEmpty();
            await Assert.That(ctx.SeekFrames).Contains(100).Because("the lane's press comes before the seek");
            await Assert.That(vm.Timeline.MenuFor(bandA).Select(m => m.Header)).IsEquivalentTo(["a:100", "Any band"]);
            await Assert.That(vm.Timeline.MenuFor(round).Select(m => m.Header)).IsEquivalentTo(["Any band"])
                .Because("a band of a track that is no lane has only the contributors' entries");
        }

        vm.Dispose();
    }

    [Test]
    public async Task LabelRequests_GoToTheEditableLane_AndDragsToTheLaneWithTheSpan()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        vm.Timeline.PixelWidth = 999; // one px per frame over the harness's 1000 frames
        FakeBehaviour behaviourA = new("a");
        FakeBehaviour behaviourB = new("b");
        ILaneHandle laneA = vm.Surface.AddLane(new FakeLaneTrack("lane-a"), TimelineBandRow.Lane, behaviourA);
        ILaneHandle laneB = vm.Surface.AddLane(new FakeLaneTrack("lane-b"), TimelineBandRow.Lane, behaviourB);

        vm.Timeline.RequestLaneLabel(50);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
            await Assert.That(vm.Timeline.ShowLane).IsFalse().Because("no bands and no editable lane");
            await Assert.That(behaviourA.Labels).IsEmpty().Because("no lane takes edits");
        }

        laneB.IsEditable = true;
        vm.Timeline.RequestLaneLabel(50);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.IsLaneEditable).IsTrue();
            await Assert.That(vm.Timeline.ShowLane).IsTrue();
            await Assert.That(behaviourB.Labels).IsEquivalentTo([50]);
            await Assert.That(behaviourA.Labels).IsEmpty();
        }

        laneA.EditSpan = (100, 200);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.HasEditSpan).IsTrue();
            await Assert.That(vm.Timeline.EditX).IsEqualTo(100);
            await Assert.That(vm.Timeline.EditWidth).IsEqualTo(100);
        }

        vm.Timeline.DragEditEdge(false, 300);
        vm.Timeline.DragEditEdge(true, 250);
        using (Assert.Multiple())
        {
            await Assert.That(laneA.EditSpan).IsEqualTo((250, 300));
            await Assert.That(behaviourA.Dragged).IsEquivalentTo([(100, 300), (250, 300)]);
            await Assert.That(behaviourB.Dragged).IsEmpty();
        }

        laneA.Dispose();
        laneB.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.HasEditSpan).IsFalse().Because("the span left with its lane");
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
            await Assert.That(vm.Timeline.ShowLane).IsFalse();
        }

        vm.Dispose();
    }

    [Test]
    public async Task Suppression_HidesTheLaneForAMode_AndNeverTouchesTheUsersToggle()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        ILaneHandle lane = vm.Surface.AddLane(new FakeLaneTrack("lane-a", (100, 200)), TimelineBandRow.Lane);
        TimelineTrackToggle toggle = vm.Timeline.Tracks.Single(t => t.Id == "lane-a");
        int saves = 0;
        vm.Timeline.TrackVisibilityChanged += () => saves++;
        await Assert.That(toggle.IsAvailable).IsTrue();

        lane.IsSuppressed = true;
        using (Assert.Multiple())
        {
            await Assert.That(lane.IsSuppressed).IsTrue();
            await Assert.That(vm.Timeline.IsTrackSuppressed("lane-a")).IsTrue().Because("the handle and the id agree");
            await Assert.That(toggle.IsAvailable).IsFalse();
            await Assert.That(toggle.IsEnabled).IsTrue();
            await Assert.That(vm.Timeline.LaneBands).IsEmpty();
        }

        lane.IsSuppressed = false;
        using (Assert.Multiple())
        {
            await Assert.That(toggle.IsAvailable).IsTrue();
            await Assert.That(vm.Timeline.LaneBands.Count).IsEqualTo(1);
            await Assert.That(saves).IsEqualTo(0).Because("suppression is the mode's, not a user choice to persist");
        }

        vm.Dispose();
    }

    [Test]
    public async Task AModeToggle_IsListed_ItsActionFlipsItWhileAvailableOrOn_AndBoundPanelsFollow()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        ModeToggle mode = new("fake.mode", "Mode", "A fake mode", Playback2DAction.ToggleReviewMode);
        int flips = 0;
        mode.Changed += () => flips++;

        await Assert.That(vm.ExecuteAction(Playback2DAction.ToggleReviewMode)).IsFalse().Because("no mode owns the action yet");

        IDisposable registration = vm.Surface.AddModeToggle(mode);
        IPanelHandle panel = vm.Surface.AddPanel(0, () => new FakePanelViewModel(), mode: mode);
        panel.Open();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.ModeToggles).IsEquivalentTo([mode]);
            await Assert.That(panel.IsShown).IsFalse();
            await Assert.That(vm.IsReviewAvailable).IsTrue();
            await Assert.That(vm.IsCardStrip).IsFalse();
        }

        await Assert.That(vm.ExecuteAction(Playback2DAction.ToggleReviewMode)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(mode.IsOn).IsTrue();
            await Assert.That(flips).IsEqualTo(1);
            await Assert.That(panel.IsShown).IsTrue();
            await Assert.That(vm.IsCardStrip).IsTrue();
        }

        // Unavailable and on: the action still leaves the mode. Unavailable and off: the key is nobody's.
        mode.IsAvailable = false;
        await Assert.That(vm.ExecuteAction(Playback2DAction.ToggleReviewMode)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(mode.IsOn).IsFalse();
            await Assert.That(panel.IsShown).IsFalse();
            await Assert.That(vm.IsCardStrip).IsFalse();
            await Assert.That(vm.ExecuteAction(Playback2DAction.ToggleReviewMode)).IsFalse();
            await Assert.That(mode.IsOn).IsFalse();
        }

        mode.IsAvailable = true;
        registration.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.ModeToggles).IsEmpty();
            await Assert.That(vm.ExecuteAction(Playback2DAction.ToggleReviewMode)).IsFalse().Because("the toggle left");
            await Assert.That(flips).IsEqualTo(2);
        }

        vm.Dispose();
    }

    [Test]
    public async Task OnDemoChanged_FiresOnActivationAndOnAReset_UntilRemoved()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
        int changes = 0;
        IDisposable registration = vm.Surface.OnDemoChanged(() => changes++);

        vm.OnDeactivated();
        vm.OnActivated(ctx);
        await Assert.That(changes).IsEqualTo(1).Because("activation resyncs to the demo now current");

        ctx.RaiseDemoReset();
        await Assert.That(changes).IsEqualTo(2);

        registration.Dispose();
        ctx.RaiseDemoReset();
        await Assert.That(changes).IsEqualTo(2);
        vm.Dispose();
    }

    private sealed class FakeLaneTrack(string id, params (int Start, int End)[] bands) : ITimelineTrack
    {
        private Action? _markersChanged;

        public int Subscribers { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public event Action? MarkersChanged
        {
            add
            {
                _markersChanged += value;
                Subscribers++;
            }
            remove
            {
                _markersChanged -= value;
                Subscribers--;
            }
        }

        public bool IsAvailable(ITimelineData data) => bands.Length > 0;

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) =>
            [.. bands.Select(b => new TimelineBand(id, b.Start, b.End, id, id, 0u))];

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => [];

        public void Raise() => _markersChanged?.Invoke();
    }

    private sealed class FakeBehaviour(string name) : ILaneBehaviour
    {
        public List<int> Pressed { get; } = [];
        public List<int> Labels { get; } = [];
        public List<(int Start, int End)> Dragged { get; } = [];

        public void OnBandPressed(TimelineBandViewModel band, ITimelineData data) => Pressed.Add(band.StartFrameIndex);

        public IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, ITimelineData data) =>
            [new MenuEntry($"{name}:{band.StartFrameIndex}", () => { })];

        public void OnLabelRequested(int frame) => Labels.Add(frame);

        public void OnEditSpanDragged(int startFrame, int endFrame) => Dragged.Add((startFrame, endFrame));
    }

    private sealed class FakePanelViewModel : ViewModelBase
    {
    }
}

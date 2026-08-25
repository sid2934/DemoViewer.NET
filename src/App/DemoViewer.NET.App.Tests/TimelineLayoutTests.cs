#region

using DemoViewer.NET.Modules.Playback2D.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="Playback2DTimelineViewModel" />'s layout math and coalescing. Pure numbers: no window, no
///     render, no platform — the degenerate cases (one frame, zero width) are exactly the ones that produce
///     NaN offsets and a silently blank bar rather than an exception.
/// </summary>
public class TimelineLayoutTests
{
    [Test]
    public async Task XForFrame_MapsZeroToLeftEdgeAndLastToRightEdge()
    {
        Playback2DTimelineViewModel vm = Sized(1000, 600);

        await Assert.That(vm.XForFrame(0)).IsEqualTo(0);
        await Assert.That(vm.XForFrame(999)).IsEqualTo(600);
        await Assert.That(vm.XForFrame(500)).IsBetween(300, 301);
    }

    [Test]
    public async Task FrameIndexAt_RoundTripsWithXForFrame()
    {
        Playback2DTimelineViewModel vm = Sized(2000, 800);

        foreach (int frame in new[] { 0, 1, 137, 999, 1500, 1999 })
        {
            await Assert.That(vm.FrameIndexAt(vm.XForFrame(frame))).IsEqualTo(frame);
        }
    }

    [Test]
    public async Task FrameIndexAt_ClampsOutOfRange()
    {
        Playback2DTimelineViewModel vm = Sized(1000, 600);

        await Assert.That(vm.FrameIndexAt(-50)).IsEqualTo(0);
        await Assert.That(vm.FrameIndexAt(5000)).IsEqualTo(999);
    }

    [Test]
    public async Task SingleFrameDemo_DoesNotDivideByZero()
    {
        Playback2DTimelineViewModel vm = Sized(1, 600);

        await Assert.That(vm.XForFrame(0)).IsEqualTo(0);
        await Assert.That(vm.FrameIndexAt(300)).IsEqualTo(0);
    }

    [Test]
    public async Task ZeroPixelWidth_ProducesNoNaN()
    {
        Playback2DTimelineViewModel vm = Sized(1000, 0);

        await Assert.That(double.IsNaN(vm.XForFrame(500))).IsFalse();
        await Assert.That(vm.FrameIndexAt(100)).IsEqualTo(0);
    }

    [Test]
    public async Task Rebuild_WithNullData_ClearsBandsAndMarkers()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new RoundTrack());
        vm.RegisterTrack(new KillTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(1000);
        data.EventFrames["round_freeze_end"] = [0, 500];
        data.Events["player_death"] = [TimelineTrackTests.Record(64, 10)];
        vm.Rebuild(data);
        await Assert.That(vm.Bands.Count).IsGreaterThan(0);
        await Assert.That(vm.Markers.Count).IsGreaterThan(0);

        vm.Rebuild(null);

        await Assert.That(vm.Bands.Count).IsEqualTo(0);
        await Assert.That(vm.Markers.Count).IsEqualTo(0);
        await Assert.That(vm.TotalFrames).IsEqualTo(0);
    }

    [Test]
    public async Task Markers_WithinTwoPixels_AreCoalescedIntoOne()
    {
        // 10 kills packed into 3 frames of a 90 000-frame demo on a 600 px bar: without coalescing that is
        // 10 stacked glyphs in one pixel column.
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new KillTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(90000);
        data.Events["player_death"] = Enumerable.Range(0, 10)
            .Select(i => TimelineTrackTests.Record(1000 + i, 40000 + i))
            .ToArray();
        vm.Rebuild(data);

        await Assert.That(vm.Markers.Count).IsEqualTo(1);
        await Assert.That(vm.Markers[0].Tooltip).StartsWith("10 kills");
    }

    [Test]
    public async Task Markers_FarApart_AreNotCoalesced()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new KillTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(1000);
        data.Events["player_death"] =
        [
            TimelineTrackTests.Record(10, 0),
            TimelineTrackTests.Record(20, 400),
            TimelineTrackTests.Record(30, 900)
        ];
        vm.Rebuild(data);

        await Assert.That(vm.Markers.Count).IsEqualTo(3);
    }

    [Test]
    public async Task UpdatePlayhead_SetsRoundLabelFromBand()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new RoundTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(300);
        data.EventFrames["round_freeze_end"] = [50, 200];
        vm.Rebuild(data);

        vm.UpdatePlayhead(10, 10);
        await Assert.That(vm.CurrentRoundLabel).IsEqualTo("wu");

        vm.UpdatePlayhead(120, 120);
        await Assert.That(vm.CurrentRoundLabel).IsEqualTo("1");

        vm.UpdatePlayhead(250, 250);
        await Assert.That(vm.CurrentRoundLabel).IsEqualTo("2");
        await Assert.That(vm.PlayheadX).IsGreaterThan(0);
    }

    [Test]
    public async Task RequestSeek_RaisesSeekRequestedWithMappedFrame()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new RoundTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(1201);
        data.EventFrames["round_freeze_end"] = [0];
        vm.Rebuild(data);

        List<int> seeks = [];
        vm.SeekRequested += seeks.Add;

        vm.RequestSeek(300);

        await Assert.That(seeks.Count).IsEqualTo(1);
        await Assert.That(seeks[0]).IsEqualTo(600);
    }

    [Test]
    public async Task SetTrackEnabled_False_DropsThatTracksContent()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new KillTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(1000);
        data.Events["player_death"] = [TimelineTrackTests.Record(10, 0), TimelineTrackTests.Record(20, 500)];
        vm.Rebuild(data);
        await Assert.That(vm.Markers.Count).IsEqualTo(2);

        vm.SetTrackEnabled("kill", false);

        await Assert.That(vm.Markers.Count).IsEqualTo(0);
        await Assert.That(vm.Tracks[0].IsAvailable).IsTrue();
    }

    private static Playback2DTimelineViewModel Sized(int totalFrames, double pixelWidth)
    {
        Playback2DTimelineViewModel vm = new();
        vm.Rebuild(new FakeTimelineData(totalFrames));
        vm.PixelWidth = pixelWidth;
        return vm;
    }
}

#region

using Avalonia.Media;
using Avalonia.Media.Immutable;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="Playback2DTimelineViewModel" />'s layout math and coalescing. Pure numbers: no window, no
///     render, no platform: the degenerate cases (one frame, zero width) are exactly the ones that produce
///     NaN offsets and a silently blank bar rather than an exception.
/// </summary>
public class TimelineLayoutTests
{
    private static readonly string[] _registeredTrackIds = ["round", "kill", "bomb", "annotation"];
    private static readonly string[] _persistedTrackIds = ["kill", "bomb", AnnotationTrack.TrackId];

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

        foreach (int frame in new[]
                 {
                     0, 1, 137, 999, 1500, 1999
                 })
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

    /// <summary>
    ///     The other half of the team-coloured kill marker: the ARGB a track hands back has to survive
    ///     <c>BrushForMarker</c>, whose non-zero branch was written for round bands and had never been
    ///     exercised by a marker. A 0 still resolves to the kind's own token, so an uncolourable kill is
    ///     visually exactly what it is today.
    ///     <para>
    ///         <b>
    ///             The kind default is asserted against another marker that shares its token, not against
    ///             the literal.
    ///         </b>
    ///         <c>Token</c> resolves from the theme when <c>Application.Current</c> exists
    ///         AND the call is on the UI thread, and falls back to a hard-coded ARGB otherwise, both of
    ///         which are properties of what else has run in this PROCESS, not of the code under test.
    ///         Asserting the fallback literal made this case fail roughly one run in ten, always as
    ///         "expected unknown to be #fff44336", and it is about to sit in a required CI lane.
    ///         <c>Kill</c> and <c>BombExplode</c> both map to <c>Pb2dHeadshot</c>, so comparing them pins
    ///         the mapping in either environment.
    ///     </para>
    /// </summary>
    [Test]
    public async Task KillMarkerBrushes_DifferBySide_AndFallBackToTheKindDefault()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new KillTrack());
        vm.PixelWidth = 600;

        FakeTimelineData data = new(1000);
        data.Events["player_death"] =
        [
            TimelineTrackTests.Record(10, 0, ("team", "2")),
            TimelineTrackTests.Record(20, 400, ("team", "3")),
            TimelineTrackTests.Record(30, 900)
        ];
        vm.Rebuild(data);

        Color t = Colour(vm.Markers[0]);
        Color ct = Colour(vm.Markers[1]);
        Color unknown = Colour(vm.Markers[2]);

        // The tints themselves, exactly: the non-zero branch must carry KillTrack's ARGB through
        // untouched, and "the two differ" would still pass if it handed back any two colours at all.
        await Assert.That(t).IsEqualTo(Color.FromUInt32(0xFFE0A030)).Because("T is amber");
        await Assert.That(ct).IsEqualTo(Color.FromUInt32(0xFF4A90D9)).Because("CT is blue");
        await Assert.That(unknown).IsNotEqualTo(t);
        await Assert.That(unknown).IsNotEqualTo(ct);

        // A second VM in the same call, so both Token lookups see the same process state.
        Playback2DTimelineViewModel bombs = new();
        bombs.RegisterTrack(new BombTrack());
        bombs.PixelWidth = 600;
        FakeTimelineData bombData = new(1000);
        bombData.Events["bomb_exploded"] = [TimelineTrackTests.Record(40, 500)];
        bombs.Rebuild(bombData);

        Console.WriteLine($"[marker-brush] t={t} ct={ct} unknown={unknown} "
                          + $"explode={Colour(bombs.Markers[0])}");

        await Assert.That(unknown).IsEqualTo(Colour(bombs.Markers[0]))
            .Because("an uncolourable kill takes the Kill KIND's default, which is the same Pb2dHeadshot "
                     + "token a bomb explosion takes — that is the claim, and it does not depend on "
                     + "whether a headless Application happens to exist in this process");
    }

    private static Color Colour(TimelineMarkerViewModel marker) =>
        ((ImmutableSolidColorBrush)marker.Brush).Color;

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

    /// <summary>
    ///     A USER toggle announces itself so the owner can persist it (the
    ///     <c>Playback2D:TimelineShow*</c> keys). Restoring a persisted value must NOT announce, otherwise
    ///     construction writes settings on every launch, and a read-only config dir turns startup into a
    ///     swallowed exception per tab open.
    /// </summary>
    [Test]
    public async Task TrackVisibilityChanged_FiresForAUserToggle_NotForARestore()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new KillTrack());
        vm.Rebuild(new FakeTimelineData(1000));

        int announced = 0;
        vm.TrackVisibilityChanged += () => announced++;

        vm.RestoreTrackEnabled("kill", false);
        await Assert.That(announced).IsEqualTo(0)
            .Because("restoring a persisted value is not a new choice to persist");
        await Assert.That(vm.Tracks[0].IsEnabled).IsFalse();

        vm.SetTrackEnabled("kill", true);
        await Assert.That(announced).IsEqualTo(1);

        // Idempotent: setting the value it already has changes nothing and announces nothing.
        vm.SetTrackEnabled("kill", true);
        await Assert.That(announced).IsEqualTo(1);
    }

    /// <summary>
    ///     Availability is a property of the DEMO, not a user choice, so a rebuild that flips it must not
    ///     look like a preference change: persisting "this demo has no bomb" would carry to the next one.
    /// </summary>
    [Test]
    public async Task AvailabilityChange_DoesNotAnnounceAVisibilityChange()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new BombTrack());

        int announced = 0;
        vm.TrackVisibilityChanged += () => announced++;

        FakeTimelineData withBomb = new(1000);
        withBomb.Events["bomb_planted"] = [TimelineTrackTests.Record(10, 100)];
        vm.Rebuild(withBomb);
        await Assert.That(vm.Tracks[0].IsAvailable).IsTrue();

        vm.Rebuild(new FakeTimelineData(1000));
        await Assert.That(vm.Tracks[0].IsAvailable).IsFalse();
        await Assert.That(announced).IsEqualTo(0);
    }

    /// <summary>
    ///     The three ids <c>Playback2DTabViewModel.LoadTimelineSettings</c> / <c>SaveTimelineSettings</c>
    ///     key <c>Playback2D:TimelineShowKills|Bomb|Annotations</c> on. <c>RestoreTrackEnabled</c> and
    ///     <c>SetTrackEnabled</c> both IGNORE an unknown id by contract, and <c>IsTrackEnabled</c> answers
    ///     <c>true</c> for one, so renaming a track's id does not fail anywhere: it just quietly turns the
    ///     persisted preference back into session state, which is the exact bug this guard catches.
    /// </summary>
    [Test]
    public async Task PersistedTrackIds_AreTheOnesTheTracksActuallyCarry()
    {
        Playback2DTimelineViewModel vm = new();
        vm.RegisterTrack(new RoundTrack());
        vm.RegisterTrack(new KillTrack());
        vm.RegisterTrack(new BombTrack());
        vm.RegisterTrack(new AnnotationTrack(new AnnotationDocument()));

        await Assert.That(vm.Tracks.Select(t => t.Id).ToArray()).IsEquivalentTo(_registeredTrackIds);

        // And a restore against those ids actually lands, rather than being swallowed as unknown.
        foreach (string id in _persistedTrackIds)
        {
            vm.RestoreTrackEnabled(id, false);
            await Assert.That(vm.Tracks.Single(t => t.Id == id).IsEnabled).IsFalse()
                .Because($"Playback2D:TimelineShow* writes '{id}' and nothing errors if it misses");
        }
    }

    /// <summary>
    ///     <b>
    ///         <see cref="ITimelineTrack.MarkersChanged" /> is documented "the host must re-query it",
    ///         and the host never subscribed.
    ///     </b>
    ///     <see cref="Playback2DTimelineViewModel.Rebuild" /> runs on
    ///     tab activation and demo-reset only, so a telestration made while the tab is open produced no
    ///     marker; worse, the Annotations toggle could never become AVAILABLE at all, because
    ///     availability was evaluated only inside a build and <c>AnnotationTrack</c> is unavailable until
    ///     the first time-anchored element exists.
    /// </summary>
    [Test]
    public async Task AnnotationTrack_MarkersAndAvailability_FollowTheDocument_WithNoRebuild()
    {
        AnnotationDocument doc = new();
        Playback2DTimelineViewModel vm = new();
        using AnnotationTrack track = new(doc);

        vm.RegisterTrack(new KillTrack());
        vm.RegisterTrack(track);
        vm.PixelWidth = 600;
        vm.Rebuild(new FakeTimelineData(1000));

        TimelineTrackToggle toggle = vm.Tracks.Single(t => t.Id == AnnotationTrack.TrackId);
        await Assert.That(toggle.IsAvailable).IsFalse();
        await Assert.That(vm.Markers.Count).IsEqualTo(0);

        // One time-anchored stroke: what Fade, Custom and "pin to now" all stamp. Nothing calls
        // Rebuild from here on; the track saying so is the only signal there is.
        doc.Apply(new DocDelta.Add(Anchored(400), 0));

        await Assert.That(toggle.IsAvailable).IsTrue()
            .Because("this is the ONLY moment the Annotations toggle can ever become reachable");
        await Assert.That(vm.Markers.Count).IsEqualTo(1);
        await Assert.That(vm.Markers[0].TrackId).IsEqualTo(AnnotationTrack.TrackId);
        await Assert.That(vm.Markers[0].Tick).IsEqualTo(400);

        // Erase / undo / clear has to take the marker back off, or the bar keeps a glyph for ink that
        // is no longer in the document.
        doc.Undo();
        await Assert.That(vm.Markers.Count).IsEqualTo(0);

        doc.Redo();
        await Assert.That(vm.Markers.Count).IsEqualTo(1);

        vm.Dispose();
        doc.Apply(new DocDelta.Add(Anchored(600), 1));
        await Assert.That(vm.Markers.Count).IsEqualTo(1)
            .Because("Dispose hands the subscription back, or the track keeps a dead view-model alive");
    }

    private static AnnotationElement Anchored(int tick) =>
        new(Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default,
            new SpaceRef.World(0), new TimeEnvelope(tick, null, 0, 0),
            [new InkPoint(0, 0, 0.5f), new InkPoint(40, 10, 0.5f)], null);

    private static Playback2DTimelineViewModel Sized(int totalFrames, double pixelWidth)
    {
        Playback2DTimelineViewModel vm = new();
        vm.Rebuild(new FakeTimelineData(totalFrames));
        vm.PixelWidth = pixelWidth;
        return vm;
    }
}

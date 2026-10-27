#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Tools;
using SkiaSharp;
using TUnit.Assertions.Enums;
using SdkP = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Scene layers and map tools an extension adds through the SDK playback surface: filed under the
///     extension's own id root, guarded, and handed only what the compositor and the press give them.
/// </summary>
[NotInParallel]
public class SdkPlaybackSurfaceSceneTests
{
    private static readonly MapLevel Floor = new() { Id = MapSpace.IdForZMin(-500), Name = "upper", ZMin = -500, ZMax = 100_000 };

    private static (Playback2DTabViewModel Vm, Playback2DSurface Surface) Surface(Func<Scene2DFrame>? frame = null)
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        return (vm, new Playback2DSurface(vm.Timeline, () => [Floor], _ => true, frame ?? (() => Scene2DFrame.Empty)));
    }

    /// <summary>
    ///     A contributed layer's only way to repaint off the playhead. Advance's return value arms the
    ///     frame loop, so a layer whose data changes while playback is paused has nothing else: it either
    ///     never repaints, or it returns true forever and burns frames. The event is what Scene2DHost
    ///     subscribes to, and it routes to the same RequestToolRender a pointer tool already reaches.
    /// </summary>
    [Test]
    public async Task RequestRender_ReachesTheHostsRepaintEvent_ThroughTheGuardedSurface()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);

        int repaints = 0;
        inner.RenderRequested += () => repaints++;

        await Assert.That(repaints).IsEqualTo(0).Because("nothing has asked yet");

        surface.RequestRender();
        surface.RequestRender();

        await Assert.That(repaints).IsEqualTo(2)
            .Because("each call is one repaint request; the host coalesces, the surface does not swallow");

        vm.Dispose();
    }

    [Test]
    public async Task AddLayer_FilesTheLayerUnderTheExtensionsRoot_AndDisposingRemovesIt()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);

        IDisposable added = surface.AddLayer("heat", () => new RecordingLayer("playback2d.radar"));
        string filed = inner.Layers.Single().Key;
        ISceneLayer built = inner.Layers.Single().Value();

        using (Assert.Multiple())
        {
            await Assert.That(filed).IsEqualTo("ext.pack.fake.heat");
            await Assert.That(built.Id).IsEqualTo(filed).Because("the layer's own id is never used, so it cannot claim a host id");
            await Assert.That(filed.StartsWith("playback2d.", StringComparison.Ordinal) || filed.StartsWith("hud.", StringComparison.Ordinal))
                .IsFalse();
        }

        added.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(inner.Layers).IsEmpty();
            await Assert.That(inner.ContributedLayers).IsEmpty();
        }

        vm.Dispose();
    }

    [Test]
    [Arguments("")]
    [Arguments("has space")]
    [Arguments("slash/es")]
    public async Task AddLayer_RefusesAnIdOutsideTheIdAlphabet(string id)
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);

        Assert.Throws<ArgumentException>(() => surface.AddLayer(id, () => new RecordingLayer("x"), "Heat"));
        using (Assert.Multiple())
        {
            await Assert.That(inner.Layers).IsEmpty();
            await Assert.That(inner.ContributedLayers).IsEmpty()
                .Because("the id is composed before anything is filed, so a bad one leaves no half-registered row");
        }

        vm.Dispose();
    }

    [Test]
    public async Task ALayerThatThrowsInRender_LeavesTheCanvasAsItFoundIt_StopsDrawing_AndCounts()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        RecordingLayer layer = new("x") { ThrowInRender = true };
        using IDisposable _ = surface.AddLayer("x", () => layer);
        ISceneLayer guarded = inner.Layers.Single().Value();

        using SKSurface target = SKSurface.Create(new SKImageInfo(16, 16));
        SKCanvas canvas = target.Canvas;
        int saves = canvas.SaveCount;
        SKMatrix before = canvas.TotalMatrix;
        guarded.Render(canvas, default);

        using (Assert.Multiple())
        {
            await Assert.That(canvas.SaveCount).IsEqualTo(saves);
            await Assert.That(canvas.TotalMatrix).IsEqualTo(before);
            await Assert.That(guarded.IsEnabled).IsFalse().Because("a layer that threw stops drawing for the session");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
            await Assert.That(guarded.Advance(default, Scene2DFrame.Empty)).IsFalse();
            await Assert.That(layer.Renders).IsEqualTo(1);
        }

        guarded.Render(canvas, default);
        await Assert.That(layer.Renders).IsEqualTo(1);
        vm.Dispose();
    }

    [Test]
    public async Task ALayerThatLeavesTheCanvasTransformed_IsRestoredAfterItsRender()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        using IDisposable _ = surface.AddLayer("x", () => new RecordingLayer("x") { LeaveTransformed = true });
        ISceneLayer guarded = inner.Layers.Single().Value();

        using SKSurface target = SKSurface.Create(new SKImageInfo(16, 16));
        SKMatrix before = target.Canvas.TotalMatrix;
        guarded.Render(target.Canvas, default);

        await Assert.That(target.Canvas.TotalMatrix).IsEqualTo(before);
        vm.Dispose();
    }

    [Test]
    public async Task AThrowingLayerFactory_BuildsALayerThatDrawsNothing()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        using IDisposable _ = surface.AddLayer("x", () => throw new InvalidOperationException("no layer"));

        ISceneLayer built = inner.Layers.Single().Value();

        await Assert.That(built.Id).IsEqualTo("ext.pack.fake.x");
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
        vm.Dispose();
    }


    /// <summary>
    ///     The user's row and the layer's own flag are two different flags. Assigning the row onto the
    ///     layer (which is what <c>SceneCompositor.SetEnabled</c> does) would overwrite the extension's own,
    ///     and <c>GuardedSceneLayer</c> honours that one.
    /// </summary>
    [Test]
    public async Task AUserToggleOff_StopsTheLayerDrawing_AndLeavesTheExtensionsOwnFlagAlone()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        RecordingLayer own = new("x");
        using IDisposable _ = surface.AddLayer("x", () => own, "Heat");
        ISceneLayer hosted = inner.Layers.Single().Value();
        ContributedLayerToggle row = inner.ContributedLayers.Single();

        await Assert.That(hosted.IsEnabled).IsTrue().Because("a layer with no choice made starts on");

        row.IsEnabled = false;

        using (Assert.Multiple())
        {
            await Assert.That(hosted.IsEnabled).IsFalse()
                .Because("the compositor skips Advance and Render for a layer that reads disabled");
            await Assert.That(own.IsEnabled).IsTrue()
                .Because("the row is the host's flag; writing it through destroys the extension's");
            await Assert.That(row.Id).IsEqualTo("ext.pack.fake.x");
            await Assert.That(row.DisplayName).IsEqualTo("Heat");
            await Assert.That(row.Tooltip).Contains("ext.pack.fake.x");
        }

        vm.Dispose();
    }

    [Test]
    public async Task AUserToggleOn_DoesNotRevive_ALayerItsOwnerSwitchedOff()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        RecordingLayer own = new("x") { IsEnabled = false };
        using IDisposable _ = surface.AddLayer("x", () => own, "Heat");
        ISceneLayer hosted = inner.Layers.Single().Value();
        ContributedLayerToggle row = inner.ContributedLayers.Single();

        row.IsEnabled = false;
        row.IsEnabled = true;

        using (Assert.Multiple())
        {
            await Assert.That(hosted.IsEnabled).IsFalse()
                .Because("both flags must be on; the row cannot overrule the layer's owner");
            await Assert.That(own.IsEnabled).IsFalse().Because("and the row never wrote to it");
        }

        vm.Dispose();
    }

    [Test]
    public async Task AUserToggleOn_DoesNotRevive_AFaultedLayer()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        using IDisposable _ = surface.AddLayer("x", () => new RecordingLayer("x") { ThrowInRender = true }, "Heat");
        ISceneLayer hosted = inner.Layers.Single().Value();
        ContributedLayerToggle row = inner.ContributedLayers.Single();

        using SKSurface target = SKSurface.Create(new SKImageInfo(16, 16));
        hosted.Render(target.Canvas, default);

        row.IsEnabled = false;
        row.IsEnabled = true;

        using (Assert.Multiple())
        {
            await Assert.That(hosted.IsEnabled).IsFalse()
                .Because("a layer that threw stops drawing for the session, whatever the row says");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
        }

        vm.Dispose();
    }

    /// <summary>
    ///     A flip arms the frame loop and nothing else. <c>LayersChanged</c> reaches the host's
    ///     <c>OnContributedLayersChanged</c>, which removes and re-creates every contributed layer: raising
    ///     it here would rebuild the whole stack on each click, and the gate is read live anyway.
    /// </summary>
    [Test]
    public async Task AFlip_AsksForOneRepaint_AndNeverRebuildsTheLayerStack()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        using IDisposable _ = surface.AddLayer("x", () => new RecordingLayer("x"), "Heat");

        int repaints = 0;
        int rebuilds = 0;
        inner.RenderRequested += () => repaints++;
        inner.LayersChanged += () => rebuilds++;

        inner.ContributedLayers.Single().IsEnabled = false;

        using (Assert.Multiple())
        {
            await Assert.That(repaints).IsEqualTo(1);
            await Assert.That(rebuilds).IsEqualTo(0).Because("a checkbox must not rebuild every contributed layer");
        }

        // Setting it to what it already is changes nothing and asks for nothing.
        inner.ContributedLayers.Single().IsEnabled = false;

        using (Assert.Multiple())
        {
            await Assert.That(repaints).IsEqualTo(1);
            await Assert.That(rebuilds).IsEqualTo(0);
        }

        vm.Dispose();
    }

    /// <summary>
    ///     The choice is keyed by the filed id and outlives the registration: re-adding the id, and the
    ///     gate cycle that disposes and re-attaches the whole contribution, both keep it. Another owner's
    ///     layer is a different id, so it starts from its own default.
    /// </summary>
    [Test]
    public async Task AUsersChoice_SurvivesAReAdd_AndAGateCycle_AndIsNotSharedWithAnotherOwner()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        SdkPlaybackSurface first = new(inner, new FaultRig().Guard);
        IDisposable initial = first.AddLayer("x", () => new RecordingLayer("x"), "Heat");
        inner.ContributedLayers.Single().IsEnabled = false;

        IDisposable again = first.AddLayer("x", () => new RecordingLayer("x"), "Heat again");

        using (Assert.Multiple())
        {
            await Assert.That(inner.ContributedLayers.Count).IsEqualTo(1).Because("re-adding an id replaces its row");
            await Assert.That(inner.ContributedLayers.Single().IsEnabled).IsFalse();
            await Assert.That(inner.ContributedLayers.Single().DisplayName).IsEqualTo("Heat again");
        }

        // The feature gate off: the contribution is detached whole, which disposes both registrations.
        initial.Dispose();
        again.Dispose();
        first.Dispose();
        await Assert.That(inner.ContributedLayers).IsEmpty();

        // And back on: a fresh SdkPlaybackSurface over the same tab.
        using SdkPlaybackSurface revived = new(inner, new FaultRig().Guard);
        using IDisposable restored = revived.AddLayer("x", () => new RecordingLayer("x"), "Heat");

        await Assert.That(inner.ContributedLayers.Single().IsEnabled).IsFalse()
            .Because("enabled is the default only for an id the user has not touched this session");

        using SdkPlaybackSurface other = new(inner, new FaultRig("pack.other").Guard);
        using IDisposable mine = other.AddLayer("x", () => new RecordingLayer("x"), "Theirs");
        using IDisposable off = other.AddLayer("y", () => new RecordingLayer("y"), "Starts off", false);

        using (Assert.Multiple())
        {
            await Assert.That(inner.ContributedLayers.Single(t => t.Id == "ext.pack.fake.x").IsEnabled).IsFalse();
            await Assert.That(inner.ContributedLayers.Single(t => t.Id == "ext.pack.other.x").IsEnabled).IsTrue()
                .Because("one owner's choice is not another's, even on the same local id");
            await Assert.That(inner.ContributedLayers.Single(t => t.Id == "ext.pack.other.y").IsEnabled).IsFalse()
                .Because("an untouched id takes the enabled the caller asked for");
        }

        vm.Dispose();
    }

    /// <summary>
    ///     Two owners can compose ONE filed id: "pack.a" + "b.c" and "pack.a.b" + "c" are both
    ///     <c>ext.pack.a.b.c</c>. The second replaces the first, so the first's registration is stale, and
    ///     disposing it must not take the row that now belongs to the second.
    /// </summary>
    [Test]
    public async Task WhenTwoOwnersComposeOneFiledId_TheLoserDisposing_LeavesTheWinnersRow()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface outer = new(inner, new FaultRig("pack.a").Guard);
        using SdkPlaybackSurface nested = new(inner, new FaultRig("pack.a.b").Guard);

        IDisposable loser = outer.AddLayer("b.c", () => new RecordingLayer("1"), "Outer");
        IDisposable winner = nested.AddLayer("c", () => new RecordingLayer("2"), "Nested");

        using (Assert.Multiple())
        {
            await Assert.That(inner.Layers.Single().Key).IsEqualTo("ext.pack.a.b.c");
            await Assert.That(inner.ContributedLayers.Single().DisplayName).IsEqualTo("Nested");
        }

        loser.Dispose();

        using (Assert.Multiple())
        {
            await Assert.That(inner.ContributedLayers.Single().DisplayName).IsEqualTo("Nested")
                .Because("the row is dropped by instance, inside the branch that actually removed a layer");
            await Assert.That(inner.Layers.Count).IsEqualTo(1);
        }

        winner.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(inner.ContributedLayers).IsEmpty();
            await Assert.That(inner.Layers).IsEmpty();
        }

        vm.Dispose();
    }

    /// <summary>
    ///     A layer with no name of its own still gets a row, labelled with the extension's name, the way
    ///     <c>CoreTrack</c> labels a lane. Null means "label it with the extension", never "no row".
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ALayerWithNoNameOfItsOwn_IsStillListed_UnderTheExtensionsName(string? name)
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        using IDisposable _ = surface.AddLayer("x", () => new RecordingLayer("x"), name);

        ContributedLayerToggle row = inner.ContributedLayers.Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.DisplayName).IsEqualTo("Fake").Because("that is the fake extension's scope name");
            await Assert.That(row.Tooltip).Contains("ext.pack.fake.x")
                .Because("two unnamed layers from one extension are told apart by the filed id on hover");
        }

        vm.Dispose();
    }

    [Test]
    public async Task AddTool_IsGuarded_AThrowingPressIsARefusedOne_AndDisposingRemovesIt()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        ThrowingTool tool = new();

        IDisposable added = surface.AddTool(tool);
        IMapTool hosted = inner.Tools.Single();
        bool taken = hosted.OnPressed(new MapToolEvent { Button = MapToolButton.Left }, new NullContext());

        using (Assert.Multiple())
        {
            await Assert.That(taken).IsFalse();
            await Assert.That(tool.Presses).IsEqualTo(1);
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
        }

        added.Dispose();
        await Assert.That(inner.Tools).IsEmpty();
        vm.Dispose();
    }

    [Test]
    public async Task AToolbarItemAndAPress_SeeTheFrameOnScreen()
    {
        Scene2DFrame shown = new() { Time = new SceneTime(640, 10, 10, 1, false) };
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface(() => shown);
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        Scene2DFrame? ranWith = null;
        Scene2DFrame? pressedWith = null;
        using IDisposable item = surface.AddToolbarItem(new SdkP.ToolbarItem("pack.fake.x", "X", "", moment =>
        {
            ranWith = moment.Frame;
            return true;
        }, "x.run"));
        using IDisposable pre = surface.AddPointerPreHandler(p =>
        {
            pressedWith = p.Frame;
            return false;
        });

        inner.TryExecute("x.run");
        inner.TryHandlePointerPress(new ScenePointer(Floor, 1, 2, SKPoint.Empty, ToolModifiers.None, shown, () => null));

        await Assert.That(ranWith).IsSameReferenceAs(shown);
        await Assert.That(pressedWith).IsSameReferenceAs(shown);
        vm.Dispose();
    }

    /// <summary>
    ///     A lane's bands and marks are filed under the LANE's id whatever the track stamped on them, so a
    ///     contribution cannot pass its band off as a round and collect the rounds row's menu. The kind it
    ///     asks for does survive: that is what themes a contributed mark.
    /// </summary>
    [Test]
    public async Task AnSdkLanesMarks_ReachTheTimeline_UnderTheLanesId()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        using SdkPlaybackSurface surface = new(vm.Surface, new FaultRig().Guard);

        using SdkP.ILaneHandle lane = surface.AddLane(new MarkingTrack());

        TimelineMarkerViewModel mark = vm.Timeline.Markers.Single(m => m.TrackId == lane.TrackId);
        TimelineBandViewModel band = vm.Timeline.LaneBands.Single(b => b.TrackId == lane.TrackId);

        using (Assert.Multiple())
        {
            await Assert.That(lane.TrackId).IsEqualTo("ext.pack.fake.marking")
                .Because("a lane is filed under its extension exactly as a layer is");
            await Assert.That(mark.Glyph).IsEqualTo("*");
            await Assert.That(mark.Tooltip).IsEqualTo("a mark");
            await Assert.That(mark.Kind).IsEqualTo(TimelineMarkerKind.Kill).Because("the kind is the track's to choose");
            await Assert.That(band.Label).IsEqualTo("3");
            await Assert.That(SdkPlaybackSurface.Band(band).IsRound).IsFalse()
                .Because("the track stamped \"round\" on it and the host overwrote that with the lane's id");
        }

        vm.Dispose();
    }

    /// <summary>
    ///     Two extensions adding the same local id get two lanes. The timeline dedups lanes by id and hands
    ///     back the existing registration, so the ids must already differ by owner when it is asked.
    /// </summary>
    [Test]
    public async Task TwoExtensions_AddingTheSameLocalId_GetTwoLanes_AndNeitherDrivesTheOther()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        using SdkPlaybackSurface first = new(vm.Surface, new FaultRig("pack.a").Guard);
        using SdkPlaybackSurface second = new(vm.Surface, new FaultRig("pack.b").Guard);

        SdkP.ILaneHandle a = first.AddLane(new LabelledTrack("tag", "A"));
        SdkP.ILaneHandle b = second.AddLane(new LabelledTrack("tag", "B"));

        using (Assert.Multiple())
        {
            await Assert.That(b).IsNotSameReferenceAs(a).Because("the host's id dedup must not join two owners");
            await Assert.That(a.TrackId).IsEqualTo("ext.pack.a.tag");
            await Assert.That(b.TrackId).IsEqualTo("ext.pack.b.tag");
            await Assert.That(vm.Timeline.Lanes.Select(l => l.Track.Id))
                .IsEquivalentTo(["ext.pack.a.tag", "ext.pack.b.tag"], CollectionOrdering.Matching);
            await Assert.That(vm.Timeline.LaneBands.Select(band => band.Label))
                .IsEquivalentTo(["A", "B"], CollectionOrdering.Matching);
        }

        b.IsSuppressed = true;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.IsTrackSuppressed(b.TrackId)).IsTrue();
            await Assert.That(a.IsSuppressed).IsFalse().Because("one owner's handle reaches its own lane only");
        }

        b.IsSuppressed = false;
        a.Dispose();

        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.Lanes.Select(l => l.Track.Id))
                .IsEquivalentTo(["ext.pack.b.tag"], CollectionOrdering.Matching).Because("disposing one leaves the other registered");
            await Assert.That(vm.Timeline.LaneBands.Select(band => band.Label))
                .IsEquivalentTo(["B"], CollectionOrdering.Matching).Because("and still drawing");
        }

        b.Dispose();
        vm.Dispose();
    }

    [Test]
    public async Task ComposedIds_NeverStartWithAHostRoot()
    {
        string[] hostIds =
        [
            SceneLayerIds.Radar, SceneLayerIds.Guides, SceneLayerIds.HudClock, SceneLayerIds.Query, SceneLayerIds.Utility
        ];

        string composed = ExtensionLayerIds.Compose("playback2d", "radar");

        await Assert.That(hostIds).DoesNotContain(composed);
        await Assert.That(composed).IsEqualTo("ext.playback2d.radar");
    }

    // Stamps "round" with an integer label, which is exactly PlaybackBand.IsRound's test, so the host's
    // re-stamp is what keeps this band out of the rounds row's menu.
    private sealed class MarkingTrack : ITimelineTrack
    {
        public string Id => "marking";

        public string DisplayName => "Marking";

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool IsAvailable(ITimelineData data) => true;

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) =>
            [new TimelineBand("round", 10, 20, "3", "", 0xFF00FF00)];

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) =>
            [new TimelineMarker("round", 15, 30, TimelineMarkerKind.Kill, "*", "a mark", 0xFFFF0000)];
    }

    // One labelled band, so two of these from two owners are told apart in the lane row.
    private sealed class LabelledTrack(string id, string label) : ITimelineTrack
    {
        public string Id => id;

        public string DisplayName => id;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool IsAvailable(ITimelineData data) => true;

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => [new TimelineBand(id, 10, 20, label, "", 0)];

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => [];
    }

    private sealed class RecordingLayer(string id) : ISceneLayer
    {
        public bool ThrowInRender { get; init; }

        public bool LeaveTransformed { get; init; }

        public int Renders { get; private set; }

        public string Id => id;

        public LayerSlot Slot => LayerSlot.Overlay;

        public int Order => 0;

        public LayerCacheHint Cache => LayerCacheHint.Dynamic;

        public bool IsEnabled { get; set; } = true;

        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            Renders++;
            canvas.Save();
            canvas.Translate(5, 5);
            if (ThrowInRender)
            {
                throw new InvalidOperationException("render");
            }

            if (!LeaveTransformed)
            {
                canvas.Restore();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingTool : IMapTool
    {
        public int Presses { get; private set; }

        public bool OnPressed(in MapToolEvent e, IMapToolContext context)
        {
            Presses++;
            throw new InvalidOperationException("press");
        }

        public void OnMoved(in MapToolEvent e, IMapToolContext context)
        {
        }

        public void OnReleased(in MapToolEvent e, IMapToolContext context)
        {
        }

        public void OnCancelled(IMapToolContext context)
        {
        }
    }

    private sealed class NullContext : IMapToolContext
    {
        public LevelPane? PaneAt(SKPoint screen) => null;

        public SKPoint WorldToScreen(LevelPane pane, SKPoint world) => world;

        public double WorldUnitsPerPixel(LevelPane pane) => 1;

        public void RequestRender()
        {
        }
    }
}

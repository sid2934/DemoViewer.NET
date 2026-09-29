#region

using System.Numerics;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.ViewModels.StratBook;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratCanvasTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Map-first editing: one selected step shared by the step rows and the canvas, Set On Map writing the selected
///     step's <c>to</c> or landing from a click as one undo entry, and a token drag writing the selected step, a step
///     that shares its tick with the next one included.
/// </summary>
[NotInParallel]
public class StratMapFirstTests
{
    private const int Step2 = 960, Step3 = 1600;

    // The upper floor of a two-floor synthetic map, key QuantizeZ(-500) = -512: Hut is the square at the origin,
    // Ramp the one to its right. The lower floor has no places.
    internal static readonly MapLevel Upper = new() { Id = MapSpace.IdForZMin(-500), Name = "upper", ZMin = -500, ZMax = 100_000 };
    private static readonly MapLevel Lower = new() { Id = MapSpace.IdForZMin(-2000), Name = "lower", ZMin = -2000, ZMax = -500 };

    internal static IZonePlaceResolver SyntheticZones() => new ZonePlaceResolverAdapter(new PlaceResolver(new ZoneSet(
        "de_synthetic", "9f1c02aa", "075a27b3", null, 64,
        [new ZoneFloor(-512, -528, 100_000), new ZoneFloor(-2048, -100_000, -528)],
        [new ZonePlace(0, "Hut", PlaceOrigin.Baked), new ZonePlace(1, "Ramp", PlaceOrigin.Baked)],
        [],
        [
            new ZoneArea(1, 0, -512, true, -400, [0, 0, 100, 0, 100, 100, 0, 100]),
            new ZoneArea(2, 1, -512, true, -400, [100, 0, 200, 0, 200, 100, 100, 100])
        ],
        [(1, 2)], [(0, 1)], null)));

    private static StratCanvasViewModel MapCanvas(StratSession session, Func<string, Task<IZonePlaceResolver?>>? places = null,
        Action<Action>? post = null)
    {
        StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: places ?? (_ => Task.FromResult<IZonePlaceResolver?>(SyntheticZones())), post: post ?? (a => a()));
        canvas.Timeline.PixelWidth = 6000;
        return canvas;
    }

    private static string SelectedRows(StratEditorViewModel editor) =>
        string.Join(",", editor.Steps.Select((r, i) => (r, i)).Where(x => x.r.IsSelected).Select(x => x.i));

    // Step 2 moved to 1:30, step 3's time: two steps on one tick, the second owning it on the schedule.
    private static StratDocument SharedTick()
    {
        StratDocument document = FiveSteps();
        document.Steps[1].AtSeconds = 90;
        return document;
    }

    [Test]
    public async Task SelectingARow_MovesTheCanvas_AndTheCanvasMovesTheRowHighlight()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = MapCanvas(session);
        StratEditorViewModel editor = new(session);
        editor.Project();
        using StratStepSelection selection = new(editor, canvas);

        selection.Select(editor.Steps[2].Id);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step3);
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(2);
            await Assert.That(SelectedRows(editor)).IsEqualTo("2");
        }

        // The step keys, the scrubber and play all move the selection the rows show.
        canvas.ExecuteAction(Playback2DAction.PrevStep);
        await Assert.That(SelectedRows(editor)).IsEqualTo("1");
        canvas.Timeline.RequestSeekToFrame(Step3 + 700);
        await Assert.That(editor.Steps.Single(r => r.IsSelected).Id).IsEqualTo(editor.Steps[3].Id);
        await Assert.That(selection.SelectedStepId).IsEqualTo(editor.Steps[3].Id);

        // Rows rebuilt by a reorder keep the highlight on the same step.
        Guid selected = editor.Steps[3].Id;
        selection.Select(selected);
        await Assert.That(canvas.Transport.Tick).IsEqualTo(2240).Because("a row goes to its step's moment");
        editor.MoveStepUpCommand.Execute(editor.Steps[3]);
        editor.Project();
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[2].Id).IsEqualTo(selected);
            await Assert.That(editor.Steps.Single(r => r.IsSelected).Id).IsEqualTo(selected);
            await Assert.That(canvas.ActiveStep!.Id).IsEqualTo(selected);
        }
    }

    [Test]
    public async Task ARowOnASharedTick_IsSelectable_AndADragWritesIt_AsOneEntry()
    {
        (StratStore _, StratSession session) = Opened(SharedTick());
        using StratCanvasViewModel canvas = MapCanvas(session);
        StratEditorViewModel editor = new(session);
        editor.Project();
        using StratStepSelection selection = new(editor, canvas);

        // Seeking to the tick alone gives the later step; selecting the row gives that row's.
        canvas.Timeline.RequestSeekToFrame(Step3);
        await Assert.That(canvas.ActiveStepIndex).IsEqualTo(2);
        selection.Select(editor.Steps[1].Id);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step3);
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(1);
            await Assert.That(editor.Steps[1].IsSelected).IsTrue();
            await Assert.That(canvas.ActiveTick).IsEqualTo(Step3);
        }

        int depth = session.UndoDepth;
        canvas.BeginDrag("C", TokenGrip.Body);
        for (int i = 1; i <= 20; i++)
        {
            canvas.MoveTo("C", new SKPoint(200 + i * 10, i * 5), 0);
        }

        canvas.EndDrag(null);
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(session.Document!.Steps[1].Positions.Single(p => p.Slot == "C").X).IsEqualTo(400);
            await Assert.That(session.Document!.Steps[2].Positions.Any(p => p.Slot == "C")).IsFalse();
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(1).Because("the edit keeps the selection");
            await Assert.That(editor.Steps[1].IsSelected).IsTrue();
        }
    }

    [Test]
    public async Task SetOnMap_WritesAMovesTo_AsOneEntry_AndEnds()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = MapCanvas(session);
        canvas.SelectStep(session.Document!.Steps[1].Id);

        using (Assert.Multiple())
        {
            await Assert.That(canvas.PlaceTarget).IsEqualTo(StratPlaceTarget.To);
            await Assert.That(canvas.SetPlaceText).IsEqualTo("Set “to” on map");
            await Assert.That(canvas.TryTagPositionAt(Upper, 50, 50)).IsFalse().Because("off, a click goes to the tools");
        }

        await Assert.That(canvas.BeginSetPlace()).IsTrue();
        await Assert.That(canvas.IsToolActive).IsTrue().Because("so Esc resolves to cancel, as it does under a tool");
        int depth = session.UndoDepth;
        await Assert.That(canvas.TryTagPositionAt(Upper, 50, 50)).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Hut");
            await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(canvas.IsSettingPlace).IsFalse().Because("one click, one write");
            await Assert.That(canvas.StatusLine).Contains("Hut");
        }

        // A step that has a to keeps its other members; undo is the one entry.
        await Assert.That(canvas.BeginSetPlace()).IsTrue();
        canvas.TryTagPositionAt(Upper, 150, 50);
        await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Ramp");
        await Assert.That(canvas.ExecuteAction(Playback2DAction.Undo)).IsTrue();
        await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Hut");
    }

    [Test]
    public async Task SetOnMap_OnAThrowWithoutALineup_WritesTheLanding_PointAndPlace_AsOneEntry()
    {
        StratDocument document = FiveSteps();
        document.Steps[3].Verb = "throw";
        document.Steps[3].Utility = new UtilityRef { Kind = "smoke" };
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = MapCanvas(session);
        canvas.SelectStep(document.Steps[3].Id);

        await Assert.That(canvas.PlaceTarget).IsEqualTo(StratPlaceTarget.Landing);
        await Assert.That(canvas.SetPlaceText).IsEqualTo("Set landing on map");
        canvas.BeginSetPlace();
        int depth = session.UndoDepth;
        canvas.TryTagPositionAt(Upper, 150.456, 50);

        UtilityLanding landing = session.Document!.Steps[3].Utility!.Landing!;
        using (Assert.Multiple())
        {
            await Assert.That(landing.Place).IsEqualTo("Ramp");
            await Assert.That(landing.X).IsEqualTo(150.46);
            await Assert.That(landing.Y).IsEqualTo(50);
            await Assert.That(landing.LevelMinZ).IsEqualTo(-512);
            await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(session.Document!.Steps[3].Utility!.Kind).IsEqualTo("smoke");
        }
    }

    [Test]
    public async Task TheFieldFollowsTheVerb_AndALineupThrowOffersNoLanding()
    {
        StratDocument document = FiveSteps();
        document.Steps[1].Verb = "fake";
        document.Steps[2].Verb = "wait";
        document.Steps[3].Verb = "throw";
        document.Steps[3].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        document.Steps[4].Verb = "throw";
        document.Steps[0].Verb = "plant";
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = MapCanvas(session);

        StratPlaceTarget TargetAt(int index)
        {
            canvas.SelectStep(session.Document!.Steps[index].Id);
            return canvas.PlaceTarget;
        }

        using (Assert.Multiple())
        {
            await Assert.That(TargetAt(0)).IsEqualTo(StratPlaceTarget.To);
            await Assert.That(canvas.SetPlaceText).IsEqualTo("Set “site” on map");
            await Assert.That(TargetAt(1)).IsEqualTo(StratPlaceTarget.To).Because("a fake sets its to");
            await Assert.That(TargetAt(2)).IsEqualTo(StratPlaceTarget.None);
            await Assert.That(TargetAt(3)).IsEqualTo(StratPlaceTarget.None).Because("a lineup says where it lands");
            await Assert.That(canvas.BeginSetPlace()).IsFalse();
            await Assert.That(TargetAt(4)).IsEqualTo(StratPlaceTarget.None).Because("no utility kind yet");
        }
    }

    [Test]
    public async Task AMiss_WritesNothing_ButALandingMiss_KeepsThePoint()
    {
        StratDocument document = FiveSteps();
        document.Steps[3].Verb = "throw";
        document.Steps[3].Utility = new UtilityRef { Kind = "flash", Landing = new UtilityLanding { Place = "Hut" } };
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = MapCanvas(session);

        canvas.SelectStep(document.Steps[1].Id);
        canvas.BeginSetPlace();
        await Assert.That(canvas.TryTagPositionAt(Upper, 5_000, 5_000)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(session.Document!.Steps[1].To).IsNull();
            await Assert.That(canvas.StatusLine).Contains("no place there");
            await Assert.That(canvas.IsSettingPlace).IsFalse();
        }

        // The lower floor has no places: the upper floor's Hut does not answer a click there.
        canvas.BeginSetPlace();
        canvas.TryTagPositionAt(Lower, 50, 50);
        await Assert.That(session.UndoDepth).IsEqualTo(0);

        canvas.SelectStep(document.Steps[3].Id);
        canvas.BeginSetPlace();
        canvas.TryTagPositionAt(Upper, 5_000, 4_000);
        UtilityLanding landing = session.Document!.Steps[3].Utility!.Landing!;
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(landing.X).IsEqualTo(5_000);
            await Assert.That(landing.Y).IsEqualTo(4_000);
            await Assert.That(landing.Place).IsNull().Because("the old place named where it used to land");
            await Assert.That(canvas.StatusLine).Contains("point only");
        }
    }

    [Test]
    public async Task Cancel_EndsTheMode_WithoutWriting_EvenWithTheLookupInFlight()
    {
        TaskCompletionSource<IZonePlaceResolver?> gate = new();
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = MapCanvas(session, _ => gate.Task);
        canvas.SelectStep(session.Document!.Steps[1].Id);

        canvas.BeginSetPlace();
        await Assert.That(canvas.CancelSetPlace()).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(canvas.IsSettingPlace).IsFalse();
            await Assert.That(canvas.IsToolActive).IsFalse();
            await Assert.That(canvas.TryTagPositionAt(Upper, 50, 50)).IsFalse();
            await Assert.That(canvas.CancelSetPlace()).IsFalse();
        }

        // A click before the map's places are read waits for them; a cancel meanwhile drops it.
        canvas.BeginSetPlace();
        await Assert.That(canvas.TryTagPositionAt(Upper, 50, 50)).IsTrue();
        await Assert.That(canvas.StatusLine).Contains("reading");
        canvas.CancelSetPlace();
        gate.SetResult(SyntheticZones());
        await Assert.That(session.UndoDepth).IsEqualTo(0);

        // With the places in, the same click writes.
        canvas.BeginSetPlace();
        canvas.TryTagPositionAt(Upper, 50, 50);
        await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Hut");
    }

    [Test]
    public async Task MovingTheSelection_DropsAPendingClick()
    {
        TaskCompletionSource<IZonePlaceResolver?> gate = new();
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = MapCanvas(session, _ => gate.Task);
        canvas.SelectStep(session.Document!.Steps[1].Id);
        canvas.BeginSetPlace();
        canvas.TryTagPositionAt(Upper, 150, 50);

        // Moving the selection is a cancel: the pending click is dropped.
        canvas.SelectStep(session.Document!.Steps[3].Id);
        await Assert.That(canvas.IsSettingPlace).IsFalse();
        gate.SetResult(SyntheticZones());
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(session.Document!.Steps[1].To).IsNull();
            await Assert.That(session.Document!.Steps[3].To).IsNull();
        }
    }

    [Test]
    public async Task SelectingARow_WhilePlaying_KeepsTheEarlierStepOfASharedTick()
    {
        (StratStore _, StratSession session) = Opened(SharedTick());
        ManualTicker ticker = new();
        using StratCanvasViewModel canvas = new(session, _ => null, ticker, null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(SyntheticZones()), post: a => a());
        canvas.Timeline.RequestSeekToFrame(320);
        canvas.ExecuteAction(Playback2DAction.TogglePlay);
        ticker.Fire(0.5);
        await Assert.That(canvas.IsPlaying).IsTrue();

        await Assert.That(canvas.SelectStep(session.Document!.Steps[1].Id)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(canvas.IsPlaying).IsFalse();
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step3);
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ADrag_WhilePlaying_PausesAndWritesTheStepItStartedOn_AsOneEntry()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        ManualTicker ticker = new();
        using StratCanvasViewModel canvas = new(session, _ => null, ticker, null, () => []);
        canvas.Timeline.RequestSeekToFrame(Step2);
        canvas.ExecuteAction(Playback2DAction.TogglePlay);
        ticker.Fire(1.0);
        await Assert.That(canvas.ActiveStepIndex).IsEqualTo(1);

        canvas.BeginDrag("B", TokenGrip.Body);
        canvas.MoveTo("B", new SKPoint(150, 250), 0);
        canvas.EndDrag(null);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.IsPlaying).IsFalse();
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step2);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.Document!.Steps[1].Positions.Single(p => p.Slot == "B").Y).IsEqualTo(250);
        }
    }

    [Test]
    public async Task TheStepKeys_VisitBothStepsOfASharedTick()
    {
        (StratStore _, StratSession session) = Opened(SharedTick());
        using StratCanvasViewModel canvas = MapCanvas(session);
        List<int> forward = [];
        while (canvas.ExecuteAction(Playback2DAction.NextStep))
        {
            forward.Add(canvas.ActiveStepIndex);
        }

        List<int> back = [];
        while (canvas.ExecuteAction(Playback2DAction.PrevStep))
        {
            back.Add(canvas.ActiveStepIndex);
        }

        await Assert.That(string.Join(",", forward)).IsEqualTo("0,1,2,3,4");
        await Assert.That(string.Join(",", back)).IsEqualTo("3,2,1,0");
    }

    [Test]
    public async Task ALandingClick_WithNoZones_KeepsTheStoredPlace_AndSetsThePoint()
    {
        StratDocument document = FiveSteps();
        document.Steps[3].Verb = "throw";
        document.Steps[3].Utility = new UtilityRef { Kind = "flash", Landing = new UtilityLanding { Place = "Hut" } };
        (StratStore _, StratSession session) = Opened(document);

        foreach (Func<string, Task<IZonePlaceResolver?>> none in new Func<string, Task<IZonePlaceResolver?>>[]
                 {
                     _ => Task.FromResult<IZonePlaceResolver?>(null),
                     _ => Task.FromException<IZonePlaceResolver?>(new IOException("unreadable"))
                 })
        {
            using StratCanvasViewModel canvas = MapCanvas(session, none);
            canvas.SelectStep(document.Steps[3].Id);
            canvas.BeginSetPlace();
            canvas.TryTagPositionAt(Upper, 700 + session.UndoDepth, 800);

            UtilityLanding landing = session.Document!.Steps[3].Utility!.Landing!;
            using (Assert.Multiple())
            {
                await Assert.That(landing.Place).IsEqualTo("Hut");
                await Assert.That(landing.Y).IsEqualTo(800);
                await Assert.That(landing.LevelMinZ).IsEqualTo(-512);
                await Assert.That(canvas.StatusLine).Contains("no places for this map");
            }
        }

        await Assert.That(session.UndoDepth).IsEqualTo(2);
    }

    [Test]
    public async Task AVerbChange_EndsTheMode_AndClearsItsPrompt_AnOtherEditKeepsIt()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = MapCanvas(session);
        canvas.SelectStep(session.Document!.Steps[1].Id);

        canvas.BeginSetPlace();
        session.Apply(PatchOp.ReplaceOp("/steps/1/note", null, System.Text.Json.Nodes.JsonValue.Create("go")));
        await Assert.That(canvas.IsSettingPlace).IsTrue();
        await Assert.That(canvas.StatusLine).Contains("click the map");

        session.Apply(PatchOp.ReplaceOp("/steps/1/verb", null, System.Text.Json.Nodes.JsonValue.Create("wait")));
        using (Assert.Multiple())
        {
            await Assert.That(canvas.IsSettingPlace).IsFalse();
            await Assert.That(canvas.StatusLine).DoesNotContain("click the map");
        }
    }

    [Test]
    public async Task ReadOnly_OffersNothing()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [], readOnly: true,
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(SyntheticZones()));
        canvas.SelectStep(session.Document!.Steps[1].Id);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.CanSetPlace).IsFalse();
            await Assert.That(canvas.BeginSetPlace()).IsFalse();
            await Assert.That(canvas.TryTagPositionAt(Upper, 50, 50)).IsFalse();
        }
    }

    /// <summary>The zones answer the place for any point: a map click with no known camera still lands somewhere.</summary>
    internal sealed class EverywhereIs(string place) : IZonePlaceResolver
    {
        public string ZonesVersion => "test";

        public string? Resolve(Vector3 world) => place;

        public string? ResolveOnFloor(double x, double y, double floorKey) => place;

        public IReadOnlySet<string> Adjacent(string name) => new HashSet<string>();
    }
}

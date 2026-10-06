#region

using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Reading and editing a start safely: an older file's round-start spots stand where they were put, a start edit
///     waits for the map's spawns when its first write would take them, an edit a step on the start's tick would hide is
///     refused rather than written, and a start drag shows where the token goes from there.
/// </summary>
[NotInParallel]
public class StratStartEditingTests
{
    private const double Floor = -512;

    private static readonly StratSpawns Spawns = new(
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(10 + 10 * i, 10, Floor, "Hut"))],
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(400 + 10 * i, 300, Floor, "Ramp"))]);

    private static StepPosition At(string slot, double x, double y) => new() { Slot = slot, X = x, Y = y, LevelMinZ = Floor };

    // An older file's round-start step: five distinct authored spots, each player sent somewhere by its line.
    private static StratDocument SeedWithSpots(string verb)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_synthetic", "T", "default", "spots", Created);
        document.Canvas = new StratCanvas { DefaultLevelMinZ = Floor };
        StratStep seed = Step(1, 115, StratVocabulary.ActorAll, verb);
        seed.Positions = [.. StratVocabulary.Slots.Select((s, i) => At(s, 100 + 200 * i, 600))];
        seed.Assignments = [.. StratVocabulary.Slots.Select(s => new StepAssignment { Slot = s, To = new PlaceRef { Place = "Mid" } })];
        document.Steps = [seed, Step(2, 100, StratVocabulary.ActorAll, "wait")];
        return document;
    }

    private static StratSceneProjection Project(StratDocument document) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), null, (place, _) => place == "Mid" ? (2000, 2000) : null);

    private static TokenKeyframe Sample(StratSceneProjection projection, string slot, int tick) =>
        projection.Tracks.Single(t => t.Slot == slot).TrySample(tick, out TokenKeyframe k) ? k : throw new InvalidOperationException($"no {slot} at {tick}");

    [Test]
    [Arguments("hold")]
    [Arguments("peek")]
    public async Task AnOlderSeedsOwnSpots_OnAPositionVerb_StandWhereTheyWerePut(string verb)
    {
        StratDocument document = SeedWithSpots(verb);
        StratSceneProjection projection = Project(document);
        using (Assert.Multiple())
        {
            await Assert.That(StratSceneProjection.IsLegacyCarry(document)).IsTrue();
            await Assert.That(projection.HasStart).IsTrue();
            for (int i = 0; i < 5; i++)
            {
                string slot = StratVocabulary.Slots[i];
                foreach (int tick in new[] { 0, 64, 640 })
                {
                    TokenKeyframe at = Sample(projection, slot, tick);
                    await Assert.That((at.X, at.Y)).IsEqualTo(((float)(100 + 200 * i), 600f)).Because($"{slot} stands on its spot at {tick}");
                }
            }
        }
    }

    [Test]
    public async Task AnOlderSeedsOwnSpots_OnAMove_AreWhereTheRunLeavesFrom()
    {
        StratSceneProjection projection = Project(SeedWithSpots("move"));
        TokenKeyframe start = Sample(projection, "C", 0), later = Sample(projection, "C", 64);
        using (Assert.Multiple())
        {
            await Assert.That((start.X, start.Y)).IsEqualTo((500f, 600f));
            await Assert.That(later.Y).IsGreaterThan(600f).Because("C runs from its spot to Mid");
            await Assert.That(Sample(projection, "C", 2000).X).IsGreaterThan(1800f).Because("C arrives at Mid, fanned out");
        }
    }

    [Test]
    public async Task AnOlderStartsKind_IsSpawnOnlyWhenItStandsOnTheSpawns()
    {
        StratDocument document = SeedWithSpots("hold");
        StratDocument onSpawns = SeedWithSpots("hold");
        onSpawns.Steps[0].Positions = [.. StratVocabulary.Slots.Select((s, i) => At(s, 10 + 10 * i, 10))];
        using (Assert.Multiple())
        {
            await Assert.That(StratStartBlock.Effective(document)!.Kind).IsEqualTo(StratStart.CustomKind);
            await Assert.That(StratStartPhrasing.Text(document, null)).IsEqualTo("from step 1");
            await Assert.That(StratStartBlock.Effective(onSpawns, Spawns)!.Kind).IsEqualTo(StratStart.SpawnKind);
            await Assert.That(StratStartPhrasing.Text(onSpawns, null, Spawns)).IsEqualTo("spawn");
        }
    }

    private static StratCanvasViewModel Canvas(StratSession session, Func<string, Task<StratSpawns?>>? spawns = null) =>
        new(session, _ => null, new ManualTicker(), null, StratTestKeymap.Shipped,
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a(), spawnsFor: spawns);

    private static StratEditorViewModel Editor(StratSession session, StratCanvasViewModel canvas)
    {
        StratEditorViewModel editor = new(session)
        {
            Spawns = () => canvas.CurrentSpawns, LegacyCarriedFor = canvas.LegacyCarried, StartRefusal = canvas.StartRefusal
        };
        editor.Project();
        return editor;
    }

    [Test]
    public async Task StartEdits_WaitForTheSpawns_WhenTheFirstWriteWouldTakeThem()
    {
        StratDocument document = SeedWithSpots("hold");
        document.Steps[0].Positions.RemoveAll(p => p.Slot is "D" or "E");
        TaskCompletionSource<StratSpawns?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session, _ => read.Task);
        StratEditorViewModel editor = Editor(session, canvas);
        canvas.SelectStart();

        canvas.BeginDrag("A", TokenGrip.Body);
        await Assert.That(canvas.DragTarget!.Refusal).IsEqualTo(StratDragTarget.SpawnsPendingNote);
        canvas.CancelDrag();
        editor.Start.Own[0].Value = [new PlaceRef { Place = "Ramp" }];
        using (Assert.Multiple())
        {
            await Assert.That(canvas.SpawnsPending).IsTrue();
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(editor.Start.Note).IsEqualTo(StratDragTarget.SpawnsPendingNote);
            await Assert.That(canvas.BeginSetPlace(StratStartBlock.FieldFor("A"))).IsFalse();
        }

        int loaded = 0;
        canvas.PlacesLoaded += () => loaded++;
        read.SetResult(Spawns);
        for (int i = 0; i < 200 && canvas.SpawnsPending; i++)
        {
            await Task.Delay(10);
        }

        editor.Project();
        editor.Start.Own[0].Value = [new PlaceRef { Place = "Ramp" }];
        StratStart written = session.Document!.Start!;
        using (Assert.Multiple())
        {
            await Assert.That(canvas.SpawnsPending).IsFalse();
            await Assert.That(loaded).IsEqualTo(1).Because("the strat open when they landed takes them");
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(StratStartBlock.For(written, "A")!.Place).IsEqualTo("Ramp");
            await Assert.That(StratStartBlock.For(written, "D")!.X).IsEqualTo(40).Because("the first write took D's spawn");
            await Assert.That(editor.Start.Note).IsEqualTo("");
        }
    }

    [Test]
    public async Task SpawnsLandingAfterASwitch_RedrawOnlyAStratThatTakesThem()
    {
        StratDocument older = SeedWithSpots("hold");
        StratDocument captured = SeedWithSpots("hold");
        captured.Id = Guid.NewGuid();
        captured.Origin = new StratOrigin { DemoSha256 = "ab", Round = 2 };
        (StratStore store, StratSession session) = StratCanvasTestData.Opened(older);
        store.Save(captured, [], "created");
        TaskCompletionSource<StratSpawns?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using StratCanvasViewModel canvas = Canvas(session, _ => read.Task);
        int loaded = 0;
        canvas.PlacesLoaded += () => loaded++;

        session.Open(captured.Id);
        read.SetResult(Spawns);
        await Task.Delay(100);
        await Assert.That(loaded).IsEqualTo(0).Because("the captured strat open now takes no spawns");

        session.Open(older.Id);
        await Assert.That(canvas.SpawnsPending).IsFalse();
        await Assert.That(StratStartBlock.For(StratStartBlock.Effective(session.Document!, canvas.CurrentSpawns), "A")).IsNotNull();
    }

    // A capture: a captured start and the freeze-end step on its tick holding the same observed spots.
    private static StratDocument Captured()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_synthetic", "T", "default", "captured", Created);
        document.Origin = new StratOrigin { DemoSha256 = "ab", Round = 5 };
        document.Start = new StratStart
        {
            Kind = StratStart.CapturedKind,
            Positions = [.. StratVocabulary.Slots.Select((s, i) => new StartPosition { Slot = s, X = 100 + 50 * i, Y = 50, LevelMinZ = Floor, Observed = true })]
        };
        StratStep freeze = Step(1, 115, StratVocabulary.ActorAll, "hold");
        freeze.Positions = [.. StratVocabulary.Slots.Select((s, i) => new StepPosition { Slot = s, X = 100 + 50 * i, Y = 50, LevelMinZ = Floor, Observed = true })];
        document.Steps = [freeze, Step(2, 100, "A", "move", to: "Ramp")];
        return document;
    }

    [Test]
    public async Task OnACapture_StartEdits_AreRefusedWithANote_AndChangeNothing()
    {
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(Captured());
        using StratCanvasViewModel canvas = Canvas(session, _ => Task.FromResult<StratSpawns?>(Spawns));
        StratEditorViewModel editor = Editor(session, canvas);
        string before = StratHistory.ToNode(session.Document!).ToJsonString();
        canvas.SelectStart();

        canvas.BeginDrag("A", TokenGrip.Body);
        string? refusal = canvas.DragTarget!.Refusal;
        canvas.MoveTo("A", new SKPoint(300, 300), Floor);
        canvas.EndDrag();
        editor.Start.Own[1].Value = [new PlaceRef { Place = "Ramp" }];
        string rowNote = editor.Start.Note;
        editor.Start.UseSpawnsCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(refusal).IsEqualTo("step 1 places A at the start: edit it there");
            await Assert.That(rowNote).IsEqualTo("step 1 places B at the start: edit it there");
            await Assert.That(editor.Start.Note).StartsWith("step 1 places A at the start");
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(StratHistory.ToNode(session.Document!).ToJsonString()).IsEqualTo(before).Because("still captured, every mark kept");
        }
    }

    [Test]
    public async Task AStartDrag_FollowsThePointer_AndShowsTheWayFromTheNewStart()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_synthetic", "T", "default", "drag", Created);
        document.Canvas = new StratCanvas { DefaultLevelMinZ = Floor };
        document.Start = new StratStart { Positions = [new StartPosition { Slot = "A", X = 10, Y = 10, LevelMinZ = Floor }] };
        document.Steps = [Step(1, 115, "A", "move", to: "Ramp")];
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStart();

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(40, 60), Floor);
        canvas.MoveTo("A", new SKPoint(50, 70), Floor);
        Playback2D.Core.PlayerMarker marker = canvas.CurrentFrame.Markers.Single(m => m.Slot == 0);
        using (Assert.Multiple())
        {
            await Assert.That((marker.WorldX, marker.WorldY)).IsEqualTo((50f, 70f));
            await Assert.That(canvas.Guides.GhostRoute.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(canvas.Guides.GhostRoute[0].X).IsEqualTo(50f).Because("the way shown leaves from the new start");
        }

        canvas.CancelDrag();
    }

    [Test]
    public async Task AClockSwitch_SaysTimesInProseAreNotMoved()
    {
        StratDocument document = SeedWithSpots("hold");
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        StratEditorViewModel editor = new(session);
        editor.Project();
        await Assert.That(editor.ClockNote).IsEqualTo("");
        editor.ClockChoice = StratEditorViewModel.TriggerClockChoice;
        editor.Project();
        await Assert.That(editor.ClockNote).Contains("notes, branch conditions and the trigger are not");
    }
}

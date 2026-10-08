#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.TestSupport;
using SkiaSharp;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratStepMotionTests;
using static DemoViewer.NET.AppTests.StratTestData;
using DemoViewer.NET.Extensions.StratBook.Services.Zones;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The start (docs/strat-format.md, "The start"): where every token stands at tick 0, read from a block, or in an
///     older file from its round-start step and the copies Add step made. The real B execute is the file that needed
///     it: its seed became a move at 1:55, and a later edit removed A to D's spawn entries from it, so only the lurk's
///     unmarked copies still say where they began.
/// </summary>
[NotInParallel]
public class StratStartBlockTests
{
    private const double Level = -99968;

    private const string LegacyName = "legacy.seed-move.dvstrat.json";

    // A to D's spawn spots as the lurk's copies hold them; E's and the opponents' are still on the move step.
    private static readonly (string Slot, float X, float Y)[] Spawn =
        [("A", -750, -791), ("B", -736, -912), ("C", -777, -690), ("D", -864, -737), ("E", -608, -800)];

    private static string LegacyText()
    {
        string repo = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found from the test output directory");
        return File.ReadAllText(Path.Combine(repo, "tests", "fixtures", "strats", LegacyName)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    internal static StratDocument Legacy() =>
        JsonSerializer.Deserialize(LegacyText(), StratJsonContext.Default.StratDocument)!;

    private static IZonePlaceResolver Dust2() =>
        new AssetZonePlaceResolverSource(MapAssetBundleReader.FindBundleDirectory, () => null).TryGet("de_dust2")
        ?? throw new SkipTestException("de_dust2's zones are not in assets/");

    private static StratSceneProjection Project(StratDocument document, IZonePlaceResolver? map) =>
        map is null
            ? StratSceneProjection.Build(document, StratPath.MainLine(document))
            : StratSceneProjection.Build(document, StratPath.MainLine(document), null, map.PlaceCentre, map.PlaceArrival,
                (place, x, y, level) => map.ResolveOnFloor(x, y, level) == place);

    private static TokenKeyframe Sample(StratSceneProjection projection, string slot, int tick) =>
        projection.Tracks.Single(t => t.Slot == slot).TrySample(tick, out TokenKeyframe k) ? k : throw new InvalidOperationException($"no {slot} at {tick}");

    private static double Distance(TokenKeyframe k, float x, float y) => Math.Sqrt((k.X - x) * (double)(k.X - x) + (k.Y - y) * (double)(k.Y - y));

    [Test]
    public async Task TheOwnersFile_LoadsAndSavesByteForByte_AndReadsItsStartFromTheSeedAndTheLurksCopies()
    {
        string text = LegacyText();
        StratDocument document = Legacy();
        StratStart? start = StratStartBlock.Legacy(document, out IReadOnlyList<StartSource> sources);
        using (Assert.Multiple())
        {
            await Assert.That(StratStore.Serialize(document)).IsEqualTo(text).Because("nothing is written on open");
            await Assert.That(document.Start).IsNull();
            await Assert.That(start).IsNotNull();
            await Assert.That(start!.Positions.Select(p => p.Slot)).IsEquivalentTo(["A", "B", "C", "D", "E", "O1", "O2", "O3", "O4", "O5"]);
            await Assert.That(sources.Where(s => s.Step == 1).Count()).IsEqualTo(4).Because("A to D's only spawn entries are the lurk's copies");
            await Assert.That(sources.Where(s => s.Step == 0).Count()).IsEqualTo(6);
            foreach ((string slot, float x, float y) in Spawn)
            {
                StartPosition entry = StratStartBlock.For(start, slot)!;
                await Assert.That((entry.X, entry.Y)).IsEqualTo(((double?)x, (double?)y));
            }
        }
    }

    [Test]
    public async Task TheOwnersExecute_LeavesSpawnAt155_AndNeverSnapsBack()
    {
        IZonePlaceResolver map = Dust2();
        StratSceneProjection projection = Project(Legacy(), map);
        (string Slot, float X, float Y)[] destinations =
            [("B", -185.33f, 453.15f), ("C", 646.59f, 480.5f), ("D", -1958.08f, 1072.62f)];
        int lurk = StepSchedule.TickFor(108, 115);
        using (Assert.Multiple())
        {
            await Assert.That(projection.HasStart).IsTrue();
            foreach ((string slot, float x, float y) in Spawn)
            {
                TokenKeyframe at = Sample(projection, slot, 0);
                await Assert.That((at.X, at.Y)).IsEqualTo((x, y)).Because($"{slot} stands in spawn at 1:55");
            }

            foreach ((string slot, float x, float y) in destinations)
            {
                (string _, float sx, float sy) = Spawn.Single(s => s.Slot == slot);
                TokenKeyframe second = Sample(projection, slot, 64);
                await Assert.That(Distance(second, x, y)).IsLessThan(Math.Sqrt((x - sx) * (double)(x - sx) + (y - sy) * (double)(y - sy)))
                    .Because($"{slot} is on its way a second in");
                await Assert.That(Distance(second, sx, sy)).IsGreaterThan(100).Because($"{slot} has left spawn");
                TokenKeyframe atLurk = Sample(projection, slot, lurk);
                await Assert.That(Distance(atLurk, sx, sy)).IsGreaterThan(300).Because($"the lurk's copy does not pull {slot} back to spawn");
            }

            TokenKeyframe a = Sample(projection, "A", lurk);
            await Assert.That(Distance(a, -750, -791)).IsGreaterThan(300).Because("A runs to Upper Tunnel");
        }
    }

    [Test]
    public async Task AFirstMoveAtTheStart_Walks_InAStratWithAStartBlock()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_motion", "T", "execute", "start", Created);
        document.Start = new StratStart
        {
            Positions = [new StartPosition { Slot = "A", X = 0, Y = 0, LevelMinZ = 0 }]
        };
        StratStep move = Step(1, 115, "A", "move", to: "Mid");
        document.Steps = [move];
        StratSceneProjection projection = Project(document, null);
        StratSceneProjection routed = StratSceneProjection.Build(document, StratPath.MainLine(document), null,
            (place, _) => place == "Mid" ? (1075, 0) : null);
        TokenKeyframe start = Sample(routed, "A", 0), later = Sample(routed, "A", 64), end = Sample(routed, "A", RunTicks(1075, 0));
        using (Assert.Multiple())
        {
            await Assert.That(projection.HasStart).IsTrue();
            await Assert.That((start.X, start.Y)).IsEqualTo((0f, 0f));
            await Assert.That(later.X).IsEqualTo(215f).Within(1f).Because("it runs from the start at 215 units a second");
            await Assert.That((end.X, end.Y)).IsEqualTo((1075f, 0f));
        }
    }

    [Test]
    public async Task AHoldAtTheStart_RunsFromTheStart()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_motion", "T", "execute", "start", Created);
        document.Start = new StratStart { Positions = [new StartPosition { Slot = "A", X = 0, Y = 0, LevelMinZ = 0 }] };
        document.Steps = [Step(1, 115, "A", "hold", to: "Mid")];
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), null,
            (place, _) => place == "Mid" ? (430, 0) : null);
        using (Assert.Multiple())
        {
            await Assert.That(Sample(projection, "A", 0).X).IsEqualTo(0f);
            await Assert.That(Sample(projection, "A", 64).X).IsEqualTo(215f).Within(1f);
            await Assert.That(Sample(projection, "A", RunTicks(430, 0)).X).IsEqualTo(430f);
        }
    }

    [Test]
    public async Task ATokenWithNoStart_StillStartsAtItsDestination()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_motion", "T", "execute", "start", Created);
        document.Steps = [Step(1, 90, "A", "move", to: "Mid")];
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), null,
            (place, _) => place == "Mid" ? (430, 0) : null);
        using (Assert.Multiple())
        {
            await Assert.That(projection.HasStart).IsFalse();
            await Assert.That(Sample(projection, "A", projection.Ticks[0]).X).IsEqualTo(430f);
        }
    }

    [Test]
    public async Task APlaceOnlyStart_ShareByFive_FansOut()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_motion", "T", "execute", "start", Created);
        document.Start = new StratStart
        {
            Kind = StratStart.CustomKind,
            Positions = [.. StratVocabulary.Slots.Select(s => new StartPosition { Slot = s, Place = "TSpawn" })]
        };
        document.Steps = [Step(1, 90, StratVocabulary.ActorAll, "wait")];
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), null,
            (place, _) => place == "TSpawn" ? (1000, 1000) : null);
        List<(float X, float Y)> spots = [.. StratVocabulary.Slots.Select(s => (Sample(projection, s, 0).X, Sample(projection, s, 0).Y))];
        await Assert.That(spots.Distinct().Count()).IsEqualTo(5).Because("five tokens at one place stand apart");
    }

    [Test]
    public async Task TheFirstStartEdit_WritesTheBlock_MarksTheCopies_AndPlaysTheSame()
    {
        IZonePlaceResolver map = Dust2();
        StratDocument before = Legacy();
        string bytes = StratStore.Serialize(before);
        List<PatchOp> ops = StratStartBlock.Ops(before,
            new Dictionary<string, StartPosition?> { ["O1"] = new() { X = 300, Y = 2300, LevelMinZ = Level } },
            StratSceneProjection.LegacyCarriedEntries(before));
        StratDocument after = StratHistory.Apply(before, ops);
        StratSceneProjection was = Project(before, map), now = Project(after, map);
        using (Assert.Multiple())
        {
            await Assert.That(after.Start).IsNotNull();
            await Assert.That(after.Start!.Kind).IsEqualTo(StratStart.CustomKind);
            await Assert.That(after.Start.Positions.Count).IsEqualTo(10);
            await Assert.That(after.Steps[0].Positions).IsEmpty().Because("the start replaces the seed's entries");
            await Assert.That(after.Steps[1].Positions.Select(p => p.Slot)).IsEquivalentTo(["E"]).Because("the lurk's spawn copies became the start");
            await Assert.That(Sample(now, "O1", 0).X).IsEqualTo(300f);
            foreach (string slot in StratVocabulary.Slots)
            {
                foreach (int tick in new[] { 0, 64, 448, 2000, 4000 })
                {
                    TokenKeyframe a = Sample(was, slot, tick), b = Sample(now, slot, tick);
                    await Assert.That((b.X, b.Y)).IsEqualTo((a.X, a.Y)).Because($"{slot} plays the same at {tick}");
                }
            }

            StratDocument undone = StratHistory.Apply(after, new HistoryEntry { Ops = ops }.Inverse().Ops);
            await Assert.That(StratStore.Serialize(undone)).IsEqualTo(bytes).Because("one entry, undone exactly");
        }
    }

    [Test]
    public async Task ACapture_WritesItsFreezeEndAsAnObservedStart_AndPlaysExactlyAsWithout()
    {
        const int freeze = 6400;
        List<CapturedPawn> start = [.. Enumerable.Range(0, 10).Select(s => new CapturedPawn(s, s < 5 ? 2 : 3, (ulong)(100 + s), "p" + s, s * 400, 0, 0, 90, "TSpawn"))];
        List<CapturedPawn> later = [.. start.Select(p => p with { X = p.X + 900, Y = 300, Place = "Mid" })];
        RoundCapture capture = new(7, freeze, freeze + 64 * 100, 64,
        [
            new CaptureMoment(freeze, CaptureTrigger.FreezeEnd, start),
            new CaptureMoment(freeze + 64 * 10, CaptureTrigger.Sweep, later)
        ]);
        StratCaptureOptions options = new(2, StratFromRound.Tokens(start, 2, StratFromRound.SlotMap(start.Where(p => p.Team == 2), null, null)),
            StratClock.DefaultRoundSeconds, false, StratFromRound.QuantizedLevel);
        StratDocument captured = StratFromRound.Document(capture, options, Team, "de_mirage", "r7", new StratOrigin { DemoSha256 = "ab", Round = 7 }, null, Created);
        StratDocument without = captured.Clone();
        without.Start = null;

        StratSceneProjection with = Project(captured, null), plain = Project(without, null);
        using (Assert.Multiple())
        {
            await Assert.That(captured.Start!.Kind).IsEqualTo(StratStart.CapturedKind);
            await Assert.That(captured.Start.Positions.All(p => p.Observed == true && p.Place == "TSpawn")).IsTrue();
            await Assert.That(captured.Steps[0].Verb).IsEqualTo("hold").Because("the freeze-end step stays");
            await Assert.That(with.Tracks.Select(t => t.Slot)).IsEquivalentTo(plain.Tracks.Select(t => t.Slot));
            foreach (TokenTrack track in plain.Tracks)
            {
                for (int tick = 0; tick <= plain.ContentEndTick; tick += 7)
                {
                    TokenKeyframe a = Sample(plain, track.Slot, tick), b = Sample(with, track.Slot, tick);
                    await Assert.That((b.X, b.Y, b.YawDegrees)).IsEqualTo((a.X, a.Y, a.YawDegrees)).Because($"{track.Slot} at {tick}");
                }
            }
        }
    }

    private static readonly StratSpawns FixedSpawns = new(
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(1000 + 100 * i, 0, 0, "TSpawn"))],
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(-1000 - 100 * i, 0, 0, "CTSpawn"))]);

    // Rush A as it was left: the seed turned into a throw at 1:55 holding only the opponents, our five gone from it.
    private static StratDocument SeedWithoutOurFive()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "rush", "Rush A", Created);
        StratStep smoke = Step(1, 115, "C", "throw");
        smoke.Utility = new UtilityRef { Kind = "smoke" };
        smoke.Positions = [.. StratVocabulary.OpponentSlots.Select((s, i) => new StepPosition { Slot = s, X = -1000 - 100 * i, Y = 50 })];
        document.Steps = [smoke, Step(2, 106, StratVocabulary.ActorAll, "move", to: "BombsiteA")];
        return document;
    }

    [Test]
    public async Task AnOlderSeedWithoutOurFive_StartsThemAtTheSpawns_OnceTheSpawnsAreRead()
    {
        StratDocument document = SeedWithoutOurFive();
        PlaceCentreResolver centres = (place, _) => place == "BombsiteA" ? (1000, 2150) : null;
        StratSceneProjection without = StratSceneProjection.Build(document, StratPath.MainLine(document), null, centres);
        StratSceneProjection with = StratSceneProjection.Build(document, StratPath.MainLine(document), null, centres, spawns: FixedSpawns);
        int move = StepSchedule.TickFor(106, 115);
        using (Assert.Multiple())
        {
            await Assert.That(StratStartBlock.Effective(document)!.Positions.Select(p => p.Slot)).IsEquivalentTo(StratVocabulary.OpponentSlots);
            await Assert.That(StratStartBlock.Effective(document, FixedSpawns)!.Positions.Count).IsEqualTo(10);
            await Assert.That(without.Tracks.Single(t => t.Slot == "A").TrySample(0, out _)).IsFalse().Because("no spawns, no start for A");
            await Assert.That(Sample(with, "A", 0).X).IsEqualTo(1000f);
            await Assert.That(Sample(with, "A", move + 64).Y).IsGreaterThan(100f).Because("A runs from spawn at 1:46");
            await Assert.That(Sample(with, "A", move + 64).Y).IsLessThan(2150f);
            await Assert.That(StratStore.Serialize(document)).DoesNotContain("\"start\"").Because("reading the spawns writes nothing");
        }
    }

    private static StratCanvasViewModel Canvas(StratSession session) =>
        new(session, _ => null, new ManualTicker(), null, StratTestKeymap.Shipped,
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a());

    private static void Drag(StratCanvasViewModel canvas, string slot, double x, double y, TokenGrip grip = TokenGrip.Body)
    {
        canvas.BeginDrag(slot, grip);
        canvas.MoveTo(slot, new SKPoint((float)x - 5, (float)y - 5), -512);
        canvas.MoveTo(slot, new SKPoint((float)x, (float)y), -512);
        canvas.EndDrag();
    }

    private static StratDocument WithStart(params StratStep[] steps)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_synthetic", "T", "execute", "start", Created);
        document.Canvas = new StratCanvas { DefaultLevelMinZ = -512 };
        document.Start = new StratStart
        {
            Positions = [.. StratVocabulary.Slots.Select((s, i) => new StartPosition { Slot = s, X = 10 + 10 * i, Y = 10, LevelMinZ = -512 })]
        };
        document.Steps = [.. steps];
        return document;
    }

    [Test]
    public async Task WithTheStartSelected_ADragEditsTheStart_InOneEntry()
    {
        StratDocument document = WithStart(Step(1, 115, "A", "move", to: "Ramp"));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStart();
        string before = StratHistory.ToNode(session.Document!).ToJsonString();

        canvas.BeginDrag("A", TokenGrip.Body);
        await Assert.That(canvas.DragTarget!.Action).IsEqualTo(StratDragAction.Start);
        canvas.MoveTo("A", new SKPoint(25, 75), -512);
        await Assert.That(canvas.DragLabel).IsEqualTo("A · start: Hut");
        canvas.EndDrag();

        StartPosition a = StratStartBlock.For(session.Document!.Start, "A")!;
        using (Assert.Multiple())
        {
            await Assert.That((a.Place, a.X, a.Y)).IsEqualTo(("Hut", (double?)25, (double?)75));
            await Assert.That(session.Document!.Start!.Kind).IsEqualTo(StratStart.CustomKind);
            await Assert.That(session.Document!.Steps[0].To!.Place).IsEqualTo("Ramp").Because("the step is not touched");
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(canvas.IsStartSelected).IsTrue();
            await Assert.That(canvas.StatusLine).Contains("A now starts at Hut");
        }

        session.Undo();
        await Assert.That(StratHistory.ToNode(session.Document!).ToJsonString()).IsEqualTo(before);
    }

    [Test]
    public async Task WithTheStepSelected_ADragAtTheStartEditsTheStep_AndAStandingTokenEditsItsStart()
    {
        StratDocument document = WithStart(Step(1, 115, "A", "move", to: "Ramp"), Step(2, 100, "A", "wait"));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[0].Id);

        Drag(canvas, "A", 25, 75);
        await Assert.That(session.Document!.Steps[0].To!.Place).IsEqualTo("Hut").Because("a move at the start time takes the drop as its to");
        await Assert.That(StratStartBlock.For(session.Document!.Start, "A")!.X).IsEqualTo(10);

        // At a wait no one moves, and nothing has placed B since it started: the drag moves its start.
        canvas.SelectStep(document.Steps[1].Id);
        canvas.BeginDrag("B", TokenGrip.Body);
        await Assert.That(canvas.DragTarget!.Action).IsEqualTo(StratDragAction.Start);
        canvas.CancelDrag();
    }

    [Test]
    public async Task AStartPick_OnTheMap_WritesTheStart()
    {
        StratDocument document = WithStart(Step(1, 110, "A", "move", to: "Ramp"));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);

        await Assert.That(canvas.BeginSetPlace(StratStartBlock.FieldFor("C"))).IsTrue();
        await Assert.That(canvas.IsStartSelected).IsTrue();
        await Assert.That(canvas.Transport.Tick).IsEqualTo(0);
        canvas.TryTagPositionAt(StratMapFirstTests.Upper, 30, 60);

        StartPosition c = StratStartBlock.For(session.Document!.Start, "C")!;
        await Assert.That((c.Place, c.X, c.Y)).IsEqualTo(("Hut", (double?)30, (double?)60));
    }

    [Test]
    public async Task TheStartRow_SaysWhereTheTokensBegin_AndWritesASlotsStart()
    {
        StratDocument document = WithStart(Step(1, 110, "A", "move", to: "Ramp"));
        document.Trigger = new StratTrigger { Text = "on call" };
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratEditorViewModel editor = new(session);
        editor.Project();
        using (Assert.Multiple())
        {
            await Assert.That(editor.Start.Line).IsEqualTo("spawn · on call");
            await Assert.That(editor.Start.IsExpanded).IsFalse();
            await Assert.That(editor.Start.Own.Single(r => r.Slot == "B").Value.Single().X).IsEqualTo(20);
        }

        editor.Start.Own.Single(r => r.Slot == "B").Value = [new PlaceRef { Place = "Ramp" }];
        editor.Project();
        using (Assert.Multiple())
        {
            await Assert.That(StratStartBlock.For(session.Document!.Start, "B")!.Place).IsEqualTo("Ramp");
            await Assert.That(StratStartBlock.For(session.Document!.Start, "B")!.X).IsNull();
            await Assert.That(editor.Start.Summary).Contains("B Ramp");
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }
    }

    [Test]
    public async Task WritingAStartBackUnchanged_IsNoEdit_SoACaptureKeepsItsMarks()
    {
        StratDocument document = WithStart(Step(1, 110, "A", "move", to: "Ramp"));
        document.Start!.Kind = StratStart.CapturedKind;
        document.Start.Positions.ForEach(p =>
        {
            p.Observed = true;
            p.YawDegrees = 90;
        });
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratEditorViewModel editor = new(session);
        editor.Project();
        string before = StratHistory.ToNode(session.Document!).ToJsonString();

        foreach (DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratStartSlotRow row in editor.Start.Own)
        {
            row.Value = [.. row.Value.Select(StratLocations.Clone)];
        }

        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(session.Document!.Start!.Kind).IsEqualTo(StratStart.CapturedKind);
            await Assert.That(StratHistory.ToNode(session.Document!).ToJsonString()).IsEqualTo(before);
            await Assert.That(StratStartBlock.WriteLocation(session.Document!, "A", StratStartBlock.LocationOf(session.Document!, "A"))).IsEmpty();
        }
    }

    [Test]
    public async Task TheZipCheck_MeasuresAFirstStepsDeparture_FromTheStart()
    {
        StratStep far = Step(1, 110, "A", "move", to: "Ramp");
        far.Positions = [new StepPosition { Slot = "A", X = 3000, Y = 10, LevelMinZ = -512 }];
        StratStep near = Step(1, 110, "A", "move", to: "Ramp");
        near.Positions = [new StepPosition { Slot = "A", X = 200, Y = 10, LevelMinZ = -512 }];
        using (Assert.Multiple())
        {
            await Assert.That(StratDepartureCheck.Check(WithStart(far)).Select(i => i.Field)).IsEquivalentTo(["/steps/0/positions/0"])
                .Because("3000 units from the start in 5 s is faster than a run");
            await Assert.That(StratDepartureCheck.Check(WithStart(near))).IsEmpty();
            await Assert.That(StratDepartureCheck.Check(Legacy()).Select(i => i.Field)).IsEquivalentTo(["/steps/1/positions/4"])
                .Because("the seed's own entries are its start; E's dragged lurk departure was already too far for a run");
        }
    }

    [Test]
    public async Task ACapturedStrat_HasNoImpliedStart()
    {
        StratDocument document = Legacy();
        document.Origin = new StratOrigin { DemoSha256 = "ab", Round = 3 };
        await Assert.That(StratStartBlock.Effective(document)).IsNull();
    }

    [Test]
    public async Task AStepNotAtTheStart_ImpliesNoStart()
    {
        StratDocument document = Legacy();
        document.Steps[0].AtSeconds = 110;
        await Assert.That(StratStartBlock.Effective(document)).IsNull();
    }
}

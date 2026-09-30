#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A step's destination moves its tokens: travel verbs run there, position verbs are there at the step's time, a
///     lurk runs to its first area; tokens sent together fan out; precedence against origins, drags and carried entries.
/// </summary>
[NotInParallel]
public class StratStepMotionTests
{
    // 1:50 and 1:40 on a 115 s clock.
    private const int One = 320, Two = 960;

    // TSpawn and BombsiteA on the upper floor, BombsiteB on the lower one only.
    internal static IZonePlaceResolver Zones() => new ZonePlaceResolverAdapter(new PlaceResolver(new ZoneSet(
        "de_motion", "1", "1", null, 64,
        [new ZoneFloor(-512, -528, 100_000), new ZoneFloor(-2048, -100_000, -528)],
        [new ZonePlace(0, "TSpawn", PlaceOrigin.Baked), new ZonePlace(1, "BombsiteA", PlaceOrigin.Baked), new ZonePlace(2, "BombsiteB", PlaceOrigin.Baked)],
        [],
        [
            new ZoneArea(1, 0, -512, true, -400, [0, 0, 200, 0, 200, 200, 0, 200]),
            new ZoneArea(2, 1, -512, true, -400, [1000, 0, 1400, 0, 1400, 400, 1000, 400]),
            new ZoneArea(3, 2, -2048, true, -1900, [0, 1000, 400, 1000, 400, 1400, 0, 1400])
        ],
        [(1, 2), (1, 3)], [(0, 1), (0, 2)], null)));

    private static readonly IZonePlaceResolver Map = Zones();

    private static bool Inside(string place, TokenKeyframe k) => Map.ResolveOnFloor(k.X, k.Y, k.LevelMinZ) == place;

    private static StratSceneProjection Project(StratDocument document, ThrowOriginResolver? origins = null) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), origins, Map.PlaceCentre, Map.PlaceArrival,
            (place, x, y, level) => Map.ResolveOnFloor(x, y, level) == place);

    private static TokenTrack Track(StratDocument document, string slot, ThrowOriginResolver? origins = null) =>
        Project(document, origins).Tracks.Single(t => t.Slot == slot);

    private static TokenKeyframe At(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe k) ? k : throw new InvalidOperationException($"no sample at {tick}");

    private static StepPosition Upper(string slot, double x, double y) => new() { Slot = slot, X = x, Y = y, LevelMinZ = -512 };

    // A stands at (100, 100) in TSpawn from 1:50; at 1:40 the step sends A to a place.
    private static StratDocument Sent(string verb, string to = "BombsiteA", string actor = "A")
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_motion", "T", "execute", "motion", Created);
        StratStep one = Step(1, 110, StratVocabulary.ActorAll, "hold");
        one.Positions = [.. StratVocabulary.Slots.Select((s, i) => Upper(s, 20 + 40 * i, 100)), .. Opponents()];
        one.Positions[0] = Upper("A", 100, 100);
        StratStep two = Step(2, 100, actor, verb, to: to);
        document.Steps = [one, two];
        return document;
    }

    // The other side's five, as a new strat's seed places them.
    private static IEnumerable<StepPosition> Opponents() => StratVocabulary.OpponentSlots.Select((s, i) => Upper(s, 1100 + 40 * i, 300));

    private static int RunTicks(double dx, double dy) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / StratSceneProjection.RotateUnitsPerSecond * 64));

    [Test]
    [Arguments("move")]
    [Arguments("push")]
    [Arguments("rotate")]
    [Arguments("other")]
    public async Task ATravelVerb_LeavesAtTheStep_AndArrivesAtRunSpeed(string verb)
    {
        TokenTrack a = Track(Sent(verb), "A");
        int arrive = Two + RunTicks(1200 - 100, 200 - 100);
        using (Assert.Multiple())
        {
            await Assert.That(At(a, Two).X).IsEqualTo(100f).Because("it leaves where it stands at the step's time");
            await Assert.That(At(a, arrive - 5).X).IsLessThan(1200f);
            await Assert.That(At(a, arrive).X).IsEqualTo(1200f);
            await Assert.That(At(a, arrive).Y).IsEqualTo(200f);
            await Assert.That(At(a, (Two + arrive) / 2).YawDegrees).IsEqualTo((float)Math.Round(Math.Atan2(100, 1100) * 180 / Math.PI, 2))
                .Because("a runner faces where it runs");
            await Assert.That(At(a, arrive + 6400).X).IsEqualTo(1200f).Because("it stays at the place");
        }
    }

    [Test]
    [Arguments("hold")]
    [Arguments("peek")]
    [Arguments("fake")]
    [Arguments("plant")]
    [Arguments("defuse")]
    public async Task APositionVerb_IsThereAtTheStepsTime(string verb)
    {
        TokenTrack a = Track(Sent(verb), "A");
        using (Assert.Multiple())
        {
            await Assert.That(At(a, One).X).IsEqualTo(100f);
            await Assert.That(At(a, (One + Two) / 2).X).IsBetween(500f, 800f).Because("it walks there from its last keyframe");
            await Assert.That(At(a, Two).X).IsEqualTo(1200f);
            await Assert.That(At(a, Two).Y).IsEqualTo(200f);
        }
    }

    [Test]
    [Arguments("throw")]
    [Arguments("wait")]
    [Arguments("call")]
    public async Task AThrowWithoutALineup_AWait_AndACall_DoNotMove(string verb)
    {
        TokenTrack a = Track(Sent(verb), "A");
        await Assert.That(a.Keyframes.All(k => k.X == 100f)).IsTrue();
    }

    [Test]
    public async Task ALurk_RunsToItsFirstArea_ThenItsRotateFollows()
    {
        StratDocument document = Sent("lurk");
        document.Steps[1].To = null;
        document.Steps[1].Lurk = new StepLurk
        {
            Areas = ["BombsiteA"], Rotate = new LurkRotate { AtSeconds = 60, To = new PlaceRef { Place = "TSpawn" } }
        };
        TokenTrack a = Track(document, "A");
        int arrive = Two + RunTicks(1100, 100);
        int rotate = StepSchedule.TickFor(60, 115);
        using (Assert.Multiple())
        {
            await Assert.That(At(a, Two).X).IsEqualTo(100f);
            await Assert.That(At(a, arrive).X).IsEqualTo(1200f);
            await Assert.That(At(a, rotate).X).IsEqualTo(1200f).Because("it works the area until the rotate");
            await Assert.That(At(a, rotate + RunTicks(1100, 100)).X).IsEqualTo(100f);
        }

        document.Steps[1].Lurk!.Areas = [];
        document.Steps[1].Lurk!.AreaPoints = [new PlaceRef { X = 600, Y = 150, LevelMinZ = -512 }];
        TokenTrack point = Track(document, "A");
        await Assert.That(At(point, Two + RunTicks(500, 50)).X).IsEqualTo(600f).Because("a picked area point is where it runs");
    }

    [Test]
    public async Task APoint_IsWhereTheTokenGoes()
    {
        StratDocument document = Sent("move");
        document.Steps[1].To = new PlaceRef { Place = "BombsiteA", X = 1050, Y = 350, LevelMinZ = -512 };
        TokenTrack a = Track(document, "A");
        TokenKeyframe end = a.Keyframes[^1];
        await Assert.That((end.X, end.Y)).IsEqualTo((1050f, 350f));
    }

    [Test]
    public async Task EveryoneSentToOnePlace_FansOut_InsideThePlace()
    {
        StratSceneProjection projection = Project(Sent("move", actor: StratVocabulary.ActorAll));
        List<TokenKeyframe> ends = [.. StratVocabulary.Slots.Select(s => projection.Tracks.Single(t => t.Slot == s).Keyframes[^1])];
        using (Assert.Multiple())
        {
            await Assert.That(ends.Select(k => (k.X, k.Y)).Distinct().Count()).IsEqualTo(5).Because("no two tokens stack");
            await Assert.That(ends.All(k => Inside("BombsiteA", k))).IsTrue();
            await Assert.That(ends.All(k => Math.Abs(k.X - 1200) <= 64 && Math.Abs(k.Y - 200) <= 64)).IsTrue();
        }

        // The same spot for a slot every time, and a group of two lines fans the same way.
        StratDocument lines = Sent("move", actor: StratVocabulary.ActorAll);
        lines.Steps[1].To = null;
        lines.Steps[1].Assignments =
        [
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteA" } },
            new StepAssignment { Slot = "D", To = new PlaceRef { Place = "BombsiteA" } }
        ];
        StratSceneProjection two = Project(lines);
        TokenKeyframe b = two.Tracks.Single(t => t.Slot == "B").Keyframes[^1], d = two.Tracks.Single(t => t.Slot == "D").Keyframes[^1];
        using (Assert.Multiple())
        {
            await Assert.That((b.X, b.Y)).IsEqualTo((ends[1].X, ends[1].Y));
            await Assert.That((d.X, d.Y)).IsEqualTo((ends[3].X, ends[3].Y));
        }

        // "all" is the strat's own five: the other side's tokens stay where the seed put them.
        foreach (string opponent in StratVocabulary.OpponentSlots)
        {
            TokenTrack o = projection.Tracks.Single(t => t.Slot == opponent);
            await Assert.That(o.Keyframes.All(k => k.X == o.Keyframes[0].X && k.Y == o.Keyframes[0].Y)).IsTrue().Because($"{opponent} never moves");
        }

        // Alone, a token goes to the centre.
        await Assert.That(Track(Sent("move"), "A").Keyframes[^1].X).IsEqualTo(1200f);

        // A place too small for the spots: a small ring around its arrival.
        (double X, double Y) ring = StratSceneProjection.SpotFor("Tiny", (0, 0, 0), "A", (_, _, _, _) => false);
        await Assert.That(Math.Round(Math.Sqrt(ring.X * ring.X + ring.Y * ring.Y), 3)).IsEqualTo(32d);
    }

    [Test]
    public async Task ARunToAPlaceOnAnotherFloor_ArrivesOnThatFloor()
    {
        TokenTrack a = Track(Sent("move", "BombsiteB"), "A");
        TokenKeyframe end = a.Keyframes[^1];
        using (Assert.Multiple())
        {
            await Assert.That(end.LevelMinZ).IsEqualTo(-2048d);
            await Assert.That((end.X, end.Y)).IsEqualTo((200f, 1200f));
            await Assert.That(At(a, Two).LevelMinZ).IsEqualTo(-512d).Because("it leaves from its own floor");
        }

        TokenTrack held = Track(Sent("hold", "BombsiteB"), "A");
        await Assert.That(At(held, Two).LevelMinZ).IsEqualTo(-2048d);
    }

    [Test]
    public async Task ALineupOrigin_BeatsTheDestination()
    {
        StratDocument document = Sent("throw");
        document.Steps[1].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        TokenTrack a = Track(document, "A", (_, _) => new TokenPlacement(-300, -300, -512, 90));
        await Assert.That(At(a, Two)).IsEqualTo(new TokenKeyframe(Two, -300, -300, -512, 90));
        await Assert.That(a.Keyframes.Any(k => k.X == 1200f)).IsFalse();
    }

    [Test]
    public async Task ADrag_BeatsTheDestination_AndTheDestinationBeatsACarriedEntry()
    {
        StratDocument dragged = Sent("move");
        dragged.Steps[1].Positions = [Upper("A", 600, 600)];
        TokenTrack a = Track(dragged, "A");
        using (Assert.Multiple())
        {
            await Assert.That((At(a, Two).X, At(a, Two).Y)).IsEqualTo((600f, 600f));
            await Assert.That(a.Keyframes.Any(k => k.X == 1200f)).IsFalse();
        }

        StratDocument carried = Sent("move");
        StepPosition copy = Upper("A", 700, 700);
        copy.Carried = true;
        carried.Steps[1].Positions = [copy];
        await Assert.That(Track(carried, "A").Keyframes[^1].X).IsEqualTo(1200f);

        // A carried entry with no destination still holds the token, as Add step meant it to.
        carried.Steps[1].To = null;
        await Assert.That(At(Track(carried, "A"), Two).X).IsEqualTo(700f);
    }

    [Test]
    public async Task AnUnmarkedCopyOfTheEntryBefore_ReadsAsCarried()
    {
        // Written by Add step before the mark: step 2 and step 3 hold A where step 1 put it.
        StratDocument document = Sent("move");
        document.Steps[1].Positions = [Upper("A", 100, 100)];
        StratStep three = Step(3, 80, "C", "hold");
        three.Positions = [Upper("A", 100, 100)];
        document.Steps.Add(three);
        TokenTrack a = Track(document, "A");
        int arrive = Two + RunTicks(1100, 100);
        using (Assert.Multiple())
        {
            await Assert.That(At(a, arrive).X).IsEqualTo(1200f).Because("the copy gives way to the destination");
            await Assert.That(At(a, StepSchedule.TickFor(80, 115)).X).IsEqualTo(1200f).Because("a later copy does not pull it back");
        }

        // One that moved the token is a person's.
        document.Steps[1].Positions = [Upper("A", 100.5, 100)];
        await Assert.That(At(Track(document, "A"), Two).X).IsEqualTo(100.5f);
    }

    [Test]
    public async Task AStepWithNoEntryInsideARun_DoesNotPinTheTokenBack()
    {
        StratDocument document = Sent("move");
        document.Steps.Add(Step(3, 99, "C", "hold"));
        document.Steps.Add(Step(4, 60, "D", "hold"));
        StratStep five = Step(5, 50, "A", "hold");
        five.Positions = [Upper("A", 1300, 300)];
        document.Steps.Add(five);
        TokenTrack a = Track(document, "A");
        int arrive = Two + RunTicks(1100, 100);
        int end = StepSchedule.TickFor(50, 115);
        List<float> xs = [.. Enumerable.Range(arrive, end - arrive).Select(t => At(a, t).X)];
        using (Assert.Multiple())
        {
            await Assert.That(At(a, StepSchedule.TickFor(99, 115)).X).IsBetween(100f, 1200f);
            await Assert.That(xs.Min()).IsGreaterThanOrEqualTo(1200f).Because("the token never goes back to where it started");
            await Assert.That(At(a, end).X).IsEqualTo(1300f);
        }
    }

    [Test]
    public async Task ALaterDestination_CutsARunThatHasNotArrived()
    {
        // A runs for Bombsite A at 1:40 and is sent to Bombsite B a second later, long before arriving.
        StratDocument twice = Sent("move");
        twice.Steps.Add(Step(3, 99, "A", "move", to: "BombsiteB"));
        TokenTrack a = Track(twice, "A");
        int turn = StepSchedule.TickFor(99, 115);
        using (Assert.Multiple())
        {
            await Assert.That(a.Keyframes[^1].LevelMinZ).IsEqualTo(-2048d);
            await Assert.That((a.Keyframes[^1].X, a.Keyframes[^1].Y)).IsEqualTo((200f, 1200f));
            await Assert.That(Enumerable.Range(turn, 2000).All(t => At(a, t).X < 1000f)).IsTrue()
                .Because("after the second call it never reaches Bombsite A");
        }

        // Everyone runs for the site; B plants at its centre before B's run ends, and stays there.
        StratDocument plant = Sent("move", actor: StratVocabulary.ActorAll);
        plant.Steps.Add(Step(3, 99, "B", "plant", to: "BombsiteA"));
        TokenTrack b = Track(plant, "B");
        using (Assert.Multiple())
        {
            await Assert.That((At(b, turn).X, At(b, turn).Y)).IsEqualTo((1200f, 200f));
            await Assert.That(Enumerable.Range(turn, 2000).All(t => At(b, t).X == 1200f && At(b, t).Y == 200f)).IsTrue()
                .Because("no slide back to its fan spot");
        }

        // A lurk that rotates before reaching its first area still rotates.
        StratDocument lurk = Sent("lurk");
        lurk.Steps[1].To = null;
        lurk.Steps[1].Lurk = new StepLurk { Areas = ["BombsiteA"], Rotate = new LurkRotate { AtSeconds = 99, To = new PlaceRef { Place = "TSpawn" } } };
        TokenTrack l = Track(lurk, "A");
        await Assert.That((l.Keyframes[^1].X, l.Keyframes[^1].Y)).IsEqualTo((100f, 100f));
        await Assert.That(l.Keyframes.Any(k => k.X == 1200f)).IsFalse();
    }

    [Test]
    public async Task ANewStep_DoesNotCarryATokenADestinationMoved()
    {
        StratDocument document = Sent("move");
        List<StepPosition> carried = StratStepCarry.PositionsAt(document, 1, null, Map.PlaceCentre);
        using (Assert.Multiple())
        {
            await Assert.That(carried.Any(p => p.Slot == "A")).IsFalse();
            await Assert.That(carried.Single(p => p.Slot == "B").Carried).IsTrue();
        }
    }

    [Test]
    public async Task SettingATo_DropsThatSlotsCarriedEntry_InOneUndoEntry()
    {
        StratDocument document = Sent("move", actor: StratVocabulary.ActorAll);
        document.Steps[1].To = null;
        document.Steps[1].Positions = [.. document.Steps[0].Positions.Where(p => StratVocabulary.Slots.Contains(p.Slot)).Select(p =>
        {
            StepPosition copy = Upper(p.Slot, p.X, p.Y);
            copy.Carried = p.Slot != "C" ? true : null;
            return copy;
        })];
        (StratStore _, StratSession session) = Opened(document);
        string before = StratStore.Serialize(session.Document!);
        int depth = session.UndoDepth;

        StratLocationField field = new(document.Steps[1].Id, "B", StratLocationKind.To);
        List<PatchOp> ops = StratLocationPatches.Write(session.Document!, 1, field, [new PlaceRef { Place = "BombsiteA" }]);
        string said = StratDiffPhrasing.Summary(StratHistory.ToNode(session.Document!), ops);
        await Assert.That(said).EndsWith("B's carried position dropped").Because("the history says what went, not that B left the step");
        session.Apply(ops);
        List<StepPosition> after = session.Document!.Steps[1].Positions;
        using (Assert.Multiple())
        {
            await Assert.That(after.Select(p => p.Slot)).IsEquivalentTo(["A", "C", "D", "E"]).Because("only B's carried entry goes");
            await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
        }

        // C's entry is a person's: setting C's place leaves it.
        field = new StratLocationField(document.Steps[1].Id, "C", StratLocationKind.To);
        session.Apply(StratLocationPatches.Write(session.Document!, 1, field, [new PlaceRef { Place = "BombsiteA" }]));
        await Assert.That(session.Document!.Steps[1].Positions.Any(p => p.Slot == "C")).IsTrue();

        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(StratStore.Serialize(session.Document!)).IsEqualTo(before).Because("undo restores the exact bytes");

        // The step's own to on a step for everyone drops every carried entry it has.
        StratLocationField own = new(document.Steps[1].Id, null, StratLocationKind.To);
        session.Apply(StratLocationPatches.Write(session.Document!, 1, own, [new PlaceRef { Place = "BombsiteA" }]));
        await Assert.That(session.Document!.Steps[1].Positions.Select(p => p.Slot)).IsEquivalentTo(["C"]);
        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(StratStore.Serialize(session.Document!)).IsEqualTo(before);
    }

    [Test]
    public async Task ADrag_WritesAnUnmarkedEntry_AndAFileWithoutMarksSavesAsBefore()
    {
        StratDocument document = Sent("move");
        StepPosition copy = Upper("A", 100, 100);
        copy.Carried = true;
        document.Steps[1].Positions = [copy];
        PatchOp op = StepAuthoringPatches.TokenPosition(document, 1, "A", 500, 500, -512, null);
        await Assert.That(op.Value!.AsObject().ContainsKey("carried")).IsFalse();

        document.Steps[1].Positions = [Upper("A", 100, 100)];
        await Assert.That(StratStore.Serialize(document)).DoesNotContain("carried");
    }

    [Test]
    public async Task TheTransport_AndTheExport_ReachTheArrival()
    {
        StratDocument document = Sent("move");
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(Map), post: a => a());
        canvas.Transport.Seek(0);
        int arrive = Two + RunTicks(1100, 100);
        StratSceneProjection projection = canvas.Projection!;
        using (Assert.Multiple())
        {
            await Assert.That(projection.LastTick).IsEqualTo(Two);
            await Assert.That(projection.ContentEndTick).IsEqualTo(arrive);
            await Assert.That(canvas.Transport.EndTick).IsEqualTo(arrive);
        }

        StratExportCapture capture = canvas.CaptureForExport()!;
        ExportRangeOption range = StratExportJob.Ranges(capture.Projection)[0];
        StratFrameSource source = new(StratExportJob.BuildSpec(capture, null, range.StartFrame, range.EndFrame, 64, 1.0));
        int frame = Enumerable.Range(0, source.FrameCount).First(i => source.TickAt(i) == arrive);
        PlayerMarker a = source.FrameAt(frame).Markers.Single(m => m.Slot == TokenSlots.OrderOf("A"));
        await Assert.That(a.WorldX).IsEqualTo(1200f).Within(0.01f);
    }

    // A strat from a template on the motion map: the five in TSpawn at the round start, then the template's steps.
    internal static StratDocument FromTemplate(StratTemplate template)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_motion", template.Side ?? "T", template.Type, template.Name, Created);
        StratStep seed = new() { Id = Guid.NewGuid(), AtSeconds = 115, Actor = StratVocabulary.ActorAll, Verb = "hold" };
        seed.Positions = [.. StratVocabulary.Slots.Select((s, i) => Upper(s, 20 + 40 * i, 100)), .. Opponents()];
        document.Steps = [seed];
        StratTemplates.Apply(document, template);
        return document;
    }

    [Test]
    public async Task TheExecute_BringsItsEntryPlayersOntoTheSite()
    {
        StratDocument document = FromTemplate(StratTemplates.Find("execute-a")!);
        StratSceneProjection projection = Project(document);
        foreach ((string slot, double at) in new[] { ("A", 53.0), ("B", 52.0) })
        {
            TokenTrack track = projection.Tracks.Single(t => t.Slot == slot);
            int tick = StepSchedule.TickFor(at, 115);
            TokenKeyframe start = At(track, tick);
            int arrive = tick + RunTicks(1200 - start.X, 200 - start.Y);
            using (Assert.Multiple())
            {
                await Assert.That(At(track, arrive).X).IsEqualTo(1200f);
                await Assert.That(Inside("BombsiteA", At(track, arrive))).IsTrue();
            }
        }
    }

    [Test]
    public async Task TheSetup_SendsItsLinesToTheirHolds()
    {
        StratDocument document = FromTemplate(StratTemplates.Find("setup")!);
        StratSceneProjection projection = Project(document);
        int later = StepSchedule.TickFor(100, 115);
        using (Assert.Multiple())
        {
            foreach ((string slot, string place) in new[] { ("A", "BombsiteA"), ("B", "BombsiteA"), ("D", "BombsiteB"), ("E", "BombsiteB") })
            {
                TokenTrack track = projection.Tracks.Single(t => t.Slot == slot);
                await Assert.That(Inside("TSpawn", At(track, 0))).IsTrue().Because($"{slot} starts in spawn");
                await Assert.That(Inside(place, At(track, later))).IsTrue().Because($"{slot} holds {place}");
            }

            TokenTrack c = projection.Tracks.Single(t => t.Slot == "C");
            await Assert.That(Inside("TSpawn", At(c, later))).IsTrue().Because("C has no place to hold");
        }
    }

    [Test]
    public async Task EveryTemplateDestination_IsReached()
    {
        List<string> still = [];
        foreach (StratTemplate template in StratTemplates.Templates)
        {
            StratDocument document = FromTemplate(template);
            StratSceneProjection projection = Project(document);
            bool any = false;
            for (int i = 1; i < document.Steps.Count; i++)
            {
                StratStep step = document.Steps[i];
                int tick = projection.Ticks[i];
                foreach (string slot in StratVocabulary.Slots)
                {
                    if (StratSceneProjection.DestinationOf(step, slot) is not { Place: { } place })
                    {
                        continue;
                    }

                    any = true;
                    TokenTrack track = projection.Tracks.Single(t => t.Slot == slot);
                    bool reached = Enumerable.Range(tick, Math.Max(1, projection.ContentEndTick - tick + 1)).Any(t => Inside(place, At(track, t)));
                    await Assert.That(reached).IsTrue().Because($"{template.Id}: {slot} reaches {place} from step {i}");
                }
            }

            if (!any)
            {
                still.Add(template.Id);
            }
        }

        await Assert.That(still).IsEquivalentTo(["default", "anti-eco"]).Because("only these two send no one anywhere");
    }
}

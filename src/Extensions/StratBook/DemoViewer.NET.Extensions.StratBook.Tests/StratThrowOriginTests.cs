#region

using System.Collections.Concurrent;
using System.Numerics;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratTestData;
using GrenadeKind = DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeKind;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A throw step with a lineup puts its actor at the lineup's throw origin at the step's time: in the
///     projection, on the canvas and in the export's frame source, never by writing a position.
/// </summary>
public class StratThrowOriginTests
{
    private const string Map = "de_mirage";

    // Step 3 of FiveSteps is at 1:30 on a 115 s clock.
    private const int StepThreeTick = 1600;

    private static readonly Vector3 StandOrigin = new(-300, 500, -160);
    private static readonly Vector3 JumpOrigin = new(-296, 504, -150);
    private static readonly Vector3 Landing = new(-1400, -1400, -170);

    [Test]
    public async Task TheExportFrameSource_FindsTheThrower_AtTheLineupOrigin_AtTheStepTime()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = Indexed();
        using LineupOriginSource origins = new(index, a => a());
        origins.Load(Map);

        StratDocument document = FiveSteps();
        StratStep three = document.Steps[2];
        three.Verb = "throw";
        three.Actor = "B";
        three.Utility = new UtilityRef { Kind = "smoke", LineupId = lineup.Id };
        (StratStore store, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), id => store.Load(id).Document, StratTestKeymap.Shipped,
            lineupOrigins: origins);

        StratExportCapture capture = canvas.CaptureForExport()!;
        StratFrameSource source = new(StratExportJob.BuildSpec(capture, null, 0, capture.Projection.LastTick, 64, 1.0));
        int frame = Enumerable.Range(0, source.FrameCount).First(i => source.TickAt(i) == StepThreeTick);
        PlayerMarker b = source.FrameAt(frame).Markers.Single(m => m.Slot == TokenSlots.OrderOf("B"));

        WorldPoint expected = GrenadeLineups.TechniqueFor(lineup, null)!.Origin;
        using (Assert.Multiple())
        {
            await Assert.That(b.WorldX).IsEqualTo(expected.X).Within(0.01f);
            await Assert.That(b.WorldY).IsEqualTo(expected.Y).Within(0.01f);
            await Assert.That(document.Steps[2].Positions.Any(p => p.X == expected.X && p.Y == expected.Y)).IsFalse()
                .Because("the origin is projected, not written");
            await Assert.That(session.Document!.Steps[2].Positions.Single(p => p.Slot == "B").X).IsEqualTo(100)
                .Because("the authored position stays in the file");
        }
    }

    [Test]
    public async Task ACanvasOpenedBeforeTheMapIsGrouped_MovesTheThrower_WhenTheGroupingLands()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = Indexed();
        ConcurrentQueue<Action> posted = new();
        using LineupOriginSource origins = new(index, posted.Enqueue);

        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Actor = "B";
        document.Steps[2].Utility = new UtilityRef { Kind = "smoke", LineupId = lineup.Id };
        (StratStore store, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), id => store.Load(id).Document, StratTestKeymap.Shipped,
            lineupOrigins: origins);

        TokenKeyframe before = BAt(canvas);
        for (int i = 0; i < 500 && posted.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        while (posted.TryDequeue(out Action? action))
        {
            action();
        }

        WorldPoint expected = GrenadeLineups.TechniqueFor(lineup, null)!.Origin;
        TokenKeyframe after = BAt(canvas);
        using (Assert.Multiple())
        {
            await Assert.That(before.X).IsEqualTo(100f).Because("the map is still grouping: the authored spot");
            await Assert.That(before.Y).IsEqualTo(900f);
            await Assert.That(after.X).IsEqualTo(expected.X);
            await Assert.That(after.Y).IsEqualTo(expected.Y);
        }
    }

    [Test]
    public async Task DraggingTheThrower_OnItsOwnThrowStep_IsRefused_AndWritesNothing()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = Indexed();
        using LineupOriginSource origins = new(index, a => a());
        origins.Load(Map);
        (StratStore store, StratSession session) = Opened(ThrowByB(lineup.Id));
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), id => store.Load(id).Document, StratTestKeymap.Shipped,
            lineupOrigins: origins);
        canvas.Timeline.RequestSeek(StepThreeTick + 10);
        List<IReadOnlyList<PatchOp>> applied = [];
        session.OpsApplied += applied.Add;
        int depth = session.UndoDepth;

        canvas.BeginDrag("B", TokenGrip.Body);
        canvas.MoveTo("B", new SKPoint(0, 0), 0);
        canvas.EndDrag();

        using (Assert.Multiple())
        {
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(2);
            await Assert.That(canvas.StatusLine).Contains("lineup");
            await Assert.That(applied).IsEmpty();
            await Assert.That(session.UndoDepth).IsEqualTo(depth);
            await Assert.That(BAt(canvas).X).IsEqualTo(GrenadeLineups.TechniqueFor(lineup, null)!.Origin.X);
        }
    }

    [Test]
    public async Task OriginsThatLandMidDrag_AreDrawn_WhenTheDragIsCancelled()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = Indexed();
        ConcurrentQueue<Action> posted = new();
        using LineupOriginSource origins = new(index, posted.Enqueue);
        (StratStore store, StratSession session) = Opened(ThrowByB(lineup.Id));
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), id => store.Load(id).Document, StratTestKeymap.Shipped,
            lineupOrigins: origins);
        canvas.Timeline.RequestSeek(330);

        canvas.BeginDrag("A", TokenGrip.Body);
        for (int i = 0; i < 500 && posted.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        while (posted.TryDequeue(out Action? action))
        {
            action();
        }

        await Assert.That(BAt(canvas).X).IsEqualTo(100f).Because("the drag keeps the projection it started on");
        canvas.CancelDrag();
        await Assert.That(BAt(canvas).X).IsEqualTo(GrenadeLineups.TechniqueFor(lineup, null)!.Origin.X);
    }

    private static StratDocument ThrowByB(Guid lineupId)
    {
        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Actor = "B";
        document.Steps[2].Utility = new UtilityRef { Kind = "smoke", LineupId = lineupId };
        return document;
    }

    private static TokenKeyframe BAt(StratCanvasViewModel canvas) =>
        canvas.Projection!.Tracks.Single(t => t.Slot == "B").Keyframes.Single(k => k.Tick == StepThreeTick);

    [Test]
    public async Task TheLineupOrigin_WinsOverAnAuthoredPosition_AndTheTechniquePicksTheOrigin()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = Indexed();
        using LineupOriginSource origins = new(index, a => a());
        origins.Load(Map);

        StratDocument document = FiveSteps();
        StratStep three = document.Steps[2];
        three.Verb = "throw";
        three.Actor = "B";
        three.Utility = new UtilityRef { Kind = "smoke", LineupId = lineup.Id, Technique = "stand-jump-left" };

        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document),
            (m, u) => origins.Resolve(m, u, StratFromRound.QuantizedLevel));
        TokenPlacement jump = projection.ThrowOriginAt(2, "B")!.Value;
        WorldPoint jumpOrigin = lineup.Techniques.Single(t => t.Key == "stand-jump-left").Origin;

        three.Utility.Technique = "run-throw-both";
        TokenPlacement fallback = StratSceneProjection.Build(document, StratPath.MainLine(document),
            (m, u) => origins.Resolve(m, u, StratFromRound.QuantizedLevel))
            .ThrowOriginAt(2, "B")!.Value;

        using (Assert.Multiple())
        {
            await Assert.That(lineup.Techniques[0].Key).IsEqualTo("stand-throw-left");
            await Assert.That(jump.X).IsEqualTo(jumpOrigin.X);
            await Assert.That(jump.Y).IsEqualTo(jumpOrigin.Y);
            await Assert.That(fallback.X).IsEqualTo(lineup.Techniques[0].Origin.X).Because("a key the lineup lacks means the most thrown");
            await Assert.That(projection.ThrowOriginAt(2, "A")).IsNull();
            await Assert.That(projection.ThrowOriginAt(1, "B")).IsNull();
        }
    }

    [Test]
    public async Task ADrag_OnAnotherStep_KeepsTheThrowerAtTheOrigin()
    {
        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Actor = "B";
        document.Steps[2].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        TokenPlacement origin = new(-300, 500, -192, 45);
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), (_, _) => origin);

        TokenTrack dragging = projection.TrackWith("B", 0, new TokenPlacement(50, 50, 0, null));
        TokenTrack built = projection.Tracks.Single(t => t.Slot == "B");

        await Assert.That(dragging.Keyframes.Single(k => k.Tick == StepThreeTick).X).IsEqualTo(-300f);
        await Assert.That(built.Keyframes.Single(k => k.Tick == StepThreeTick).Y).IsEqualTo(500f);
    }

    [Test]
    public async Task AThrowByAll_OrWithoutALineup_OrNotAThrow_IsNotProjected()
    {
        StratDocument document = FiveSteps();
        document.Steps[0].Verb = "throw";
        document.Steps[0].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() }; // actor "all"
        document.Steps[1].Verb = "throw";
        document.Steps[1].Utility = new UtilityRef { Kind = "smoke" }; // no lineup
        document.Steps[3].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() }; // verb move
        int asked = 0;
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), (_, _) =>
        {
            asked++;
            return new TokenPlacement(1, 1, 0, null);
        });

        await Assert.That(asked).IsEqualTo(0);
        await Assert.That(Enumerable.Range(0, 5).All(i => StratVocabulary.Slots.All(s => projection.ThrowOriginAt(i, s) is null))).IsTrue();
    }

    [Test]
    public async Task AnOldAliasId_ResolvesToTheLineupThatAbsorbedIt()
    {
        Guid old = Guid.NewGuid();
        GrenadeLineup absorbing = Lineup(Guid.NewGuid(), [old]);
        GrenadeLineup other = Lineup(Guid.NewGuid(), []);
        GrenadeCluster cluster = new(GrenadeKind.Smoke, (0, 0, 0), new WorldPoint(0, 0, 0), null, [absorbing, other]);

        Dictionary<Guid, GrenadeLineup> byId = LineupOriginSource.ByAnyId([cluster]);

        await Assert.That(byId[old]).IsSameReferenceAs(absorbing);
        await Assert.That(byId[absorbing.Id]).IsSameReferenceAs(absorbing);
        await Assert.That(byId[other.Id]).IsSameReferenceAs(other);
    }

    [Test]
    public async Task ThePlacement_FacesTheReleaseYaw_WrappedIntoZeroTo360_OnTheGivenLevel()
    {
        GrenadeLineup lineup = Lineup(Guid.NewGuid(), []);
        lineup.Throws[0].Row.ReleaseEyeYaw = -90;

        TokenPlacement placement = LineupOriginSource.PlacementOf(lineup, null, _ => -512);

        await Assert.That(placement.YawDegrees).IsEqualTo(270f);
        await Assert.That(placement.LevelMinZ).IsEqualTo(-512);
    }

    [Test]
    public async Task TheValidator_Warns_OnAnUnknownTechnique_AndALineupThrownByAll()
    {
        StratDocument document = Minimal();
        document.Steps[0].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid(), Technique = "sideways" };
        document.Steps[1].Actor = StratVocabulary.ActorAll;
        document.Steps[1].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid(), Technique = "run-jump-both" };

        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, lineupExists: (_, _) => true);

        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
            await Assert.That(issues.Count(i => i.Field == "/steps/0/utility/technique" && i.Severity == StratIssueSeverity.Warning)).IsEqualTo(1);
            await Assert.That(issues.Any(i => i.Field == "/steps/1/utility/technique")).IsFalse();
            await Assert.That(issues.Count(i => i.Field == "/steps/1/actor" && i.Severity == StratIssueSeverity.Warning)).IsEqualTo(1);
            await Assert.That(StratVocabulary.Techniques.Count).IsEqualTo(12);
        }
    }

    [Test]
    public async Task TheTechniqueKey_RoundTrips_AndIsLeftOutWhenNull()
    {
        StratDocument document = Minimal();
        document.Steps[0].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid(), Technique = "stand-jump-left" };
        document.Steps[1].Utility = new UtilityRef { Kind = "smoke" };

        StratDocument copy = document.Clone();
        string json = System.Text.Json.JsonSerializer.Serialize(document, StratJsonContext.Default.StratDocument);

        await Assert.That(copy.Steps[0].Utility!.Technique).IsEqualTo("stand-jump-left");
        await Assert.That(json.Split("\"technique\"").Length - 1).IsEqualTo(1);
    }

    // Five demos: three standing throws and two jump-throws from one spot into one landing, one lineup of two techniques.
    internal static (GrenadeIndex Index, GrenadeLineup Lineup) Indexed(Action<GrenadeRow>? shape = null)
    {
        DemoCacheStore cache = new(null);
        for (int n = 1; n <= 5; n++)
        {
            string path = $"/d/origin-{n}.dem";
            bool jump = n > 3;
            GrenadeRow row = new()
            {
                Id = "s",
                Kind = GrenadeKind.Smoke,
                ThrowerTeam = 2,
                ReleaseTick = 1000,
                ReleasePosition = WorldPoint.From((jump ? JumpOrigin : StandOrigin) + new Vector3(n, 0, 0)),
                ReleaseEyeYaw = 30,
                JumpThrow = jump,
                DetonationPosition = WorldPoint.From(Landing),
                EndKind = GrenadeEndKind.Detonated
            };
            shape?.Invoke(row);
            DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, Map, $"sha{n}");
            GrenadeDocument document = new()
            {
                Demo = new GrenadeDemoHeader { Sha256 = $"sha{n}", StableKey = DemoCacheStore.StableKey(path) },
                Grenades = [row]
            };
            cache.Upsert(record);
            cache.WriteGrenades(path, document);
        }

        GrenadeIndex index = new(cache.Library());
        index.Load();
        GrenadeLineup lineup = index.Query(new GrenadeQuery(Map)).SelectMany(c => c.Lineups).Single();
        return (index, lineup);
    }

    private static GrenadeLineup Lineup(Guid id, IReadOnlyList<Guid> aliases)
    {
        GrenadeRow row = new() { Id = id.ToString(), Kind = GrenadeKind.Smoke };
        IndexedGrenade throwOf = new(new global::DemoViewer.NET.Extensions.StratBook.DemoRef("/d/x.dem", "x", null), Map, row, new WorldPoint(1, 2, 3), new WorldPoint(4, 5, 6), null, null);
        return new GrenadeLineup(new WorldPoint(1, 2, 3), false, [throwOf], id) { AliasIds = [id, .. aliases] };
    }
}

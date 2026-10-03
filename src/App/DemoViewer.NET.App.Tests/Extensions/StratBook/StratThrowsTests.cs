#region

using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using GrenadeKind = DemoViewer.NET.Playback2D.Core.GrenadeKind;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The preview throws a step's utility: a projectile from the thrower to the landing, then the kind's effect, in
///     the projection, so the canvas, the Detected preview and an export all draw it.
/// </summary>
public class StratThrowsTests
{
    private const string Map = "de_mirage";

    // Step 3 of FiveSteps is at 1:30 on a 115 s clock; its lineup flies 2 s and goes off 2.5 s after release.
    private const int Release = 1600;
    private const int Landed = Release + 128;
    private const int Pops = Release + 160;
    private static readonly int SmokeEnds = Pops + (int)(StratFrameSource.SmokeSeconds * StepSchedule.TicksPerSecond);

    [Test]
    public async Task ALineupSmoke_FliesFromTheOrigin_ToTheLanding_ThenSmokesThere_For18Seconds()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = TimedIndex();
        using LineupOriginSource origins = new(index, a => a());
        origins.Load(Map);
        (StratStore store, StratSession session) = Opened(ThrowByB(lineup.Id));
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), id => store.Load(id).Document, () => [],
            lineupOrigins: origins);

        StratExportCapture capture = canvas.CaptureForExport()!;
        UtilityCue cue = capture.Projection.Utility.Single();
        WorldPoint origin = GrenadeLineups.TechniqueFor(lineup, null)!.Origin;
        StratFrameSource source = new(StratExportJob.BuildSpec(capture, null, 0, capture.Projection.ContentEndTick, 64, 1.0));

        GrenadeTrail midFlight = source.FrameAt(Release + 64).Trails.Single();
        GrenadeTrailPoint head = midFlight.Points[^1];
        using (Assert.Multiple())
        {
            await Assert.That(cue.ThrowTick).IsEqualTo(Release);
            await Assert.That(cue.Flight![^1].Tick).IsEqualTo(Landed).Because("the median air time, 128 ticks at 64");
            await Assert.That(cue.Tick).IsEqualTo(Pops).Because("the smoke goes off at the detonation, after it stops");
            await Assert.That(cue.Flight[0].X).IsEqualTo(origin.X);
            await Assert.That(cue.X).IsEqualTo(-1400f).Because("the landing group's detonation point");
            await Assert.That(cue.Team).IsEqualTo(2);

            await Assert.That(source.FrameAt(Release - 1).Trails).IsEmpty();
            await Assert.That(midFlight.Team).IsEqualTo(2);
            await Assert.That(midFlight.Alpha).IsEqualTo(1.0);
            await Assert.That(head.X).IsBetween(-1400f, origin.X);
            await Assert.That(head.Y).IsBetween(-1400f, origin.Y);
            await Assert.That(Smokes(source.FrameAt(Pops - 1))).IsEqualTo(0);

            AreaEffect popped = source.FrameAt(Pops).AreaEffects.Single();
            await Assert.That(popped.Kind).IsEqualTo(AreaEffectKind.Smoke);
            await Assert.That(popped.WorldX).IsEqualTo(-1400f);
            await Assert.That(popped.WorldRadius).IsLessThan(144f).Because("it blooms");
            await Assert.That(source.FrameAt(Pops + 64).AreaEffects.Single().WorldRadius).IsEqualTo(144f);
            await Assert.That(Smokes(source.FrameAt(SmokeEnds - 1))).IsEqualTo(1);
            await Assert.That(Smokes(source.FrameAt(SmokeEnds))).IsEqualTo(0);
            await Assert.That(source.FrameAt(Landed + 2 * 64).Trails).IsEmpty().Because("the line fades over 2 s after it stops");
        }
    }

    [Test]
    public async Task TheContentEnd_CoversTheSmoke_AndTheExportRunsThroughIt()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = TimedIndex();
        using LineupOriginSource origins = new(index, a => a());
        origins.Load(Map);
        (StratStore store, StratSession session) = Opened(ThrowByB(lineup.Id));
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), id => store.Load(id).Document, () => [],
            lineupOrigins: origins);

        StratExportCapture capture = canvas.CaptureForExport()!;
        StratSceneProjection projection = capture.Projection;
        ExportRangeOption range = StratExportJob.Ranges(projection)[0];
        StratFrameSource source = new(StratExportJob.BuildSpec(capture, null, range.StartFrame, range.EndFrame, 30, 1.0));
        List<Scene2DFrame> frames = [];
        int smokes = 0, trails = 0;
        for (int i = 0; i < source.FrameCount; i++)
        {
            Scene2DFrame frame = source.FrameAt(i);
            smokes += Smokes(frame) > 0 ? 1 : 0;
            trails += frame.Trails.Count > 0 ? 1 : 0;
        }

        using (Assert.Multiple())
        {
            await Assert.That(projection.LastTick).IsLessThan(SmokeEnds).Because("the last step is before the smoke clears");
            await Assert.That(projection.ContentEndTick).IsEqualTo(SmokeEnds - 1);
            await Assert.That(range.EndFrame).IsGreaterThanOrEqualTo(SmokeEnds - 1);
            await Assert.That(smokes).IsGreaterThanOrEqualTo(18 * 30 - 1).Because("the whole smoke is in the file");
            await Assert.That(trails).IsGreaterThan(0);
            await Assert.That(canvas.Transport.EndTick).IsEqualTo(SmokeEnds - 1);
        }
    }

    [Test]
    public async Task ARecordedFlight_KeepsItsShape_BentOntoTheOriginAndTheLanding()
    {
        StratDocument document = ThrowByB(Guid.NewGuid());
        LineupFlight flight = new(new TokenPlacement(0, 0, 0, null), 1000, 0, 0, 2, 2,
            [(0, 10, 10, 0), (1, 500, 300, 0), (2, 990, 10, 0)]);

        UtilityCue cue = Build(document, flights: (_, _) => flight).Utility.Single();
        IReadOnlyList<FlightPoint> points = cue.Flight!;

        using (Assert.Multiple())
        {
            await Assert.That(points.Select(p => p.Tick)).IsEquivalentTo([Release, Release + 64, Release + 128]);
            await Assert.That(points[0]).IsEqualTo(new FlightPoint(Release, 0, 0, (float)(MapSpace.LevelQuantum / 2)));
            await Assert.That(points[1].X).IsEqualTo(500f).Because("half way, half of each end's correction");
            await Assert.That(points[1].Y).IsEqualTo(290f);
            await Assert.That(points[^1].X).IsEqualTo(1000f);
            await Assert.That(cue.Tick).IsEqualTo(Release + 128);
        }
    }

    [Test]
    public async Task AThrowWithoutALineup_FliesFromTheToken_ToItsLandingPoint()
    {
        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Utility = new UtilityRef { Kind = "flash", Landing = new UtilityLanding { X = 100, Y = 1700, LevelMinZ = 0 } };

        StratSceneProjection projection = Build(document);
        UtilityCue cue = projection.Utility.Single();
        TokenKeyframe b = projection.Tracks.Single(t => t.Slot == "B").Keyframes.Single(k => k.Tick == Release);

        using (Assert.Multiple())
        {
            await Assert.That(cue.Flight![0].X).IsEqualTo(b.X).Because("B stands where the step put it");
            await Assert.That(cue.Flight[0].Y).IsEqualTo(b.Y);
            await Assert.That(cue.Flight[^1].Tick - Release).IsEqualTo(64).Because("800 units at 800 per second");
            await Assert.That(cue.Tick).IsEqualTo(cue.Flight[^1].Tick);
            await Assert.That((cue.X, cue.Y)).IsEqualTo((100f, 1700f));
            await Assert.That(cue.Kind).IsEqualTo(GrenadeKind.Flash);
        }
    }

    [Test]
    public async Task AThrowWithoutALineup_LandsOnItsPlace_WhenItHasNoPoint()
    {
        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Utility = new UtilityRef { Kind = "molotov", Landing = new UtilityLanding { Place = "Jungle" } };

        UtilityCue cue = Build(document, (place, _) => place == "Jungle" ? (-1200d, -400d) : null).Utility.Single();

        using (Assert.Multiple())
        {
            await Assert.That((cue.X, cue.Y)).IsEqualTo((-1200f, -400f));
            await Assert.That(cue.Kind).IsEqualTo(GrenadeKind.Molotov);
            await Assert.That(cue.Flight).IsNotNull();
        }
    }

    [Test]
    public async Task AThrowWithNoLanding_DrawsNothing()
    {
        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        document.Steps[3].Verb = "throw";
        document.Steps[3].Utility = new UtilityRef { Kind = "he", Landing = new UtilityLanding { Place = "Nowhere" } };

        StratSceneProjection projection = Build(document, (_, _) => null, (_, _) => null);

        await Assert.That(projection.Utility).IsEmpty();
        await Assert.That(projection.UtilityEndTick).IsEqualTo(0);
    }

    [Test]
    public async Task EveryLineOfAThrow_ThrowsItsOwn_InItsSidesColour()
    {
        StratDocument document = FiveSteps();
        document.Side = "CT";
        document.Steps[2].Verb = "throw";
        document.Steps[2].Actor = "all";
        document.Steps[2].Assignments = [new StepAssignment { Slot = "A" }, new StepAssignment { Slot = "B" }];
        document.Steps[2].Utility = new UtilityRef { Kind = "decoy", Landing = new UtilityLanding { X = 0, Y = 0, LevelMinZ = 0 } };

        IReadOnlyList<UtilityCue> cues = Build(document).Utility;

        await Assert.That(cues.Select(c => c.Team)).IsEquivalentTo([3, 3]);
        await Assert.That(cues.Select(c => c.Flight![0].X).Distinct().Count()).IsEqualTo(2).Because("each from its own token");
    }

    [Test]
    public async Task ThePreview_ShowsTheFlight_AndTheSmoke()
    {
        (GrenadeIndex index, GrenadeLineup lineup) = TimedIndex();
        using LineupOriginSource origins = new(index, a => a());
        origins.Load(Map);
        using StratPreviewViewModel preview = new(StratMiningServiceTests.Pattern("k", 1), "Execute", _ => null, a => a(), origins);

        preview.Show(ThrowByB(lineup.Id));
        StratCanvasViewModel canvas = preview.Canvas!;
        canvas.Transport.Pause();
        canvas.Transport.Seek(Release + 64);
        Scene2DFrame flying = canvas.CurrentFrame;
        canvas.Transport.Seek(Pops + 128);
        Scene2DFrame smoked = canvas.CurrentFrame;

        using (Assert.Multiple())
        {
            await Assert.That(canvas.ShowTrails).IsTrue();
            await Assert.That(flying.Trails.Single().Points.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(Smokes(flying)).IsEqualTo(0);
            await Assert.That(Smokes(smoked)).IsEqualTo(1);
        }
    }

    private static StratSceneProjection Build(StratDocument document, PlaceCentreResolver? centres = null,
        ThrowFlightResolver? flights = null) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), null, centres, null, null, flights);

    private static StratDocument ThrowByB(Guid lineupId)
    {
        StratDocument document = FiveSteps();
        document.Steps[2].Verb = "throw";
        document.Steps[2].Actor = "B";
        document.Steps[2].Utility = new UtilityRef { Kind = "smoke", LineupId = lineupId };
        return document;
    }

    // Every throw spawns 10 ticks after release, stops 128 ticks later and goes off 160 ticks after spawn.
    private static (GrenadeIndex Index, GrenadeLineup Lineup) TimedIndex() =>
        StratThrowOriginTests.Indexed(row =>
        {
            row.SpawnTick = 1010;
            row.AirTimeTicks = 128;
            row.DetonationTick = 1170;
        });

    private static int Smokes(Scene2DFrame frame) => frame.AreaEffects.Count(e => e.Kind == AreaEffectKind.Smoke);
}

#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A strat projected for the canvas (step-authoring.md §3.3, §3.5, §3.11): steps onto the strat frame clock,
///     positions into token tracks, strokes into one annotation document windowed by step with the canvas fades,
///     and back: an unchanged projection writes nothing, and an edited stroke keeps the fields this build does
///     not know.
/// </summary>
public class StratSceneProjectionTests
{
    // The schema sample's four steps at 1:30, 1:16, 1:05 and 1:00 on a 115 s clock.
    private const int Smoke = 1600, Molly = 2496, Peek = 3200, All = 3520;

    private static readonly string[] _placedSlots = ["A", "B", "C", "O1"];
    private static readonly int[] _heldTicks = [1600, 1600];

    [Test]
    public async Task TheSample_ProjectsEachStroke_OverItsStepWindow_WithTheCanvasFades()
    {
        StratDocument sample = SchemaSample();
        StratSceneProjection projection = StratSceneProjection.Build(sample, StratPath.MainLine(sample));

        AnnotationElement arrow = projection.Elements[0];
        AnnotationElement label = projection.Elements[1];
        using (Assert.Multiple())
        {
            await Assert.That(projection.Ticks).IsEquivalentTo(new[] { Smoke, Molly, Peek, All });
            await Assert.That(projection.Elements.Count).IsEqualTo(2);
            await Assert.That(arrow.Kind).IsEqualTo(AnnotationKind.Arrow);
            await Assert.That(arrow.Time).IsEqualTo(new TimeEnvelope(Smoke, Molly - 1, 8, 16));
            await Assert.That(arrow.Space).IsEqualTo(new SpaceRef.World(-256));
            await Assert.That(arrow.Style.ColorArgb).IsEqualTo(0xFFFFC107u).Because("the sample's #FFC107 shorthand is read");
            await Assert.That(arrow.Style.WidthWorld).IsEqualTo(3f);
            await Assert.That(arrow.Points.Select(p => (p.X, p.Y))).IsEquivalentTo(new[] { (420f, -1750f), (-900f, -1600f) });
            await Assert.That(label.Kind).IsEqualTo(AnnotationKind.Text);
            await Assert.That(label.Text).IsEqualTo("swing wide");
            await Assert.That(label.Time).IsEqualTo(new TimeEnvelope(Peek, All - 1, 8, 16));
            await Assert.That(AnnotationText.WorldSize(label.Style.WidthWorld)).IsEqualTo(6 * label.Style.WidthWorld)
                .Because("text is 6 × WidthWorld world units, following the pen (decision 9)");
        }

        // A stroke stored without an id is drawn under the same one every time it is projected.
        StratSceneProjection again = StratSceneProjection.Build(sample, StratPath.MainLine(sample));
        await Assert.That(again.Elements.Select(e => e.Id)).IsEquivalentTo(projection.Elements.Select(e => e.Id));
        await Assert.That(projection.TryFindStroke(label.Id, out StrokeRef at)).IsTrue();
        await Assert.That(at).IsEqualTo(new StrokeRef(2, 0));
    }

    [Test]
    public async Task TheSample_ProjectsBackToItself()
    {
        StratDocument sample = SchemaSample();
        StratSceneProjection projection = StratSceneProjection.Build(sample, StratPath.MainLine(sample));

        IReadOnlyList<PatchOp> ops = StepAuthoringPatches.FromInk(sample, projection, projection.Elements, _ => 0);
        await Assert.That(ops).IsEmpty();
    }

    [Test]
    public async Task TheSample_TracksItsTokens_AndItsLandings()
    {
        StratDocument sample = SchemaSample();
        StratSceneProjection projection = StratSceneProjection.Build(sample, StratPath.MainLine(sample));

        TokenTrack a = projection.Tracks.Single(t => t.Slot == "A");
        TokenTrack c = projection.Tracks.Single(t => t.Slot == "C");
        using (Assert.Multiple())
        {
            await Assert.That(projection.Tracks.Select(t => t.Slot)).IsEquivalentTo(_placedSlots);
            await Assert.That(a.Keyframes[0]).IsEqualTo(new TokenKeyframe(Smoke, 400, -1800, -256, 180));
            await Assert.That(a.Keyframes[^1]).IsEqualTo(new TokenKeyframe(Peek, -600, -1400, -256, 180))
                .Because("an entry with no yaw keeps the previous keyframe's");
            await Assert.That(c.HoldTicks[0]).IsEqualTo(160).Because("holdSeconds 2.5 × 64");
            await Assert.That(c.Segments[0]).IsEqualTo(TokenInterpolation.Hold);
            await Assert.That(projection.Utility.Single()).IsEqualTo(new UtilityCue(Smoke, GrenadeKind.Smoke, -1024.5f, -1616f, -224f))
                .Because("the molotov names only a place, so only the smoke has a point to draw");
            await Assert.That(projection.Labels.Single(l => l.Slot == "O1")).IsEqualTo(new TokenLabel("O1", "1", 3));
            await Assert.That(projection.Clock.Kind).IsEqualTo("dv-strat-clock");
            await Assert.That(projection.Clock.FrameCount).IsEqualTo(All + 1);
            await Assert.That(projection.Clock.LastTick).IsEqualTo(All);
        }
    }

    [Test]
    public async Task AnEditedStroke_KeepsItsUnknownFields_AndWritesNoTime()
    {
        JsonObject stored = JsonNode.Parse("""
            { "id": "0b4b0a52-7d0a-4a55-9d3f-6f3e4c2b1a00", "kind": "Arrow", "colorArgb": 4294951175, "widthWorld": 4,
              "opacity": 1, "space": "world", "levelMinZ": 128, "points": [1, 2, 0.5, 3, 4, 0.5],
              "futureStroke": { "kept": true }, "fromTick": 99 }
            """)!.AsObject();

        AnnotationElement element = StratStrokes.ToElement(stored, Guid.Empty, new TimeEnvelope(10, 20, 0, 0))!;
        AnnotationElement moved = element with { Points = [new InkPoint(5, 6, 0.5f), new InkPoint(7, 8, 0.5f)] };
        JsonObject written = StratStrokes.ToJson(moved, stored);

        using (Assert.Multiple())
        {
            await Assert.That(element.Time).IsEqualTo(new TimeEnvelope(10, 20, 0, 0)).Because("a stroke's time is its step's");
            await Assert.That(written["futureStroke"]!["kept"]!.GetValue<bool>()).IsTrue();
            await Assert.That(written.ContainsKey("fromTick")).IsFalse();
            await Assert.That(written["space"]!.GetValue<string>()).IsEqualTo("world");
            await Assert.That(written["levelMinZ"]!.GetValue<double>()).IsEqualTo(128);
            await Assert.That(written["points"]!.AsArray().Count).IsEqualTo(6);
            await Assert.That(StratStrokes.ToElement(written, Guid.Empty, element.Time)).IsEqualTo(moved);
        }
    }

    [Test]
    public async Task AnEntityStroke_IsNotDrawn()
    {
        JsonObject entity = JsonNode.Parse("""{ "kind": "Freehand", "space": "entity", "steamId": 7, "points": [] }""")!.AsObject();
        await Assert.That(StratStrokes.ToElement(entity, Guid.NewGuid(), TimeEnvelope.Static)).IsNull();
    }

    [Test]
    public async Task AClockThatRunsBackwards_IsHeldInOrder_AndSaysSo()
    {
        StratDocument document = Minimal();
        document.Steps[1].AtSeconds = 100; // after step 1 at 90: the countdown runs backwards

        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document));
        await Assert.That(projection.ClockClamped).IsTrue();
        await Assert.That(projection.Ticks).IsEquivalentTo(_heldTicks);
    }
}

#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A strat timed from its trigger (docs/strat-format.md, "The clock"): times count up from 0 and show as +0:08, steps
///     run in increasing time, the switch between the clocks rewrites every time in one entry and moves no tick, and every
///     reader prints the strat's own clock.
/// </summary>
[NotInParallel]
public class StratTriggerClockTests
{
    private static readonly StratClockInfo Trigger = new() { Kind = StratClock.TriggerKind, RoundSeconds = 115 };

    private static StratDocument Triggered(params StratStep[] steps)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "execute", "after contact", Created);
        document.TargetSite = "A";
        document.Clock.Kind = StratClock.TriggerKind;
        document.Trigger = new StratTrigger { Text = "contact at B apps", Kind = "contact" };
        document.Steps = [.. steps];
        return document;
    }

    // A round-clock strat with a lurk rotate and a step after the plant.
    private static StratDocument OnTheRoundClock()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "execute", "exec", Created);
        document.TargetSite = "A";
        document.Trigger = new StratTrigger { Text = "on call at 1:15", Kind = "time", AtSeconds = 75 };
        StratStep lurk = Step(1, 110, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["Palace"], Rotate = new LurkRotate { AtSeconds = 40, To = new PlaceRef { Place = "BombsiteB" } } };
        StratStep move = Step(2, 57.8, "A", "move", to: "BombsiteA");
        move.HoldSeconds = 1.5;
        StratStep plant = Step(3, 45, "B", "plant", to: "BombsiteA");
        StratStep retake = Step(4, -12.5, StratVocabulary.ActorAll, "hold");
        document.Steps = [lurk, move, plant, retake];
        return document;
    }

    [Test]
    public async Task TriggerTimes_FormatAsPlusSeconds_AndParseEveryWayAPersonTypesThem()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratClock.Format(Trigger, 8)).IsEqualTo("+0:08");
            await Assert.That(StratClock.Format(Trigger, 0)).IsEqualTo("+0:00");
            await Assert.That(StratClock.Format(Trigger, 75.5)).IsEqualTo("+1:15.5");
            await Assert.That(StratClock.Format(Trigger, -2)).IsEqualTo("-0:02");
            await Assert.That(StratClock.Format(null, 75)).IsEqualTo("1:15").Because("no clock is the round clock");
            await Assert.That(StratClock.Format(null, -5)).IsEqualTo("+0:05").Because("after the plant on the round clock");

            foreach ((string text, double seconds) in new[] { ("+0:08", 8.0), ("0:08", 8), ("8", 8), ("+1:15.5", 75.5), ("-0:02", -2) })
            {
                await Assert.That(StratClock.TryParse(Trigger, text, out double parsed)).IsTrue().Because(text);
                await Assert.That(parsed).IsEqualTo(seconds).Because(text);
            }

            await Assert.That(StratClock.TryParse(Trigger, "++0:08", out _)).IsFalse();
            await Assert.That(StratClock.TryParse(Trigger, "0:61", out _)).IsFalse();
            await Assert.That(StratClock.TryParse(null, "+0:05", out double round)).IsTrue();
            await Assert.That(round).IsEqualTo(-5).Because("on the round clock + is after the timer stopped");
        }
    }

    [Test]
    public async Task TriggerTimes_CountUpFromTickZero_AndTheProjectionShowsThem()
    {
        StratDocument document = Triggered(Step(1, 0, "A", "move", to: "Mid"), Step(2, 10, "B", "hold"), Step(3, 20.5, "C", "wait"));
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document));
        using (Assert.Multiple())
        {
            await Assert.That(projection.Ticks).IsEquivalentTo([0, 640, 1312]);
            await Assert.That(projection.CountsUp).IsTrue();
            await Assert.That(projection.ClockTextAt(640)).IsEqualTo("+0:10");
            await Assert.That(projection.ClockClamped).IsFalse();
            await Assert.That(StratClock.StratTickOf(Trigger, 8)).IsEqualTo(512);
            await Assert.That(StratClock.StratTickOf(null, 107)).IsEqualTo(StepSchedule.TickFor(107, 115));
        }
    }

    [Test]
    public async Task TheValidator_OrdersATriggerStratUpwards_WarnsBeforeTheTrigger_AndKnowsTheKind()
    {
        StratDocument ordered = Triggered(Step(1, 0, "A", "hold"), Step(2, 8, "B", "hold"), Step(3, 8, "C", "hold"));
        StratDocument backwards = Triggered(Step(1, 10, "A", "hold"), Step(2, 5, "B", "hold"));
        StratDocument early = Triggered(Step(1, -3, "A", "hold"));
        StratStep lurk = Step(1, 10, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["Palace"], Rotate = new LurkRotate { AtSeconds = 5 } };
        StratDocument rotate = Triggered(lurk);
        using (Assert.Multiple())
        {
            await Assert.That(StratValidator.Validate(ordered).Where(i => i.Severity != StratIssueSeverity.Info)).IsEmpty();
            await Assert.That(StratValidator.Validate(backwards).Single(i => i.Severity == StratIssueSeverity.Refusal).Message)
                .IsEqualTo("+0:05 comes before +0:10; the clock counts up from the trigger");
            await Assert.That(StratValidator.Validate(early).Single(i => i.Field == "/steps/0/atSeconds").Severity).IsEqualTo(StratIssueSeverity.Warning);
            await Assert.That(StratValidator.Validate(rotate).Any(i => i.Field == "/steps/0/lurk/rotate/atSeconds")).IsTrue()
                .Because("a rotate at +0:05 comes before its step at +0:10");
        }
    }

    [Test]
    public async Task TheSwitch_RewritesEveryTimeInOneEntry_MovesNoTick_AndUndoesExactly()
    {
        StratDocument document = OnTheRoundClock();
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        StratEditorViewModel editor = new(session);
        editor.Project();
        string before = StratHistory.ToNode(session.Document!).ToJsonString();
        StratSceneProjection was = StratSceneProjection.Build(session.Document!, StratPath.MainLine(session.Document!), null,
            (place, _) => place switch { "Palace" => (400, 0), "BombsiteA" => (1000, 0), "BombsiteB" => (0, 900), _ => null });
        int depth = session.UndoDepth;

        editor.ClockChoice = StratEditorViewModel.TriggerClockChoice;
        editor.Project();

        StratDocument after = session.Document!;
        StratSceneProjection now = StratSceneProjection.Build(after, StratPath.MainLine(after), null,
            (place, _) => place switch { "Palace" => (400, 0), "BombsiteA" => (1000, 0), "BombsiteB" => (0, 900), _ => null });
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(depth + 1).Because("one entry");
            await Assert.That(after.Clock.Kind).IsEqualTo(StratClock.TriggerKind);
            await Assert.That(after.Steps.Select(s => s.AtSeconds)).IsEquivalentTo([5.0, 57.2, 70, 127.5]);
            await Assert.That(after.Steps[0].Lurk!.Rotate!.AtSeconds).IsEqualTo(75);
            await Assert.That(after.Steps[1].HoldSeconds).IsEqualTo(1.5).Because("a hold is a duration");
            await Assert.That(after.Trigger!.AtSeconds).IsEqualTo(75).Because("the trigger's time is a round time");
            await Assert.That(now.Ticks).IsEquivalentTo(was.Ticks);
            await Assert.That(now.ContentEndTick).IsEqualTo(was.ContentEndTick);
            foreach (TokenTrack track in was.Tracks)
            {
                for (int tick = 0; tick <= was.ContentEndTick; tick += 11)
                {
                    TokenKeyframe a = track.TrySample(tick, out TokenKeyframe x) ? x : default;
                    TokenKeyframe b = now.Tracks.Single(t => t.Slot == track.Slot).TrySample(tick, out TokenKeyframe y) ? y : default;
                    await Assert.That((b.X, b.Y)).IsEqualTo((a.X, a.Y)).Because($"{track.Slot} at {tick}");
                }
            }

            await Assert.That(StratValidator.Validate(after).Where(i => i.Severity == StratIssueSeverity.Refusal)).IsEmpty();
            await Assert.That(editor.Steps[1].TimeText).IsEqualTo("+0:57.2");
            await Assert.That(editor.Steps[3].TimeText).IsEqualTo("+2:07.5").Because("after the plant runs on past the round length");
        }

        session.Undo();
        await Assert.That(StratHistory.ToNode(session.Document!).ToJsonString()).IsEqualTo(before);
        session.Redo();
        await Assert.That(session.Document!.Clock.Kind).IsEqualTo(StratClock.TriggerKind);

        // And back: the round clock has its countdown and its after-plant time again.
        editor.Project();
        editor.ClockChoice = StratEditorViewModel.RoundClockChoice;
        await Assert.That(session.Document!.Steps.Select(s => s.AtSeconds)).IsEquivalentTo([110.0, 57.8, 45, -12.5]);
    }

    [Test]
    public async Task TheSwitch_ReadsAsOneHistoryLine()
    {
        StratDocument document = OnTheRoundClock();
        List<PatchOp> ops = StratClock.SwitchOps(document, StratClock.TriggerKind);
        await Assert.That(StratDiffPhrasing.Summary(StratHistory.ToNode(document), ops)).IsEqualTo("clock: round clock → from the trigger");
        await Assert.That(StratClock.SwitchOps(document, StratClock.RoundKind)).IsEmpty();
    }

    [Test]
    public async Task TheEditor_ReadsAndWritesTimesOnTheTriggerClock_AndAddsLaterSteps()
    {
        StratDocument document = Triggered(Step(1, 0, "A", "move", to: "BombsiteA"), Step(2, 8, "B", "hold"));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        StratEditorViewModel editor = new(session);
        editor.Project();
        using (Assert.Multiple())
        {
            await Assert.That(editor.Steps[1].TimeText).IsEqualTo("+0:08");
            await Assert.That(editor.ClockChoice).IsEqualTo(StratEditorViewModel.TriggerClockChoice);
            await Assert.That(editor.StepsHeader).Contains("from the trigger");
            await Assert.That(editor.ClockLine).IsEqualTo("from the trigger, counting up from +0:00");
        }

        editor.Steps[1].TimeText = "+0:12";
        await Assert.That(session.Document!.Steps[1].AtSeconds).IsEqualTo(12);

        editor.AddStepCommand.Execute(null);
        await Assert.That(session.Document!.Steps[^1].AtSeconds).IsEqualTo(12 + StratEditorViewModel.NewStepOffsetSeconds)
            .Because("a new step is later on a clock that counts up");
        StratSceneProjection projection = StratSceneProjection.Build(session.Document!, StratPath.MainLine(session.Document!));
        await Assert.That(StepAuthoringPatches.DuplicateStep(session.Document!, 1, Guid.NewGuid()).Value!["atSeconds"]!.GetValue<double>())
            .IsGreaterThan(12).IsLessThanOrEqualTo(17);
        await Assert.That(projection.ClockClamped).IsFalse();
    }

    [Test]
    public async Task ATemplate_OnATriggerStrat_IsLaidOutInSecondsAfterTheStart()
    {
        List<StratStep> steps = StratTemplates.BuildSteps(StratTemplates.Find("rush-a")!, Trigger);
        await Assert.That(steps.Select(s => s.AtSeconds)).IsEquivalentTo([5.0, 7, 9, 20, 25]);
    }

    [Test]
    public async Task Exports_PrintTheTriggerClock_TheTrigger_AndTheStart()
    {
        StratDocument document = Triggered(Step(1, 0, "A", "move", to: "BombsiteA"), Step(2, 8, "B", "throw"));
        document.Steps[1].Utility = new UtilityRef { Kind = "smoke" };
        document.Start = new StratStart
        {
            Kind = StratStart.CustomKind,
            Positions = [new StartPosition { Slot = "A", Place = "Palace" }, new StartPosition { Slot = "B", X = 1234.4, Y = -560.6 }]
        };
        string sheet = StratTextExporter.CallSheet(document);
        RoleSheet a = RoleSheet.Derive(document, "A");
        string html = RoleSheetHtmlWriter.Html(document, [a]);
        using (Assert.Multiple())
        {
            await Assert.That(sheet).Contains("Trigger: contact at B apps\nStart: A Palace, B (1234, -561)\n");
            await Assert.That(sheet).Contains("- **+0:00** A moves to A site").Or.Contains("- **+0:00** A moves");
            await Assert.That(sheet).Contains("- **+0:08** B throws smoke");
            await Assert.That(a.Header.StartText).IsEqualTo("Palace");
            await Assert.That(html).Contains("Start: Palace");
            await Assert.That(html).Contains("+0:00 · ");
        }

        StratDocument spawn = Triggered(Step(1, 0, "A", "move", to: "BombsiteA"));
        spawn.Start = new StratStart { Positions = [new StartPosition { Slot = "A", X = 1, Y = 2 }] };
        await Assert.That(StratTextExporter.CallSheet(spawn)).Contains("Start: spawn\n");
        await Assert.That(RoleSheet.Derive(spawn, "A").Header.StartText).IsEqualTo("spawn");
    }

    [Test]
    public async Task TheHistory_PhrasesStepsOnTheirOwnClock()
    {
        StratDocument document = Triggered(Step(1, 0, "A", "move", to: "BombsiteA"));
        JsonNode tree = StratHistory.ToNode(document);
        PatchOp moved = PatchOp.ReplaceOp("/steps/0/atSeconds", JsonValue.Create(0), JsonValue.Create(8));
        PatchOp added = PatchOp.AddOp("/steps/1", System.Text.Json.JsonSerializer.SerializeToNode(Step(2, 12, "B", "hold"), StratJsonContext.Default.StratStep));
        await Assert.That(StratDiffPhrasing.Summary(tree, [moved])).IsEqualTo("A's move moved from +0:00 to +0:08");
        await Assert.That(StratDiffPhrasing.Summary(tree, [added])).EndsWith("at +0:12");
    }
}

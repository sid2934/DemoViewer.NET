#region

using System.Collections.Concurrent;
using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Step templates: every template validates clean on every shipped map and side it applies to, round-trips
///     through the store, follows the spawn seed in New Strat's first revision, and applies to an open strat as
///     one undo entry only while it has no steps beyond the seed.
/// </summary>
public class StratTemplatesTests
{
    private static readonly string[] ShippedMaps =
        ["de_ancient", "de_anubis", "de_cache", "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_train", "de_vertigo"];

    private static readonly StratSpawns Fixed = new(
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(1000 + 100 * i, 0, 64))],
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(-1000 - 100 * i, 0, -128))]);

    private static IEnumerable<string> SidesOf(StratTemplate template) =>
        template.Side is { } side ? [side] : [StratVocabulary.SideT, StratVocabulary.SideCt];

    private static StratDocument Seeded(StratTemplate template, string map, string side)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, map, side, "default", template.Name, Created);
        Fixed.PlaceStart(document);
        StratTemplates.Apply(document, template);
        return document;
    }

    [Test]
    public async Task EveryTemplate_OnEverySideAndShippedMap_HasNoRefusalAndNoWarning()
    {
        List<string> problems = [];
        foreach (StratTemplate template in StratTemplates.Templates)
        {
            foreach (string side in SidesOf(template))
            {
                foreach (string map in ShippedMaps)
                {
                    StratDocument document = Seeded(template, map, side);
                    CalloutResolver places = CalloutResolver.For(map, null, null);
                    problems.AddRange(StratValidator.Validate(document, places)
                        .Where(i => i.Severity != StratIssueSeverity.Info)
                        .Select(i => $"{template.Id} {side} {map}: {i.Severity} {i.Field} {i.Message}"));
                }
            }
        }

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task EveryTemplateStep_UsesOnlyItsVerbsFields_AndCarriesNoPositionOrLineup()
    {
        List<string> problems = [];
        foreach (StratTemplate template in StratTemplates.Templates)
        {
            if (template.Roles.Count != StratVocabulary.Slots.Count)
            {
                problems.Add(template.Id + ": roles are not one per slot");
            }

            foreach (StratStep step in StratTemplates.BuildSteps(template))
            {
                string where = $"{template.Id} {step.AtSeconds} {step.Actor} {step.Verb}";
                if (step.To is not null && !StratStepFields.Uses(step.Verb, StratStepField.To))
                {
                    problems.Add(where + ": to on a verb that does not use it");
                }

                if (step.Utility is not null && !StratStepFields.Uses(step.Verb, StratStepField.Utility))
                {
                    problems.Add(where + ": utility on a verb that does not use it");
                }

                if (step.Verb == "throw" && (step.Utility is null || step.Actor == StratVocabulary.ActorAll))
                {
                    problems.Add(where + ": a throw names a kind and a thrower");
                }

                if (step.Positions.Count > 0 || step.Utility?.LineupId is not null || step.From is not null)
                {
                    problems.Add(where + ": positions, lineup or from set");
                }

                if (step.Assignments is { } lines
                    && (step.To is not null || step.Actor != StratStepLines.ActorFor(lines)
                                            || lines.Any(l => l.Watch is not null
                                                              || (l.To is not null && !StratStepFields.Uses(step.Verb, StratStepField.To)))))
                {
                    problems.Add(where + ": lines with a step-level to, a stale actor, a watch, or a place the verb does not use");
                }
            }
        }

        await Assert.That(problems).IsEmpty();
        await Assert.That(StratTemplates.Templates.Select(t => t.Id).Distinct().Count()).IsEqualTo(StratTemplates.Templates.Count);
    }

    [Test]
    public async Task TheSplitAndTheSetup_SendTheirGroupsWithLines()
    {
        StratTemplate split = StratTemplates.Find("split-b")!;
        StratTemplate setup = StratTemplates.Find("setup")!;
        List<StratStep> splitSteps = StratTemplates.BuildSteps(split);
        StratStep hold = StratTemplates.BuildSteps(setup)[0];

        using (Assert.Multiple())
        {
            await Assert.That(splitSteps.Where(StratStepLines.HasLines).Select(s => s.Verb)).IsEquivalentTo(["hold", "move"]);
            await Assert.That(splitSteps.Single(s => s.Verb == "move" && StratStepLines.HasLines(s)).Assignments!
                .Select(l => l.Slot + ">" + l.To?.Place)).IsEquivalentTo(["A>BombsiteB", "D>BombsiteB"]);
            await Assert.That(hold.Assignments!.Select(l => l.Slot + ">" + l.To?.Place))
                .IsEquivalentTo(["A>BombsiteA", "B>BombsiteA", "C>", "D>BombsiteB", "E>BombsiteB"]);
            await Assert.That(hold.Actor).IsEqualTo(StratVocabulary.ActorAll);
        }
    }

    [Test]
    public async Task ANewStrat_HasNoSteps_AndAnOlderFilesSeedAlone_CountsAsNone_UnlessItHasLines()
    {
        StratDocument fresh = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", StratVocabulary.SideT, "default", "n", Created);
        Fixed.PlaceStart(fresh);
        StratDocument legacy = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", StratVocabulary.SideT, "default", "l", Created);
        StratStep seed = Step(1, legacy.Clock.RoundSeconds, StratVocabulary.ActorAll, "hold");
        seed.Positions = [.. StratVocabulary.Slots.Select((s, i) => new StepPosition { Slot = s, X = 1000 + 100 * i })];
        legacy.Steps = [seed];
        using (Assert.Multiple())
        {
            await Assert.That(fresh.Steps).IsEmpty();
            await Assert.That(StratTemplates.HasNoSteps(fresh)).IsTrue();
            await Assert.That(StratTemplates.HasNoSteps(legacy)).IsTrue().Because("an older file's seed reads as its start");
        }

        seed.Assignments = [new StepAssignment { Slot = "A" }, new StepAssignment { Slot = "B" }];
        await Assert.That(StratTemplates.HasNoSteps(legacy)).IsFalse().Because("Apply template would write after lines the author set");
    }

    [Test]
    public async Task TheSideTimes_FollowTheModel()
    {
        StratTemplate execute = StratTemplates.Find("execute-a")!;
        StratTemplate setup = StratTemplates.Find("setup")!;
        StratTemplate retake = StratTemplates.Find("retake-b")!;
        using (Assert.Multiple())
        {
            await Assert.That(execute.Steps.Last(s => s.Verb == "throw").AtSeconds)
                .IsGreaterThan(execute.Steps.First(s => s.Verb == "move").AtSeconds).Because("utility goes in before the entry");
            await Assert.That(execute.Steps.Where(s => s.Verb is "throw" or "move").All(s => s.AtSeconds is >= 40 and <= 65)).IsTrue();
            await Assert.That(setup.Steps[0].AtSeconds).IsEqualTo(StratClock.DefaultRoundSeconds).Because("a CT setup holds from round start");
            await Assert.That(retake.Steps.All(s => s.AtSeconds < 0)).IsTrue().Because("a retake starts after the plant");
            await Assert.That(retake.Steps.Min(s => s.AtSeconds)).IsGreaterThan(-40).Because("the bomb is 40 s");
        }
    }

    [Test]
    public async Task ATemplatedStrat_RoundTripsThroughTheStore()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratTemplate template = StratTemplates.Find("retake-a")!;
            StratDocument created = store.Create(Team, "de_inferno", StratVocabulary.SideCt, template.Type, template.Name, d =>
            {
                Fixed.PlaceStart(d);
                StratTemplates.Apply(d, template);
            });

            StratStore reader = new(root);
            StratDocument loaded = reader.Load(created.Id).Document!;
            using (Assert.Multiple())
            {
                await Assert.That(created.Revision).IsEqualTo(1);
                await Assert.That(JsonSerializer.Serialize(loaded, StratJsonContext.Default.StratDocument))
                    .IsEqualTo(JsonSerializer.Serialize(created, StratJsonContext.Default.StratDocument));
                await Assert.That(loaded.TargetSite).IsEqualTo("A");
                await Assert.That(loaded.Steps.Count).IsEqualTo(template.Steps.Count).Because("the spawns are the start, not a step");
                await Assert.That(loaded.Start!.Positions.Count).IsEqualTo(10);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task NewStrat_WithATemplate_WritesTheSpawnStart_ThenTheTemplate_AllInRevisionOne()
    {
        StratStore store = new(null);
        StratSpawnSource spawns = new(_ => Fixed);
        await spawns.ForAsync("de_mirage");
        using StratBookTabViewModel vm = new(store, null, null, false, spawns: spawns);
        vm.SelectedMap = "de_mirage";
        vm.SelectedSide = StratVocabulary.SideCt;

        vm.NewStratCommand.Execute("execute-b");

        StratTemplate template = StratTemplates.Find("execute-b")!;
        StratDocument document = vm.Session.Document!;
        StratDocument stored = store.Load(document.Id).Document!;
        using (Assert.Multiple())
        {
            await Assert.That(document.Revision).IsEqualTo(1);
            await Assert.That(document.Side).IsEqualTo(StratVocabulary.SideT).Because("an execute is a T strat, whatever the filter");
            await Assert.That(document.Type).IsEqualTo("execute");
            await Assert.That(document.TargetSite).IsEqualTo("B");
            await Assert.That(document.Name).IsEqualTo("Execute B");
            await Assert.That(document.Start!.Kind).IsEqualTo(StratStart.SpawnKind);
            await Assert.That(document.Start.Positions.Count).IsEqualTo(10).Because("the spawns are the start");
            await Assert.That(document.Steps.Select(s => s.Verb)).IsEquivalentTo(template.Steps.Select(s => s.Verb));
            await Assert.That(document.Steps.All(s => s.Positions.Count == 0)).IsTrue();
            await Assert.That(stored.Steps.Count).IsEqualTo(template.Steps.Count).Because("the template is in the created revision");
            await Assert.That(stored.Start!.Positions.Count).IsEqualTo(10);
            await Assert.That(document.Slots[0].Role).IsEqualTo("entry");
            await Assert.That(vm.CanApplyTemplate).IsFalse();
        }
    }

    [Test]
    public async Task NewStrat_Blank_StaysAsBefore()
    {
        StratStore store = new(null);
        StratSpawnSource spawns = new(_ => Fixed);
        await spawns.ForAsync("de_mirage");
        using StratBookTabViewModel vm = new(store, null, null, false, spawns: spawns);
        vm.SelectedMap = "de_mirage";

        vm.NewStratCommand.Execute(null);

        StratDocument document = vm.Session.Document!;
        using (Assert.Multiple())
        {
            await Assert.That(document.Steps).IsEmpty();
            await Assert.That(document.Start!.Positions.Count).IsEqualTo(10);
            await Assert.That(document.Type).IsEqualTo("default");
            await Assert.That(document.Name).IsEqualTo("New strat");
            await Assert.That(vm.CanApplyTemplate).IsTrue();
            await Assert.That(vm.ApplicableTemplates.All(t => t.AppliesTo(StratVocabulary.SideT))).IsTrue();
        }
    }

    [Test]
    public async Task ApplyTemplate_IsOneUndoEntry_AndIsRefusedOnceTheStratHasSteps()
    {
        StratStore store = new(null);
        StratSpawnSource spawns = new(_ => Fixed);
        await spawns.ForAsync("de_nuke");
        using StratBookTabViewModel vm = new(store, null, null, false, spawns: spawns);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_nuke";
        vm.SelectedSide = StratVocabulary.SideCt;
        vm.NewStratCommand.Execute(null);

        StratDocument document = vm.Session.Document!;
        string before = JsonSerializer.Serialize(document, StratJsonContext.Default.StratDocument);
        int depth = vm.Session.UndoDepth;
        StratTemplate template = StratTemplates.Find("retake-b")!;

        await Assert.That(vm.ApplyTemplateCommand.CanExecute("retake-b")).IsTrue();
        await Assert.That(vm.ApplyTemplateCommand.CanExecute("execute-a")).IsFalse().Because("an execute is not for a CT strat");
        vm.ApplyTemplateCommand.Execute("retake-b");

        StratDocument applied = vm.Session.Document!;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(applied.Type).IsEqualTo("retake");
            await Assert.That(applied.TargetSite).IsEqualTo("B");
            await Assert.That(applied.Steps.Count).IsEqualTo(template.Steps.Count);
            await Assert.That(applied.Start!.Positions.Count).IsEqualTo(10);
            await Assert.That(vm.CanApplyTemplate).IsFalse();
            await Assert.That(vm.ApplyTemplateCommand.CanExecute("setup")).IsFalse();
        }

        int steps = applied.Steps.Count;
        vm.ApplyTemplateCommand.Execute("setup");
        await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(steps).Because("a strat with real steps is refused");
        await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);

        vm.Session.Undo();
        await Assert.That(JsonSerializer.Serialize(vm.Session.Document!, StratJsonContext.Default.StratDocument)).IsEqualTo(before)
            .Because("one undo takes the whole template back");
        await Assert.That(vm.CanApplyTemplate).IsTrue();
    }

    [Test]
    public async Task ACapturedOrMinedStrat_WithOnlyItsFreezeEndStep_TakesNoTemplate()
    {
        const int freeze = 6400;
        List<CapturedPawn> pawns =
        [
            .. Enumerable.Range(0, 10).Select(s => new CapturedPawn(s, s < 5 ? 2 : 3, (ulong)(100 + s), "p" + s, s * 1000, 0, 0, 90, "TSpawn"))
        ];
        RoundCapture capture = new(7, freeze, freeze + 64 * 100, 64, [new CaptureMoment(freeze, CaptureTrigger.FreezeEnd, pawns)]);
        StratCaptureOptions options = new(2,
            StratFromRound.Tokens(pawns, 2, StratFromRound.SlotMap(pawns.Where(p => p.Team == 2), null, null)),
            StratClock.DefaultRoundSeconds, true, StratFromRound.QuantizedLevel);
        StratDocument captured = StratFromRound.Document(capture, options, Team, "de_mirage", "r7",
            new StratOrigin { DemoSha256 = "ab", Round = 7 }, null, Created);

        StratDocument mined = captured.Clone();
        mined.Origin = null;
        mined.Tags = [MinedStratBuilder.Tag];

        StratDocument bare = captured.Clone();
        bare.Origin = null;

        using (Assert.Multiple())
        {
            await Assert.That(captured.Steps.Count).IsEqualTo(1).Because("the precondition: one freeze-end step");
            await Assert.That(captured.Steps[0].Positions.Count).IsGreaterThan(0);
            await Assert.That(StratTemplates.HasNoSteps(bare)).IsTrue().Because("the step alone has the seed's shape");
            await Assert.That(StratTemplates.HasNoSteps(captured)).IsFalse();
            await Assert.That(StratTemplates.HasNoSteps(mined)).IsFalse();
            await Assert.That(StratTemplates.Ops(captured, StratTemplates.Find("default")!)).IsEmpty();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewStrat_WithAnotherSidesTemplate_OnAColdMap_OpensOnlyIfNothingChanged(bool openAnother)
    {
        StratStore store = new(null);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StratSpawnSource spawns = new(_ =>
        {
            gate.Task.Wait();
            return Fixed;
        });
        ConcurrentQueue<Action> posted = new();
        using StratBookTabViewModel vm = new(store, null, posted.Enqueue, false, spawns: spawns);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        Drain(posted);
        vm.SelectedMap = "de_mirage";
        vm.SelectedSide = StratVocabulary.SideCt;

        vm.NewStratCommand.Execute("execute-a");
        await Assert.That(vm.NewStratCommand.CanExecute("setup")).IsFalse().Because("one create at a time");

        Guid? other = null;
        if (openAnother)
        {
            StratDocument ct = store.Create(vm.SelectedOwner!.Owner, "de_mirage", StratVocabulary.SideCt, "setup", "other");
            Drain(posted);
            vm.SelectedStrat = vm.Strats.Single(r => r.Id == ct.Id);
            Drain(posted);
            other = ct.Id;
        }

        gate.SetResult();
        await spawns.ForAsync("de_mirage");
        for (int i = 0; i < 500 && posted.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        Drain(posted);

        StratIndexEntry made = store.Index.Single(e => e.Type == "execute");
        using (Assert.Multiple())
        {
            await Assert.That(made.Side).IsEqualTo(StratVocabulary.SideT);
            await Assert.That(made.StepCount).IsEqualTo(StratTemplates.Find("execute-a")!.Steps.Count);
            await Assert.That(vm.Session.Document?.Id).IsEqualTo(openAnother ? other : made.Id);
            await Assert.That(vm.NewStratCommand.CanExecute(null)).IsTrue();
        }
    }

    private static void Drain(ConcurrentQueue<Action> posted)
    {
        while (posted.TryDequeue(out Action? action))
        {
            action();
        }
    }

    [Test]
    public async Task Ops_AreEmpty_ForAnotherSidesTemplate_OrAStratWithSteps()
    {
        StratDocument ct = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", StratVocabulary.SideCt, "setup", "s", Created);
        Fixed.PlaceStart(ct);
        StratDocument t = Seeded(StratTemplates.Find("default")!, "de_dust2", StratVocabulary.SideT);
        using (Assert.Multiple())
        {
            await Assert.That(StratTemplates.Ops(ct, StratTemplates.Find("execute-a")!)).IsEmpty();
            await Assert.That(StratTemplates.Ops(ct, StratTemplates.Find("anti-eco")!)).IsNotEmpty();
            await Assert.That(StratTemplates.HasNoSteps(ct)).IsTrue();
            await Assert.That(StratTemplates.HasNoSteps(t)).IsFalse();
            await Assert.That(StratTemplates.Ops(t, StratTemplates.Find("anti-eco")!)).IsEmpty();
        }
    }
}

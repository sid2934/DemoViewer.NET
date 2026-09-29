#region

using System.Text.Json;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
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
        Fixed.Seed(document);
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
            }
        }

        await Assert.That(problems).IsEmpty();
        await Assert.That(StratTemplates.Templates.Select(t => t.Id).Distinct().Count()).IsEqualTo(StratTemplates.Templates.Count);
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
                Fixed.Seed(d);
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
                await Assert.That(loaded.Steps.Count).IsEqualTo(template.Steps.Count + 1);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task NewStrat_WithATemplate_PutsTheSeedFirst_ThenTheTemplate_AllInRevisionOne()
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
            await Assert.That(document.Steps[0].Positions.Count).IsEqualTo(10).Because("the spawn seed stays first");
            await Assert.That(document.Steps[0].Verb).IsEqualTo("hold");
            await Assert.That(document.Steps.Skip(1).Select(s => s.Verb)).IsEquivalentTo(template.Steps.Select(s => s.Verb));
            await Assert.That(document.Steps.Skip(1).All(s => s.Positions.Count == 0)).IsTrue();
            await Assert.That(stored.Steps.Count).IsEqualTo(template.Steps.Count + 1).Because("the template is in the created revision");
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
            await Assert.That(document.Steps.Count).IsEqualTo(1);
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
            await Assert.That(applied.Steps.Count).IsEqualTo(template.Steps.Count + 1);
            await Assert.That(applied.Steps[0].Positions.Count).IsEqualTo(10);
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
    public async Task Ops_AreEmpty_ForAnotherSidesTemplate_OrAStratWithSteps()
    {
        StratDocument ct = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", StratVocabulary.SideCt, "setup", "s", Created);
        Fixed.Seed(ct);
        StratDocument t = Seeded(StratTemplates.Find("default")!, "de_dust2", StratVocabulary.SideT);
        using (Assert.Multiple())
        {
            await Assert.That(StratTemplates.Ops(ct, StratTemplates.Find("execute-a")!)).IsEmpty();
            await Assert.That(StratTemplates.Ops(ct, StratTemplates.Find("anti-eco")!)).IsNotEmpty();
            await Assert.That(StratTemplates.HasOnlySeed(ct)).IsTrue();
            await Assert.That(StratTemplates.HasOnlySeed(t)).IsFalse();
            await Assert.That(StratTemplates.Ops(t, StratTemplates.Find("anti-eco")!)).IsEmpty();
        }
    }
}

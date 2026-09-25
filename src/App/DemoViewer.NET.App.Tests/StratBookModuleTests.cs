#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book module (strat-model.md §3.11): its persisted ids and feature row, and the tab VM over an
///     in-memory store: the book selector over Team Identity's teams plus me, a new strat opened in the editor,
///     field edits as one op each, a step removed with the branches that named it, the step clock parsed from what
///     is typed, and the commit on tab deactivate and at shutdown.
/// </summary>
[NotInParallel]
public class StratBookModuleTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static StratBookTabViewModel Tab(StratStore store, TeamIdentityService? teams = null, bool browser = false)
    {
        StratBookTabViewModel vm = new(store, teams, null, browser);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        return vm;
    }

    private static StratBookTabViewModel OpenNew(StratStore store, string map = "de_mirage")
    {
        StratBookTabViewModel vm = Tab(store);
        vm.SelectedMap = map;
        vm.NewStratCommand.Execute(null);
        return vm;
    }

    [Test]
    public async Task TheModule_ContributesOneMainTab_UnderThePersistedIds()
    {
        StratBookModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.stratbook");
            await Assert.That(tab.TabId).IsEqualTo("stratbook.browser");
            await Assert.That(tab.Header).IsEqualTo("Strat Book");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tab.Order).IsEqualTo(8).Because("after the Matrix");
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
        }

        // Shutdown before the tab was ever opened builds nothing.
        module.Shutdown();
    }

    [Test]
    public async Task TheTabFeature_IsATab_VisibleToEveryCategory_OnBothHosts()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.stratbook");

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsNull();
            await Assert.That(feature.GroupId).IsNull();
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the tab");
            }

            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain(StratBookModule.TabFeatureId)
                .Because("strats work in the browser for the session");
        }
    }

    [Test]
    public async Task TheBooks_AreMePlusEveryTeam_WithUsTheDefault()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        string[] five = ["76560001", "76560002", "76560003", "76560004", "76560005"];
        string[] other = ["76560011", "76560012", "76560013", "76560014", "76560015"];
        using (cache.BeginBatch())
        {
            for (int day = 1; day <= 2; day++)
            {
                DemoCacheRecord record = new()
                {
                    Path = $"/d/{day}.dem",
                    Size = 1000,
                    ModifiedTicks = new DateTime(2026, 3, day, 0, 0, 0, DateTimeKind.Utc).Ticks,
                    Map = "de_mirage"
                };
                int slot = 0;
                foreach (string id in five)
                {
                    record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "p" + id[^2..], SteamId64 = id, Team = 2 });
                }

                foreach (string id in other)
                {
                    record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "p" + id[^2..], SteamId64 = id, Team = 3 });
                }

                DemoCacheStore.StampParse(record);
                cache.Upsert(record);
            }
        }

        await teams.Idle;
        Team ours = teams.Teams[0];
        teams.SetUs(ours.Id);

        using StratBookTabViewModel vm = Tab(new StratStore(null), teams);

        using (Assert.Multiple())
        {
            await Assert.That(vm.Owners.Count).IsEqualTo(teams.Teams.Count + 1);
            await Assert.That(vm.Owners[0].Owner).IsEqualTo(StratOwner.Me());
            await Assert.That(vm.Owners[1].Label).EndsWith("(us)").Because("us comes first among the teams");
            await Assert.That(vm.SelectedOwner!.Owner).IsEqualTo(StratOwner.Team(ours.Id)).Because("the default book is us");
        }

        // The editor's pins are the team's latest roster.
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        Roster latest = ours.Rosters.OrderBy(r => r.Since).Last();
        string member = teams.MembersOf(ours.Id, latest.Id).Keys.First();
        await Assert.That(vm.Editor.PinOptions.Any(p => p.SteamId == member)).IsTrue();
        await Assert.That(vm.Editor.PinOptions[0]).IsEqualTo(StratPinOption.BookDefault);
    }

    [Test]
    public async Task WithoutTeams_TheOnlyBookIsMe()
    {
        using StratBookTabViewModel vm = Tab(new StratStore(null));

        using (Assert.Multiple())
        {
            await Assert.That(vm.Owners.Count).IsEqualTo(1);
            await Assert.That(vm.SelectedOwner!.Label).IsEqualTo("me");
        }
    }

    [Test]
    public async Task NewStrat_NeedsAMap_ThenOpensInTheEditor()
    {
        StratStore store = new(null);
        using StratBookTabViewModel vm = Tab(store);
        vm.NewStratCommand.Execute(null);
        await Assert.That(vm.ListLine).IsEqualTo("choose a map for the new strat");

        vm.SelectedMap = "de_nuke";
        vm.SelectedSide = "CT";
        vm.NewStratCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.Strats.Count).IsEqualTo(1);
            await Assert.That(vm.HasOpenStrat).IsTrue();
            await Assert.That(vm.Editor.Map).IsEqualTo("de_nuke");
            await Assert.That(vm.Editor.Side).IsEqualTo("CT");
            await Assert.That(vm.Editor.Type).IsEqualTo("setup");
            await Assert.That(vm.Editor.Slots.Select(s => s.Letter)).IsEquivalentTo(StratVocabulary.Slots);
            await Assert.That(store.SessionFor(vm.Strats[0].Id)).IsSameReferenceAs(vm.Session);
        }
    }

    [Test]
    public async Task AFieldEdit_IsOneOp_AndUndoProjectsBack()
    {
        using StratBookTabViewModel vm = OpenNew(new StratStore(null));

        vm.Editor.Name = "B split";
        vm.Editor.TargetSite = "B";
        vm.Editor.Slots[3].Role = "awp";

        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(3);
            await Assert.That(vm.Session.Document!.Name).IsEqualTo("B split");
            await Assert.That(vm.Session.Document.TargetSite).IsEqualTo("B");
            await Assert.That(vm.Session.Document.Slots[3].Role).IsEqualTo("awp");
            await Assert.That(vm.CanUndo).IsTrue();
        }

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Editor.TargetSite).IsEqualTo(StratEditorViewModel.None);
            await Assert.That(vm.Editor.Slots[3].Role).IsEqualTo("");
            await Assert.That(vm.Editor.Name).IsEqualTo("B split");
        }
    }

    [Test]
    public async Task TheStepTable_ParsesTheRoundClock_AndResolvesCallouts()
    {
        StratStore store = new(null);
        using StratBookTabViewModel vm = OpenNew(store);
        StratOwner owner = vm.Session.Document!.Owner;
        store.SaveCallouts(owner, new CalloutTable
        {
            Map = "de_mirage",
            Aliases = [new CalloutAlias { Alias = "A ramp", Place = "TRamp", Primary = true }]
        });

        // Re-open so the editor reads the owner's words.
        Guid id = vm.Session.Document.Id;
        vm.SelectedStrat = null;
        vm.SelectedStrat = vm.Strats.Single(r => r.Id == id);

        vm.Editor.AddStepCommand.Execute(null);
        StratStepRow row = vm.Editor.Steps.Single();
        row.TimeText = "1:30";
        row.FromText = "a ramp";
        row.ToText = "Somewhere New";
        row.UtilityKind = "smoke";
        row.LandingText = "BombsiteA";

        StratStep step = vm.Session.Document!.Steps.Single();
        using (Assert.Multiple())
        {
            await Assert.That(step.AtSeconds).IsEqualTo(90);
            await Assert.That(step.From!.Place).IsEqualTo("TRamp").Because("steps store places, never callouts");
            await Assert.That(step.To!.Place).IsEqualTo("Somewhere New").Because("an unknown place is kept and warns");
            await Assert.That(step.Utility!.Kind).IsEqualTo("smoke");
            await Assert.That(step.Utility.Landing!.Place).IsEqualTo("BombsiteA");
            await Assert.That(row.FromText).IsEqualTo("A ramp").Because("the table shows the owner's word");
            await Assert.That(row.TimeText).IsEqualTo("1:30");
        }

        row.TimeText = "soon";
        using (Assert.Multiple())
        {
            await Assert.That(step.AtSeconds).IsEqualTo(90);
            await Assert.That(row.TimeText).IsEqualTo("1:30").Because("text that is not a time falls back to the stored one");
        }
    }

    [Test]
    public async Task RemovingAStep_RemovesTheBranchesThatNameIt_AsOneUndoEntry()
    {
        using StratBookTabViewModel vm = OpenNew(new StratStore(null));
        vm.Editor.AddStepCommand.Execute(null);
        vm.Editor.AddStepCommand.Execute(null);
        vm.Editor.AddBranchCommand.Execute(null);
        StratBranchRow branch = vm.Editor.Branches.Single();
        branch.AfterStep = vm.Editor.StepOptions[0];
        branch.TargetStep = branch.TargetStepOptions.Last();
        branch.ConditionText = "contact at Connector";

        StratBranch stored = vm.Session.Document!.Branches.Single();
        using (Assert.Multiple())
        {
            await Assert.That(stored.AfterStepId).IsEqualTo(vm.Session.Document.Steps[0].Id);
            await Assert.That(stored.Target.StratId).IsEqualTo(vm.Session.Document.Id);
            await Assert.That(stored.Target.StepId).IsEqualTo(vm.Session.Document.Steps[1].Id);
            await Assert.That(stored.Condition.Text).IsEqualTo("contact at Connector");
        }

        int depth = vm.Session.UndoDepth;
        vm.Editor.RemoveStepCommand.Execute(vm.Editor.Steps[1]);

        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(1);
            await Assert.That(vm.Session.Document.Branches).IsEmpty();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
        }

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(2);
            await Assert.That(vm.Session.Document.Branches.Single().Target.StepId).IsEqualTo(vm.Session.Document.Steps[1].Id);
            await Assert.That(vm.Editor.Branches.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task LeavingTheTab_Commits_AndShutdownWritesTheIndex()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratBookTabViewModel vm = OpenNew(store);
            Guid id = vm.Session.Document!.Id;
            vm.Editor.Name = "committed on deactivate";
            vm.OnDeactivated();

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoad(id)!.Revision).IsEqualTo(2);
                await Assert.That(vm.Session.HasPending).IsFalse();
                await Assert.That(File.Exists(Path.Combine(root, "index.json"))).IsFalse();
            }

            vm.Editor.Name = "committed at shutdown";
            StratBookModule module = new(() => vm);
            _ = module.CreateTabs(null!).Single().ViewModelFactory!();
            module.Shutdown();

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoad(id)!.Name).IsEqualTo("committed at shutdown");
                await Assert.That(store.TryLoad(id)!.Revision).IsEqualTo(3);
                await Assert.That(File.Exists(Path.Combine(root, "index.json"))).IsTrue();
                await Assert.That(store.SessionFor(id)).IsNull();
            }

            vm.Dispose();
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task DeletingTheOpenStrat_ClosesIt_AndEmptiesTheList()
    {
        StratStore store = new(null);
        using StratBookTabViewModel vm = OpenNew(store);
        vm.DeleteStratCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasOpenStrat).IsFalse();
            await Assert.That(vm.Strats).IsEmpty();
            await Assert.That(store.Index).IsEmpty();
        }
    }

    [Test]
    public async Task TheBrowserHost_SaysStratsAreSessionOnly()
    {
        StratStore store = new(null);
        using StratBookTabViewModel vm = Tab(store, browser: true);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsBrowser).IsTrue();
            await Assert.That(vm.StatusLine).IsEqualTo(StratBookTabViewModel.BrowserNote);
        }
    }
}

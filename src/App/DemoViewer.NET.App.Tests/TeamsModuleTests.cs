#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Teams;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Teams module's persisted ids, its feature-catalog row, the tab VM over a synthetic library
///     (the list, the actions, the me suggestion, the footer, the browser note) and the Library's Team
///     filter.
/// </summary>
[NotInParallel]
public class TeamsModuleTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map = "de_nuke")
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    // Two rosters that met twice, and a demo nobody recurs on.
    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams)> Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record("/d/a.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
            cache.Upsert(Record("/d/b.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_mirage"));
            cache.Upsert(Record("/d/c.dem", 3, Ids(21, 22, 23, 24, 25), Ids(31, 32, 33, 34, 35)));
        }

        await teams.Idle;
        return (cache, teams);
    }

    [Test]
    public async Task TheModule_ContributesOneMainTab_UnderThePersistedIds()
    {
        TeamsModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.teams");
            await Assert.That(tab.TabId).IsEqualTo("teams.browser");
            await Assert.That(tab.Header).IsEqualTo("Teams");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
        }
    }

    [Test]
    public async Task TheFeatureId_IsATab_VisibleToEveryCategory()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.teams");

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsNull();
            await Assert.That(feature.GroupId).IsNull().Because("it must not disturb the leader-lock ordering");
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the tab");
            }

            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain("tab.teams")
                .Because("the tab renders on the browser and says what it cannot do");
        }
    }

    [Test]
    public async Task TheTab_ListsTeams_ProjectsTheSelection_AndRunsTheActions()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using TeamsTabViewModel vm = new(teams, cache, isBrowser: false);

        using (Assert.Multiple())
        {
            await Assert.That(vm.Teams.Count).IsEqualTo(2);
            await Assert.That(vm.HasTeams).IsTrue();
            await Assert.That(vm.Teams.All(t => t.DemoCount == 2)).IsTrue();
            await Assert.That(vm.FooterLine).Contains("no recurring opponent").Because("nothing is us yet, so no demo has an opponent");
            await Assert.That(vm.HasMeSuggestion).IsFalse().Because("three demos are below the twenty the suggestion needs");
            await Assert.That(vm.IsBrowser).IsFalse();
        }

        vm.SelectedTeam = vm.Teams[0];
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasSelection).IsTrue();
            await Assert.That(vm.Rosters.Count).IsEqualTo(1);
            await Assert.That(vm.Rosters[0].FiveLine).StartsWith("fixed five: ");
            await Assert.That(vm.Rosters[0].Members.Count).IsEqualTo(5);
            await Assert.That(vm.Rosters[0].Members[0].Appearances).IsEqualTo(2);
            await Assert.That(vm.Demos.Count).IsEqualTo(2);
            await Assert.That(vm.Demos[0].Map).IsEqualTo("de_mirage").Because("newest first");
            await Assert.That(vm.Demos[0].Opponent).IsNotEqualTo("(unaffiliated)").Because("the other five also recur");
            await Assert.That(vm.MergeTargets.Count).IsEqualTo(1);
        }

        vm.RenameText = "Our Five";
        vm.RenameCommand.Execute(null);
        vm.SetAsUsCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedTeam!.Name).IsEqualTo("Our Five").Because("the selection survives the re-projection");
            await Assert.That(vm.SelectedTeam.IsUs).IsTrue();
            await Assert.That(teams.OurDemos().Count).IsEqualTo(2);
            await Assert.That(vm.FooterLine).DoesNotContain("no recurring opponent");
        }

        vm.MyAccountsText = $"{Ids(21)[0]}, {Ids(99)[0]}";
        vm.SaveMyAccountsCommand.Execute(null);
        await Assert.That(teams.MyAccounts).IsEquivalentTo(Ids(21, 99));
        await Assert.That(teams.OurDemos().Count).IsEqualTo(3).Because("the me account resolves the third demo");

        vm.MergeTarget = vm.MergeTargets[0];
        vm.MergeCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Teams.Count).IsEqualTo(1);
            await Assert.That(vm.SelectedTeam!.RosterCount).IsEqualTo(2);
            await Assert.That(vm.SelectedTeam.DemoCount).IsEqualTo(2).Because("both sides of a and b are one team now");
        }

        vm.HideCommand.Execute(null);
        await Assert.That(vm.Teams).IsEmpty().Because("hidden teams leave the list");
        vm.ShowHidden = true;
        await Assert.That(vm.Teams.Single().Hidden).IsTrue();
    }

    [Test]
    public async Task OnTheBrowser_TheTabSaysItForgets()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using TeamsTabViewModel vm = new(teams, cache, isBrowser: true);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsBrowser).IsTrue();
            await Assert.That(TeamsTabViewModel.BrowserNote).IsEqualTo("session only: this browser tab forgets teams when it reloads");
            await Assert.That(teams.IsSessionOnly).IsTrue();
        }
    }

    [Test]
    public async Task TheLibraryTeamFilter_ListsUsAndEveryTeam_AndKeepsTheirDemos()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using DemoLibraryService library = new(a => a(), Path.Combine(Path.GetTempPath(), "dvlibteam_" + Guid.NewGuid().ToString("N") + ".json"));
        foreach (string path in (string[]) ["/d/a.dem", "/d/b.dem", "/d/c.dem"])
        {
            library.Entries.Add(new DemoEntry
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Directory = "/d",
                FileSizeBytes = 1000,
                Modified = new DateTime(2026, 3, 1),
                MapName = cache.TryGetIndex(path)!.Map,
                State = DemoIndexState.Indexed
            });
        }

        LibraryTabViewModel vm = new(library, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]), teams: teams);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasTeamFilter).IsTrue();
            await Assert.That(vm.AvailableTeams.Count).IsEqualTo(4).Because("All teams, Us, and two teams");
            await Assert.That(vm.AvailableTeams[0]).IsEqualTo(TeamFilterItem.All);
            await Assert.That(vm.AvailableTeams[1]).IsEqualTo(TeamFilterItem.Us);
            await Assert.That(vm.FilteredEntries.Count).IsEqualTo(3);
            await Assert.That(vm.HasActiveFilters).IsFalse();
        }

        vm.SelectedTeam = vm.AvailableTeams[2];
        using (Assert.Multiple())
        {
            await Assert.That(vm.FilteredEntries.Select(e => e.FilePath)).IsEquivalentTo(["/d/a.dem", "/d/b.dem"]);
            await Assert.That(vm.HasActiveFilters).IsTrue();
        }

        vm.SelectedTeam = TeamFilterItem.Us;
        await Assert.That(vm.FilteredEntries).IsEmpty().Because("nothing is us yet");

        teams.SetUs(vm.AvailableTeams[2].TeamId);
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2).Because("the filter re-applies on the service's Changed");

        teams.Rename(vm.AvailableTeams[2].TeamId!.Value, "Renamed");
        await Assert.That(vm.AvailableTeams.Select(t => t.Display)).Contains("Renamed");

        vm.ClearFiltersCommand.Execute(null);
        await Assert.That(vm.SelectedTeam).IsEqualTo(TeamFilterItem.All);
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(3);
    }
}

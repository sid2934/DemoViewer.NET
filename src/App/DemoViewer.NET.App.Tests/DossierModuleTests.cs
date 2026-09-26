#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Dossier;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Opponent Dossier module's persisted ids, its feature-catalog row (on by default, every
///     category), the Map Pool Record service's per-map aggregation and decider inference, the tab VM's
///     projection and sample-size lines, and the veto history store beside it.
/// </summary>
[NotInParallel]
public class DossierModuleTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(
        string path, int day, string[] t, string[] ct, string map,
        int ctScore, int tScore, int ctSideWins, int tSideWins)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            CtScore = ctScore,
            TScore = tScore,
            CtSideWins = ctSideWins,
            TSideWins = tSideWins
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
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    // Team A (1..5) against team B (11..15): a same-day best-of-three (A wins the decider on the third
    // demo), a lone fourth demo against B on another day (excluded from the decider group by size), and
    // a repeat of de_nuke on a fifth day so the map row aggregates across more than one demo.
    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams, Guid TeamA, Guid TeamB)> Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            // Day 10, game 1: A (T) beats B (CT) on de_nuke.
            cache.Upsert(Record("/d/s1.dem", 10, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_nuke",
                ctScore: 9, tScore: 16, ctSideWins: 9, tSideWins: 16));
            // Day 10, game 2: A (CT) loses to B (T) on de_mirage.
            cache.Upsert(Record("/d/s2.dem", 10, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_mirage",
                ctScore: 9, tScore: 13, ctSideWins: 9, tSideWins: 13));
            // Day 10, game 3 (the decider): A (T) beats B (CT) on de_inferno.
            cache.Upsert(Record("/d/s3.dem", 10, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_inferno",
                ctScore: 6, tScore: 13, ctSideWins: 6, tSideWins: 13));
            // Day 20, alone: A (T) beats B (CT) on de_dust2. A group of one is not a decider.
            cache.Upsert(Record("/d/s4.dem", 20, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_dust2",
                ctScore: 5, tScore: 13, ctSideWins: 5, tSideWins: 13));
            // Day 30, alone: A (CT) loses to B (T), de_nuke again.
            cache.Upsert(Record("/d/s5.dem", 30, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_nuke",
                ctScore: 8, tScore: 13, ctSideWins: 8, tSideWins: 13));
        }

        await teams.Idle;
        Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup != null && r.CoreLineup.Contains(Ids(1)[0]))).Id;
        Guid teamB = teams.Teams.First(t => t.Id != teamA).Id;
        return (cache, teams, teamA, teamB);
    }

    [Test]
    public async Task TheModule_ContributesOneMainTab_UnderThePersistedIds()
    {
        DossierModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.dossier");
            await Assert.That(tab.TabId).IsEqualTo("dossier.browser");
            await Assert.That(tab.Header).IsEqualTo("Dossier");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
        }
    }

    [Test]
    public async Task TheFeatureId_IsATab_OnByDefault_ForEveryCategory()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.dossier");

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsNull();
            await Assert.That(feature.GroupId).IsNull().Because("it must not disturb the leader-lock ordering");
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the tab by default");
            }

            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain("tab.dossier")
                .Because("the tab renders on the browser and says teams are session only");
        }
    }

    [Test]
    public async Task MapPoolRecordService_AggregatesPerMap_AndInfersTheDecider()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();

        MapPoolRecord record = MapPoolRecordService.Build(teams, cache, teamA);

        using (Assert.Multiple())
        {
            await Assert.That(record.TotalDemos).IsEqualTo(5);
            await Assert.That(record.Maps.Count).IsEqualTo(4);

            MapPoolMapRow nuke = record.Maps.Single(m => m.Map == "de_nuke");
            await Assert.That(nuke.Played).IsEqualTo(2);
            await Assert.That(nuke.Wins).IsEqualTo(1);
            await Assert.That(nuke.Losses).IsEqualTo(1);
            await Assert.That(nuke.CtRoundsWon).IsEqualTo(9 + 8);
            await Assert.That(nuke.TRoundsWon).IsEqualTo(16 + 13);

            MapPoolMapRow inferno = record.Maps.Single(m => m.Map == "de_inferno");
            await Assert.That(inferno.Played).IsEqualTo(1);
            await Assert.That(inferno.Wins).IsEqualTo(1);
            await Assert.That(inferno.WinRate).IsEqualTo(1.0);

            await Assert.That(record.Deciders.Played).IsEqualTo(1).Because("only the day-10 group of three is a recognizable series");
            await Assert.That(record.Deciders.Wins).IsEqualTo(1);
            await Assert.That(record.Deciders.Losses).IsEqualTo(0);
        }
    }

    [Test]
    public async Task TheTab_ProjectsTheSelectedTeam_WithSampleSizes_AndRunsTheVetoActions()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, Guid teamB) = await Library();
        VetoHistoryStore vetoes = new(null);
        using DossierTabViewModel vm = new(teams, cache, vetoes, isBrowser: false);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasTeams).IsTrue();
            await Assert.That(vm.Teams.Count).IsEqualTo(2);
            await Assert.That(vm.HasSelection).IsFalse();
            await Assert.That(vm.IsBrowser).IsFalse();
        }

        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasSelection).IsTrue();
            await Assert.That(vm.HasMaps).IsTrue();
            await Assert.That(vm.Maps.Count).IsEqualTo(4);
            await Assert.That(vm.SampleSizeLine).Contains("5 demos");
            await Assert.That(vm.SampleSizeLine).Contains("4 maps");
            await Assert.That(vm.HasDeciderData).IsTrue();
            await Assert.That(vm.DeciderLine).Contains("1-0");
        }

        await Assert.That(vm.HasVetoes).IsFalse();
        vm.NewVetoMap = "de_ancient";
        vm.NewVetoAction = VetoAction.Ban;
        vm.NewVetoByOpponent = false;
        vm.AddVetoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasVetoes).IsTrue();
            await Assert.That(vm.Vetoes.Single().Map).IsEqualTo("de_ancient");
            await Assert.That(vm.Vetoes.Single().ActionLabel).IsEqualTo("Ban");
            await Assert.That(vm.Vetoes.Single().ByLabel).IsEqualTo("us");
            await Assert.That(vetoes.For(teamA).Count).IsEqualTo(1);
        }

        vm.RemoveVetoCommand.Execute(vm.Vetoes.Single());
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasVetoes).IsFalse();
            await Assert.That(vetoes.For(teamA)).IsEmpty();
        }

        // The other team's own record has no shared history with A's veto entries.
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamB);
        await Assert.That(vm.HasVetoes).IsFalse();
    }

    [Test]
    public async Task WithNoDemos_TheRecordSaysSo_AndVetoesStaySessionOnly()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        VetoHistoryStore vetoes = new(null);
        using DossierTabViewModel vm = new(teams, cache, vetoes);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasTeams).IsFalse();
            await Assert.That(vm.VetoesAreSessionOnly).IsTrue();
            await Assert.That(vetoes.IsSessionOnly).IsTrue();
        }
    }
}

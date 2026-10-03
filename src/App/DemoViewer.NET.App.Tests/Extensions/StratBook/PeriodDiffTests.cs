#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Period Diff over one team's six synthetic demos (plan.md §3, Period Diff, Phase 5): a window of
///     three splits them into "last" (days 4 to 6, team A losing every one) and "previous" (days 1 to 3,
///     team A winning every one), read newest first the way <see cref="TeamIdentityService.SidesOf" />
///     already orders them. The roster-boundary test is the item's own "done" bar: a
///     <see cref="TeamIdentityService.StartRoster" /> call between the two windows, with the same five
///     players on both sides of it, still shows up as <see cref="PeriodDiffSet.RosterChanged" /> — the
///     mechanic team-identity.md's design names for exactly this ("Period Diff then reads the boundary").
/// </summary>
[NotInParallel]
public class PeriodDiffTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly DateTime _baseDate = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map, int ctScore, int tScore,
        int ctSideWins, int tSideWins)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = _baseDate.AddDays(day).Ticks,
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

    // Team A (1..5) always wins days 1 to 3 and always loses days 4 to 6, against team B (11..15).
    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams, Guid TeamA)> Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            // Previous period: A wins every demo.
            cache.Upsert(Record("/d/day1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_nuke",
                ctScore: 5, tScore: 13, ctSideWins: 5, tSideWins: 13));
            cache.Upsert(Record("/d/day2.dem", 2, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_mirage",
                ctScore: 13, tScore: 4, ctSideWins: 13, tSideWins: 4));
            cache.Upsert(Record("/d/day3.dem", 3, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_inferno",
                ctScore: 7, tScore: 13, ctSideWins: 7, tSideWins: 13));
            // Recent period: A loses every demo.
            cache.Upsert(Record("/d/day4.dem", 4, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_dust2",
                ctScore: 13, tScore: 8, ctSideWins: 13, tSideWins: 8));
            cache.Upsert(Record("/d/day5.dem", 5, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_nuke",
                ctScore: 6, tScore: 13, ctSideWins: 6, tSideWins: 13));
            cache.Upsert(Record("/d/day6.dem", 6, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_overpass",
                ctScore: 13, tScore: 9, ctSideWins: 13, tSideWins: 9));
        }

        await teams.Idle;
        Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Ids(1)[0]) == true)).Id;
        return (cache, teams, teamA);
    }

    [Test]
    public async Task Build_SplitsNewestFirst_IntoRecentAndPrevious()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA) = await Library();

        PeriodDiffSet set = PeriodDiffService.Build(teams, cache, teamA, windowSize: 3);

        using (Assert.Multiple())
        {
            await Assert.That(set.WindowSize).IsEqualTo(3);
            await Assert.That(set.TotalDemos).IsEqualTo(6);
            await Assert.That(set.HasBothPeriods).IsTrue();

            await Assert.That(set.Recent.Demos.Select(d => d.Map)).IsEquivalentTo(["de_overpass", "de_nuke", "de_dust2"])
                .Because("newest first: day 6, day 5, day 4");
            await Assert.That(set.Previous.Demos.Select(d => d.Map)).IsEquivalentTo(["de_inferno", "de_mirage", "de_nuke"])
                .Because("newest first within the older window: day 3, day 2, day 1");

            await Assert.That(set.Recent.Wins).IsEqualTo(0);
            await Assert.That(set.Recent.Losses).IsEqualTo(3);
            await Assert.That(set.Recent.WinRate).IsEqualTo(0.0);
            await Assert.That(set.Previous.Wins).IsEqualTo(3);
            await Assert.That(set.Previous.Losses).IsEqualTo(0);
            await Assert.That(set.Previous.WinRate).IsEqualTo(1.0);
            await Assert.That(set.WinRateDelta).IsEqualTo(-1.0);

            await Assert.That(set.Recent.CtRoundsWon).IsEqualTo(13 + 6 + 13);
            await Assert.That(set.Recent.TRoundsWon).IsEqualTo(8 + 13 + 9);
            await Assert.That(set.Previous.CtRoundsWon).IsEqualTo(5 + 13 + 7);
            await Assert.That(set.Previous.TRoundsWon).IsEqualTo(13 + 4 + 13);
            await Assert.That(set.CtRoundShareDelta)
                .IsEqualTo((32.0 / 62) - (25.0 / 55));

            // No roster boundary was drawn: both periods are the same fixed five under one roster id.
            await Assert.That(set.RosterChanged).IsFalse();
            await Assert.That(set.Recent.DominantRoster?.RosterId).IsEqualTo(set.Previous.DominantRoster?.RosterId);
        }
    }

    [Test]
    public async Task Build_WithFewerDemosThanTwoWindows_LeavesPreviousShortOrEmpty()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA) = await Library();

        PeriodDiffSet set = PeriodDiffService.Build(teams, cache, teamA, windowSize: 5);

        using (Assert.Multiple())
        {
            await Assert.That(set.Recent.Count).IsEqualTo(5);
            await Assert.That(set.Previous.Count).IsEqualTo(1).Because("only one demo is left once the last five are taken");
            await Assert.That(set.HasBothPeriods).IsTrue();
        }

        PeriodDiffSet none = PeriodDiffService.Build(teams, cache, teamA, windowSize: 10);
        using (Assert.Multiple())
        {
            await Assert.That(none.Recent.Count).IsEqualTo(6);
            await Assert.That(none.Previous.Count).IsEqualTo(0);
            await Assert.That(none.HasBothPeriods).IsFalse();
            await Assert.That(DossierTabViewModel.PeriodDiffNoteFor(none)).Contains("not enough");
        }
    }

    [Test]
    public async Task ARosterBoundary_BetweenThePeriods_ShowsUpAsARosterChange()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA) = await Library();
        DateOnly boundary = DateOnly.FromDateTime(_baseDate.AddDays(4));

        // The same five players sit on both sides of the boundary; only the user-drawn line changes.
        teams.StartRoster(teamA, boundary, "post day 3");

        PeriodDiffSet set = PeriodDiffService.Build(teams, cache, teamA, windowSize: 3);

        using (Assert.Multiple())
        {
            await Assert.That(set.RosterChanged).IsTrue();
            await Assert.That(set.Recent.DominantRoster).IsNotNull();
            await Assert.That(set.Previous.DominantRoster).IsNotNull();
            await Assert.That(set.Recent.DominantRoster!.RosterId).IsNotEqualTo(set.Previous.DominantRoster!.RosterId);
            await Assert.That(set.Recent.DominantRoster.Label).IsEqualTo("post day 3");
            await Assert.That(set.Recent.DominantRoster.Demos).IsEqualTo(3);
            await Assert.That(set.Previous.DominantRoster.Demos).IsEqualTo(3);

            await Assert.That(DossierTabViewModel.PeriodDiffNoteFor(set)).Contains("roster changed");
            await Assert.That(DossierTabViewModel.PeriodDiffNoteFor(set)).Contains("post day 3");
        }
    }

    [Test]
    public async Task AStandIn_IsCountedAtTierOne_AndNotAgainstAnExtendedCore()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            // Team A fixes its five on days 1 and 2, then plays day 3 with two stand-ins (tier 1).
            cache.Upsert(Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(51, 52, 53, 54, 55), "de_nuke", 5, 13, 5, 13));
            cache.Upsert(Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Ids(56, 57, 58, 59, 60), "de_nuke", 5, 13, 5, 13));
            cache.Upsert(Record("/d/a3.dem", 3, Ids(1, 2, 3, 30, 31), Ids(61, 62, 63, 64, 65), "de_nuke", 5, 13, 5, 13));
            // Team B never repeats a five, so it only has an extended core; day 6 has a stand-in (tier 2).
            cache.Upsert(Record("/d/b1.dem", 4, Ids(11, 12, 13, 14, 15), Ids(71, 72, 73, 74, 75), "de_nuke", 5, 13, 5, 13));
            cache.Upsert(Record("/d/b2.dem", 5, Ids(11, 12, 13, 16, 17), Ids(76, 77, 78, 79, 80), "de_nuke", 5, 13, 5, 13));
            cache.Upsert(Record("/d/b3.dem", 6, Ids(11, 12, 13, 14, 40), Ids(81, 82, 83, 84, 85), "de_nuke", 5, 13, 5, 13));
        }

        await teams.Idle;
        SideAssignment tierOne = teams.GetAssignment("/d/a3.dem")!.T;
        SideAssignment tierTwo = teams.GetAssignment("/d/b3.dem")!.T;
        using (Assert.Multiple())
        {
            await Assert.That(tierOne.Tier).IsEqualTo(1);
            await Assert.That(tierOne.StandIn).IsTrue();
            await Assert.That(tierTwo.Tier).IsEqualTo(2);
            await Assert.That(tierTwo.StandIn).IsTrue().Because("the stamp is stored at both tiers");
        }

        PeriodDiffSet a = PeriodDiffService.Build(teams, cache, tierOne.TeamId!.Value, windowSize: 3);
        PeriodDiffSet b = PeriodDiffService.Build(teams, cache, tierTwo.TeamId!.Value, windowSize: 3);
        using (Assert.Multiple())
        {
            await Assert.That(a.Recent.StandInCount).IsEqualTo(1);
            await Assert.That(b.Recent.StandInCount).IsEqualTo(0)
                .Because("with a stand-in has no referent until a five exists");
            await Assert.That(b.Recent.Demos.Any(d => d.StandIn)).IsFalse();
        }
    }

    [Test]
    public async Task TheDossier_ShowsThePeriodDiffSection_AndThePickerRebuildsIt()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA) = await Library();
        using DossierTabViewModel vm = new(teams, cache, new VetoHistoryStore(null), isBrowser: false);

        await Assert.That(vm.HasPeriodDiff).IsFalse();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasPeriodDiff).IsTrue();
            await Assert.That(vm.WindowSize).IsEqualTo(PeriodDiffService.DefaultWindowSize);
            await Assert.That(vm.PeriodDiffRecent!.CountLabel).IsEqualTo("5 demos");
            await Assert.That(vm.PeriodDiffPrevious!.CountLabel).IsEqualTo("1 demo");
        }

        vm.WindowSize = 3;
        using (Assert.Multiple())
        {
            await Assert.That(vm.PeriodDiffRecent!.CountLabel).IsEqualTo("3 demos");
            await Assert.That(vm.PeriodDiffRecent!.RecordLabel).IsEqualTo("0-3");
            await Assert.That(vm.PeriodDiffPrevious!.RecordLabel).IsEqualTo("3-0");
            await Assert.That(vm.PeriodDiffNote).IsEqualTo("same roster across both periods");
        }

        // Deselecting drops the section.
        vm.SelectedTeam = null;
        await Assert.That(vm.HasPeriodDiff).IsFalse();
        await Assert.That(vm.PeriodDiffNote).IsEqualTo("");
    }
}

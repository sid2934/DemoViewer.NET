#region

using System.Globalization;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's personal bests over hand-made stat rows: each mark comes from the window's best demo,
///     a tie goes to the newest match, the win streak counts oldest to newest and skips demos without an outcome,
///     and a row opens its demo with the subject focused.
/// </summary>
public class PlayerOverviewPersonalBestsTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;

    private static Dictionary<string, double> Line(int kills, double hltv, double damage, bool won) =>
        new Dictionary<string, double>
        {
            ["rounds"] = 20, ["TotalK"] = kills, ["TotalD"] = 10, ["EnemyDmg"] = damage, ["HLTV"] = hltv,
            ["CTW"] = won ? 13 : 7, ["CTL"] = won ? 7 : 13
        };

    // Demos newest first, as the window lists them.
    private static (PlayerDirectory Directory, List<PlayerDemo> Demos) Demos(params IReadOnlyDictionary<string, double>?[] stats)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < stats.Length; i++)
            {
                store.Upsert(PlayerOverviewHistoryTests.Demo(i));
            }
        }

        PlayerDirectory directory = new(store, _ => true);
        List<PlayerMatch> matches = [.. directory.Matches(Grim, PlayerFilter.All)];
        return (directory, [.. matches.Select((m, i) => new PlayerDemo(m, stats[stats.Length - 1 - i] is not null, stats[stats.Length - 1 - i]))]);
    }

    [Test]
    public async Task Bests_ComeFromTheBestDemoOfEach_AndATieGoesToTheNewest()
    {
        // Oldest to newest: 30 kills, 25 kills twice.
        (_, List<PlayerDemo> demos) = Demos(Line(30, 1.5, 100, true), Line(25, 1.2, 80, true), Line(25, 1.2, 80, false));
        IReadOnlyList<PersonalBestItem> rows = PersonalBestsSectionViewModel.Build(demos);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(r => r.Label)).IsEquivalentTo(["Most kills", "Best HLTV", "Best ADR", "Longest win streak"]);
            await Assert.That(rows[0].ValueText).IsEqualTo("30");
            await Assert.That(rows[0].Match.Row.Path).IsEqualTo("/lib/0.dem");
            await Assert.That((rows[1].ValueText, rows[2].ValueText)).IsEqualTo(("1.50", "5.0"));
        }

        (_, List<PlayerDemo> tied) = Demos(Line(25, 1.2, 80, true), Line(25, 1.2, 80, true));
        await Assert.That(PersonalBestsSectionViewModel.Build(tied)[0].Match.Row.Path).IsEqualTo("/lib/1.dem");
    }

    [Test]
    public async Task WinStreak_CountsOldestToNewest_SkipsAMissingOutcome_AndLinksItsLastWin()
    {
        // Oldest to newest: W W (no stats) W L W; the run of three spans the demo with no row.
        (_, List<PlayerDemo> demos) = Demos(
            Line(10, 1, 50, true), Line(10, 1, 50, true), null, Line(10, 1, 50, true), Line(10, 1, 50, false), Line(10, 1, 50, true));
        PersonalBestItem streak = PersonalBestsSectionViewModel.Build(demos).Single(r => r.Label == "Longest win streak");

        using (Assert.Multiple())
        {
            await Assert.That(streak.ValueText).IsEqualTo("3");
            await Assert.That(streak.Match.Row.Path).IsEqualTo("/lib/3.dem");
        }
    }

    [Test]
    public async Task NoStats_HidesTheSection_AndALossOnlyWindowHasNoStreak()
    {
        await Assert.That(PersonalBestsSectionViewModel.Build(Demos(null, null).Demos)).IsEmpty();
        (_, List<PlayerDemo> demos) = Demos(Line(5, 0.8, 40, false));
        await Assert.That(PersonalBestsSectionViewModel.Build(demos).Select(r => r.Label)).DoesNotContain("Longest win streak");
    }

    [Test]
    public async Task Open_FocusesTheSubjectInThatDemo_AndAMissingFileIsMarked()
    {
        (PlayerDirectory directory, List<PlayerDemo> demos) = Demos(Line(30, 1.5, 100, true));
        using IdentityService identity = new(new SettingsService((string?) null));
        List<(string, ulong)> focused = [];
        PlayerOverviewServices services = new(directory, identity, null, _ => { }, _ => { }, (p, id) => focused.Add((p, id)), _ => { });
        PersonalBestsSectionViewModel section = new(services);
        PlayerEntry player = directory.Get(Grim)!;
        PlayerFilter filter = PlayerFilter.All;
        section.Update(new PlayerOverviewContext(Grim, player, [.. demos.Select(d => d.Match)], 1, false, services, demos, filter, demos, false, null, false));

        await Assert.That(section.IsVisible).IsTrue();
        section.OpenCommand.Execute(section.Rows[0]);
        await Assert.That(focused.Single()).IsEqualTo(("/lib/0.dem", Grim));

        section.Update(new PlayerOverviewContext(null, null, [], 0, false, services, [], filter, [], false, null, false));
        await Assert.That(section.IsVisible).IsFalse();
        await Assert.That(string.Create(CultureInfo.InvariantCulture, $"{section.Rows.Count}")).IsEqualTo("0");
    }
}

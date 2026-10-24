#region

using System.Globalization;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The aggregator pools an account's merged facet rows by the column rules: one demo is its facet row, rates
///     are recomputed from summed components, HLTV is weighted by rounds, a reconnect's slots fold into one row and
///     a ghost slot is dropped. Every row is built in memory or read from the committed sample.
/// </summary>
public class PlayerAggregateTests
{
    private const ulong Grim = 76561190000000003;

    private static readonly DemoCacheIndexEntry Row = new() { Path = "/d/a.dem", Map = "de_nuke" };

    // HLTV 2.0 as rules/player_stats.rules.yaml computes it, from a row's own columns.
    private static double Hltv(Dictionary<string, double> c)
    {
        double rounds = c["rounds"];
        double kpr = c["TotalK"] / rounds;
        return (0.73 * (c["KAST%"] / 100)) + (0.3591 * kpr) - (0.5329 * (c["TotalD"] / rounds))
               + (0.2372 * ((2.13 * kpr) + (0.42 * (c["TotalA"] / rounds)) - 0.41)) + (0.0032 * (c["EnemyDmg"] / rounds)) + 0.1587;
    }

    private static Dictionary<string, double> Cells(double rounds, double kills, double deaths, double assists, double damage, double kastRounds,
        double won = 0, double lost = 0)
    {
        Dictionary<string, double> cells = new(StringComparer.Ordinal)
        {
            ["rounds"] = rounds,
            ["TotalK"] = kills,
            ["TotalD"] = deaths,
            ["TotalA"] = assists,
            ["EnemyDmg"] = damage,
            ["KAST%"] = kastRounds / rounds * 100,
            ["Shots"] = kills * 5,
            ["CTW"] = won,
            ["TL"] = lost
        };
        cells["HLTV"] = Hltv(cells);
        return cells;
    }

    private static FactRow Facet(int slot, ulong? steamId, int team, IReadOnlyDictionary<string, double> cells) => new(
        new Dictionary<string, FactValue>
        {
            ["player_slot"] = FactValue.Of((long)slot),
            ["steam_id"] = FactValue.Of(steamId?.ToString(CultureInfo.InvariantCulture)),
            ["team"] = FactValue.Of((long)team)
        },
        cells.ToDictionary(c => c.Key, c => c.Value == Math.Floor(c.Value) && c.Key is not ("KAST%" or "HLTV") ? FactValue.Of((long)c.Value) : FactValue.Of(c.Value),
            StringComparer.Ordinal));

    private static PlayerDemo Demo(IReadOnlyDictionary<string, double>? stats, string map = "de_nuke", int? side = 3, long time = 0) =>
        new(new PlayerMatch(new DemoCacheIndexEntry { Path = $"/d/{map}{time}.dem", Map = map, MatchTimeUtcTicks = time }, side, [], [], side is null),
            stats is not null, stats);

    [Test]
    public async Task OneDemo_IsItsFacetRow()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        string? path = repo is null ? null : PlayerMatchFacetTests.SamplePath(repo);
        if (path is null || !File.Exists(path))
        {
            throw new SkipTestException("missing the player_match sample");
        }

        FactTable table = FactsCodec.Decode(PlayerMatchFacetTests.Gzip(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)))!;
        List<string> mismatches = [];
        foreach (FactRow row in table.Rows)
        {
            ulong id = ulong.Parse(row.Dimensions["steam_id"].AsText()!, CultureInfo.InvariantCulture);
            Dictionary<string, double> cells = row.Values.ToDictionary(c => c.Key, c => c.Value.AsNumber() ?? 0, StringComparer.Ordinal);
            IReadOnlyDictionary<string, double> merged = PlayerAggregate.Merge(table.Rows, id)!;
            bool sight = PlayerMatchFacet.HasSight(table.Rows);
            PlayerAggregate aggregate = PlayerAggregate.Of([new PlayerDemo(new PlayerMatch(Row, 3, [], [], false), true, merged) { HasSight = sight }]);
            foreach ((string key, ColumnRule rule) in ColumnRules.All.Where(r => r.Value is not ColumnRule.NotAggregable))
            {
                double? expected = ColumnCatalogue.RequiresVisibility(key) && !sight ? null
                    : rule is ColumnRule.Sum sum ? cells[sum.Column] : rule.Pool([cells]);
                if (aggregate[key] != expected)
                {
                    mismatches.Add($"{id} {key}: {aggregate[key]} against {expected}");
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(table.Rows.Count).IsEqualTo(2);
            await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches));
        }
    }

    [Test]
    public async Task ASightColumn_PoolsOnlyTheDemosWithSight_AndIsNoDataWithout()
    {
        Dictionary<string, double> sighted = Cells(20, 15, 10, 3, 1500, 14);
        sighted["Spots"] = 30;
        sighted["preaim_angle_sum"] = 300;
        sighted["ttd_ticks"] = 400;
        sighted["TTDn"] = 25;
        Dictionary<string, double> blind = Cells(20, 25, 10, 3, 2500, 14);

        PlayerAggregate mixed = PlayerAggregate.Of([Demo(sighted) with { HasSight = true }, Demo(blind)]);
        PlayerAggregate none = PlayerAggregate.Of([Demo(blind), Demo(blind)]);

        using (Assert.Multiple())
        {
            await Assert.That(mixed.WithStats).IsEqualTo(2);
            await Assert.That(mixed.WithSight).IsEqualTo(1);
            await Assert.That(mixed["Preaim"]).IsEqualTo(10.0).Because("the demo without sight measured nothing, so it does not dilute the pool");
            await Assert.That(mixed["TTD"]).IsEqualTo(400.0 / 25 * 15.625);
            await Assert.That(mixed["Spots"]).IsEqualTo(30.0);
            await Assert.That(mixed["TotalK"]).IsEqualTo(40.0).Because("every other column still pools both demos");
            await Assert.That(none.WithSight).IsEqualTo(0);
            await Assert.That(none["Preaim"]).IsNull().Because("no data, never 0");
            await Assert.That(none["Spots"]).IsNull();
            await Assert.That(none.Values.Keys.Where(ColumnCatalogue.RequiresVisibility)).IsEmpty();
            await Assert.That(none["Acc%"]).IsNotNull().Because("accuracy needs no geometry");
        }
    }

    [Test]
    public async Task ARatioOfSums_IsNotTheMeanOfRatios()
    {
        // ADR 100 over 10 rounds and 50 over 30: 2500 damage in 40 rounds is 62.5, where the mean says 75.
        PlayerAggregate aggregate = PlayerAggregate.Of([Demo(Cells(10, 10, 5, 2, 1000, 7)), Demo(Cells(30, 20, 20, 4, 1500, 18))]);
        using (Assert.Multiple())
        {
            await Assert.That(aggregate["ADR"]).IsEqualTo(62.5);
            await Assert.That(aggregate["KD"]).IsEqualTo(30.0 / 25);
            await Assert.That(aggregate["KAST%"]!.Value).IsEqualTo(25.0 / 40 * 100).Within(1e-9);
            await Assert.That(aggregate["TotalK"]).IsEqualTo(30);
        }
    }

    [Test]
    public async Task PooledHltv_IsTheRoundsWeightedMean_AndTheRatingOfThePool()
    {
        Dictionary<string, double> a = Cells(13, 15, 8, 3, 1620, 11);
        Dictionary<string, double> b = Cells(24, 14, 19, 6, 1702, 15);
        PlayerAggregate aggregate = PlayerAggregate.Of([Demo(a), Demo(b)]);
        Dictionary<string, double> pool = new(StringComparer.Ordinal)
        {
            ["rounds"] = 37, ["TotalK"] = 29, ["TotalD"] = 27, ["TotalA"] = 9, ["EnemyDmg"] = 3322, ["KAST%"] = 26.0 / 37 * 100
        };
        using (Assert.Multiple())
        {
            await Assert.That(aggregate["HLTV"]!.Value).IsEqualTo(((a["HLTV"] * 13) + (b["HLTV"] * 24)) / 37).Within(1e-9);
            await Assert.That(aggregate["HLTV"]!.Value).IsEqualTo(Hltv(pool)).Within(1e-9).Because("HLTV 2.0 is linear in its per-round terms");
        }
    }

    [Test]
    public async Task AGhostSlot_IsDropped_AndAReconnectFolds()
    {
        Dictionary<string, double> ghost = Cells(24, 0, 0, 0, 0, 24);
        ghost["Shots"] = 0;
        Dictionary<string, double> before = Cells(24, 6, 4, 1, 610, 20);
        Dictionary<string, double> after = Cells(24, 9, 7, 2, 905, 12);
        Dictionary<string, double> other = Cells(24, 30, 1, 0, 3000, 24);

        IReadOnlyDictionary<string, double> alone = PlayerAggregate.Merge([Facet(3, Grim, 0, ghost), Facet(4, Grim, 3, after), Facet(5, Grim + 1, 3, other)], Grim)!;
        IReadOnlyDictionary<string, double> folded = PlayerAggregate.Merge([Facet(3, Grim, 0, before), Facet(4, Grim, 3, after), Facet(5, null, 3, other)], Grim)!;
        Dictionary<string, double> expected = new(StringComparer.Ordinal)
        {
            ["rounds"] = 24, ["TotalK"] = 15, ["TotalD"] = 11, ["TotalA"] = 3, ["EnemyDmg"] = 1515, ["KAST%"] = 100
        };
        using (Assert.Multiple())
        {
            await Assert.That(alone).IsEquivalentTo(after, CollectionOrdering.Matching).Because("a ghost on no side with no shots, deaths or damage is dropped");
            await Assert.That(PlayerAggregate.Merge([Facet(3, Grim, 0, ghost)], Grim)).IsNull();
            await Assert.That(PlayerAggregate.Merge([Facet(5, Grim + 1, 3, other)], Grim)).IsNull();
            await Assert.That(folded["rounds"]).IsEqualTo(24).Because("every row carries the match's rounds");
            await Assert.That(folded["TotalK"]).IsEqualTo(15);
            await Assert.That(folded["EnemyDmg"]).IsEqualTo(1515);
            await Assert.That(folded["KAST%"]).IsEqualTo(100).Because("32 KAST rounds of 24 is every round");
            await Assert.That(folded["HLTV"]).IsEqualTo(Hltv(expected)).Within(1e-9).Because("the folded rating is the rating of the folded row");
        }
    }

    [Test]
    public async Task TwoSlotsOfOneAccount_TallyTheirTeamsRoundsOnce()
    {
        // Both slots tallied all 24 rounds: the team's 9 + 4 wins and 2 + 9 losses each.
        Dictionary<string, double> first = Cells(24, 13, 13, 2, 1400, 16, 9, 9);
        Dictionary<string, double> second = Cells(24, 3, 22, 1, 400, 22, 9, 9);
        foreach (Dictionary<string, double> slot in (Dictionary<string, double>[]) [first, second])
        {
            slot["CTL"] = 2;
            slot["TW"] = 4;
        }

        first["Survived"] = 13;
        second["Survived"] = 17;

        IReadOnlyDictionary<string, double> both = PlayerAggregate.Merge([Facet(7, Grim, 2, first), Facet(8, Grim, 2, second)], Grim)!;
        IReadOnlyDictionary<string, double> halves = PlayerAggregate.Merge(
            [Facet(7, Grim, 2, Cells(24, 5, 5, 0, 500, 5, 3, 4)), Facet(8, Grim, 2, Cells(24, 9, 9, 0, 900, 9, 9, 8))], Grim)!;
        using (Assert.Multiple())
        {
            await Assert.That((both["CTW"], both["CTL"], both["TW"], both["TL"])).IsEqualTo((9d, 2d, 4d, 9d))
                .Because("a round is won or lost once however many slots tallied it");
            await Assert.That(both["TotalK"]).IsEqualTo(16).Because("kills are the slot's own and still add up");
            await Assert.That(both["Survived"]).IsEqualTo(24).Because("no account survives more rounds than the match has");
            await Assert.That((halves["CTW"], halves["TL"])).IsEqualTo((12d, 12d)).Because("slots that split the rounds add up");
        }
    }

    [Test]
    public async Task Outcome_IsTheAccountsRoundWinsAgainstItsLosses()
    {
        Dictionary<string, double> won = Cells(24, 1, 1, 0, 1, 1, 13, 11);
        Dictionary<string, double> tied = Cells(30, 1, 1, 0, 1, 1, 15, 15);
        Dictionary<string, double> lost = Cells(20, 1, 1, 0, 1, 1, 7, 13);
        lost["TW"] = 2;
        PlayerAggregate aggregate = PlayerAggregate.Of([Demo(won), Demo(tied), Demo(lost), Demo(null)]);
        using (Assert.Multiple())
        {
            await Assert.That(PlayerAggregate.OutcomeOf(won)).IsEqualTo(MatchOutcome.Win);
            await Assert.That(PlayerAggregate.OutcomeOf(tied)).IsEqualTo(MatchOutcome.Tie);
            await Assert.That(PlayerAggregate.OutcomeOf(lost)).IsEqualTo(MatchOutcome.Loss).Because("9 wins on both sides against 13 losses");
            await Assert.That(PlayerAggregate.OutcomeOf(Cells(1, 0, 0, 0, 0, 0))).IsNull();
            await Assert.That((aggregate.Matches, aggregate.WithStats, aggregate.Wins, aggregate.Losses, aggregate.Ties)).IsEqualTo((4, 3, 1, 1, 1));
            await Assert.That(PlayerAggregate.Of([Demo(null)]).Values).IsEmpty().Because("no stats is not a row of zeros");
        }
    }

    [Test]
    public async Task Window_FiltersThenTakesTheNewest()
    {
        Dictionary<string, double> cells = Cells(10, 1, 1, 0, 1, 1);
        IReadOnlyList<PlayerDemo> demos =
        [
            Demo(cells, "de_nuke", 3, 50), Demo(cells, "de_inferno", 2, 40), Demo(cells, "de_nuke", 2, 30), Demo(cells, "de_nuke", null, 20),
            Demo(cells, "de_nuke", 3, 10)
        ];
        using (Assert.Multiple())
        {
            await Assert.That(PlayerAggregate.Window(demos, PlayerFilter.All)).IsSameReferenceAs(demos);
            await Assert.That(PlayerAggregate.Window(demos, new PlayerFilter { Map = "DE_NUKE", Last = 3 }).Select(d => d.Match.TimeTicks)).IsEquivalentTo([50L, 30, 20], CollectionOrdering.Matching);
            await Assert.That(PlayerAggregate.Window(demos, new PlayerFilter { Side = 3 }).Select(d => d.Match.TimeTicks)).IsEquivalentTo([50L, 10], CollectionOrdering.Matching);
            await Assert.That(PlayerAggregate.Window(demos, new PlayerFilter { FromTicks = 20, BeforeTicks = 50 }).Select(d => d.Match.TimeTicks))
                .IsEquivalentTo([40L, 30, 20], CollectionOrdering.Matching);
            await Assert.That(PlayerAggregate.Window(demos, new PlayerFilter { Last = 0 })).IsEmpty();
        }
    }
}

#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Focus by SteamID on the Stats tab and its "Open player overview" entry: which rows carry an account, which slot a
///     reconnecting player focuses, the wait for an evaluation, and the rule that greys the entry out for a player with
///     no SteamID, a bot or a coach. Synthetic evaluation, no demo.
/// </summary>
public class FocusPlayerTests
{
    internal const ulong Alice = 76561198000000001;
    internal const ulong Kirk = 76561198000000002;
    internal const ulong Coach = 76561198000000003;

    private static NodeSnapshot Num(int value) => new(true, value.ToString(CultureInfo.InvariantCulture), value);

    /// <summary>
    ///     Alice left as slot 0 after five rounds and came back as slot 3 for thirteen; Bob (slot 1) has no SteamID; Kirk
    ///     (slot 2) is a bot with one; slot 4 coached CT, which the run's library_coaches table says.
    /// </summary>
    internal static (EvaluationResult Result, ParsedDemo Demo, MetricTable Coaches) BuildScenario()
    {
        StubNode roundNode = new("RoundNumber");
        List<StateNode> tracked = [roundNode];
        (int Slot, string Name, int Tw, int Tl)[] players =
        [
            (0, "Alice", 3, 2),
            (1, "Bob", 9, 4),
            (2, "BOT Kirk", 9, 4),
            (3, "Alice", 8, 5),
            (4, "Coach", 0, 0)
        ];
        string[] columns = ["TotalK", "CTW", "CTL", "TW", "TL"];

        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = [];
        NodeSnapshot[] vector = new NodeSnapshot[1 + players.Length * columns.Length];
        vector[0] = Num(13);
        foreach ((int slot, string name, int tw, int tl) in players)
        {
            List<PerPlayerColumnAssignment> assignments = [];
            List<StateNode> nodes = [];
            foreach (string column in columns)
            {
                StubNode node = new($"{slot}_{column}");
                vector[tracked.Count] = Num(column switch
                {
                    "TW" => tw,
                    "TL" => tl,
                    "TotalK" => slot * 3,
                    _ => 0
                });
                tracked.Add(node);
                nodes.Add(node);
                assignments.Add(new PerPlayerColumnAssignment(node, column));
            }

            materialized.Add(new PerPlayerNodeTemplate.MaterializedPlayer(slot, name, nodes, [], assignments, []));
        }

        EvaluationResult result = new(new RuleChainTimeline([]), new[] { vector }, [], tracked, materialized, []);
        Dictionary<int, PlayerInfo> infos = new()
        {
            [0] = new PlayerInfo(0, "Alice", Alice, 0, 2, false),
            [1] = new PlayerInfo(1, "Bob", 0UL, 1, 3, false),
            [2] = new PlayerInfo(2, "BOT Kirk", Kirk, 2, 3, IsBot: true),
            [3] = new PlayerInfo(3, "Alice", Alice, 3, 2, false),
            [4] = new PlayerInfo(4, "Coach", Coach, 4, 3, false)
        };
        ParsedDemo demo = SyntheticParsedDemo.Create(
            [], [], infos, null,
            "de_test", 0, 1f / 64f,
            "t", "t", "csgo", 0,
            0, 0, "valve_demo_2",
            "", "", DemoProfile.Unknown);
        MetricTable coaches = new(LibraryTeamTables.CoachesTable,
            ["player_slot", "player_name", "steam_id"],
            ["coaching_team"],
            [
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["player_slot"] = 4,
                        ["player_name"] = "Coach",
                        ["steam_id"] = Coach.ToString(CultureInfo.InvariantCulture)
                    },
                    new Dictionary<string, object?>
                    {
                        ["coaching_team"] = 3
                    })
            ]);
        return (result, demo, coaches);
    }

    /// <summary>A Stats tab over the scenario whose overview entry records what it was asked to open.</summary>
    internal static StatsTabViewModel Updated(List<(ulong SteamId64, string? Name)>? opened = null, bool withOpener = true)
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        if (withOpener)
        {
            vm.PlayerOverviewOpener = (id, name) => opened?.Add((id, name));
        }

        (EvaluationResult result, ParsedDemo demo, MetricTable coaches) = BuildScenario();
        vm.Update(result, demo, [coaches]);
        return vm;
    }

    [Test]
    public async Task Rows_CarryTheAccount_OnlyForAHumanWithASteamId()
    {
        StatsTabViewModel vm = Updated();

        await Assert.That(vm.GameRows.Where(r => r.PlayerName == "Alice").Select(r => r.SteamId64))
            .IsEquivalentTo(new[] { Alice, Alice });
        foreach (string name in (string[])["Bob", "BOT Kirk", "Coach"])
        {
            StatsRow row = vm.GameRows.Single(r => r.PlayerName == name);
            await Assert.That(row.SteamId64).IsEqualTo(0UL).Because(name);
            await Assert.That(row.CanOpenPlayerOverview).IsFalse();
            await Assert.That(row.ShowsPlayerOverview).IsTrue().Because("the entry stays, greyed out with its reason");
            await Assert.That(row.PlayerOverviewTip).IsEqualTo("this demo carries no SteamID for this player");
        }

        await Assert.That(vm.DetailPlayers.Single(p => p.Slot == 3).SteamId64).IsEqualTo(Alice);
        await Assert.That(vm.DetailPlayers.Single(p => p.Slot == 1).CanOpenPlayerOverview).IsFalse();
    }

    [Test]
    public async Task TotalsRows_AndAShellWithoutTheTab_ShowNoEntry()
    {
        StatsTabViewModel vm = Updated(withOpener: false);

        await Assert.That(vm.GameRows.Any(r => r.ShowsPlayerOverview)).IsFalse();
        await Assert.That(Updated().TeamSections.Select(s => s.Totals).Any(r => r.ShowsPlayerOverview)).IsFalse();
    }

    [Test]
    public async Task OpenPlayerOverview_OpensTheRowsAccount_AndANoSteamIdRowOpensNothing()
    {
        List<(ulong, string?)> opened = [];
        StatsTabViewModel vm = Updated(opened);

        foreach (StatsRow row in vm.GameRows.Where(r => r.PlayerName != "Alice"))
        {
            vm.OpenPlayerOverviewCommand.Execute(row);
        }

        await Assert.That(opened).IsEmpty();
        vm.OpenPlayerOverviewCommand.Execute(vm.GameRows.First(r => r.PlayerName == "Alice"));
        await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (Alice, "Alice") });
    }

    [Test]
    public async Task FocusPlayer_OpensTheReconnectSlotWithTheMostRounds()
    {
        StatsTabViewModel vm = Updated();

        await Assert.That(vm.FocusPlayer(Alice)).IsTrue();
        await Assert.That(vm.IsPlayerDetailsOpen).IsTrue();
        await Assert.That(vm.PlayerDetails!.PlayerSlot).IsEqualTo(3);
    }

    [Test]
    public async Task FocusPlayer_OpensNothing_ForAnAccountNotInTheDemo_OrABotOrCoach()
    {
        StatsTabViewModel vm = Updated();

        foreach (ulong id in (ulong[])[76561198000000099, Kirk, Coach, 0])
        {
            await Assert.That(vm.FocusPlayer(id)).IsFalse().Because(id.ToString(CultureInfo.InvariantCulture));
        }

        await Assert.That(vm.IsPlayerDetailsOpen).IsFalse();
    }

    [Test]
    public async Task FocusPlayer_BeforeTheEvaluation_WaitsForIt()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo, MetricTable coaches) = BuildScenario();

        await Assert.That(vm.FocusPlayer(Alice)).IsFalse();
        vm.Update(result, demo, [coaches]);

        await Assert.That(vm.IsPlayerDetailsOpen).IsTrue();
        await Assert.That(vm.PlayerDetails!.PlayerSlot).IsEqualTo(3);
    }

    [Test]
    public async Task FocusPlayer_IsDropped_ByADemoUnload()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo, MetricTable coaches) = BuildScenario();

        vm.FocusPlayer(Alice);
        vm.ResetForDemoUnload();
        vm.Update(result, demo, [coaches]);

        await Assert.That(vm.IsPlayerDetailsOpen).IsFalse();
    }

    [Test]
    public async Task Link_GivesNoAccount_ForABotACoachOrAnUnparsableId()
    {
        await Assert.That(PlayerOverviewLink.Of(Alice, false, false)).IsEqualTo(Alice);
        await Assert.That(PlayerOverviewLink.Of(Alice, true, false)).IsEqualTo(0UL);
        await Assert.That(PlayerOverviewLink.Of(Alice, false, true)).IsEqualTo(0UL);
        await Assert.That(PlayerOverviewLink.Of("76561198000000001", false, false)).IsEqualTo(Alice);
        foreach (string? text in (string?[])[null, "", "0", "BOT", "-1", "STEAM_1:1:0", " 76561198000000001"])
        {
            await Assert.That(PlayerOverviewLink.Of(text, false, false)).IsEqualTo(0UL).Because(text ?? "null");
        }
    }

    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }
}

#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     PLAYERS counts only people on a side; everyone else named is a SPECTATOR.
///     <para>
///         The card's contract is that its headline number equals the two rosters printed beneath
///         it. Counting every named non-proxy entry broke that on any demo carrying observers,
///         coaches or admins — four of the seven tournament demos in this repo do, and would have
///         read "13" over rosters of ten. Matchmaking demos hid it by having none, which is why it
///         survived the original fix.
///     </para>
///     <para>
///         Synthetic rosters rather than a real demo: the point is the classification rule, and a
///         demo that happens to contain the right mix of observers is a fragile way to pin it.
///     </para>
/// </summary>
public class MatchOverviewSpectatorTests
{
    [Test]
    public async Task Spectators_AreCountedSeparately_AndKeptOutOfPlayers()
    {
        // 10 on sides, 3 with no team (observer / coach / admin), 1 GOTV proxy.
        Dictionary<int, PlayerInfo> players = new();
        for (int i = 0; i < 10; i++)
        {
            players[i] = new PlayerInfo(i, $"player{i}", 100UL + (ulong)i, i, i % 2 == 0 ? 2 : 3, false);
        }

        players[10] = new PlayerInfo(10, "observer", 201UL, 10, 0, false);
        players[11] = new PlayerInfo(11, "coach", 202UL, 11, 1, false);
        players[12] = new PlayerInfo(12, "admin", 203UL, 12, 0, false);
        players[13] = new PlayerInfo(13, "CSTV", 0UL, 13, 0, true)
        {
            IsHltv = true
        };

        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("synthetic.dem", null, null, "synthetic.dem");
        vm.SetSummary(vm.SubjectKey, Synthetic(players));

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerCountDisplay).IsEqualTo("10");
            await Assert.That(vm.SpectatorCountDisplay).IsEqualTo("3");
            await Assert.That(vm.HasSpectators).IsTrue();
            await Assert.That(vm.Terrorists.Count).IsEqualTo(5);
            await Assert.That(vm.CounterTerrorists.Count).IsEqualTo(5);
        }

        // The invariant this change exists to restore.
        await Assert.That(vm.PlayerCountDisplay)
            .IsEqualTo((vm.Terrorists.Count + vm.CounterTerrorists.Count).ToString(CultureInfo.InvariantCulture));

        // The proxy is neither a player nor a spectator — it is infrastructure.
        IReadOnlyList<string> named = vm.Terrorists.Concat(vm.CounterTerrorists).Select(p => p.Name).ToList();
        await Assert.That(named).DoesNotContain("CSTV");
    }

    /// <summary>The common case: no observers → the tile stays hidden rather than showing a bare 0.</summary>
    [Test]
    public async Task NoSpectators_HidesTheTile()
    {
        Dictionary<int, PlayerInfo> players = new();
        for (int i = 0; i < 10; i++)
        {
            players[i] = new PlayerInfo(i, $"player{i}", 100UL + (ulong)i, i, i % 2 == 0 ? 2 : 3, false);
        }

        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("synthetic.dem", null, null, "synthetic.dem");
        vm.SetSummary(vm.SubjectKey, Synthetic(players));

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerCountDisplay).IsEqualTo("10");
            await Assert.That(vm.SpectatorCountDisplay).IsEqualTo("0");
            await Assert.That(vm.HasSpectators).IsFalse();
        }
    }

    private static ParsedDemo Synthetic(IReadOnlyDictionary<int, PlayerInfo> players) =>
        SyntheticParsedDemo.Create([], [], players, null, "de_nuke", 0, 1f / 64f, "test", "test", "csgo", 0, 0, 0,
            "valve_demo_2", "", "", DemoProfile.Unknown);
}

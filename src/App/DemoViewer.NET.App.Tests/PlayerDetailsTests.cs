#region

using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Player-details dashboard tests (docs/ui/player-details-ux-design.md) — synthetic fixture per the
///     StatsTabTests pattern. Covers: PlayerSlot threading on StatsRow (the P0 linchpin), overlay
///     open/guard/close life-cycle (including force-close on Update), player switching with section
///     retention, panel projections (core strip, form geometry, achievements, weapon breakdown with
///     both empty states), and a headless Skia render of the OPEN overlay.
/// </summary>
[NotInParallel]
public class PlayerDetailsTests
{
    private static readonly string[] _expectedChains = ["ace", "clutch_1v3"];
    private static readonly string[] _expectedRoundKeys = ["Kills", "Damage"];
    private static readonly string[] _expectedWeapons = ["awp", "ak47"];

    private static NodeSnapshot Num(int value) => new(true, value.ToString(CultureInfo.InvariantCulture), value);

    private static NodeSnapshot Flag(bool active) => new(active);

    /// <summary>
    ///     Two players (Alice slot 0 / T, Bob slot 1 / CT); game columns [TotalK, ADR], round-scoped
    ///     [Kills, Damage]; three messages spanning rounds 1..2; timeline carries one per-player
    ///     achievement for Alice plus a game-scoped chain that must NOT appear on any player page.
    /// </summary>
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario(bool withRoundBools = false)
    {
        StubNode roundNode = new("RoundNumber");
        List<StateNode> tracked = [roundNode];

        (int Slot, string Name)[] players = [(0, "Alice"), (1, "Bob")];
        (string Label, bool RoundScoped)[] columns = withRoundBools
            ?
            [
                ("TotalK", false), ("ADR", false), ("Kills", true), ("Damage", true),
                ("HasKAST", true), ("FK", true), ("FD", true)
            ]
            : [("TotalK", false), ("ADR", false), ("Kills", true), ("Damage", true)];
        HashSet<string> boolColumns = withRoundBools ? ["HasKAST", "FK", "FD"] : [];

        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = new();
        List<List<int>> colIdx = new();
        foreach ((int slot, string name) in players)
        {
            List<PerPlayerColumnAssignment> assignments = new();
            List<int> indices = new();
            List<StateNode> nodes = new();
            foreach ((string column, bool roundScoped) in columns)
            {
                StateNode node = boolColumns.Contains(column)
                    ? new RoundBoolStubNode($"{name}_{column}")
                    : roundScoped
                        ? new RoundStubNode($"{name}_{column}")
                        : new StubNode($"{name}_{column}");
                indices.Add(tracked.Count);
                tracked.Add(node);
                nodes.Add(node);
                assignments.Add(new PerPlayerColumnAssignment(node, column, IsRoundScoped: roundScoped));
            }

            colIdx.Add(indices);
            materialized.Add(new PerPlayerNodeTemplate.MaterializedPlayer(slot, name, nodes, [], assignments, []));
        }

        int[] rounds = [1, 1, 2];
        NodeSnapshot[][] snapshots = new NodeSnapshot[rounds.Length][];
        for (int m = 0; m < rounds.Length; m++)
        {
            NodeSnapshot[] vec = new NodeSnapshot[tracked.Count];
            vec[0] = Num(rounds[m]);
            for (int p = 0; p < players.Length; p++)
            {
                bool alice = players[p].Slot == 0;
                vec[colIdx[p][0]] = Num(m * 10 + players[p].Slot); // TotalK
                vec[colIdx[p][1]] = Num(50 + players[p].Slot); // ADR
                vec[colIdx[p][2]] = Num(m + players[p].Slot); // Kills (round-scoped)
                vec[colIdx[p][3]] = Num(m * 25 + players[p].Slot); // Damage (round-scoped)
                if (withRoundBools)
                {
                    // Alice: KAST every round, opening kill in round 1, opening death in round 2.
                    vec[colIdx[p][4]] = Flag(alice);
                    vec[colIdx[p][5]] = Flag(alice && rounds[m] == 1);
                    vec[colIdx[p][6]] = Flag(alice && rounds[m] == 2);
                }
            }

            snapshots[m] = vec;
        }

        RuleChainTimeline timeline = new([
            new RuleChainEvent("_chain_ace", 0, 1000, 0, "Alice"),
            new RuleChainEvent("_chain_clutch_1v3", 1, 2000, 0, "Alice"),
            new RuleChainEvent("_chain_game_scoped_thing", 2, 3000)
        ]);

        EvaluationResult result = new(timeline, snapshots, [], tracked, materialized, []);

        Dictionary<int, PlayerInfo> infos = new()
        {
            [0] = new PlayerInfo(0, "Alice", 0UL, 0, 2, false), // T
            [1] = new PlayerInfo(1, "Bob", 0UL, 1, 3, false) // CT
        };

        ParsedDemo demo = SyntheticParsedDemo.Create(
            [], [], infos, null,
            "de_test", 0, 1f / 64f,
            "t", "t", "csgo", 0,
            0, 0, "valve_demo_2",
            "", "", DemoProfile.Unknown);

        return (result, demo);
    }

    /// <summary>A hand-built keyed per-weapon table (KeyedStatsProjector schema).</summary>
    private static MetricTable BuildWeaponTable() =>
        new("player_kills_by_weapon",
            ["match_id", "map", "player_slot", "player_name", "team", "key"],
            ["kills_by_weapon"],
            [
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["player_slot"] = 0,
                        ["key"] = "ak47"
                    },
                    new Dictionary<string, object?>
                    {
                        ["kills_by_weapon"] = 3
                    }),
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["player_slot"] = 0,
                        ["key"] = "awp"
                    },
                    new Dictionary<string, object?>
                    {
                        ["kills_by_weapon"] = 7
                    }),
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["player_slot"] = 1,
                        ["key"] = "knife"
                    },
                    new Dictionary<string, object?>
                    {
                        ["kills_by_weapon"] = 1
                    })
            ]);

    private static StatsTabViewModel BuildUpdatedVm(bool withWeaponTable = false)
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo) = BuildScenario();
        vm.Update(result, demo, withWeaponTable ? [BuildWeaponTable()] : null);
        return vm;
    }

    // ── PlayerSlot threading (P0 linchpin) ────────────────────────────────────

    /// <summary>Every player row carries its player_slot; totals rows keep the -1 sentinel.</summary>
    [Test]
    public async Task StatsRows_CarryPlayerSlot()
    {
        StatsTabViewModel vm = BuildUpdatedVm();

        StatsRow alice = vm.GameRows.Single(r => r.PlayerName == "Alice");
        StatsRow bob = vm.GameRows.Single(r => r.PlayerName == "Bob");
        await Assert.That(alice.PlayerSlot).IsEqualTo(0);
        await Assert.That(bob.PlayerSlot).IsEqualTo(1);
        await Assert.That(vm.TeamSections[0].Totals.PlayerSlot).IsEqualTo(-1);
    }

    // ── Overlay life-cycle ────────────────────────────────────────────────────

    /// <summary>Opening from a player row targets that slot; totals rows are guarded out.</summary>
    [Test]
    public async Task OpenPlayerDetails_OpensForRowSlot_AndGuardsTotals()
    {
        StatsTabViewModel vm = BuildUpdatedVm();

        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Bob"));
        await Assert.That(vm.IsPlayerDetailsOpen).IsTrue();
        await Assert.That(vm.PlayerDetails!.PlayerSlot).IsEqualTo(1);
        await Assert.That(vm.PlayerDetails.PlayerName).IsEqualTo("Bob");
        await Assert.That(vm.PlayerDetails.TeamLabel).IsEqualTo("CT");

        vm.ClosePlayerDetailsCommand.Execute(null);
        await Assert.That(vm.IsPlayerDetailsOpen).IsFalse();
        await Assert.That(vm.PlayerDetails).IsNull();

        vm.OpenPlayerDetailsCommand.Execute(vm.TeamSections[0].Totals);
        await Assert.That(vm.IsPlayerDetailsOpen).IsFalse();
        await Assert.That(vm.PlayerDetails).IsNull();
    }

    /// <summary>Prev/next switch the slot and keep the active sub-section.</summary>
    [Test]
    public async Task SwitchPlayer_KeepsSection()
    {
        StatsTabViewModel vm = BuildUpdatedVm();
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Alice"));
        PlayerDetailsViewModel details = vm.PlayerDetails!;

        details.Section = DetailSection.Rounds;
        details.NextPlayerCommand.Execute(null);

        await Assert.That(details.PlayerName).IsEqualTo("Bob");
        await Assert.That(details.Section).IsEqualTo(DetailSection.Rounds);

        // Dropdown switching re-targets too (two players → prev wraps back to Alice).
        details.PrevPlayerCommand.Execute(null);
        await Assert.That(details.PlayerName).IsEqualTo("Alice");
        await Assert.That(details.SelectedPlayer!.Slot).IsEqualTo(0);
    }

    /// <summary>Update replaces every table → the overlay force-closes.</summary>
    [Test]
    public async Task Update_ClosesOpenOverlay()
    {
        StatsTabViewModel vm = BuildUpdatedVm();
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows[0]);
        await Assert.That(vm.IsPlayerDetailsOpen).IsTrue();

        (EvaluationResult result, ParsedDemo demo) = BuildScenario();
        vm.Update(result, demo);

        await Assert.That(vm.IsPlayerDetailsOpen).IsFalse();
        await Assert.That(vm.PlayerDetails).IsNull();
    }

    // ── Panel projections ─────────────────────────────────────────────────────

    /// <summary>Core strip, form geometry, achievements, and rounds table project the slot's data.</summary>
    [Test]
    public async Task Panels_ProjectSlotFilteredData()
    {
        StatsTabViewModel vm = BuildUpdatedVm();
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Alice"));
        PlayerDetailsViewModel details = vm.PlayerDetails!;

        // Core strip: TotalK (final snapshot: Alice = 20) and ADR tiles from the game row.
        await Assert.That(details.HasGameRow).IsTrue();
        StatTileItem kills = details.CoreTiles.Single(t => t.Label == "K");
        await Assert.That(kills.Value).IsEqualTo("20");

        // Form: two live rounds → 2 points / 2 damage bars; no HasKAST/FK columns → strips hidden.
        await Assert.That(details.Form.HasRounds).IsTrue();
        await Assert.That(details.Form.KillPoints.Count).IsEqualTo(2);
        await Assert.That(details.Form.DamageBars.Count).IsEqualTo(2);
        await Assert.That(details.Form.HasKast).IsFalse();
        await Assert.That(details.Form.HasDuels).IsFalse();

        // Achievements: only Alice's chains; the game-scoped chain never appears.
        await Assert.That(details.Achievements.Count).IsEqualTo(2);
        await Assert.That(details.Achievements.Select(a => a.Chain)).IsEquivalentTo(_expectedChains);
        await Assert.That(details.Achievements[1].IsClutch).IsTrue();

        // Rounds table: one row per live round for the slot.
        await Assert.That(details.RoundTableRows.Count).IsEqualTo(2);
        await Assert.That(details.RoundTableColumns.Select(c => c.Key)).IsEquivalentTo(_expectedRoundKeys);

        // Form deep-link: selecting a round jumps the section and highlights the row.
        details.SelectRoundFromForm(2);
        await Assert.That(details.Section).IsEqualTo(DetailSection.Rounds);
        await Assert.That(details.RoundTableRows.Single(r => r.Round == 2).IsSelected).IsTrue();

        // Bob's page has no achievements → empty state.
        details.NextPlayerCommand.Execute(null);
        await Assert.That(details.HasAchievements).IsFalse();
    }

    /// <summary>
    ///     Round-scoped BOOL columns (HasKAST / FK / FD) arrive as bool true, not numbers — the Opn
    ///     glyph and form strips must read them truthily. (Regression: AsDouble(bool) is 0, which
    ///     left Opn blank and duel ticks empty on real demos while synthetic numeric fixtures passed.)
    /// </summary>
    [Test]
    public async Task RoundBoolColumns_DriveOpnGlyph_AndFormStrips()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(true);
        vm.Update(result, demo);
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Alice"));
        PlayerDetailsViewModel details = vm.PlayerDetails!;

        // Rounds table: the synthetic Opn column materializes; ▲ round 1 (FK), ▼ round 2 (FD).
        int opnIdx = details.RoundTableColumns.ToList().FindIndex(c => c.Key == "__opn__");
        await Assert.That(opnIdx).IsGreaterThanOrEqualTo(0);
        StatCell r1Opn = details.RoundTableRows.Single(r => r.Round == 1).Cells[opnIdx];
        StatCell r2Opn = details.RoundTableRows.Single(r => r.Round == 2).Cells[opnIdx];
        await Assert.That(r1Opn.Raw?.ToString()).IsEqualTo("▲");
        await Assert.That(r2Opn.Raw?.ToString()).IsEqualTo("▼");

        // Form strips: KAST dots filled both rounds; duel ticks ▲ then ▼.
        await Assert.That(details.Form.HasKast).IsTrue();
        await Assert.That(details.Form.HasDuels).IsTrue();
        await Assert.That(details.Form.KastDots.All(d => d.Filled)).IsTrue();
        await Assert.That(details.Form.DuelTicks.Select(t => t.Glyph)).IsEquivalentTo(["▲", "▼"]);

        // Bob had no KAST / opening duels: hollow dots, no glyphs.
        details.NextPlayerCommand.Execute(null);
        await Assert.That(details.Form.KastDots.All(d => !d.Filled)).IsTrue();
        await Assert.That(details.Form.DuelTicks.All(t => t.Glyph == "·")).IsTrue();
        await Assert.That(details.RoundTableRows.All(r => r.Cells[opnIdx].Raw is null)).IsTrue();
    }

    /// <summary>Weapon panel: located by table Name, ValueColumns[0], sorted desc; both empty states.</summary>
    [Test]
    public async Task WeaponBreakdown_ReadsKeyedTable_AndEmptyStates()
    {
        // With the keyed table: Alice's bars sorted by value descending (awp 7 > ak47 3).
        StatsTabViewModel vm = BuildUpdatedVm(true);
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Alice"));
        WeaponBreakdownViewModel weapons = vm.PlayerDetails!.Weapons;

        await Assert.That(weapons.Bars.Select(b => b.Label)).IsEquivalentTo(_expectedWeapons);
        await Assert.That(weapons.Bars[0].ValueText).IsEqualTo("7");
        await Assert.That(weapons.Bars[0].BarWidth > weapons.Bars[1].BarWidth).IsTrue();

        // Damage metric selected but no damage table → per-metric empty message; toggle persists.
        weapons.SelectDamageCommand.Execute(null);
        await Assert.That(weapons.ShowEmpty).IsTrue();
        await Assert.That(weapons.EmptyMessage).Contains("No weapon damage");
        vm.PlayerDetails.NextPlayerCommand.Execute(null);
        await Assert.That(weapons.ShowDamage).IsTrue();

        // No keyed tables at all → the rules-not-loaded empty state.
        StatsTabViewModel bare = BuildUpdatedVm();
        bare.OpenPlayerDetailsCommand.Execute(bare.GameRows[0]);
        await Assert.That(bare.PlayerDetails!.Weapons.ShowEmpty).IsTrue();
        await Assert.That(bare.PlayerDetails.Weapons.EmptyMessage).Contains("weapon-stats rules");
    }

    /// <summary>Vision panel without a bake or computed stats lands on the unavailable state.</summary>
    [Test]
    public async Task Vision_NoBake_ShowsUnavailableMessage()
    {
        StatsTabViewModel vm = BuildUpdatedVm(); // collision resolver default → no bake for de_test
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows[0]);
        VisionViewModel vision = vm.PlayerDetails!.Vision;

        await Assert.That(vision.HasData).IsFalse();
        await Assert.That(vision.ShowCta).IsFalse();
        await Assert.That(vision.ShowUnavailable).IsTrue();
        await Assert.That(vision.UnavailableMessage).Contains("de_test");
    }

    // ── Headless render ───────────────────────────────────────────────────────

    /// <summary>The OPEN overlay renders a non-blank frame over the Stats tab.</summary>
    [Test]
    public async Task OpenOverlay_RendersNonBlankFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatsTabViewModel vm = BuildUpdatedVm(true);
            vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Alice"));

            StatsTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1200,
                Height = 700,
                Content = view
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            await Assert.That(frame).IsNotNull();

            string path = Path.Combine(HeadlessSession.ArtifactDir, "player-details.png");
            frame!.Save(path);
            Console.WriteLine(
                $"[capture] {path}  tiles={vm.PlayerDetails!.CoreTiles.Count} " +
                $"bars={vm.PlayerDetails.Weapons.Bars.Count} ach={vm.PlayerDetails.Achievements.Count}");

            await Assert.That(CountDistinctColors(frame)).IsGreaterThan(8);
        });
    }

    private static int CountDistinctColors(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4]; // BGRA8888
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        HashSet<int> colors = new();
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            colors.Add(buffer[i] | buffer[i + 1] << 8 | buffer[i + 2] << 16);
            if (colors.Count > 64)
            {
                break;
            }
        }

        return colors.Count;
    }

    // ── Synthetic evaluation fixture (mirrors StatsTabTests.BuildScenario) ────

    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }

    private sealed class RoundStubNode(string name) : StateNode, IRoundScopedNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;

        public void Reset()
        {
        }
    }

    // Mirrors the engine's round-scoped bool columns (has_kast / opening_kill_round): the
    // projector emits BOOL TRUE for an active BoolNode, not a number or display string.
    private sealed class RoundBoolStubNode(string name) : RoundScopedBoolNode(false)
    {
        public override string Name { get; } = name;
    }
}

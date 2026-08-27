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
using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Stats tab tests (release plan P1-3.1/4.1) — fully synthetic per the harness practice
///     (Playback2DHeadlessSmokeTests caveat: the headless session can't complete the async demo-load
///     path, so the VM is fed a hand-built <see cref="EvaluationResult" /> directly via
///     <see cref="StatsTabViewModel.Update" />). Covers: scoreboard rows + default kills-desc sort,
///     round browser filtering, column-follows-rules behavior, folder export writing both tables,
///     and a headless Skia render of the view producing a non-blank frame.
/// </summary>
[NotInParallel]
[Category("Render")]
public class StatsTabTests
{
    private static readonly string[] _expectedColumns = ["TotalK", "ADR"];
    private static readonly int[] _expectedRounds = [1, 2];

    private static NodeSnapshot Num(int value) => new(true, value.ToString(CultureInfo.InvariantCulture), value);

    /// <summary>
    ///     Two players (Alice slot 0 / team T, Bob slot 1 / team CT); columns: game-lifetime
    ///     [TotalK, ADR] (scoreboard) + round-scoped [Kills] (round browser); three messages
    ///     spanning rounds 1..2; values rise per message so final ≠ per-round samples.
    /// </summary>
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario()
    {
        StubNode roundNode = new("RoundNumber");
        List<StateNode> tracked = [roundNode];

        (int Slot, string Name)[] players = [(0, "Alice"), (1, "Bob")];
        (string Label, bool RoundScoped)[] columns = [("TotalK", false), ("ADR", false), ("Kills", true)];

        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = new();
        List<List<int>> colIdx = new();
        foreach ((int slot, string name) in players)
        {
            List<PerPlayerColumnAssignment> assignments = new();
            List<int> indices = new();
            List<StateNode> nodes = new();
            foreach ((string column, bool roundScoped) in columns)
            {
                StateNode node = roundScoped
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

        // rounds per message: 1, 1, 2. TotalK/Kills = msg*10 + slot; ADR fixed per player.
        int[] rounds = [1, 1, 2];
        NodeSnapshot[][] snapshots = new NodeSnapshot[rounds.Length][];
        for (int m = 0; m < rounds.Length; m++)
        {
            NodeSnapshot[] vec = new NodeSnapshot[tracked.Count];
            vec[0] = Num(rounds[m]);
            for (int p = 0; p < players.Length; p++)
            {
                vec[colIdx[p][0]] = Num(m * 10 + players[p].Slot); // TotalK
                vec[colIdx[p][1]] = Num(50 + players[p].Slot); // ADR
                vec[colIdx[p][2]] = Num(m * 10 + players[p].Slot); // Kills (round-scoped)
            }

            snapshots[m] = vec;
        }

        EvaluationResult result = new(
            new RuleChainTimeline([]), snapshots, [], tracked, materialized, []);

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

    private static StatsTabViewModel BuildUpdatedVm()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo) = BuildScenario();
        vm.Update(result, demo);
        return vm;
    }

    // ── VM behavior ───────────────────────────────────────────────────────────

    /// <summary>Scoreboard has one row per player, defaulting to kills-descending order.</summary>
    [Test]
    public async Task Update_BuildsScoreboard_SortedByKillsDescending()
    {
        StatsTabViewModel vm = BuildUpdatedVm();

        await Assert.That(vm.HasStats).IsTrue();
        // Columns are in catalogue display order now — assert membership, then index by label.
        await Assert.That(vm.Columns.Select(c => c.Label).Order()).IsEquivalentTo(_expectedColumns.Order());
        await Assert.That(vm.GameRows.Count).IsEqualTo(2);
        // Final snapshot kills: Alice 20, Bob 21 → Bob first under kills-desc.
        int killsIdx = vm.Columns.Single(c => c.Label == "TotalK").Index;
        await Assert.That(vm.GameRows[0].PlayerName).IsEqualTo("Bob");
        await Assert.That(vm.GameRows[0].Cells[killsIdx].Display).IsEqualTo("21");
        await Assert.That(vm.GameRows[0].TeamLabel).IsEqualTo("CT");
        // The scoreboard sections: CT (Bob) and T (Alice), each with a totals row summing kills.
        await Assert.That(vm.TeamSections.Count).IsEqualTo(2);
        await Assert.That(vm.TeamSections[0].SideLabel).IsEqualTo("CT");
        await Assert.That(vm.TeamSections[0].Totals.Cells[killsIdx].Display).IsEqualTo("21");
    }

    /// <summary>Round browser lists live rounds and filters rows to the selected one.</summary>
    [Test]
    public async Task RoundBrowser_FiltersRowsToSelectedRound()
    {
        StatsTabViewModel vm = BuildUpdatedVm();

        await Assert.That(vm.Rounds).IsEquivalentTo(_expectedRounds);

        vm.IsRoundView = true;
        vm.SelectedRound = 1;
        // Round 1's last snapshot is message 1 → Alice kills 10, Bob 11.
        await Assert.That(vm.CurrentRows.Count).IsEqualTo(2);
        int killsIdx = vm.RoundColumns.Single(c => c.Label == "Kills").Index;
        StatsRow alice = vm.CurrentRows.Single(r => r.PlayerName == "Alice");
        await Assert.That(alice.Cells[killsIdx].Display).IsEqualTo("10");

        vm.SelectedRound = 2;
        StatsRow alice2 = vm.CurrentRows.Single(r => r.PlayerName == "Alice");
        await Assert.That(alice2.Cells[killsIdx].Display).IsEqualTo("20");
    }

    /// <summary>Header sort command re-orders; a second click flips direction.</summary>
    [Test]
    public async Task SortByColumn_TogglesDirection()
    {
        StatsTabViewModel vm = BuildUpdatedVm();
        StatColumn kills = vm.Columns.Single(c => c.Label == "TotalK");

        vm.SortByColumnCommand.Execute(kills); // same column → flips to ascending
        await Assert.That(vm.GameRows[0].PlayerName).IsEqualTo("Alice");

        vm.SortByColumnCommand.Execute(kills); // flips back to descending
        await Assert.That(vm.GameRows[0].PlayerName).IsEqualTo("Bob");
    }

    /// <summary>Export writes one file per table, named by table id (the bench-parity contract).</summary>
    [Test]
    public async Task ExportTo_WritesBothTables()
    {
        StatsTabViewModel vm = BuildUpdatedVm();
        string dir = Directory.CreateTempSubdirectory("demoviewer-stats-export-").FullName;
        try
        {
            string message = vm.ExportTo(dir, "csv");

            await Assert.That(message).Contains("Exported 3 table(s)");
            string gamePath = Path.Combine(dir, "player_game_stats.csv");
            string roundPath = Path.Combine(dir, "player_round_stats.csv");
            await Assert.That(File.Exists(gamePath)).IsTrue();
            await Assert.That(File.Exists(roundPath)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "rule_chain_events.csv"))).IsTrue();

            string game = await File.ReadAllTextAsync(gamePath);
            await Assert.That(game).Contains("player_name");
            await Assert.That(game).Contains("Alice");
            await Assert.That(game).Contains("match.dem"); // match_id flowed from the demo path
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ── Headless render ───────────────────────────────────────────────────────

    /// <summary>The populated view renders a non-blank frame in the headless Skia session.</summary>
    [Test]
    public async Task PopulatedView_RendersNonBlankFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatsTabViewModel vm = BuildUpdatedVm();

            StatsTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1100,
                Height = 600,
                Content = view
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            await Assert.That(frame).IsNotNull();

            string path = Path.Combine(HeadlessSession.ArtifactDir, "stats-tab.png");
            frame!.Save(path);
            Console.WriteLine($"[capture] {path}  rows={vm.GameRows.Count} cols={vm.Columns.Count}");

            // Non-blank: the frame must contain a meaningful number of distinct colors (background +
            // header + row text). A blank/unbound view renders 1-2.
            int distinct = CountDistinctColors(frame);
            await Assert.That(distinct).IsGreaterThan(8);

            // Second capture: the Rounds view (its own column set + header — the alignment surface
            // the misalignment report was about).
            vm.IsRoundView = true;
            vm.SelectedRound = 1;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? roundsFrame = window.CaptureRenderedFrame();
            await Assert.That(roundsFrame).IsNotNull();
            string roundsPath = Path.Combine(HeadlessSession.ArtifactDir, "stats-rounds.png");
            roundsFrame!.Save(roundsPath);
            Console.WriteLine($"[capture] {roundsPath}  rows={vm.RoundRows.Count} cols={vm.RoundColumns.Count}");
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
                break; // enough evidence; no need to scan the whole frame
            }
        }

        return colors.Count;
    }

    // ── Synthetic evaluation fixture (mirrors PlayerGameStatsProjectorTests) ──

    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }

    /// <summary>Round-scoped stub — lands in the per-round table, excluded from the scoreboard.</summary>
    private sealed class RoundStubNode(string name) : StateNode, IRoundScopedNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;

        public void Reset()
        {
        }
    }
}

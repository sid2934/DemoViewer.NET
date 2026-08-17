#region

using System.Globalization;
using Cs2DemoKit.Analysis;
using Cs2DemoKit.Analysis.Abstractions;
using Cs2DemoKit.Analysis.Visibility;
using Cs2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Stats-tab 3D visibility tests (deferred-features plan F4). The gating tests are fully
///     synthetic (injected collision resolver, hand-built <see cref="EvaluationResult" /> — the
///     StatsTabTests harness practice); the single demo-gated smoke drives the real pipeline
///     (locator → <c>VisibilityEngine</c> → <c>VisibilityAnalyzer.Analyze</c> → projector → VM
///     tables) end-to-end through <see cref="StatsTabViewModel.ComputeVisibilityCommand" /> on a
///     baked map, skipping when the demo or bake is absent.
/// </summary>
[NotInParallel]
public class StatsVisibilityTests
{
    private const string Dust2Demo = "vitality-vs-fut-m2-dust2.dem";

    /// <summary>One player (Alice slot 0 / T), one Kills column, one snapshot — enough for HasStats.</summary>
    private static EvaluationResult BuildResult()
    {
        StubNode node = new("Alice_Kills");
        List<StateNode> tracked = [node];
        PerPlayerNodeTemplate.MaterializedPlayer alice =
            new(0, "Alice", [node], [], [new PerPlayerColumnAssignment(node, "Kills")], []);

        NodeSnapshot[][] snapshots = [[new NodeSnapshot(true, "5", 5)]];
        return new EvaluationResult(new RuleChainTimeline([]), snapshots, [], tracked, [alice], []);
    }

    private static ParsedDemo BuildDemo(string mapName) => new(
        [], [],
        new Dictionary<int, PlayerInfo>
        {
            [0] = new(0, "Alice", 0UL, 0, 2, false)
        },
        null, mapName, 0, 1f / 64f,
        "t", "t", "csgo", 0,
        0, 0, "valve_demo_2",
        "", "", DemoProfile.Unknown);

    // ── Gating (synthetic; no demo) ────────────────────────────────────────────

    /// <summary>Bake resolves → compute action available, no status noise.</summary>
    [Test]
    public async Task Update_WithResolvableBake_EnablesComputeAction()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem", _ => "/fake/collision.tris");
        vm.Update(BuildResult(), BuildDemo("de_baked"));

        await Assert.That(vm.HasStats).IsTrue();
        await Assert.That(vm.CanComputeVisibility).IsTrue();
        await Assert.That(vm.HasVisibilityStats).IsFalse(); // nothing computed yet
        await Assert.That(vm.StatusMessage).IsEqualTo("");
    }

    /// <summary>No bake → action gated off, one info line explains why (F4 acceptance).</summary>
    [Test]
    public async Task Update_WithoutBake_HidesComputeAction_AndExplains()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem", _ => null);
        vm.Update(BuildResult(), BuildDemo("de_unbaked"));

        await Assert.That(vm.HasStats).IsTrue();
        await Assert.That(vm.CanComputeVisibility).IsFalse();
        await Assert.That(vm.StatusMessage).Contains("No collision bake for de_unbaked");
    }

    /// <summary>The visibility view toggle is mutually exclusive with the other three views.</summary>
    [Test]
    public async Task VisibilityView_IsMutuallyExclusive_WithOtherViews()
    {
        StatsTabViewModel vm = new(null, () => null, _ => null);
        vm.Update(BuildResult(), BuildDemo("de_unbaked"));

        vm.IsVisibilityView = true;
        await Assert.That(vm.IsMatchView).IsFalse();
        await Assert.That(vm.IsTableView).IsFalse();

        vm.IsHighlightsView = true;
        await Assert.That(vm.IsVisibilityView).IsFalse();

        vm.IsVisibilityView = true;
        vm.IsRoundView = true;
        await Assert.That(vm.IsVisibilityView).IsFalse();

        vm.IsVisibilityView = true;
        vm.ShowMatchViewCommand.Execute(null);
        await Assert.That(vm.IsVisibilityView).IsFalse();
        await Assert.That(vm.IsMatchView).IsTrue();
    }

    // ── Demo-gated smoke (the ONE demo-parsing test) ───────────────────────────

    /// <summary>
    ///     End-to-end on a baked map: compute → both visibility tables join the export with
    ///     non-zero seconds, and the VM switches to the Visibility view. Skips without the dust2
    ///     demo + bake.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task ComputeVisibility_OnBakedMap_ProducesNonZeroTables()
    {
        string? tris = CollisionAssetLocator.FindCollisionTris("de_dust2");
        string? demoPath = DemoTestHelper.FindDemoPath(Dust2Demo);
        if (tris is null || demoPath is null)
        {
            throw new SkipTestException("no de_dust2 demo + baked collision");
        }

        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);

        // Window the LOS replay to a representative mid-match quarter (frames N/4 → N/2). The
        // full-demo compute took ~108 s and dominated the suite; the quarter slice runs the
        // IDENTICAL end-to-end path (locator → engine → Analyze → projector → VM tables) and,
        // being mid-match with live engaged players, keeps every non-zero-table assertion
        // meaningful. (P5 of the app-suite speed plan — windowed visibility compute.)
        int quarter = demo.Frames.Count / 4;
        VisibilityAnalyzer.Options window = new(StartFrame: quarter, EndFrame: quarter * 2);

        StatsTabViewModel vm = new(null, () => demoPath, visibilityOptions: window);
        vm.Update(BuildResult(), demo); // synthetic scoreboard + REAL frames/map for the replay

        await Assert.That(vm.CanComputeVisibility).IsTrue();
        await vm.ComputeVisibilityCommand.ExecuteAsync(null);

        await Assert.That(vm.HasVisibilityStats).IsTrue();
        await Assert.That(vm.IsVisibilityView).IsTrue();
        await Assert.That(vm.IsComputingVisibility).IsFalse();
        await Assert.That(vm.VisibilityRows.Count).IsGreaterThan(0);
        await Assert.That(vm.VisibilityRows.Max(r => r.ExposedSec)).IsGreaterThan(0);
        // could-see ⊆ exposed (union accumulators preserve the pair invariant).
        foreach (VisibilityRow row in vm.VisibilityRows)
        {
            await Assert.That(row.CouldSeeSec).IsLessThanOrEqualTo(row.ExposedSec + 1e-6);
            await Assert.That(row.ExposedShare).IsGreaterThanOrEqualTo(0);
            await Assert.That(row.ExposedShare).IsLessThanOrEqualTo(1 + 1e-6);
        }

        // Both tables joined the export (3 built-ins + 2 visibility).
        await Assert.That(vm.ExportTables.Count).IsEqualTo(5);
        await Assert.That(vm.ExportTables.Any(t => t.Name == "player_visibility_stats")).IsTrue();
        await Assert.That(vm.ExportTables.Any(t => t.Name == "visibility_pairs")).IsTrue();

        double topExposed = vm.VisibilityRows.Max(r => r.ExposedSec);
        Console.WriteLine($"[f4smoke] rows={vm.VisibilityRows.Count} topExposed={topExposed.ToString("F1", CultureInfo.InvariantCulture)}s");
    }

    // ── Synthetic evaluation fixture (minimal StatsTabTests mirror) ───────────

    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }
}

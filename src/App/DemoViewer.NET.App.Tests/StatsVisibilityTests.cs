#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Stats-tab 3D visibility tests (deferred-features plan F4). The gating tests are fully
///     synthetic (injected collision resolver, hand-built <see cref="EvaluationResult" />, the
///     StatsTabTests harness practice); the single demo-gated smoke drives the real pipeline
///     (resolver → <c>VisibilityEngine</c> → <c>VisibilityAnalyzer.Analyze</c> → projector → VM
///     tables) end-to-end through <see cref="StatsTabViewModel.ComputeVisibilityCommand" /> on a
///     baked map, skipping when the demo or bake is absent.
/// </summary>
[NotInParallel]
public class StatsVisibilityTests
{
    private const string Dust2Demo = "vitality-vs-fut-m2-dust2.dem";

    /// <summary>
    ///     One player (Alice slot 0 / T), one snapshot, one node per named column: enough for HasStats.
    ///     <para>
    ///         TTS rides along by default because the sight notice is about COLUMNS. It fires only where
    ///         the run carries one that needs collision geometry, so a Kills-only evaluation has nothing
    ///         for it to explain; name the columns to get an evaluation without one.
    ///     </para>
    /// </summary>
    private static EvaluationResult BuildResult(params string[] columns)
    {
        if (columns.Length == 0)
        {
            columns = ["Kills", "TTS"];
        }

        List<StateNode> tracked = [];
        List<StateNode> nodes = [];
        List<PerPlayerColumnAssignment> assignments = [];
        List<NodeSnapshot> vector = [];
        foreach (string column in columns)
        {
            StubNode node = new($"Alice_{column}");
            tracked.Add(node);
            nodes.Add(node);
            assignments.Add(new PerPlayerColumnAssignment(node, column));
            vector.Add(new NodeSnapshot(true, "5", 5));
        }

        PerPlayerNodeTemplate.MaterializedPlayer alice = new(0, "Alice", nodes, [], assignments, []);
        NodeSnapshot[][] snapshots = [vector.ToArray()];
        return new EvaluationResult(new RuleChainTimeline([]), snapshots, [], tracked, [alice], []);
    }

    private static ParsedDemo BuildDemo(string mapName) => SyntheticParsedDemo.Create(
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

    /// <summary>
    ///     No bake: the compute action is gated off and the board explains why. The explanation used to
    ///     sit in the toolbar StatusMessage beside that button, which is why it read as being about the
    ///     button rather than about the columns; it now lives in the board notice. This fixture carries a
    ///     column that needs geometry but declares no anchor count at all, so it also pins the fallback:
    ///     with nothing in the data to judge by, the missing bake file is the answer.
    /// </summary>
    [Test]
    public async Task Update_WithoutBake_HidesComputeAction_AndExplains()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem", _ => null);
        vm.Update(BuildResult(), BuildDemo("de_unbaked"));

        await Assert.That(vm.HasStats).IsTrue();
        await Assert.That(vm.CanComputeVisibility).IsFalse();
        await Assert.That(vm.SightUnavailable).IsTrue();
        await Assert.That(vm.HasSightNotice).IsTrue();
        await Assert.That(vm.SightNotice).Contains("de_unbaked");
    }

    /// <summary>
    ///     An evaluation with no line-of-sight column raises no notice, bake or no bake. The band names
    ///     seventeen columns and explains the cells they would have filled; over a run carrying none of
    ///     them it is a paragraph about columns the reader cannot see, and the missing bake file is not
    ///     evidence about anything on that board.
    /// </summary>
    [Test]
    public async Task Update_WithNoSightColumns_RaisesNoNotice()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem", _ => null);
        vm.Update(BuildResult("Kills"), BuildDemo("de_unbaked"));

        await Assert.That(vm.HasStats).IsTrue();
        await Assert.That(vm.CanComputeVisibility).IsFalse();
        await Assert.That(vm.SightUnavailable).IsFalse();
        await Assert.That(vm.HasSightNotice).IsFalse();
        await Assert.That(vm.SightNotice).IsEqualTo("");
    }

    /// <summary>
    ///     The band is gated like the chip rail it sits under, not on content alone. It explains cells in
    ///     the stat tables, and the highlights log, the vision table and the keyed extra tables have no
    ///     such cell for it to be about.
    /// </summary>
    [Test]
    public async Task SightNotice_StaysOverTheStatTables()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem", _ => null);
        vm.Update(BuildResult(), BuildDemo("de_unbaked"));

        await Assert.That(vm.IsSightNoticeVisible).IsTrue();

        vm.IsHighlightsView = true;
        await Assert.That(vm.HasSightNotice).IsTrue(); // the notice still stands
        await Assert.That(vm.IsSightNoticeVisible).IsFalse(); // it is just not about this view

        vm.IsVisibilityView = true;
        await Assert.That(vm.IsSightNoticeVisible).IsFalse();

        vm.ShowMatchViewCommand.Execute(null);
        await Assert.That(vm.IsSightNoticeVisible).IsTrue();
    }

    /// <summary>
    ///     Closing the demo retracts the band. <c>SightNotice</c> is a plain getter over a field, so
    ///     clearing the field without announcing it leaves the amber band up over an empty tab, still
    ///     naming the map of the demo that was just closed.
    /// </summary>
    [Test]
    public async Task ResetForDemoUnload_RetractsTheSightNotice()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem", _ => null);
        vm.Update(BuildResult(), BuildDemo("de_unbaked"));
        await Assert.That(vm.HasSightNotice).IsTrue();

        List<string> announced = [];
        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");
        vm.ResetForDemoUnload();

        await Assert.That(vm.SightNotice).IsEqualTo("");
        await Assert.That(vm.HasSightNotice).IsFalse();
        await Assert.That(vm.SightUnavailable).IsFalse();
        await Assert.That(announced).Contains(nameof(StatsTabViewModel.SightNotice));
        await Assert.That(announced).Contains(nameof(StatsTabViewModel.HasSightNotice));
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
        // Through CollisionSoup, which is what the VM's own default resolver goes through: the pack
        // ships the bake gzipped and the engine's locator only knows the uncompressed name, so probing
        // for that alone returned null and skipped this test on a machine that has the geometry.
        string? tris = CollisionSoup.Find("de_dust2");
        string? demoPath = DemoTestHelper.FindDemoPath(Dust2Demo);
        if (tris is null || demoPath is null)
        {
            throw new SkipTestException("no de_dust2 demo + baked collision");
        }

        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);

        // Window the LOS replay to a representative mid-match quarter (frames N/4 → N/2). The
        // full-demo compute took ~108 s and dominated the suite; the quarter slice runs the
        // IDENTICAL end-to-end path (resolver → engine → Analyze → projector → VM tables) and,
        // being mid-match with live engaged players, keeps every non-zero-table assertion
        // meaningful. (P5 of the app-suite speed plan: windowed visibility compute.)
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

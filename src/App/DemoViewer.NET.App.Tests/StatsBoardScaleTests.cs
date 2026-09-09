#region

using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The scoreboard's per-column scales (docs/ui/stats-components.md §9). A ten-player fixture with a
///     realistic spread, because the questions these scales exist to answer are about how a COLUMN reads,
///     and the two-player fixture in <see cref="StatsTabTests" /> cannot show that.
///     <para>
///         The behaviour under test is the hybrid: the bar domain comes from the ten players on screen,
///         the tint comes from a fixed benchmark. Those are different facts and the tests below pin them
///         apart, because a regression that collapsed one onto the other would still look plausible.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class StatsBoardScaleTests
{
    /// <summary>Name, team wire value, then TotalK / TotalD / TotalA / ADR / KAST% / HLTV / KD.</summary>
    private static readonly (string Name, int Team, double[] Values)[] _roster =
    [
        ("Alice", 3, [24, 14, 5, 92, 78, 1.43, 1.71]),
        ("Bravo", 3, [19, 16, 7, 85, 74, 1.18, 1.19]),
        ("Charlie", 3, [17, 17, 4, 78, 71, 1.05, 1.00]),
        ("Delta", 3, [14, 18, 9, 71, 69, 0.94, 0.78]),
        ("Echo", 3, [11, 20, 3, 57, 61, 0.76, 0.55]),
        ("Foxtrot", 2, [21, 15, 6, 88, 76, 1.30, 1.40]),
        ("Golf", 2, [18, 17, 8, 81, 73, 1.12, 1.06]),
        ("Hotel", 2, [16, 18, 5, 75, 70, 1.01, 0.89]),
        ("India", 2, [13, 19, 6, 68, 66, 0.88, 0.68]),
        ("Juliet", 2, [9, 21, 2, 46, 58, 0.65, 0.43])
    ];

    private static readonly string[] _columns = ["TotalK", "TotalD", "TotalA", "ADR", "KAST%", "HLTV", "KD"];

    /// <summary>
    ///     Round-win columns, appended per team so <c>ComputeTeamScores</c> has something to sum. They
    ///     live in the RoundWins group and so never appear under the default Core category; they exist to
    ///     drive the scoreline, not to be looked at.
    /// </summary>
    private static readonly string[] _scoreColumns = ["CTW", "TW"];

    private static readonly string[] _expectedPodium = ["Alice", "Foxtrot", "Bravo"];

    /// <summary>
    ///     Columns the non-table category boards read. Values are derived from the roster index so the
    ///     spread is deterministic and varied: someone tops each board, someone bottoms it, and the
    ///     duel board gets both a positive and a negative net plus a low-volume row.
    /// </summary>
    private static readonly string[] _boardColumns =
    [
        "Flash", "Smokes", "HE", "Molly", "EFlash", "AvgBlind",
        "TotalFK", "TotalFD",
        "2K", "3K", "4K", "5K",
        "Rifle", "AWP", "SMG", "Pistol", "Knife"
    ];

    /// <summary>
    ///     User-authored columns: not in the catalogue, so they fall through to StatGroup.Other and get
    ///     the fallback width. Enough of them, wide enough, to overflow the table and exercise the
    ///     horizontal scroll that Other is the page most likely to need.
    /// </summary>
    private static readonly string[] _otherColumns =
    [
        "custom_entry_success_rate", "custom_trade_window_ms", "custom_util_efficiency",
        "custom_crosshair_placement", "custom_spray_control_index", "custom_economy_discipline",
        "custom_rotation_speed", "custom_site_hold_rating"
    ];

    private static double BoardValue(string column, int i) => column switch
    {
        "Flash" => 14 - i,
        "Smokes" => 6 + (i % 4),
        "HE" => 5 + (i % 3),
        "Molly" => 3 + (i % 2),
        "EFlash" => 18 - i,
        "AvgBlind" => 3.1 - (i * 0.12),
        // Falls from 9 to 0 while deaths climb from 2 to 11, so the board shows a clear crossover.
        // Index 7 is deliberately a near-abstainer: three duels cannot support a rate, and the board
        // has to be seen dimming one.
        "TotalFK" => i == 7 ? 2 : Math.Max(0, 9 - i),
        "TotalFD" => i == 7 ? 1 : 2 + i,
        "2K" => Math.Max(0, 6 - (i / 2)),
        "3K" => Math.Max(0, 3 - (i / 3)),
        "4K" => i == 0 ? 2 : i == 4 ? 1 : 0,
        "5K" => i == 0 ? 1 : 0,
        "Rifle" => 15 - i,
        "AWP" => i < 2 ? 9 - (i * 3) : 0,
        "SMG" => i % 3,
        "Pistol" => 2 + (i % 4),
        "Knife" => i == 3 ? 1 : 0,
        _ => 0
    };

    // ── Catalogue wiring ──────────────────────────────────────────────────────

    /// <summary>
    ///     The specs actually reach the catalogue. This exists because the failure mode when they do not
    ///     is SILENT: C# runs static field initializers in declaration order, so a spec declared below
    ///     <c>_byKey</c> is still null while <c>BuildCatalogue()</c> reads it. Every column then quietly
    ///     gets no scale and the board renders exactly as it did before the feature landed. That is
    ///     precisely what happened during this work, and nothing but this test would have said so.
    /// </summary>
    [Test]
    public async Task Catalogue_CarriesTheScaleSpecs()
    {
        foreach (string column in _columns)
        {
            await Assert.That(ColumnCatalogue.Resolve(column).Scale).IsNotNull();
        }

        // An absolute-colour column must carry its benchmark, not merely a polarity.
        StatScaleSpec hltv = ColumnCatalogue.Resolve("HLTV").Scale!;
        await Assert.That(hltv.ColourMin).IsEqualTo(0.40);
        await Assert.That(hltv.ColourMax).IsEqualTo(1.80);
        await Assert.That(hltv.NeutralLow).IsEqualTo(0.95);

        // The three we deliberately refuse to judge keep a bar and lose the tint.
        await Assert.That(ColumnCatalogue.Resolve("HS%").Scale!.Polarity).IsEqualTo(StatPolarity.Neutral);
        await Assert.That(ColumnCatalogue.Resolve("Surv%").Scale!.Polarity).IsEqualTo(StatPolarity.Neutral);

        // And the gated one carries its gate.
        StatScaleSpec duel = ColumnCatalogue.Resolve("Duel%").Scale!;
        await Assert.That(duel.ColourGateMinimum).IsEqualTo(8);
        await Assert.That(duel.ColourGateColumns).IsNotNull();
    }

    // ── The hybrid contract ───────────────────────────────────────────────────

    /// <summary>
    ///     The bar spans the players on screen. Best in the lobby fills it, worst empties it, whatever the
    ///     absolute numbers happen to be.
    /// </summary>
    [Test]
    public async Task Bar_SpansThePlayersOnScreen()
    {
        StatsTabViewModel vm = BuildVm();
        StatCell best = Cell(vm, "Alice", "HLTV");
        StatCell worst = Cell(vm, "Juliet", "HLTV");

        await Assert.That(best.Scale).IsNotNull();
        await Assert.That(best.Scale!.Fraction(best.Numeric!.Value)).IsEqualTo(1);
        await Assert.That(worst.Scale!.Fraction(worst.Numeric!.Value)).IsEqualTo(0);
    }

    /// <summary>
    ///     The tint does NOT span the players on screen. Alice tops the lobby at 1.43 and is strongly
    ///     good; Charlie tops nothing and sits inside the 0.95-1.05 band, so he is uncoloured despite
    ///     being mid-table. If the tint were peer-relative Charlie would be tinted for being average.
    /// </summary>
    [Test]
    public async Task Tint_ComesFromTheBenchmarkNotThePeers()
    {
        StatsTabViewModel vm = BuildVm();
        StatCell alice = Cell(vm, "Alice", "HLTV");
        StatCell charlie = Cell(vm, "Charlie", "HLTV");
        StatCell juliet = Cell(vm, "Juliet", "HLTV");

        await Assert.That(alice.Scale!.Sentiment(alice.Numeric!.Value)).IsGreaterThan(0);
        await Assert.That(charlie.Scale!.Sentiment(charlie.Numeric!.Value)).IsEqualTo(0);
        await Assert.That(juliet.Scale!.Sentiment(juliet.Numeric!.Value)).IsLessThan(0);

        // The colour extent is the benchmark's, not the lobby's.
        await Assert.That(alice.Scale.EffectiveColourMin).IsEqualTo(0.40);
        await Assert.That(alice.Scale.EffectiveColourMax).IsEqualTo(1.80);
    }

    /// <summary>
    ///     A deaths column draws its longest bar for the MOST deaths and tints it bad. Getting this
    ///     backwards produces a board that looks fine and reads inverted.
    /// </summary>
    [Test]
    public async Task LowerIsBetterColumn_KeepsTheBarAndFlipsTheTint()
    {
        StatsTabViewModel vm = BuildVm();
        StatCell most = Cell(vm, "Juliet", "TotalD");
        StatCell fewest = Cell(vm, "Alice", "TotalD");

        await Assert.That(most.Scale!.Fraction(most.Numeric!.Value)).IsEqualTo(1);
        await Assert.That(most.Scale.Sentiment(most.Numeric.Value)).IsLessThan(0);
        await Assert.That(fewest.Scale!.Fraction(fewest.Numeric!.Value)).IsEqualTo(0);
        await Assert.That(fewest.Scale.Sentiment(fewest.Numeric.Value)).IsGreaterThan(0);
    }

    /// <summary>The leader marker follows polarity: fewest deaths, not most.</summary>
    [Test]
    public async Task Leader_FollowsPolarity()
    {
        StatsTabViewModel vm = BuildVm();

        await Assert.That(Cell(vm, "Alice", "TotalK").IsLeader).IsTrue();
        await Assert.That(Cell(vm, "Juliet", "TotalK").IsLeader).IsFalse();

        await Assert.That(Cell(vm, "Alice", "TotalD").IsLeader).IsTrue();
        await Assert.That(Cell(vm, "Juliet", "TotalD").IsLeader).IsFalse();
    }

    /// <summary>
    ///     Peers are the whole lobby, both teams. The enemy top-fragger must be measured against ours, or
    ///     each table silently becomes its own little world and cross-team comparison is lost.
    /// </summary>
    [Test]
    public async Task Peers_AreTheWholeLobby_NotOneTeam()
    {
        StatsTabViewModel vm = BuildVm();
        // Foxtrot tops the enemy team at 88 but Alice tops the lobby at 92, so his bar is short of full.
        StatCell foxtrot = Cell(vm, "Foxtrot", "ADR");
        await Assert.That(foxtrot.Scale!.Max).IsEqualTo(92);
        await Assert.That(foxtrot.Scale.Fraction(foxtrot.Numeric!.Value)).IsLessThan(1);
        await Assert.That(foxtrot.Scale.Fraction(foxtrot.Numeric.Value)).IsGreaterThan(0.8);
    }

    /// <summary>
    ///     Totals rows render but must not carry a player's scale, or their bar clamps to full and the
    ///     row reads as a player who beat everyone.
    /// </summary>
    [Test]
    public async Task TotalsRow_HasNoScale()
    {
        StatsTabViewModel vm = BuildVm();

        foreach (TeamSection section in vm.TeamSections)
        {
            await Assert.That(section.Totals.IsTotals).IsTrue();
            foreach (StatCell cell in section.Totals.Cells)
            {
                await Assert.That(cell.Scale).IsNull();
                await Assert.That(cell.IsScaled).IsFalse();
            }
        }
    }

    /// <summary>An uncatalogued column keeps exactly today's behaviour: text, no bar, no tint.</summary>
    [Test]
    public async Task ColumnWithNoSpec_StaysPlain()
    {
        StatsTabViewModel vm = BuildVm();
        // TotalA IS specced, so use it as the control, then prove the catalogue gates the rest.
        await Assert.That(Cell(vm, "Alice", "TotalA").Scale).IsNotNull();
        await Assert.That(ColumnCatalogue.Resolve("Knife").Scale).IsNull();
        await Assert.That(ColumnCatalogue.Resolve("__not_a_column__").Scale).IsNull();
    }

    /// <summary>
    ///     Sorting must not disturb the scales. The value multiset is unchanged, so every bar and tint has
    ///     to survive a sort click identically.
    /// </summary>
    [Test]
    public async Task Sorting_LeavesTheScalesAlone()
    {
        StatsTabViewModel vm = BuildVm();
        StatScale before = Cell(vm, "Alice", "ADR").Scale!;

        vm.SortByColumnCommand.Execute(vm.Columns.Single(c => c.Label == "ADR"));

        await Assert.That(Cell(vm, "Alice", "ADR").Scale).IsEqualTo(before);
    }

    // ── Team outcome ──────────────────────────────────────────────────────────

    [Test]
    public async Task Outcome_IsDerivedFromTheScoreline()
    {
        StatsTabViewModel vm = BuildVm(13, 9);

        TeamSection ct = vm.TeamSections.Single(t => t.IsCt);
        TeamSection t = vm.TeamSections.Single(x => !x.IsCt);

        await Assert.That(ct.Score).IsEqualTo(13);
        await Assert.That(ct.Outcome).IsEqualTo(TeamOutcome.Win);
        await Assert.That(t.Outcome).IsEqualTo(TeamOutcome.Loss);
    }

    /// <summary>
    ///     The gate that matters. A demo cut at the buzzer can lose the winner's final round, leaving a
    ///     scoreline that reads as a tie. Showing DRAW on a match somebody won is worse than showing
    ///     nothing, so an implausible total must yield no pill at all.
    /// </summary>
    [Test]
    public async Task Outcome_IsWithheldWhenTheScorelineIsImplausible()
    {
        StatsTabViewModel vm = BuildVm(12, 12);

        foreach (TeamSection section in vm.TeamSections)
        {
            await Assert.That(section.Score).IsEqualTo(12);
            await Assert.That(section.Outcome).IsEqualTo(TeamOutcome.None);
        }
    }

    [Test]
    public async Task Outcome_AllowsARealOvertimeDraw()
    {
        StatsTabViewModel vm = BuildVm(15, 15);

        foreach (TeamSection section in vm.TeamSections)
        {
            await Assert.That(section.Outcome).IsEqualTo(TeamOutcome.Draw);
        }
    }

    /// <summary>
    ///     Sides swap at half, so a bare "CT" beside a TEAM total is the pairing Match Overview was
    ///     rewritten to eliminate. The label has to say which side the team FINISHED on.
    /// </summary>
    [Test]
    public async Task TeamLabel_NamesTheSideTheTeamEndedOn()
    {
        StatsTabViewModel vm = BuildVm();

        await Assert.That(vm.TeamSections.Single(t => t.IsCt).TeamLabel).IsEqualTo("ENDED CT");
        await Assert.That(vm.TeamSections.Single(t => !t.IsCt).TeamLabel).IsEqualTo("ENDED T");
    }

    // ── Podium ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Top three by rating, ACROSS both teams. Every other ordering in the app partitions by side
    ///     first; this one must not, because "who carried this match" is not a per-team question.
    /// </summary>
    [Test]
    public async Task Podium_RanksAcrossBothTeams()
    {
        StatsTabViewModel vm = BuildVm();

        await Assert.That(vm.HasPodium).IsTrue();
        await Assert.That(vm.Podium.Count).IsEqualTo(3);
        await Assert.That(vm.Podium.Select(e => e.Name)).IsEquivalentTo(_expectedPodium);
        await Assert.That(vm.Podium[0].Rank).IsEqualTo(1);
        await Assert.That(vm.Podium[0].RankLabel).IsEqualTo("1ST");

        // Alice is CT, Foxtrot is T: the strip crosses the team boundary.
        await Assert.That(vm.Podium[0].IsCt).IsTrue();
        await Assert.That(vm.Podium[1].IsCt).IsFalse();
    }

    /// <summary>The podium is about the match, so sorting a column must not reorder it.</summary>
    [Test]
    public async Task Podium_DoesNotFollowTheTableSort()
    {
        StatsTabViewModel vm = BuildVm();
        string[] before = vm.Podium.Select(e => e.Name).ToArray();

        vm.SortByColumnCommand.Execute(vm.Columns.Single(c => c.Label == "TotalD"));

        await Assert.That(vm.Podium.Select(e => e.Name)).IsEquivalentTo(before);
    }

    // ── Non-table category boards ─────────────────────────────────────────────

    [Test]
    public async Task Layout_IsChosenPerCategory()
    {
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.Core)).IsEqualTo(CategoryLayout.Table);
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.Utility)).IsEqualTo(CategoryLayout.Composition);
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.Weapons)).IsEqualTo(CategoryLayout.Composition);
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.OpeningDuels)).IsEqualTo(CategoryLayout.Diverging);
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.MultiKill)).IsEqualTo(CategoryLayout.Pips);
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.Damage)).IsEqualTo(CategoryLayout.Table);
    }

    /// <summary>
    ///     Composition bars are only comparable if every row shares one maximum. Per-row scaling would
    ///     draw the player who threw fifty grenades and the one who threw five at the same size, which
    ///     is the whole failure the form exists to avoid.
    /// </summary>
    [Test]
    public async Task Composition_SharesOneScaleAcrossTheLobby()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.Utility;

        await Assert.That(vm.IsBoardLayout).IsTrue();
        await Assert.That(vm.IsCompositionBoard).IsTrue();

        CompositionRow[] rows = vm.BoardSections
            .SelectMany(s => s.Rows).Cast<CompositionRow>().ToArray();
        await Assert.That(rows.Length).IsEqualTo(10);

        double max = rows.Max(r => r.Total);
        foreach (CompositionRow row in rows)
        {
            await Assert.That(row.MaxTotal).IsEqualTo(max);
        }

        // Sorted by volume, so the first row is the one whose bar fills. Asserted on the value rather
        // than on a name: which player throws the most is a property of the fixture data, and pinning
        // the name would make this test fail for a reason that has nothing to do with the board.
        await Assert.That(rows[0].Total).IsEqualTo(max);
        await Assert.That(rows[0].Total).IsGreaterThanOrEqualTo(rows[1].Total);
    }

    /// <summary>
    ///     Segments name their colour by palette SLOT and never carry a brush. That is what keeps the
    ///     categorical palette inside the theme layer: a view model that held a colour would be a colour
    ///     no theme could reach.
    /// </summary>
    [Test]
    public async Task Composition_NamesColoursBySlot_NeverByBrush()
    {
        StatsTabViewModel vm = BuildVm();

        foreach (StatGroup category in new[] { StatGroup.Utility, StatGroup.Weapons })
        {
            vm.SelectedCategory = category;
            CompositionRow row = vm.BoardSections.SelectMany(x => x.Rows)
                .Cast<CompositionRow>().First(r => r.Segments.Count > 1);

            foreach (StatSegment segment in row.Segments)
            {
                await Assert.That(segment.Brush).IsNull().Because($"{category} segment holds a brush");
                await Assert.That(segment.Slot).IsGreaterThanOrEqualTo(0);
                await Assert.That(segment.Slot).IsLessThan(6);
            }

            // Distinct slots within a row, or two categories would share a colour on one bar.
            int[] slots = row.Segments.Select(seg => seg.Slot).ToArray();
            await Assert.That(slots.Distinct().Count()).IsEqualTo(slots.Length);
        }
    }

    /// <summary>
    ///     The duel board's arms share one half-scale, and the rate is flagged when too few duels back
    ///     it. That flag is the point of the form: a bare rate hides volume, and volume is what makes
    ///     an opening-duel rate mean anything.
    /// </summary>
    [Test]
    public async Task Duels_ShareAScale_AndFlagThinVolume()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.OpeningDuels;

        await Assert.That(vm.IsDuelBoard).IsTrue();

        DuelRow[] rows = vm.BoardSections.SelectMany(s => s.Rows).Cast<DuelRow>().ToArray();
        await Assert.That(rows.Length).IsEqualTo(10);

        double extent = rows[0].Extent;
        foreach (DuelRow row in rows)
        {
            await Assert.That(row.Extent).IsEqualTo(extent);
            await Assert.That(row.LowVolume).IsEqualTo(row.Won + row.Lost < 8);
        }

        // Sorted by net, so the board opens with whoever won their duels.
        await Assert.That(rows[0].Won).IsGreaterThan(rows[0].Lost);
        await Assert.That(rows[^1].Won).IsLessThan(rows[^1].Lost);
    }

    [Test]
    public async Task Pips_CountEventsAndRankBusiestFirst()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.MultiKill;

        await Assert.That(vm.IsPipBoard).IsTrue();

        PipsRow[] rows = vm.BoardSections.SelectMany(s => s.Rows).Cast<PipsRow>().ToArray();
        await Assert.That(rows[0].PlayerName).IsEqualTo("Alice");
        await Assert.That(rows[0].Groups.Single(g => g.Label == "ACE").Count).IsEqualTo(1);
        await Assert.That(rows[0].Groups.Single(g => g.Label == "ACE").IsRare).IsTrue();
        await Assert.That(rows[0].Groups.Single(g => g.Label == "2K").IsGood).IsFalse();
    }

    /// <summary>A board whose columns this evaluation never produced falls back to the table.</summary>
    [Test]
    public async Task Board_FallsBackToTheTable_WhenItsColumnsAreAbsent()
    {
        IReadOnlyList<BoardSection> sections = CategoryBoard.Build(
            [], [], StatGroup.Utility, new Dictionary<int, int?>(), _ => TeamOutcome.None);

        await Assert.That(sections.Count).IsEqualTo(0);
    }

    /// <summary>Switching back to a table category must put the column table back.</summary>
    [Test]
    public async Task Category_TogglesBetweenBoardAndTable()
    {
        StatsTabViewModel vm = BuildVm();

        await Assert.That(vm.IsColumnTable).IsTrue();
        await Assert.That(vm.IsBoardLayout).IsFalse();

        vm.SelectedCategory = StatGroup.Utility;
        await Assert.That(vm.IsBoardLayout).IsTrue();
        await Assert.That(vm.IsColumnTable).IsFalse();

        vm.SelectedCategory = StatGroup.Core;
        await Assert.That(vm.IsBoardLayout).IsFalse();
        await Assert.That(vm.IsColumnTable).IsTrue();
    }

    /// <summary>The Core block no longer rides along on a specialist page.</summary>
    [Test]
    public async Task CategoryPage_ShowsOnlyItsOwnColumns()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.Rating;

        foreach (StatColumn column in vm.Columns)
        {
            await Assert.That(ColumnCatalogue.Resolve(column.Label).Group).IsEqualTo(StatGroup.Rating);
        }

        await Assert.That(vm.Columns.Any(c => c.Label == "TotalK")).IsFalse();
    }

    // ── Category rail ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Round wins are a TEAM fact replicated onto every player row, so a per-player page of them
    ///     would show five identical rows and invite a comparison that cannot exist. The columns stay in
    ///     the catalogue (the engine emits them, the export carries them, the team score is derived from
    ///     them); they just are not a page.
    /// </summary>
    [Test]
    public async Task CategoryRail_ExcludesTeamScopedGroups()
    {
        StatsTabViewModel vm = BuildVm();

        await Assert.That(ColumnCatalogue.IsPlayerFacing(StatGroup.RoundWins)).IsFalse();
        await Assert.That(ColumnCatalogue.IsPlayerFacing(StatGroup.Utility)).IsTrue();
        await Assert.That(vm.Categories.Any(c => c.Group == StatGroup.RoundWins)).IsFalse();

        // Still catalogued, so the score derivation and the export keep working.
        await Assert.That(ColumnCatalogue.Resolve("CTW").Group).IsEqualTo(StatGroup.RoundWins);
        await Assert.That(vm.TeamSections.Single(t => t.IsCt).Score).IsEqualTo(13);
    }

    /// <summary>A category that can only ever hold one column is a tab that costs a click for one number.</summary>
    [Test]
    public async Task RoundsSurvived_LivesWithTheOtherRatingColumns()
    {
        await Assert.That(ColumnCatalogue.Resolve("Survived").Group).IsEqualTo(StatGroup.Rating);
        await Assert.That(ColumnCatalogue.Resolve("Surv%").Group).IsEqualTo(StatGroup.Rating);
    }

    // ── Table scrolling ───────────────────────────────────────────────────────

    /// <summary>
    ///     A table wider than its card must be reachable in BOTH directions.
    ///     <para>
    ///         This shipped broken. The body used to be a vertical scroller nested inside a horizontal
    ///         one, and a nested scroller is laid out at the full CONTENT width rather than the viewport
    ///         width, which parks its scrollbar permanently off-screen. The Other page had eight columns,
    ///         five visible, and no way to reach the other three. The assertion that catches it is that
    ///         the viewport must be SMALLER than the bounds: that gap is the space the scrollbars occupy,
    ///         and it is zero when they have nowhere to live.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WideTable_ScrollsInBothDirections()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatsTabViewModel vm = BuildVm();
            vm.SelectedCategory = StatGroup.Other;

            StatsTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1280, Height = 620, Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = view.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "BodyScroll");
            ScrollViewer header = view.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "HeaderScroll");

            // There is genuinely more content than viewport on both axes.
            await Assert.That(body.Extent.Width).IsGreaterThan(body.Viewport.Width);
            await Assert.That(body.Extent.Height).IsGreaterThan(body.Viewport.Height);

            // Both scrollbars have somewhere on screen to live.
            await Assert.That(body.Viewport.Width).IsLessThan(body.Bounds.Width);
            await Assert.That(body.Viewport.Height).IsLessThan(body.Bounds.Height);

            // The header spans the same content, so the columns can line up with the rows.
            await Assert.That(header.Extent.Width).IsEqualTo(body.Extent.Width);

            // And it follows the body when the body moves.
            body.Offset = body.Offset.WithX(240);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(header.Offset.X).IsEqualTo(body.Offset.X);
        });
    }

    // ── Render ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The board draws, under Dark, with the ramp actually reaching the frame. Pinned to Dark because
    ///     the colour assertion names Dark token values and the headless Default variant resolves to Light.
    /// </summary>
    [Test]
    public async Task Board_RendersTheRamp()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                }

                StatsTabView view = new()
                {
                    DataContext = BuildVm()
                };
                Window window = new()
                {
                    Width = 1280, Height = 560, Content = view,
                    RequestedThemeVariant = ThemeVariant.Dark
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "stats-board.png");
                frame!.Save(outPath);
                Console.WriteLine($"[stats-board] {outPath}");

                byte[] pixels = FrameProbe.ToBytes(frame);
                foreach ((string token, uint hex) in
                         new[] { ("StatPositive", 0x4CAF50u), ("StatNegative", 0xDC5A52u) })
                {
                    int hits = FrameProbe.CountPixels(pixels, hex);
                    Console.WriteLine($"[stats-board] {token} hits={hits}");
                    await Assert.That(hits).IsGreaterThan(0);
                }
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }
        });
    }

    [Test]
    [MethodDataSource(nameof(BoardCases))]
    public async Task Board_Renders(StatGroup category, string name)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                }

                StatsTabViewModel vm = BuildVm();
                vm.SelectedCategory = category;

                Window window = new()
                {
                    Width = 1280, Height = 620,
                    Content = new StatsTabView { DataContext = vm },
                    RequestedThemeVariant = ThemeVariant.Dark
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, $"stats-board-{name}.png");
                frame!.Save(outPath);
                Console.WriteLine($"[stats-board-{name}] {outPath}");
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }
        });
    }

    /// <summary>
    ///     The board under every shipped theme. Renders through a real <see cref="ThemeRegistry" /> so the
    ///     custom variants resolve exactly as they do in the app, and asserts the ramp actually retints:
    ///     a token the theme overrode must NOT still be painting its Dark default.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ThemeCases))]
    public async Task Board_RendersUnderEveryShippedTheme(string themeId, string expectedPositive)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                ThemeRegistry registry = new();
                registry.Install(Application.Current!);
                ThemeVariant variant = registry.VariantFor(themeId);
                Application.Current!.RequestedThemeVariant = variant;

                // The ramp resolves to the THEME's value, not the base palette's.
                Application.Current.TryGetResource("StatPositive", variant, out object? positive);
                await Assert.That((positive as ISolidColorBrush)?.Color)
                    .IsEqualTo(Color.Parse(expectedPositive));

                // The column table AND a composition board: the board pulls four separate tokens
                // through the slot palette, which the table never touches.
                foreach ((StatGroup category, string suffix) in
                         new[] { (StatGroup.Core, "table"), (StatGroup.Utility, "utility") })
                {
                    StatsTabViewModel vm = BuildVm();
                    vm.SelectedCategory = category;

                    Window window = new()
                    {
                        Width = 1280, Height = 620,
                        Content = new StatsTabView { DataContext = vm },
                        RequestedThemeVariant = variant
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();

                    WriteableBitmap? frame = window.CaptureRenderedFrame();
                    await Assert.That(frame).IsNotNull();
                    string outPath = Path.Combine(HeadlessSession.ArtifactDir,
                        $"stats-theme-{themeId}-{suffix}.png");
                    frame!.Save(outPath);
                    Console.WriteLine($"[stats-theme-{themeId}-{suffix}] {outPath}");
                }
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }
        });
    }

    public static IEnumerable<(string, string)> ThemeCases()
    {
        yield return ("high-contrast", "#00E676");
        yield return ("egirl", "#5AE0A0");
    }

    public static IEnumerable<(StatGroup, string)> BoardCases()
    {
        yield return (StatGroup.Utility, "utility");
        yield return (StatGroup.Weapons, "weapons");
        yield return (StatGroup.OpeningDuels, "duels");
        yield return (StatGroup.MultiKill, "multikill");
        yield return (StatGroup.Other, "other");
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Cells are indexed by the VISIBLE column order, which is the catalogue's order narrowed by the
    ///     active category chip, not the order this fixture happens to declare. Looking the index up by
    ///     engine key is the only stable way in.
    /// </summary>
    private static StatCell Cell(StatsTabViewModel vm, string player, string column)
    {
        int index = -1;
        for (int i = 0; i < vm.Columns.Count; i++)
        {
            if (vm.Columns[i].Label == column)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"column '{column}' is not visible; visible: {string.Join(", ", vm.Columns.Select(c => c.Label))}");
        }

        return vm.GameRows.Single(r => r.PlayerName == player).Cells[index];
    }

    private static StatsTabViewModel BuildVm(int ctRoundWins = 13, int tRoundWins = 9)
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(ctRoundWins, tRoundWins);
        vm.Update(result, demo);
        return vm;
    }

    /// <summary>Mirrors the fixture shape in <see cref="StatsTabTests" />, widened to ten players.</summary>
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario(
        int ctRoundWins, int tRoundWins)
    {
        StubNode roundNode = new("RoundNumber");
        List<StateNode> tracked = [roundNode];
        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = [];
        List<List<int>> colIdx = [];

        for (int p = 0; p < _roster.Length; p++)
        {
            List<PerPlayerColumnAssignment> assignments = [];
            List<int> indices = [];
            List<StateNode> nodes = [];
            foreach (string column in _columns.Concat(_scoreColumns).Concat(_boardColumns)
                         .Concat(_otherColumns))
            {
                StubNode node = new($"{_roster[p].Name}_{column}");
                indices.Add(tracked.Count);
                tracked.Add(node);
                nodes.Add(node);
                assignments.Add(new PerPlayerColumnAssignment(node, column, IsRoundScoped: false));
            }

            colIdx.Add(indices);
            materialized.Add(new PerPlayerNodeTemplate.MaterializedPlayer(
                p, _roster[p].Name, nodes, [], assignments, []));
        }

        NodeSnapshot[] vec = new NodeSnapshot[tracked.Count];
        vec[0] = Snap(1);
        for (int p = 0; p < _roster.Length; p++)
        {
            for (int c = 0; c < _columns.Length; c++)
            {
                vec[colIdx[p][c]] = Snap(_roster[p].Values[c]);
            }

            // CTW + TW is what ComputeTeamScores sums, and it must agree across every row of a team.
            bool isCt = _roster[p].Team == 3;
            vec[colIdx[p][_columns.Length]] = Snap(isCt ? ctRoundWins : 0);
            vec[colIdx[p][_columns.Length + 1]] = Snap(isCt ? 0 : tRoundWins);

            for (int b = 0; b < _boardColumns.Length; b++)
            {
                vec[colIdx[p][_columns.Length + _scoreColumns.Length + b]] =
                    Snap(BoardValue(_boardColumns[b], p));
            }

            int otherBase = _columns.Length + _scoreColumns.Length + _boardColumns.Length;
            for (int o = 0; o < _otherColumns.Length; o++)
            {
                vec[colIdx[p][otherBase + o]] = Snap(10 + (p * 3) + o);
            }
        }

        EvaluationResult result = new(
            new RuleChainTimeline([]), new[] { vec }, [], tracked, materialized, []);

        Dictionary<int, PlayerInfo> infos = [];
        for (int p = 0; p < _roster.Length; p++)
        {
            infos[p] = new PlayerInfo(p, _roster[p].Name, 0UL, p, _roster[p].Team, false);
        }

        ParsedDemo demo = SyntheticParsedDemo.Create(
            [], [], infos, null, "de_test", 0, 1f / 64f,
            "t", "t", "csgo", 0, 0, 0, "valve_demo_2", "", "", DemoProfile.Unknown);

        return (result, demo);
    }

    private static NodeSnapshot Snap(double value) =>
        new(true, value.ToString("0.##", CultureInfo.InvariantCulture), (float)value);



    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }
}

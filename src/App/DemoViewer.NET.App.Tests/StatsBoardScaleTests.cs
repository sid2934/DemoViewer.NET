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
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Stats;
using TUnit.Core.Exceptions;

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
        "TeamDmg", "SelfDmg",
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

    /// <summary>
    ///     The aim board (rules/aim_rating.rules.yaml), in that file's show order. Declared game-scoped
    ///     here even though Spot ships as a round column: this fixture carries one
    ///     snapshot vector rather than a round timeline, and what these tests pin is the scale wiring,
    ///     which is per column and identical either way.
    /// </summary>
    private static readonly string[] _aimColumns =
    [
        "Acc%", "HSAcc%", "HSDmg%", "CS%", "CSAll%", "Linear%", "FB%",
        "Spray", "Preaim", "SAcc%", "SprayAcc%",
        "CSAtt", "SprayN", "Spots",
        "XPlace", "FlickErr", "XShots",
        "TTS", "TTSn", "TTD", "TTDn",
        "AimRx", "AimRxn", "TTK", "TTKn", "Spot"
    ];

    /// <summary>
    ///     Aim values, shaped so each volume gate has exactly one row that trips it and no other. Three
    ///     different players are starved, one per gate, because a single starved row would prove the
    ///     gates fire without proving they are wired to the right denominators.
    ///     <para>
    ///         Juliet (9) attempted four counter-strafes and stopped all four, which is the two-duels-won-
    ///         of-two failure in a different column. India (8) has three measurable sprays and the
    ///         tightest residual on the board. Echo (4) was seen making contact twice and tops every
    ///         after-contact ratio. All three must keep their bar and lose their tint.
    ///     </para>
    /// </summary>
    private static double AimValue(string column, int i) => column switch
    {
        "Acc%" => 34 - (i * 2),
        "HSAcc%" => 22 + ((i % 5) * 3),
        "HSDmg%" => 30 + ((i % 4) * 5),
        "CS%" => i == 9 ? 100 : 78 - (i * 4),
        "CSAll%" => 41 - (i * 2),
        "Linear%" => 8 + (i * 2),
        "FB%" => i == 4 ? 56 : 44 - (i * 3),
        "Spray" => i == 8 ? 0.6 : 1.0 + (i * 0.35),
        "Preaim" => i == 4 ? 2.5 : 3.0 + (i * 0.9),
        "SAcc%" => i == 4 ? 52 : 38 - (i * 2),
        "SprayAcc%" => i == 4 ? 40 : 26 - (i * 1.5),
        "CSAtt" => i == 9 ? 4 : 62 - (i * 3),
        "SprayN" => i == 8 ? 3 : 44 - (i * 2),
        "Spots" => i == 4 ? 2 : 18 - i,
        "XPlace" => i == 6 ? 0 : 12 + (i * 1.5),
        "FlickErr" => (i * 2.0) - 7,
        // Hotel (7) is the fourth starved row, one per gate: a 96 ms aimed reaction, the fastest on
        // the board by 100 ms, off exactly two acquisitions.
        "AimRx" => i == 7 ? 96 : 280 - (i * 9),
        "AimRxn" => i == 7 ? 2 : 34 - i,
        "Spot" => i == 6 ? 0 : 1,
        // Golf (6) starves crosshair travel: a 0.0 degree travel is the cleanest flick on the board
        // and would tint hardest, off two shots.
        "XShots" => i == 6 ? 2 : 30 - i,
        // Hotel (7) starves the rest of the ladder the same way it starves AimRx, each on its OWN
        // denominator: the gates are deliberately not shared, so each needs its own thin row.
        "TTS" => i == 7 ? 300 : 470 - (i * 8),
        "TTSn" => i == 7 ? 3 : 40 - i,
        "TTD" => i == 7 ? 380 : 620 - (i * 10),
        "TTDn" => i == 7 ? 3 : 36 - i,
        // TTK ships untinted, so it carries no gate and needs no starved row; it is here so the
        // board renders the whole ladder rather than a column of zeroes.
        "TTK" => 900 - (i * 20),
        "TTKn" => 14 - (i % 5),
        _ => 0
    };

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
        // Penalty columns, shaped for the two ends of the leader-marker rule. Team damage is the
        // ordinary case: almost nobody does any, so the good bound is where most of the lobby already
        // sits. Self damage puts exactly two players on the bound, which IS a lead worth marking.
        "TeamDmg" => i == 4 ? 63 : i == 9 ? 21 : 0,
        "SelfDmg" => i < 2 ? 0 : 5 + i,
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
        // Every board column, not just the Core seven: a StatScaleSpec declared BELOW _byKey reads
        // null and the column silently loses its scale, and sweeping only _columns meant the whole
        // aim board could regress that way without this test noticing. BOTH aim chips are named here,
        // plus the hidden denominators, so a spec that only one half lost still fails by name.
        foreach (string column in _columns.Concat(_accuracyColumns).Concat(_aimQualityColumns)
                     .Concat(_denominatorColumns))
        {
            await Assert.That(ColumnCatalogue.Resolve(column).Scale).IsNotNull()
                .Because($"{column} must resolve a scale; a null one means it was declared below _byKey");
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

    // ── Aim board ──────────────────────────────────────────────────

    /// <summary>
    ///     The two chips the aim ruleset splits across, each in the ruleset's own order. Twenty-six
    ///     columns did not fit one board, and the seam the metrics already had is the unit: percentages
    ///     say how often a bullet landed, degrees and milliseconds say how good the aim itself was.
    /// </summary>
    private static readonly string[] _accuracyColumns =
    [
        "Acc%", "HSAcc%", "HSDmg%", "CS%", "CSAll%", "Linear%", "FB%", "SAcc%", "SprayAcc%", "Spot"
    ];

    private static readonly string[] _aimQualityColumns =
    [
        "Spray", "Preaim", "XPlace", "FlickErr", "TTS", "TTD", "AimRx", "TTK"
    ];

    /// <summary>
    ///     The populations. Still emitted, exported and read by the gates, but no longer columns: each
    ///     reads in the tooltip of the cell it qualifies.
    /// </summary>
    private static readonly string[] _denominatorColumns =
    [
        "CSAtt", "SprayN", "Spots", "XShots", "TTSn", "TTDn", "AimRxn", "TTKn"
    ];

    /// <summary>
    ///     Every aim column is catalogued under one of the two aim chips or as a hidden denominator, and
    ///     every one of them carries a scale. Same failure mode as the test above and worth its own case
    ///     because the aim specs are a second batch of static fields: declared below <c>_byKey</c> they
    ///     are all null when the catalogue is built, and the whole page then renders as bare text.
    ///     <para>
    ///         BOTH chips are swept by name. A column that drifted to Other, or to the wrong half, still
    ///         renders as a plausible page, so membership is pinned column by column.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Catalogue_CarriesTheAimColumns_UnderBothChips()
    {
        // Sorted on both sides: IsEquivalentTo matches order, and a partition has none.
        await Assert.That(_accuracyColumns.Concat(_aimQualityColumns).Concat(_denominatorColumns)
                .OrderBy(c => c, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(_aimColumns.OrderBy(c => c, StringComparer.Ordinal).ToArray())
            .Because("the two chips and the denominators must partition the ruleset");

        foreach (string column in _accuracyColumns)
        {
            ColumnMeta meta = ColumnCatalogue.Resolve(column);
            await Assert.That(meta.Group).IsEqualTo(StatGroup.Accuracy)
                .Because($"{column} is a hit rate and belongs under Accuracy");
            await Assert.That(meta.Hidden).IsFalse();
            await Assert.That(meta.Scale).IsNotNull().Because($"{column} has no scale");
        }

        foreach (string column in _aimQualityColumns)
        {
            ColumnMeta meta = ColumnCatalogue.Resolve(column);
            await Assert.That(meta.Group).IsEqualTo(StatGroup.AimQuality)
                .Because($"{column} is degrees or milliseconds and belongs under Aim Quality");
            await Assert.That(meta.Hidden).IsFalse();
            await Assert.That(meta.Scale).IsNotNull().Because($"{column} has no scale");
        }

        foreach (string column in _denominatorColumns)
        {
            await Assert.That(ColumnCatalogue.Resolve(column).Hidden).IsTrue()
                .Because($"{column} is a population and belongs in a tooltip, not a column");
        }
    }

    /// <summary>
    ///     The catalogue Key has to match the ruleset label BYTE FOR BYTE, so this reads the labels out
    ///     of the shipped YAML rather than out of the fixture array above. A typo in either place is
    ///     otherwise invisible: the column still renders, it just quietly lands in Other.
    /// </summary>
    [Test]
    public async Task AimColumns_MatchTheShippedRulesetLabels()
    {
        string[] labels = AimRulesetLabels();
        await Assert.That(labels).IsNotEmpty();

        foreach (string label in labels)
        {
            StatGroup group = ColumnCatalogue.Resolve(label).Group;
            await Assert.That(group == StatGroup.Accuracy || group == StatGroup.AimQuality).IsTrue()
                .Because($"the ruleset shows '{label}' and the catalogue does not register it");
        }

        // And nothing in the fixture has drifted away from the file it stands in for.
        await Assert.That(_aimColumns).IsEquivalentTo(labels);
    }

    /// <summary>
    ///     Two aim chips, Accuracy then Aim Quality, between Combat and Damage. The rail orders by enum
    ///     ORDINAL, so the position is decided by where the members were declared and by nothing else;
    ///     pinned here because moving a member is a one-line change with a visible consequence. The
    ///     labels are pinned too: a group without a LabelFor arm ships as its raw enum name.
    /// </summary>
    [Test]
    public async Task CategoryRail_CarriesBothAimChips_BetweenCombatAndDamage()
    {
        StatsTabViewModel vm = BuildVm();
        List<CategoryChip> chips = vm.Categories.ToList();

        await Assert.That(chips.Single(c => c.Group == StatGroup.Accuracy).Label).IsEqualTo("Accuracy");
        await Assert.That(chips.Single(c => c.Group == StatGroup.AimQuality).Label).IsEqualTo("Aim Quality");

        int combat = chips.FindIndex(c => c.Group == StatGroup.Combat);
        int accuracy = chips.FindIndex(c => c.Group == StatGroup.Accuracy);
        int quality = chips.FindIndex(c => c.Group == StatGroup.AimQuality);
        int damage = chips.FindIndex(c => c.Group == StatGroup.Damage);
        await Assert.That(accuracy).IsGreaterThan(combat);
        await Assert.That(quality).IsEqualTo(accuracy + 1);
        await Assert.That(quality).IsLessThan(damage);

        // Ten and eight columns are tables, not boards.
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.Accuracy)).IsEqualTo(CategoryLayout.Table);
        await Assert.That(CategoryBoard.LayoutFor(StatGroup.AimQuality)).IsEqualTo(CategoryLayout.Table);
    }

    /// <summary>
    ///     Each aim chip shows its half in the ruleset's board order, top to bottom. The catalogue's
    ///     declaration order IS the board order, so a column inserted in the wrong place silently
    ///     reshuffles the page.
    /// </summary>
    [Test]
    [Arguments(StatGroup.Accuracy)]
    [Arguments(StatGroup.AimQuality)]
    public async Task AimChip_ShowsItsHalf_InRulesetOrder(StatGroup chip)
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = chip;
        string[] expected = chip == StatGroup.Accuracy ? _accuracyColumns : _aimQualityColumns;

        await Assert.That(vm.IsColumnTable).IsTrue();
        await Assert.That(vm.Columns.Select(c => c.Label)).IsEquivalentTo(expected);
    }

    /// <summary>
    ///     The eight population columns are off the board under both chips and still in the table, where
    ///     the gates and the export read them. <see cref="ThinAimSample_KeepsItsBar_AndLosesItsTint" /> is
    ///     the other half of this proof: every gate still trips with its denominator no longer a column,
    ///     because <c>ClearsColourGate</c> reads the MetricRow and not the cells.
    /// </summary>
    [Test]
    public async Task Denominators_LeaveTheBoard_AndStayInTheTable()
    {
        StatsTabViewModel vm = BuildVm();
        foreach (StatGroup chip in new[] { StatGroup.Accuracy, StatGroup.AimQuality })
        {
            vm.SelectedCategory = chip;
            foreach (string denominator in _denominatorColumns)
            {
                await Assert.That(vm.Columns.Select(c => c.Label)).DoesNotContain(denominator)
                    .Because($"{denominator} is a population and reads in a tooltip, not a column");
            }
        }

        foreach (string denominator in _denominatorColumns)
        {
            await Assert.That(vm.GameTable!.ValueColumns).Contains(denominator)
                .Because("hidden is not deleted: the gate and the export still read it");
        }
    }

    /// <summary>
    ///     The count sits next to the value it qualifies, as one phrase, in the cell's tooltip. A
    ///     denominator a reader cannot connect to its metric has been deleted, not moved, so the phrase is
    ///     pinned word for word, and the rows pinned are the starved ones: they are exactly the cells a
    ///     reader hovers to ask why the tint is missing.
    ///     <para>
    ///         Two prepositions, on purpose. "over" is a claim of division: TTS is the mean of 3
    ///         engagements, Preaim the mean of 2 contacts, CS% the share of 4 attempts. FB%, SAcc% and
    ///         SprayAcc% are NOT fractions of their contacts (rules/aim_rating.rules.yaml divides them by
    ///         first_bullet_shots, contact_shots and spray_shots_fired, none of which is exported), so
    ///         the contact count is the gate the ruleset guards them with and reads "after": no fraction
    ///         of 2 contacts is 56%, and a phrase that said "over" would send a reader looking for one.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("TTS", "Hotel", "300 ms over 3 engagements")]
    [Arguments("TTS", "Alice", "470 ms over 40 engagements")]
    [Arguments("TTD", "Hotel", "380 ms over 3 engagements")]
    [Arguments("AimRx", "Hotel", "96 ms over 2 acquisitions")]
    [Arguments("TTK", "Alice", "900 ms over 14 kills")]
    [Arguments("XPlace", "Golf", "0° over 2 shots")]
    [Arguments("FlickErr", "Alice", "-7° over 30 shots")]
    [Arguments("Preaim", "Echo", "2.5° over 2 contacts")]
    [Arguments("FB%", "Echo", "56% after 2 contacts")]
    [Arguments("SAcc%", "Echo", "52% after 2 contacts")]
    [Arguments("SprayAcc%", "Echo", "40% after 2 contacts")]
    [Arguments("CS%", "Juliet", "100% over 4 attempts")]
    [Arguments("Spray", "India", "0.6° over 3 shots")]
    public async Task GatedAimCell_ReadsItsDenominator_InTheTooltip(string column, string player, string expected)
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = ColumnCatalogue.Resolve(column).Group;

        await Assert.That(Cell(vm, player, column).Tooltip).IsEqualTo(expected);
    }

    /// <summary>
    ///     A column with no denominator shows no tip at all, not an empty one, and a count of one reads
    ///     as one: "over 1 engagements" is the kind of phrase that tells a reader nobody looked.
    /// </summary>
    [Test]
    public async Task UngatedAimCell_HasNoTooltip_AndACountOfOneReadsSingular()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.Accuracy;
        await Assert.That(Cell(vm, "Alice", "Acc%").Tooltip).IsNull();

        StatCell one = new(300.0, ColumnCatalogue.Resolve("TTS"))
        {
            Denominator = 1
        };
        await Assert.That(one.Tooltip).IsEqualTo("300 ms over 1 engagement");
    }

    /// <summary>
    ///     The totals row pools the team's sample, so its rate is the mean over the summed count, not the
    ///     mean of five means. The two differ here only in the second decimal, because the fixture's
    ///     counts are nearly equal; the test below is the one with the gap.
    /// </summary>
    [Test]
    public async Task TotalsRow_PoolsTheDenominator()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.AimQuality;
        int index = vm.Columns.Single(c => c.Label == "TTS").Index;
        TeamSection ct = vm.TeamSections.Single(s => s.IsCt);

        // Alice to Echo: 470, 462, 454, 446 and 438 ms over 40, 39, 38, 37 and 36 engagements.
        // (470*40 + 462*39 + 454*38 + 446*37 + 438*36) / 190 = 86340 / 190 = 454.42; the mean of
        // the five means is 454 flat.
        await Assert.That(ct.Totals.Cells[index].Tooltip).IsEqualTo("454.42 ms over 190 engagements");
    }

    /// <summary>
    ///     A member with no sample contributes nothing to the team's rate. The ruleset guards every
    ///     ratio with <c>max(d, 1)</c>, so an unmeasured member is a confident 0.0 beside a count of 0:
    ///     averaged with equal weight it drags the team mean by a third while adding nothing to the
    ///     pooled count, and the tooltip then lends the figure a population it was never measured over.
    ///     Weighting by the counts makes the phrase true: 400 ms over 20 engagements IS 400 ms.
    /// </summary>
    [Test]
    public async Task TotalsRow_GivesAnUnmeasuredMember_NoWeight()
    {
        ColumnMeta tts = ColumnCatalogue.Resolve("TTS");
        List<StatsRow> members =
        [
            new("Alice", 3, [new StatCell(400.0, tts) { Denominator = 10 }]),
            new("Bravo", 3, [new StatCell(400.0, tts) { Denominator = 10 }]),
            new("Charlie", 3, [new StatCell(0.0, tts) { Denominator = 0 }])
        ];

        StatsRow totals = StatsTabViewModel.BuildTotalsRow(members, ["TTS"]);

        await Assert.That(totals.Cells[0].Tooltip).IsEqualTo("400 ms over 20 engagements")
            .Because("a 0 ms row off 0 engagements is an empty population, not a fast one");
    }

    /// <summary>
    ///     Every count a tooltip reads is a catalogued hidden column. The count is looked up on the
    ///     MetricRow by the Denominator's key, and a key that no longer matches a ruleset label does not
    ///     throw: the cell quietly shows no tip, which is the eight columns deleted for real this time.
    ///     Sweeping the catalogue rather than the aim list means a denominator added to any future column
    ///     is held to the same rule. The chip is deliberately not asserted: Spots gates three Accuracy
    ///     rates and divides Preaim on Aim Quality, and can only be catalogued under one of them.
    /// </summary>
    [Test]
    public async Task EveryDenominator_ResolvesToAHiddenColumn()
    {
        int seen = 0;
        foreach (ColumnMeta meta in ColumnCatalogue.All)
        {
            if (meta.Denominator is not { } over)
            {
                continue;
            }

            seen++;
            ColumnMeta count = ColumnCatalogue.Resolve(over.Key);
            await Assert.That(count.Hidden).IsTrue()
                .Because($"{meta.Key} reads {over.Key}, which must be a catalogued hidden column and not a stray label");
        }

        await Assert.That(seen).IsEqualTo(12).Because("twelve aim columns read a count in their tooltip");
    }

    /// <summary>
    ///     Four volume gates, one starved row each. The gate neuters the tint and keeps the bar, so a
    ///     four-attempt 100% still shows how it compares and stops claiming to be the best play in the
    ///     lobby. Every ratio on this board guards its denominator with <c>max(d, 1)</c>, which turns an
    ///     unmeasured population into a confident zero, so without these gates the emptiest rows would
    ///     be the ones painted hardest.
    ///     <para>
    ///         The gate column is no longer on the board. It still trips, because ClearsColourGate reads
    ///         the MetricRow and not the visible cells; a gate that silently read 0 would neuter every
    ///         tint on this page, which is the XPlace bug in a new coat.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("CS%", "Juliet", "CSAtt")]
    [Arguments("Spray", "India", "SprayN")]
    [Arguments("FB%", "Echo", "Spots")]
    [Arguments("SAcc%", "Echo", "Spots")]
    [Arguments("SprayAcc%", "Echo", "Spots")]
    [Arguments("Preaim", "Echo", "Spots")]
    [Arguments("XPlace", "Golf", "XShots")]
    [Arguments("AimRx", "Hotel", "AimRxn")]
    [Arguments("TTS", "Hotel", "TTSn")]
    [Arguments("TTD", "Hotel", "TTDn")]
    public async Task ThinAimSample_KeepsItsBar_AndLosesItsTint(string column, string starved, string gate)
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = ColumnCatalogue.Resolve(column).Group;

        StatScaleSpec spec = ColumnCatalogue.Resolve(column).Scale!;
        await Assert.That(spec.ColourGateColumns).IsEquivalentTo(new[] { gate });

        StatCell thin = Cell(vm, starved, column);
        await Assert.That(thin.Scale).IsNotNull().Because("the bar survives the gate");
        await Assert.That(thin.Scale!.Polarity).IsEqualTo(StatPolarity.Neutral);
        await Assert.That(thin.Scale.Sentiment(thin.Numeric!.Value)).IsEqualTo(0);

        // A row with the volume behind it still gets judged, or the gate would just be the column off.
        StatCell alice = Cell(vm, "Alice", column);
        await Assert.That(alice.Scale!.Polarity).IsNotEqualTo(StatPolarity.Neutral);
    }

    /// <summary>
    ///     Degrees off, degrees travelled and penalised bullets all read the other way round from the
    ///     percentages beside them. Getting one of these backwards produces a page that looks finished
    ///     and rewards the worst row on it.
    /// </summary>
    [Test]
    [Arguments("Linear%", "Alice", "Juliet")]
    [Arguments("Spray", "Alice", "Juliet")]
    [Arguments("Preaim", "Alice", "Juliet")]
    public async Task LowerIsBetterAimColumn_TintsTheSmallValueGood(string column, string best, string worst)
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = ColumnCatalogue.Resolve(column).Group;

        StatCell good = Cell(vm, best, column);
        StatCell bad = Cell(vm, worst, column);
        double small = good.Numeric!.Value;
        double large = bad.Numeric!.Value;

        await Assert.That(small).IsLessThan(large);
        await Assert.That(good.Scale!.Sentiment(small)).IsGreaterThan(0);
        await Assert.That(bad.Scale!.Sentiment(large)).IsLessThan(0);
    }

    /// <summary>
    ///     The columns this board deliberately refuses to judge, and why each one is on the list.
    ///     <para>
    ///         Spot is a population: a first contact is more fighting, not better play, and tinting it
    ///         says it is. The match-scoped populations are no longer columns at all; each reads in the
    ///         tooltip of the cell it qualifies (GatedAimCell_ReadsItsDenominator_InTheTooltip).
    ///     </para>
    ///     <para>
    ///         The two headshot shares are a STYLE, exactly as HS% already is: an AWPer's body hits kill
    ///         as well as a rifler's heads, and HSDmg% divides by all enemy damage, so a player who
    ///         throws grenades scores lower with no change to their aim. FlickErr is signed, so neither
    ///         direction is the good one and the bar carries the sign, which is FK+/-'s treatment.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("Spot")]
    [Arguments("HSAcc%")]
    [Arguments("HSDmg%")]
    [Arguments("FlickErr")]
    public async Task UnjudgedAimColumn_KeepsABarAndNoTint(string column)
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = ColumnCatalogue.Resolve(column).Group;

        await Assert.That(ColumnCatalogue.Resolve(column).Scale!.Polarity)
            .IsEqualTo(StatPolarity.Neutral);

        StatCell cell = Cell(vm, "Alice", column);
        await Assert.That(cell.Scale).IsNotNull();
        await Assert.That(cell.Scale!.Sentiment(cell.Numeric!.Value)).IsEqualTo(0);
    }

    /// <summary>
    ///     No aim column carries an absolute colour band. The available reference data is 50
    ///     player-match rows and five pro accounts, which cannot define a percentile scale, and a
    ///     per-opportunity efficiency has no structurally pinned mean to hang one off anyway. A band
    ///     invented here would look exactly as authoritative as the researched ones above it.
    /// </summary>
    [Test]
    public async Task AimColumns_CarryNoInventedBenchmark()
    {
        foreach (string column in _aimColumns)
        {
            StatScaleSpec spec = ColumnCatalogue.Resolve(column).Scale!;
            await Assert.That(spec.Domain).IsEqualTo(StatDomain.Peer);
            await Assert.That(spec.ColourMin).IsNull().Because($"{column} invented a benchmark");
            await Assert.That(spec.ColourMax).IsNull().Because($"{column} invented a benchmark");
        }
    }

    /// <summary>
    ///     The <c>label:</c> values from the shipped ruleset's <c>show: scoreboard:</c> block, in file
    ///     order. Read line by line rather than through a YAML parser because the assertion is about the
    ///     exact bytes between <c>label:</c> and the next comma, which is what the catalogue Key must
    ///     equal ordinally.
    /// </summary>
    private static string[] AimRulesetLabels()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        string path = Path.Combine(root, "rules", "aim_rating.rules.yaml");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"the aim ruleset is missing at {path}");
        }

        List<string> labels = [];
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (!line.StartsWith("- {", StringComparison.Ordinal))
            {
                continue;
            }

            int start = line.IndexOf("label:", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            start += "label:".Length;
            int end = line.IndexOf(',', start);
            labels.Add(line[start..end].Trim().Trim('"'));
        }

        return [.. labels];
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

    /// <summary>
    ///     The podium ranks the SCOREBOARD, so it goes away with it. It is drawn in a band above the
    ///     body rows rather than inside them, so gating it on content alone left "the match's top three"
    ///     hanging over the highlights list, the vision table and the keyed extra tables, none of which
    ///     it is ranking.
    /// </summary>
    [Test]
    public async Task Podium_HidesOnTheViewsItDoesNotRank()
    {
        StatsTabViewModel vm = BuildVm();
        await Assert.That(vm.IsPodiumVisible).IsTrue();

        vm.IsHighlightsView = true;
        await Assert.That(vm.HasPodium).IsTrue();
        await Assert.That(vm.IsPodiumVisible).IsFalse();

        vm.IsHighlightsView = false;
        vm.IsVisibilityView = true;
        await Assert.That(vm.IsPodiumVisible).IsFalse();

        vm.IsVisibilityView = false;
        await Assert.That(vm.IsPodiumVisible).IsTrue();

        // Rounds keeps it, which is the behaviour it has always had. Pinned so that a later decision to
        // drop it there is a deliberate one rather than a side effect of tidying this gate.
        vm.IsRoundView = true;
        await Assert.That(vm.IsPodiumVisible).IsTrue();
    }

    // ── Leader marker ─────────────────────────────────────────────────────────

    /// <summary>
    ///     A star has to mark a minority. On a penalty column the best value is also the ORDINARY one:
    ///     eight of ten players did no team damage, so the domain's good end is zero and, on the bound
    ///     test alone, eight rows won an award for doing nothing. Two players tied at the good end is a
    ///     real lead and still stars.
    /// </summary>
    [Test]
    public async Task Leader_MarksAMinority_NotEveryoneOnTheBound()
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.Damage;

        // Eight of ten sit on zero. The scale still has a domain (two players did damage), so this is
        // not the everyone-tied case the domain check already handles.
        StatCell teamDmg = Cell(vm, "Alice", "TeamDmg");
        await Assert.That(teamDmg.Numeric).IsEqualTo(0);
        await Assert.That(teamDmg.Scale!.HasDomain).IsTrue();
        await Assert.That(teamDmg.Scale.Min).IsEqualTo(0);
        await Assert.That(teamDmg.IsLeader).IsFalse();

        // Exactly two on the bound: both lead, both starred.
        await Assert.That(Cell(vm, "Alice", "SelfDmg").IsLeader).IsTrue();
        await Assert.That(Cell(vm, "Bravo", "SelfDmg").IsLeader).IsTrue();
        await Assert.That(Cell(vm, "Juliet", "SelfDmg").IsLeader).IsFalse();
    }

    // ── Layout notifications ──────────────────────────────────────────────────

    /// <summary>
    ///     What one flag was worth EACH TIME it was announced, over the course of <paramref name="act" />.
    ///     <para>
    ///         <b>These tests subscribe to PropertyChanged, and that is the whole point.</b> Every layout
    ///         flag is a computed getter, and a getter is always correct: read one after any of these
    ///         transitions and it returns the right answer whether or not it was ever announced. A
    ///         binding re-reads on the notification and on nothing else, so a missing announcement is
    ///         invisible to a test that asserts on values. Both defects here shipped green for exactly
    ///         that reason.
    ///     </para>
    ///     <para>
    ///         The recorded VALUE matters, not just the name. Update() announces the layout flags from
    ///         inside the row rebuild, which runs before it knows whether there were any rows, so a
    ///         name-only recorder sees IsColumnTable announced and calls it covered while every
    ///         announcement carried false. The invariant that catches that is the last thing the view
    ///         was told equalling what is actually true when the dust settles.
    ///     </para>
    /// </summary>
    private static List<bool> Announcements(StatsTabViewModel vm, string property, Func<bool> read,
        Action act)
    {
        List<bool> seen = [];
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, property, StringComparison.Ordinal))
            {
                seen.Add(read());
            }
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            act();
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }

        return seen;
    }

    /// <summary>
    ///     Loading a demo announces that the table is now showable. Update() rebuilds the rows BEFORE it
    ///     knows whether there were any, so the layout flags are first announced while HasStats is still
    ///     false. Without a second announcement the scoreboard binding latches hidden and the table
    ///     never appears at all, for the whole session, on a page that is otherwise fully populated.
    /// </summary>
    [Test]
    public async Task FirstLoad_AnnouncesThatTheTableCanShow()
    {
        StatsTabViewModel vm = new(null, () => "/demos/match.dem");
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(13, 9);

        List<bool> seen = Announcements(vm, nameof(StatsTabViewModel.IsColumnTable),
            () => vm.IsColumnTable, () => vm.Update(result, demo));

        // The table IS showable, and the last thing the view heard has to say so. Announcing it while
        // the rows were still being counted does not count: that announcement carried false.
        await Assert.That(vm.IsColumnTable).IsTrue();
        await Assert.That(seen).IsNotEmpty();
        await Assert.That(seen[^1]).IsTrue();
    }

    /// <summary>
    ///     Switching view mode announces the board flags too. A category board and the round table are
    ///     drawn by different panels gated on different flags, so leaving IsCompositionBoard stale left
    ///     the utility board painted over the rounds it does not describe.
    /// </summary>
    [Test]
    [Arguments("Rounds")]
    [Arguments("Highlights")]
    [Arguments("Vision")]
    public async Task ViewSwitch_AnnouncesTheBoardFlags(string target)
    {
        StatsTabViewModel vm = BuildVm();
        vm.SelectedCategory = StatGroup.Utility;
        await Assert.That(vm.IsCompositionBoard).IsTrue();

        void Switch()
        {
            switch (target)
            {
                case "Rounds":
                    vm.IsRoundView = true;
                    break;
                case "Highlights":
                    vm.IsHighlightsView = true;
                    break;
                default:
                    vm.IsVisibilityView = true;
                    break;
            }
        }

        List<bool> board = Announcements(vm, nameof(StatsTabViewModel.IsCompositionBoard),
            () => vm.IsCompositionBoard, Switch);

        // The utility board is gone, and the panel drawing it has to have been told so.
        await Assert.That(vm.IsCompositionBoard).IsFalse();
        await Assert.That(board).IsNotEmpty();
        await Assert.That(board[^1]).IsFalse();
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
        yield return (StatGroup.Accuracy, "accuracy");
        yield return (StatGroup.AimQuality, "aim-quality");
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
                         .Concat(_otherColumns).Concat(_aimColumns))
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

            int aimBase = otherBase + _otherColumns.Length;
            for (int a = 0; a < _aimColumns.Length; a++)
            {
                vec[colIdx[p][aimBase + a]] = Snap(AimValue(_aimColumns[a], p));
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

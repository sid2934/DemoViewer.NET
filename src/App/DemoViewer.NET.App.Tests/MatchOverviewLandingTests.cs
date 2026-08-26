#region

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CS2DemoKit.Analysis.Output;
using DemoViewer.NET.Modules;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Match Overview landing page — the surface that makes a library double-click feel instant. Two
///     properties are load-bearing and each has a gate here:
///     <list type="number">
///         <item>
///             <b>No layout jump.</b> Every section exists from the first rendered frame and only its values
///             change, so the page's total content height is IDENTICAL before the parse, after the parse and
///             after analysis. This is asserted on the measured content height — the actual thing a user sees
///             move — because element-presence assertions alone would pass even if a card doubled in height.
///         </item>
///         <item>
///             <b>The three stages actually advance</b> across a real open through the real load funnel, and
///             the post-analysis score / scoreboard match what the Stats tab derived (one evaluation, one set
///             of numbers — a second projection would be a second thing to drift).
///         </item>
///     </list>
/// </summary>
[NotInParallel]
public class MatchOverviewLandingTests
{
    // Fills what SetSummary(ParsedDemo) fills, without needing a real ParsedDemo — the roster shape of a
    // standard 5v5 with one filler bot per side.
    private static void ApplyParsedStage(MatchOverviewTabViewModel vm)
    {
        vm.DurationDisplay = "42:18";
        vm.TickRateDisplay = "64";
        vm.PlayerCountDisplay = "10";
        foreach (string n in new[] { "s1mple", "b1t", "electroNic", "Perfecto", "BOT Rock" })
        {
            vm.CounterTerrorists.Add(new OverviewPlayer(n, n.StartsWith("BOT", StringComparison.Ordinal)));
        }

        foreach (string n in new[] { "ZywOo", "apEX", "flameZ", "mezii", "BOT Wolf" })
        {
            vm.Terrorists.Add(new OverviewPlayer(n, n.StartsWith("BOT", StringComparison.Ordinal)));
        }

        vm.HasSummary = true;
        // A live parse always yields the team split, so the roster gate lands with the summary; the two only
        // diverge in a cached render of a migrated row (names, no teams).
        vm.HasRoster = true;
        vm.ParseStage.IsActive = false;
        vm.ParseStage.IsDone = true;
        vm.EnrichStage.IsActive = true;
        vm.SetStage(vm.SubjectKey, "Preparing playback and navigation…", 0.45);
    }

    // A cache record at an arbitrary tier, without touching the store or the filesystem.
    private static DemoCacheRecord Record(
        DemoCacheTier tier,
        DemoAnalysisState analysisState = DemoAnalysisState.Pending,
        bool teamSplit = true,
        string path = "/demos/cached_de_dust2.dem")
    {
        DemoCacheRecord r = new()
        {
            Path = path,
            Size = 123456,
            ModifiedTicks = 638000000000000000
        };

        if (tier >= DemoCacheTier.Header)
        {
            r.Header = new TierStamp { Schema = DemoCacheRecord.HeaderSchema, ComputedAtTicks = 1 };
            r.Map = "de_dust2";
            r.Server = "FACEIT Server EU #4021";
        }

        if (tier >= DemoCacheTier.Parse)
        {
            r.Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 };
            r.DurationSeconds = 2292;
            r.TickRate = 64;
            r.TickCount = 146688;
            r.CtScore = 13;
            r.TScore = 9;
            r.CtClan = "NAVI";
            r.TClan = "FaZe";
            for (int i = 0; i < 24; i++)
            {
                r.Rounds.Add(new CachedRound { Number = i + 1, StartTickFrameClock = 1000 + (i * 5000) });
            }

            string[] ct = ["s1mple", "b1t", "electroNic", "Perfecto", "BOT Rock"];
            string[] t = ["ZywOo", "apEX", "flameZ", "mezii", "BOT Wolf"];
            int slot = 0;
            foreach (string n in ct)
            {
                r.Players.Add(new CachedPlayerInfo
                {
                    Slot = slot++,
                    Name = n,
                    SteamId64 = "7656119800000000" + slot,
                    // A MIGRATED row has names with no team — the case the roster cards must present as
                    // "team split needs a re-index" rather than as two empty teams.
                    Team = teamSplit ? 3 : 0,
                    IsBot = n.StartsWith("BOT", StringComparison.Ordinal)
                });
            }

            foreach (string n in t)
            {
                r.Players.Add(new CachedPlayerInfo
                {
                    Slot = slot++,
                    Name = n,
                    SteamId64 = "7656119800000000" + slot,
                    Team = teamSplit ? 2 : 0,
                    IsBot = n.StartsWith("BOT", StringComparison.Ordinal)
                });
            }
        }

        r.AnalysisState = analysisState;
        if (tier >= DemoCacheTier.Analysis)
        {
            r.Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 };
            r.AnalysisRoundCount = 22;
            r.CtSideWins = 12;
            r.TSideWins = 10;
            for (int i = 0; i < 10; i++)
            {
                r.Scoreboard.Add(new CachedStatRow
                {
                    Slot = i,
                    Team = i < 5 ? 3 : 2,
                    Kills = 24 - i,
                    Deaths = 10 + i,
                    Assists = 5,
                    Adr = 90.5 - i,
                    Rating = 1.34 - (i * 0.05)
                });
            }

            r.Highlights.Add(new CachedHighlightEvent
            {
                RulesetId = "clutch",
                HighlightId = "ace",
                Tick = 61200,
                PlayerSlot = 0,
                RoundNumber = 12,
                RenderedTitle = "s1mple — ace (round 12)"
            });
            r.Highlights.Add(new CachedHighlightEvent
            {
                RulesetId = "clutch",
                HighlightId = "plant_kills",
                Tick = 54321,
                PlayerSlot = 0,
                RoundNumber = 7,
                RenderedTitle = "s1mple — 2 kills after the plant (round 7)"
            });
            r.Highlights.Add(new CachedHighlightEvent
            {
                RulesetId = "clutch",
                HighlightId = "retake_3k",
                Tick = 30110,
                PlayerSlot = 5,
                RoundNumber = 4,
                RenderedTitle = "ZywOo — 3k retake (round 4)"
            });
        }

        return r;
    }

    // A stand-in for the analysis engine's per-player match table, using the column keys the real
    // PlayerGameStatsProjector emits (see ColumnCatalogue) — 10 players, the standard full lobby.
    private static MetricTable GameTable()
    {
        (string Name, int Team, int K, int D, int A, double Adr, double Rating)[] rows =
        [
            ("s1mple", 3, 24, 14, 4, 92.4, 1.34),
            ("b1t", 3, 19, 15, 6, 78.1, 1.12),
            ("electroNic", 3, 17, 16, 8, 74.9, 1.08),
            ("Perfecto", 3, 12, 17, 9, 61.3, 0.92),
            ("BOT Rock", 3, 6, 19, 2, 34.0, 0.51),
            ("ZywOo", 2, 22, 16, 5, 88.7, 1.27),
            ("apEX", 2, 15, 18, 7, 69.2, 0.97),
            ("flameZ", 2, 14, 17, 6, 66.8, 0.95),
            ("mezii", 2, 11, 18, 10, 58.4, 0.88),
            ("BOT Wolf", 2, 5, 20, 3, 31.2, 0.47)
        ];

        return new MetricTable(
            "player_game",
            ["player_name", "team"],
            ["TotalK", "TotalD", "TotalA", "ADR", "HLTV", "CTW", "TW"],
            rows.Select(r => new MetricRow(
                    new Dictionary<string, object?> { ["player_name"] = r.Name, ["team"] = r.Team },
                    new Dictionary<string, object?>
                    {
                        ["TotalK"] = r.K,
                        ["TotalD"] = r.D,
                        ["TotalA"] = r.A,
                        ["ADR"] = r.Adr,
                        ["HLTV"] = r.Rating,
                        // Per-team round wins by side. The CT-ending team took 6 as CT + 7 as T = 13; the
                        // T-ending team 6 + 3 = 9. So the SIDE split (CT 12 / T 10) differs from the TEAM
                        // totals (13 / 9) — exactly the half-swap case the page must not conflate.
                        ["CTW"] = r.Team == 3 ? 6 : 6,
                        ["TW"] = r.Team == 3 ? 7 : 3
                    }))
                .ToList());
    }

    /// <summary>
    ///     THE anti-jump gate. Renders the real view and measures its total content height at the three load
    ///     points a user actually passes through. All three must be equal: the skeleton reserves the space that
    ///     the arriving values then occupy. A regression here (a section gated on HasSummary, a card without a
    ///     MinHeight) shows up as a height delta, which is precisely the flicker being prevented.
    /// </summary>
    /// <remarks>
    ///     Run at BOTH sides of the two-column breakpoint (1000px). The no-jump property has to hold within
    ///     each layout — the wide body reserves two columns whose heights differ, so a regression that only
    ///     shows when the right column is the taller one would slip past a single-width test.
    /// </remarks>
    [Test]
    [Arguments(1400)] // wide — two columns
    [Arguments(820)] // narrow — stacked
    public async Task ContentHeight_IsIdentical_BeforeParse_AfterParse_AndAfterAnalysis(int windowWidth)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            MatchOverviewTabView view = new()
            {
                DataContext = vm
            };
            // Short window ON PURPOSE, and the measurement is the content panel's DESIRED height, not the
            // ScrollViewer's Extent. Extent is clamped up to the viewport by the content presenter, so in a
            // window taller than the page every state reports the same clamped number and the whole test
            // passes vacuously (measured: viewport 1600 → extent 1544 in all three states, including the
            // empty skeleton). DesiredSize comes from a measure pass with infinite height, so it is the real
            // content height; the overflow assert below proves we are on the unclamped side.
            Window window = new()
            {
                Width = windowWidth,
                Height = 700,
                Content = view
            };
            window.Show();

            vm.BeginOpening("match730_pug_de_mirage_2024.dem", "Mirage", "Valve Counter-Strike 2 Server", "match730_pug_de_mirage_2024.dem");
            vm.SetStage(vm.SubjectKey, "Parsing demo…", 0.15);
            Pump();

            ScrollViewer scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Control content = (Control)scroller.Content!;
            double opening = content.DesiredSize.Height;
            await Assert.That(opening).IsGreaterThan(scroller.Viewport.Height)
                .Because("the content must overflow the viewport, or the height would be clamped and equal by construction");

            ApplyParsedStage(vm);
            Pump();
            double parsed = content.DesiredSize.Height;

            vm.BeginAnalysis(vm.SubjectKey);
            vm.SetAnalysis(vm.SubjectKey, GameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
            vm.SetTeamScores(vm.SubjectKey, 13, 9);
            Pump();
            double ready = content.DesiredSize.Height;
            Console.WriteLine(FormattableString.Invariant(
                $"[mo-height w={windowWidth}] viewport={scroller.Viewport.Height} narrow={vm.IsNarrow} opening={opening} parsed={parsed} ready={ready}"));

            using (Assert.Multiple())
            {
                await Assert.That(vm.IsNarrow).IsEqualTo(windowWidth < 1000)
                    .Because("the layout under test must actually be the one this width selects");
                await Assert.That(parsed).IsEqualTo(opening)
                    .Because("the parsed values fill reserved space — the rosters must not grow the page");
                await Assert.That(ready).IsEqualTo(opening)
                    .Because("the score + scoreboard fill reserved space — the page must not grow on analysis");
            }
        });
    }

    /// <summary>
    ///     The cached render must reserve the SAME space as the live one. This is the property that makes
    ///     "open the demo you were previewing" produce no visual discontinuity: the cached page IS the
    ///     skeleton the live fill lands into, so a preview that measured differently would make the open jump.
    /// </summary>
    [Test]
    [Arguments(1400)]
    [Arguments(820)]
    public async Task CachedRender_ReservesTheSameHeight_AsTheLivePage(int windowWidth)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            MatchOverviewTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = windowWidth,
                Height = 700,
                Content = view
            };
            window.Show();

            vm.BeginOpening("live.dem", "Mirage", "Server", "/demos/live.dem");
            Pump();
            ScrollViewer scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Control content = (Control)scroller.Content!;
            double live = content.DesiredSize.Height;

            // Every cached tier, including the richest one — an analysis-tier record fills the scoreboard AND
            // the highlight section, which is the state most likely to overflow a reserved slot.
            foreach (DemoCacheTier tier in new[]
                     { DemoCacheTier.Header, DemoCacheTier.Parse, DemoCacheTier.Analysis })
            {
                vm.SetCachedRecord(Record(tier,
                    tier == DemoCacheTier.Analysis ? DemoAnalysisState.Indexed : DemoAnalysisState.Pending));
                Pump();
                double cached = content.DesiredSize.Height;

                // Measure the HIGHLIGHTS card specifically. The page-level equality could in principle hold
                // while the moments column silently overflowed its reserve (the match column is the taller
                // one at every width), so assert the reserved slot itself rather than trusting the total.
                Border? hl = view.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.MinHeight == 300);
                Console.WriteLine(FormattableString.Invariant(
                    $"[mo-hl w={windowWidth}] tier={tier} card={hl?.Bounds.Height ?? -1:F1} rows={vm.HighlightGroups.Sum(g => g.Highlights.Count)}"));
                Console.WriteLine(FormattableString.Invariant(
                    $"[mo-cached w={windowWidth}] tier={tier} live={live:F1} cached={cached:F1}"));
                await Assert.That(cached).IsEqualTo(live)
                    .Because($"a {tier}-tier cached render must occupy the live page's reserved slots exactly");
            }

            // SEEDED-LIVE: a live page carrying cached highlights and a cached scoreboard. This state did not
            // exist before SeedFromCache — a live page's moments column was necessarily empty while loading,
            // so the reserved MinHeights were only ever measured against an empty section in this mode. It is
            // now the FIRST thing the user sees on opening an indexed demo, and it is the state most likely
            // to overflow a reserve, because it is loading chrome and full content at the same time.
            vm.Clear();
            Pump();
            vm.BeginOpening("live.dem", "Mirage", "Server", "/demos/live.dem");
            vm.SeedFromCache("/demos/live.dem",
                Record(DemoCacheTier.Analysis, DemoAnalysisState.Indexed, path: "/demos/live.dem"));
            Pump();
            double seeded = content.DesiredSize.Height;

            Console.WriteLine(FormattableString.Invariant(
                $"[mo-seeded w={windowWidth}] live={live:F1} seeded={seeded:F1} rows={vm.HighlightGroups.Sum(g => g.Highlights.Count)} stats={vm.PlayerStats.Count}"));

            using (Assert.Multiple())
            {
                await Assert.That(vm.HighlightGroups.Count).IsGreaterThan(0)
                    .Because("the point of the state is that it carries content while loading");
                await Assert.That(seeded).IsEqualTo(live)
                    .Because("seeding an opening demo must fill reserved slots, never grow the page");
            }
        });
    }

    /// <summary>
    ///     The staged fill over the REAL load funnel and a real demo: the three stages advance in order, the
    ///     page leaves its loading state only once analysis lands, and the score / scoreboard it shows are the
    ///     SAME numbers the Stats tab derived from that one evaluation.
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task RealOpen_AdvancesAllThreeStages_AndMatchesTheStatsTab()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty());
            try
            {
                MatchOverviewTabViewModel overview = vm.MatchOverviewTab;
                await Assert.That(overview.HasContent).IsFalse().Because("nothing is open yet");

                await vm.LoadDemoFromPathAsync(demo);
                // The final score resolves off the load path (CCSTeam replay / library entry) — wait for it,
                // since the round count reconciles against it.
                for (int i = 0; i < 400 && !overview.HasScore; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(50);
                }

                Dispatcher.UIThread.RunJobs();

                using (Assert.Multiple())
                {
                    await Assert.That(overview.ParseStage.IsDone).IsTrue();
                    await Assert.That(overview.EnrichStage.IsDone).IsTrue();
                    await Assert.That(overview.AnalyseStage.IsDone).IsTrue();
                    await Assert.That(overview.Stages.Any(s => s.IsActive)).IsFalse()
                        .Because("no stage is still running once the open completes");
                    await Assert.That(overview.IsLoading).IsFalse();
                    await Assert.That(overview.Failed).IsFalse();
                    await Assert.That(overview.Progress).IsEqualTo(1.0);
                    await Assert.That(overview.HasSummary).IsTrue();
                    await Assert.That(overview.HasAnalysis).IsTrue();
                }

                // Parse-stage values are real, not placeholders.
                using (Assert.Multiple())
                {
                    await Assert.That(overview.DurationDisplay)
                        .IsNotEqualTo(MatchOverviewTabViewModel.Placeholder);
                    await Assert.That(overview.TickRateDisplay)
                        .IsNotEqualTo(MatchOverviewTabViewModel.Placeholder);
                    await Assert.That(overview.PlayerCountDisplay)
                        .IsNotEqualTo(MatchOverviewTabViewModel.Placeholder);
                    await Assert.That(overview.CounterTerrorists.Count + overview.Terrorists.Count)
                        .IsGreaterThan(0);
                }

                // Analysis-stage values agree with the Stats tab — the no-drift invariant. Comparing the row
                // SET (not just the count) is what would catch a projection reading the wrong dimension key.
                await Assert.That(overview.PlayerStats.Count).IsEqualTo(vm.StatsTab.GameRows.Count)
                    .Because("both project the same analysis game table");
                await Assert.That(overview.PlayerStatsMessage).IsEmpty()
                    .Because("rows arrived, so the 'produced nothing' message must not also show");
                await Assert.That(overview.RoundCountDisplay)
                    .IsNotEqualTo(MatchOverviewTabViewModel.Placeholder);

                HashSet<string> statsNames = vm.StatsTab.GameRows
                    .Select(r => r.PlayerName)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (OverviewStatRow row in overview.PlayerStats)
                {
                    await Assert.That(statsNames.Contains(row.Name)).IsTrue()
                        .Because($"'{row.Name}' must be a player the Stats tab also scored");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    /// <summary>
    ///     A failure AFTER the parse keeps what already filled in. A demo that parsed but failed to analyse is
    ///     still worth showing, and blanking the page back to an error card would be the same jump in reverse.
    /// </summary>
    [Test]
    public async Task Fail_AfterParse_KeepsTheSummary_AndReportsTheStalledStage()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.BeginOpening("half_broken.dem", "Nuke", "Some server", "half_broken.dem");
            ApplyParsedStage(vm);
            vm.BeginAnalysis(vm.SubjectKey);
            vm.Fail(vm.SubjectKey, "The analysis engine ran out of memory.");

            using (Assert.Multiple())
            {
                await Assert.That(vm.Failed).IsTrue();
                await Assert.That(vm.HasSummary).IsTrue().Because("the parsed roster survives the failure");
                await Assert.That(vm.CounterTerrorists.Count).IsGreaterThan(0);
                await Assert.That(vm.StatusText).IsEqualTo("Loaded with errors");
                await Assert.That(vm.Stages.Any(s => s.IsActive)).IsFalse()
                    .Because("a failed open must not leave a stage spinning forever");
                await Assert.That(vm.AnalyseStage.IsDone).IsFalse();
                await Assert.That(vm.PlayerStatsMessage).IsNotEmpty()
                    .Because("the scoreboard slot must say why it is empty, not skeleton forever");
                await Assert.That(vm.CanExploreStats).IsFalse().Because("no viewStats action was supplied");
            }
        });
    }

    /// <summary>
    ///     Analysis that legitimately produces no per-player stats is a THIRD state, not a permanent skeleton:
    ///     the section stays put and says so.
    /// </summary>
    [Test]
    public async Task Analysis_WithNoRows_ShowsTheUnavailableMessage_InPlace()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.BeginOpening("no_stats.dem", "Ancient", "Some server", "no_stats.dem");
            ApplyParsedStage(vm);
            vm.BeginAnalysis(vm.SubjectKey);
            vm.SetAnalysis(vm.SubjectKey, null, null, 0);

            using (Assert.Multiple())
            {
                await Assert.That(vm.PlayerStats).IsEmpty();
                await Assert.That(vm.PlayerStatsMessage).IsNotEmpty();
                await Assert.That(vm.RoundCountDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
                await Assert.That(vm.AnalyseStage.IsDone).IsTrue().Because("it ran — it just found nothing");
                await Assert.That(vm.IsLoading).IsFalse();
                await Assert.That(vm.Failed).IsFalse().Because("no rows is not an error");
            }
        });
    }

    /// <summary>
    ///     A SECOND open whose analysis produces nothing must not inherit the FIRST demo's score. The shell
    ///     reads the score from StatsTab, whose derived-score dictionary is per-demo state, so a leak there —
    ///     or any caller handing over a stale dict beside a null table — would paint the previous match's
    ///     13 – 9 next to an empty scoreboard. A fresh-VM test cannot see this; only a sequential open can.
    /// </summary>
    [Test]
    public async Task SecondOpen_WithNoAnalysisTable_DoesNotInheritTheFirstDemosScore()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();

            // Demo A — a complete run, with its authoritative score.
            Dictionary<int, int?> scores = new() { [0] = 13, [1] = 9 };
            vm.BeginOpening("first.dem", "Mirage", "Server", "first.dem");
            ApplyParsedStage(vm);
            vm.BeginAnalysis(vm.SubjectKey);
            vm.SetAnalysis(vm.SubjectKey, GameTable(), scores, 22);
            vm.SetTeamScores(vm.SubjectKey, 13, 9);
            await Assert.That(vm.CtTeamScoreDisplay).IsEqualTo("13").Because("demo A really did finish 13-9");

            // Demo B — analysis ran but produced no table, and no score has resolved yet. The stale dict is
            // passed DELIBERATELY: that is what a caller whose per-demo state outlived the unload would do,
            // and the VM must not trust it.
            vm.BeginOpening("second.dem", "Nuke", "Server", "second.dem");
            ApplyParsedStage(vm);
            vm.BeginAnalysis(vm.SubjectKey);
            vm.SetAnalysis(vm.SubjectKey, null, scores, 22);

            using (Assert.Multiple())
            {
                await Assert.That(vm.CtTeamScoreDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder)
                    .Because("no table means no trustworthy score — showing the last demo's is worse than none");
                await Assert.That(vm.TTeamScoreDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
                await Assert.That(vm.CtSideWinsDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
                await Assert.That(vm.HasScore).IsFalse()
                    .Because("no score resolved for demo B — the plate must not still be showing demo A's");
                await Assert.That(vm.RoundCountDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder)
                    .Because("the round count comes from the same run and is equally untrustworthy");
                await Assert.That(vm.PlayerStats).IsEmpty();
                await Assert.That(vm.PlayerStatsMessage).IsNotEmpty();
            }
        });
    }

    /// <summary>
    ///     The general form of the defect above, and the reason the page carries a subject key at all: a fill
    ///     for demo A that arrives AFTER demo B has been opened must be dropped, not painted.
    ///     <para>
    ///         This is not hypothetical. <c>ResolveTeamNamesAsync</c> and the analysis run are async
    ///         continuations that routinely outlive the open that started them, while this VM is a singleton
    ///         owned by the shell — so a slow continuation for A lands on B's page. The previous guard was
    ///         <c>HasContent</c>, which cannot tell "a demo is open" from "THIS demo is open", so every one of
    ///         these pushes was accepted. The cached render makes the race routine rather than rare:
    ///         previewing B while A is still loading is a normal gesture.
    ///     </para>
    /// </summary>
    [Test]
    public async Task LateFillForThePreviousDemo_IsDropped()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();

            vm.BeginOpening("alpha.dem", "Mirage", "Server A", "/demos/alpha.dem");
            ApplyParsedStage(vm);

            // B replaces A while A's continuations are still in flight.
            vm.BeginOpening("bravo.dem", "Nuke", "Server B", "/demos/bravo.dem");

            // Everything below is A's work arriving late, each presenting A's key.
            vm.SetStage("/demos/alpha.dem", "Parsing demo…", 0.9);
            vm.SetTeamNames("/demos/alpha.dem", "NAVI", "FaZe");
            vm.SetTeamScores("/demos/alpha.dem", 13, 9);
            vm.SetAnalysis("/demos/alpha.dem", GameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
            vm.Fail("/demos/alpha.dem", "alpha exploded");

            using (Assert.Multiple())
            {
                await Assert.That(vm.SubjectKey).IsEqualTo("/demos/bravo.dem");
                await Assert.That(vm.FileName).IsEqualTo("bravo.dem");
                await Assert.That(vm.CtTeamScoreDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder)
                    .Because("alpha's score must not appear on bravo's plate");
                await Assert.That(vm.HasScore).IsFalse();
                await Assert.That(vm.CtTeamLabel).IsEqualTo("ENDED CT")
                    .Because("alpha's clan names must not relabel bravo's plate");
                await Assert.That(vm.PlayerStats).IsEmpty()
                    .Because("alpha's scoreboard must not appear under bravo's rosters");
                await Assert.That(vm.HasAnalysis).IsFalse();
                await Assert.That(vm.Failed).IsFalse()
                    .Because("alpha's failure is not bravo's failure");
                await Assert.That(vm.StatusText).IsNotEqualTo("Ready");
            }

            // And the guard is a filter, not a freeze: bravo's own fills still land.
            vm.SetTeamScores("/demos/bravo.dem", 7, 13);
            await Assert.That(vm.TTeamScoreDisplay).IsEqualTo("13");
        });
    }

    /// <summary>
    ///     A roster whose team split is not known yet must not claim the match had zero players on a side.
    ///     The header badges bound to <c>CounterTerrorists.Count</c>, which renders a confident "0" — the one
    ///     kind of number this page never prints. See <c>CtRosterCountDisplay</c>.
    /// </summary>
    [Test]
    public async Task RosterCountBadges_ShowThePlaceholder_BeforeTheRosterLands()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.BeginOpening("pending.dem", "Anubis", "Server", "pending.dem");

            using (Assert.Multiple())
            {
                await Assert.That(vm.CtRosterCountDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
                await Assert.That(vm.TRosterCountDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
            }

            ApplyParsedStage(vm);

            using (Assert.Multiple())
            {
                await Assert.That(vm.CtRosterCountDisplay).IsEqualTo("5");
                await Assert.That(vm.TRosterCountDisplay).IsEqualTo("5");
            }
        });
    }

    /// <summary>
    ///     The other half of the same defect, at its source: StatsTab's derived per-team score is per-demo
    ///     state and must be cleared on unload, exactly like the tables it is derived from.
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task StatsTab_ClearsItsDerivedScore_OnDemoUnload()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty());
            try
            {
                await vm.LoadDemoFromPathAsync(demo);
                await Assert.That(vm.StatsTab.GameTable).IsNotNull()
                    .Because("the reference demo analyses to a real table — otherwise this proves nothing");

                vm.StatsTab.ResetForDemoUnload();

                using (Assert.Multiple())
                {
                    await Assert.That(vm.StatsTab.GameTable).IsNull();
                    await Assert.That(vm.StatsTab.TeamScoresBySort).IsEmpty()
                        .Because("the derived score is per-demo state — it must not outlive the demo");
                    await Assert.That(vm.StatsTab.Rounds).IsEmpty();
                }
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    /// <summary>
    ///     The team-total vs side-total distinction, pinned against a real demo. A team's score is its total
    ///     across BOTH halves, attributed to it by the side it finished on; the per-side split is a different
    ///     number entirely, because sides swap. Presenting a team total under a bare side label was the
    ///     original defect — on the reference demo the team ending CT totalled 3 while the CT side won 15 of
    ///     16 rounds, so the old plate claimed "Counter-Terrorists 3" about a side that won 15.
    ///     <para>
    ///         Both pairs must total the round count, and this test also asserts they DIFFER on this demo —
    ///         a demo where they happened to coincide would let the bug back in unnoticed.
    ///     </para>
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task TeamTotals_AndSideTotals_AreDistinct_AndBothSumToTheRoundCount()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty());
            try
            {
                await vm.LoadDemoFromPathAsync(demo);
                MatchOverviewTabViewModel o = vm.MatchOverviewTab;
                for (int i = 0; i < 400 && !o.HasScore; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(50);
                }

                Dispatcher.UIThread.RunJobs();

                int rounds = int.Parse(o.RoundCountDisplay, CultureInfo.InvariantCulture);
                int ctTeam = int.Parse(o.CtTeamScoreDisplay, CultureInfo.InvariantCulture);
                int tTeam = int.Parse(o.TTeamScoreDisplay, CultureInfo.InvariantCulture);
                int ctSide = int.Parse(o.CtSideWinsDisplay, CultureInfo.InvariantCulture);
                int tSide = int.Parse(o.TSideWinsDisplay, CultureInfo.InvariantCulture);

                using (Assert.Multiple())
                {
                    await Assert.That(ctTeam + tTeam).IsEqualTo(rounds)
                        .Because("every round was won by one of the two teams");
                    await Assert.That(ctSide + tSide).IsEqualTo(rounds)
                        .Because("every round was also won from one of the two sides");
                    await Assert.That(ctTeam).IsNotEqualTo(ctSide)
                        .Because("this demo has a half swap — if these matched, the test could not tell a "
                                 + "team total from a side total and the original defect would slip back in");
                }

                // The team totals come from CCSTeam.m_iScore — the same source the Library card badge reads.
                // MatchOverviewScoreSourceTests owns that equivalence across every available demo.
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    /// <summary>
    ///     The progress bar must not sit frozen through a multi-second stage. While a stage is in flight the
    ///     VM creeps the value toward that stage's ceiling, and the two properties that keep the creep honest
    ///     are asserted here: it MOVES, and it never reaches or passes the ceiling (so it can never claim a
    ///     stage is nearly done when the stage has barely started, and finishing a stage always produces real
    ///     forward motion). It must also stop dead once the load is over.
    /// </summary>
    [Test]
    public async Task Progress_CreepsWithinAStage_WithoutReachingItsCeiling_AndStopsWhenDone()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.BeginOpening("creep.dem", "Mirage", "Server", "creep.dem");
            double atStart = vm.Progress;

            // Real elapsed time: the creep runs on a DispatcherTimer, so it needs the clock to advance.
            await PumpForAsync(900);
            double crept = vm.Progress;

            using (Assert.Multiple())
            {
                await Assert.That(crept).IsGreaterThan(atStart)
                    .Because("a stage in flight must show motion, not a frozen bar");
                await Assert.That(crept).IsLessThan(0.45)
                    .Because("the creep must never reach the parse stage's ceiling — only finishing it may");
            }

            // A coarse shell nudge behind the creep must not drag the bar backwards.
            vm.SetStage(vm.SubjectKey, "Parsing demo…", 0.15);
            await Assert.That(vm.Progress).IsGreaterThanOrEqualTo(crept)
                .Because("progress only ever moves forward");

            // Finishing the load stops the creep and pins the bar at full.
            ApplyParsedStage(vm);
            vm.BeginAnalysis(vm.SubjectKey);
            vm.SetAnalysis(vm.SubjectKey, GameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
            vm.SetTeamScores(vm.SubjectKey, 13, 9);
            await Assert.That(vm.Progress).IsEqualTo(1.0);

            await PumpForAsync(400);
            await Assert.That(vm.Progress).IsEqualTo(1.0)
                .Because("the creep timer must be stopped — a finished load must not keep ticking");
        });
    }

    /// <summary>
    ///     The "sample clip" banner flags ONLY the bundled tour sample, through the real load funnel: opening
    ///     the sample path sets it (and it survives a failed parse — the label is about WHAT was opened, not
    ///     whether it loaded), opening any other demo clears it. Garbage bytes on purpose: banner truth must
    ///     not depend on load outcome.
    /// </summary>
    [Test]
    public async Task SampleClipBanner_FlagsOnlyTheBundledSampleOpen()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            string sample = Path.Combine(
                Path.GetTempPath(), "dvmo_sample_" + Guid.NewGuid().ToString("N") + ".dem");
            string other = Path.Combine(
                Path.GetTempPath(), "dvmo_other_" + Guid.NewGuid().ToString("N") + ".dem");
            await File.WriteAllBytesAsync(sample, [1, 2, 3]);
            await File.WriteAllBytesAsync(other, [1, 2, 3]);

            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(),
                tourSampleLocator: () => sample);
            try
            {
                await vm.LoadDemoFromPathAsync(sample);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(vm.MatchOverviewTab.IsSampleClip).IsTrue()
                    .Because("the opened path is the bundled sample, even though the parse failed");

                await vm.LoadDemoFromPathAsync(other);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(vm.MatchOverviewTab.IsSampleClip).IsFalse()
                    .Because("any other demo must not carry the sample-clip banner");
            }
            finally
            {
                vm.Dispose();
                File.Delete(sample);
                File.Delete(other);
            }
        });
    }

    /// <summary>
    ///     The player count is the count of people who PLAYED. Regression for the reported bug: every
    ///     demo showed 11 for a 10-player match, because the GOTV proxy occupies a <c>userinfo</c> slot
    ///     with a name and was counted — while never appearing in either roster (it has no team), so
    ///     the headline number disagreed with the rosters printed directly beneath it.
    ///     <para>
    ///         End-to-end against the bundled sample (a real demo, sub-second parse) rather than a
    ///         stubbed roster: the bug lived in the parse → <see cref="MatchOverviewTabViewModel.SetSummary" />
    ///         path, which a hand-filled view-model cannot exercise.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetSummary_FromARealDemo_CountsPlayers_NotTheGotvProxy()
    {
        string? sample = TourDemoLocator.FindSampleDemo();
        await Assert.That(sample).IsNotNull().Because("the bundled sample is committed under assets/tour");

        ParsedDemo parsed = DemoParser.Parse(await File.ReadAllBytesAsync(sample!));
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening(Path.GetFileName(sample!), null, null, Path.GetFileName(sample!));
        vm.SetSummary(vm.SubjectKey, parsed);

        // The demo carries MORE userinfo entries than players: the GOTV proxies each occupy a slot.
        // Derived, not pinned — the bundled sample's source match can change (it has: a matchmaking
        // demo with one 'DemoRecorder' became a tournament demo with two 'CSTV' proxies), and a
        // hardcoded total turns that into a failure that looks like a regression but is not one.
        List<PlayerInfo> proxies = parsed.Players.Values.Where(p => p.IsHltv).ToList();
        await Assert.That(proxies).IsNotEmpty()
            .Because("the sample is a GOTV recording, so at least one proxy holds a userinfo slot");
        await Assert.That(parsed.Players.Count).IsEqualTo(10 + proxies.Count)
            .Because("the proxies still occupy their slots; they are excluded at the presentation layer, not dropped");

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerCountDisplay).IsEqualTo("10");
            await Assert.That(vm.CounterTerrorists.Count).IsEqualTo(5);
            await Assert.That(vm.Terrorists.Count).IsEqualTo(5);
            // No proxy, whatever it is named, reaches a roster.
            IReadOnlyList<string> rostered = vm.CounterTerrorists.Concat(vm.Terrorists).Select(p => p.Name).ToList();
            foreach (PlayerInfo proxy in proxies)
            {
                await Assert.That(rostered).DoesNotContain(proxy.Name);
            }
        }

        // The invariant the bug broke: the headline number must equal the rosters shown beneath it.
        await Assert.That(vm.PlayerCountDisplay)
            .IsEqualTo((vm.Terrorists.Count + vm.CounterTerrorists.Count).ToString(CultureInfo.InvariantCulture))
            .Because("a count that disagrees with the visible rosters is what made this look broken");
    }

    /// <summary>
    ///     The highlight card is reserved AND bounded. A roster caps at ~5 and a scoreboard at ~10, so their
    ///     overflow is rare; a demo can fire thirty highlights, and an unbounded card would dwarf the page.
    ///     Beyond the bound the inner ScrollViewer has to take over instead.
    /// </summary>
    [Test]
    public async Task HighlightCard_ReservesItsSlot_AndIsBoundedAbove()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            MatchOverviewTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1400,
                Height = 700,
                Content = view
            };
            window.Show();

            static Border Card(MatchOverviewTabView v) =>
                v.GetVisualDescendants().OfType<Border>().First(b => b.MinHeight == 300);

            // Empty: holds the reserve.
            vm.SetCachedRecord(Record(DemoCacheTier.Parse));
            Pump();
            await Assert.That(Card(view).Bounds.Height).IsEqualTo(300)
                .Because("an un-analysed demo still reserves the slot");

            // Forty highlights across four players: bounded, and scrolling internally.
            DemoCacheRecord big = Record(DemoCacheTier.Analysis, DemoAnalysisState.Indexed);
            big.Highlights.Clear();
            for (int i = 0; i < 40; i++)
            {
                big.Highlights.Add(new CachedHighlightEvent
                {
                    RulesetId = "clutch",
                    HighlightId = "multi",
                    Tick = 10_000 + (i * 500),
                    PlayerSlot = i % 4,
                    RoundNumber = (i % 22) + 1,
                    RenderedTitle = $"highlight number {i + 1} with a fairly long rendered title"
                });
            }

            vm.SetCachedRecord(big);
            Pump();
            double tall = Card(view).Bounds.Height;
            Console.WriteLine(FormattableString.Invariant($"[mo-hl-big] card={tall:F1} rows=40"));
            await Assert.That(tall).IsLessThanOrEqualTo(620)
                .Because("forty highlights must scroll inside the card, not stretch the page arbitrarily");
        });
    }

    /// <summary>
    ///     NO STATE MAY CONTRADICT ITSELF ABOUT WHETHER ANALYSIS RAN. The completeness chip and the two slot
    ///     messages describe the same fact from different places, so they have to agree.
    ///     <para>
    ///         This caught a real one: a finished live open set <c>Completeness = Full</c> while the highlight
    ///         card said "needs a full analysis pass" — a FULL chip above a not-analysed card, on one screen,
    ///         about one demo. The cause is genuine (the interactive pipeline fills the scoreboard; highlights
    ///         come from the separate rules pass the cache stores), so the fix was to stop claiming Full for a
    ///         live open and to give the highlight card its own honest sentence and action.
    ///     </para>
    /// </summary>
    [Test]
    public async Task NoState_ClaimsFullWhileASlotSaysAnalysisIsMissing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            static async Task Check(MatchOverviewTabViewModel vm, string state)
            {
                bool claimsComplete = vm.Completeness == OverviewCompleteness.Full;
                bool aSlotSaysMissing =
                    vm.PlayerStatsMessage.Contains("analysis pass", StringComparison.OrdinalIgnoreCase)
                    || vm.HighlightsMessage.Contains("analysis pass", StringComparison.OrdinalIgnoreCase);

                await Assert.That(claimsComplete && aSlotSaysMissing).IsFalse()
                    .Because($"[{state}] the chip reads '{vm.CompletenessLabel}' while a slot says analysis is "
                             + $"missing (stats: '{vm.PlayerStatsMessage}' / highlights: '{vm.HighlightsMessage}')");

                // And the converse: any slot that says something is missing must offer a way to fix it.
                if (vm.HighlightsMessage.Length > 0 && !vm.HasHighlights)
                {
                    await Assert.That(vm.HighlightsActionLabel).IsNotNull()
                        .Because($"[{state}] a slot that says it is empty must name the action that fills it");
                }
            }

            // Every live milestone.
            MatchOverviewTabViewModel live = new(computeFullStats: _ => { });
            live.BeginOpening("live.dem", "Mirage", "Server", "/demos/live.dem");
            await Check(live, "live/opening");
            ApplyParsedStage(live);
            await Check(live, "live/parsed");
            live.BeginAnalysis(live.SubjectKey);
            live.SetAnalysis(live.SubjectKey, GameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
            live.SetTeamScores(live.SubjectKey, 13, 9);
            await Check(live, "live/ready");
            await Assert.That(live.Completeness).IsNotEqualTo(OverviewCompleteness.Full)
                .Because("a finished OPEN is not a full cache record — highlights come from a different pass");

            // DELIBERATE REVERSAL — this asserted IsTrue, and its premise has since changed.
            //
            // It was written when a live open did not harvest highlights at all, so the card's honest
            // sentence was "needs a full analysis pass" and the user genuinely needed a button. An open now
            // harvests them unconditionally (Analysis.EvaluationCompleted → OnOpenDemoEvaluated), just
            // off-thread, landing after this point. The card therefore says "Harvesting highlights…", and an
            // action under THAT would contradict the sentence above it and, if pressed, queue a second full
            // pass over the demo already being harvested — a redundant parse plus snapshot analysis through
            // a gate that allows one heavy job machine-wide.
            await Assert.That(live.HasHighlightsAction).IsFalse()
                .Because("the open is already harvesting — offering to start it again is both a "
                         + "contradiction and a wasted heavy pass");
            await Assert.That(live.HighlightsMessage).IsEqualTo("Harvesting highlights…")
                .Because("which is only defensible while that sentence is actually true");

            // Every cached tier, including the failed one.
            foreach ((DemoCacheTier tier, DemoAnalysisState st) in new[]
                     {
                         (DemoCacheTier.Header, DemoAnalysisState.Pending),
                         (DemoCacheTier.Parse, DemoAnalysisState.Pending),
                         (DemoCacheTier.Analysis, DemoAnalysisState.Failed),
                         (DemoCacheTier.Analysis, DemoAnalysisState.Indexed)
                     })
            {
                MatchOverviewTabViewModel c = new(computeFullStats: _ => { });
                c.SetCachedRecord(Record(tier, st));
                await Check(c, $"cached/{tier}/{st}");
            }
        });
    }

    /// <summary>
    ///     A cached render shows every stage PENDING with zero progress. Nothing ran, so claiming a completed
    ///     pipeline would be the page's own honesty rule broken from the inside — the completeness chip is
    ///     what carries the real state, and marking the strip done would also make a subsequent real open
    ///     look like it had already finished.
    /// </summary>
    [Test]
    public async Task CachedRender_LeavesEveryStagePending_AndCarriesStateOnTheChip()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.SetCachedRecord(Record(DemoCacheTier.Analysis, DemoAnalysisState.Indexed));

            using (Assert.Multiple())
            {
                await Assert.That(vm.Mode).IsEqualTo(OverviewMode.Cached);
                await Assert.That(vm.Progress).IsEqualTo(0.0);
                await Assert.That(vm.IsLoading).IsFalse();
                await Assert.That(vm.Stages.Any(s => s.IsDone)).IsFalse()
                    .Because("no stage ran — a cached page must not claim a pipeline it never executed");
                await Assert.That(vm.Stages.Any(s => s.IsActive)).IsFalse();
                await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Full);
                await Assert.That(vm.CompletenessLabel).IsEqualTo("FULL");
            }
        });
    }

    /// <summary>
    ///     The tier → completeness map, and the rule that every incomplete state names ONE action. This is
    ///     the page's whole answer to partial fill: the user is told which tier is missing and given the one
    ///     button that fills it, instead of being left to infer it from blank cards.
    /// </summary>
    [Test]
    public async Task Completeness_ClassifiesEachTier_AndOffersTheActionThatAdvancesIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            List<string> computed = [];
            MatchOverviewTabViewModel vm = new(computeFullStats: computed.Add);

            vm.SetCachedRecord(Record(DemoCacheTier.Header));
            using (Assert.Multiple())
            {
                await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.NotIndexed);
                await Assert.That(vm.CompletenessActionLabel).IsEqualTo("Index this demo");
                await Assert.That(vm.IsCompletenessOff).IsTrue();
            }

            vm.SetCachedRecord(Record(DemoCacheTier.Parse));
            using (Assert.Multiple())
            {
                await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Indexed);
                await Assert.That(vm.CompletenessActionLabel).IsEqualTo("Compute full stats");
                await Assert.That(vm.IsCompletenessPartial).IsTrue()
                    .Because("indexed is the HOLLOW ring — partial, not the whole story");
                await Assert.That(vm.PlayerStatsMessage).IsEqualTo("Player stats need a full analysis pass.");
                await Assert.That(vm.HighlightsMessage).IsEqualTo("Highlights need a full analysis pass.");
            }

            vm.SetCachedRecord(Record(DemoCacheTier.Analysis, DemoAnalysisState.Failed));
            using (Assert.Multiple())
            {
                await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Failed);
                await Assert.That(vm.CompletenessActionLabel).IsEqualTo("Retry");
                await Assert.That(vm.IsCompletenessError).IsTrue();
            }

            vm.SetCachedRecord(Record(DemoCacheTier.Analysis, DemoAnalysisState.Indexed));
            using (Assert.Multiple())
            {
                await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Full);
                await Assert.That(vm.CompletenessActionLabel).IsNull()
                    .Because("a full record has nothing left to advance");
                await Assert.That(vm.HasCompletenessAction).IsFalse();
            }

            // The action enqueues the SUBJECT, and does not open it: computing and opening are different
            // intents, and conflating them would make a glance at the cache cost a full load.
            vm.SetCachedRecord(Record(DemoCacheTier.Parse));
            vm.ComputeFullStatsCommand.Execute(null);
            await Assert.That(computed).IsEquivalentTo(new List<string> { "/demos/cached_de_dust2.dem" });
        });
    }

    /// <summary>
    ///     A tier-2 cached record fills the facts, rosters and score for real — the payoff of extending the
    ///     cheap pass rather than making the rules pass ambient. Only the scoreboard and highlights wait.
    /// </summary>
    [Test]
    public async Task CachedParseTier_FillsFactsRostersAndScore_ButNotTheScoreboard()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.SetCachedRecord(Record(DemoCacheTier.Parse));

            using (Assert.Multiple())
            {
                await Assert.That(vm.MapDisplay).IsEqualTo("Dust2")
                    .Because("the cached map name goes through the same DemoEntry.PrettifyMap the Library uses");
                await Assert.That(vm.DurationDisplay).IsEqualTo("38:12");
                await Assert.That(vm.TickRateDisplay).IsEqualTo("64");
                await Assert.That(vm.RoundCountDisplay).IsEqualTo("24");
                await Assert.That(vm.PlayerCountDisplay).IsEqualTo("10");
                await Assert.That(vm.CounterTerrorists.Count).IsEqualTo(5);
                await Assert.That(vm.Terrorists.Count).IsEqualTo(5);
                await Assert.That(vm.CtRosterCountDisplay).IsEqualTo("5");
                await Assert.That(vm.CtTeamScoreDisplay).IsEqualTo("13");
                await Assert.That(vm.TTeamScoreDisplay).IsEqualTo("9");
                await Assert.That(vm.CtTeamLabel).IsEqualTo("NAVI");
                await Assert.That(vm.HasScore).IsTrue();
                await Assert.That(vm.RosterMessage).IsEmpty();
                // The tiers that genuinely are not there.
                await Assert.That(vm.PlayerStats).IsEmpty();
                await Assert.That(vm.HighlightGroups).IsEmpty();
                await Assert.That(vm.HasSideSplit).IsFalse();
            }
        });
    }

    /// <summary>
    ///     A MIGRATED legacy row has player NAMES but no teams. The rosters must say so rather than draw two
    ///     empty teams — and, critically, the header badges must not assert a confident "0", the one kind of
    ///     number this page never prints.
    /// </summary>
    [Test]
    public async Task CachedRecord_WithoutTeamSplit_SaysSo_AndNeverClaimsZeroPlayers()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.SetCachedRecord(Record(DemoCacheTier.Parse, teamSplit: false));

            using (Assert.Multiple())
            {
                await Assert.That(vm.HasRoster).IsFalse();
                await Assert.That(vm.CtRosterCountDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
                await Assert.That(vm.TRosterCountDisplay).IsEqualTo(MatchOverviewTabViewModel.Placeholder);
                await Assert.That(vm.RosterMessage).IsEqualTo("Team split needs a re-index.");
                await Assert.That(vm.CounterTerrorists).IsEmpty();
                await Assert.That(vm.Terrorists).IsEmpty();
                // The facts the row DOES carry are still real — a missing split is not a missing demo.
                await Assert.That(vm.PlayerCountDisplay).IsEqualTo("10");
                await Assert.That(vm.DurationDisplay).IsEqualTo("38:12");
            }
        });
    }

    /// <summary>
    ///     Highlights are joined to the roster by SLOT (the unified record does not repeat a name per event),
    ///     grouped per player CT-block first, and ordered by tick within a player. Verify is offered only in
    ///     LIVE mode: on a cached page the demo shown is not the demo CS2 has loaded, so seeking would play
    ///     the wrong moment — the Highlights tab's demo-identity rule, arriving here for free.
    /// </summary>
    [Test]
    public async Task CachedHighlights_JoinBySlot_GroupPerPlayer_AndCannotVerifyFromACachedPage()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new(isVerifyPresent: () => true);
            vm.SetCachedRecord(Record(DemoCacheTier.Analysis, DemoAnalysisState.Indexed));

            using (Assert.Multiple())
            {
                await Assert.That(vm.HighlightGroups.Count).IsEqualTo(2);
                await Assert.That(vm.HighlightCountDisplay).IsEqualTo("3 highlights");
                // CT block first (s1mple is slot 0, team 3), then T.
                await Assert.That(vm.HighlightGroups[0].PlayerName).IsEqualTo("s1mple");
                await Assert.That(vm.HighlightGroups[0].IsCt).IsTrue();
                await Assert.That(vm.HighlightGroups[1].PlayerName).IsEqualTo("ZywOo");
                // Within a player, ordered by tick — not by the order they were harvested.
                await Assert.That(vm.HighlightGroups[0].Highlights[0].Tick).IsEqualTo(54321);
                await Assert.That(vm.HighlightGroups[0].Highlights[1].Tick).IsEqualTo(61200);
                await Assert.That(vm.HighlightGroups[0].Highlights[0].RoundDisplay).IsEqualTo("r7");
                await Assert.That(vm.HighlightGroups[0].CountDisplay).IsEqualTo("2");
                // Present (the chrome.livesync gate is on) but not offerable from a cached page.
                await Assert.That(vm.HighlightGroups[0].Highlights[0].VerifyPresent).IsTrue();
                await Assert.That(vm.HighlightGroups[0].Highlights[0].CanVerify).IsFalse();
                // The scoreboard joins by slot too.
                await Assert.That(vm.PlayerStats.Count).IsEqualTo(10);
                await Assert.That(vm.PlayerStats[0].Name).IsEqualTo("s1mple");
                await Assert.That(vm.HasSideSplit).IsTrue()
                    .Because("12 + 10 reconciles against the 13 + 9 score");
            }
        });
    }

    /// <summary>
    ///     The mode guard, which is the other half of the subject key: a LIVE pipeline push must not land on a
    ///     cached page even when the keys agree (the user previewed the very demo that is also open).
    ///     Accepting it would restart the stage strip under a page that says "cached".
    /// </summary>
    [Test]
    public async Task LivePush_ForTheSameDemo_IsDroppedWhileThePageIsCached()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.SetCachedRecord(Record(DemoCacheTier.Parse));
            string key = vm.SubjectKey!;

            vm.SetStage(key, "Parsing demo…", 0.5);
            vm.BeginAnalysis(key);
            vm.SetAnalysis(key, GameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
            vm.Fail(key, "boom");

            using (Assert.Multiple())
            {
                await Assert.That(vm.Mode).IsEqualTo(OverviewMode.Cached);
                await Assert.That(vm.Progress).IsEqualTo(0.0);
                await Assert.That(vm.Failed).IsFalse();
                await Assert.That(vm.PlayerStats).IsEmpty()
                    .Because("a live analysis push must not fill a cached page's scoreboard");
                await Assert.That(vm.Stages.Any(s => s.IsDone)).IsFalse();
            }

            // And a real open takes the page back — mode is a filter, not a latch.
            vm.BeginOpening("cached_de_dust2.dem", "Dust II", "Server", key);
            await Assert.That(vm.Mode).IsEqualTo(OverviewMode.Live);
            vm.SetStage(key, "Parsing demo…", 0.3);
            await Assert.That(vm.Progress).IsGreaterThanOrEqualTo(0.3);
        });
    }

    /// <summary>
    ///     The explore CTAs are mode-gated: from a cached preview the Stats and 2D tabs hold a DIFFERENT demo
    ///     (or none), so offering the jump would land the user on another match's scoreboard. The cached page
    ///     offers "Open this demo" instead.
    /// </summary>
    [Test]
    public async Task ExploreCtas_AreOfferedOnlyForTheOpenDemo_NotForAPreview()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            List<string> opened = [];
            MatchOverviewTabViewModel vm = new(
                viewStats: () => { }, viewPlayback: () => { }, openDemo: opened.Add);

            vm.SetCachedRecord(Record(DemoCacheTier.Analysis, DemoAnalysisState.Indexed));
            using (Assert.Multiple())
            {
                await Assert.That(vm.HasAnalysis).IsTrue().Because("the record is full — the data is there");
                await Assert.That(vm.CanExploreStats).IsFalse()
                    .Because("the Stats tab is not showing this demo; it is showing whatever is open");
                await Assert.That(vm.CanExplorePlayback).IsFalse();
                await Assert.That(vm.CanOpenDemo).IsTrue();
            }

            vm.OpenDemoCommand.Execute(null);
            await Assert.That(opened).IsEquivalentTo(new List<string> { "/demos/cached_de_dust2.dem" });

            // A real open re-enables them through the normal fill path.
            vm.BeginOpening("x.dem", "Nuke", "Server", "/demos/x.dem");
            ApplyParsedStage(vm);
            vm.BeginAnalysis(vm.SubjectKey);
            vm.SetAnalysis(vm.SubjectKey, GameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
            using (Assert.Multiple())
            {
                await Assert.That(vm.CanExploreStats).IsTrue();
                await Assert.That(vm.CanExplorePlayback).IsTrue();
                await Assert.That(vm.CanOpenDemo).IsFalse().Because("it is already open");
            }
        });
    }

    private static async Task PumpForAsync(int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 30)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}

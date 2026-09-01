#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Opening a demo paints what the cache already knows BEFORE the parse runs, and tier 3's two halves are
///     reported independently.
///     <para>
///         The mode model always claimed "the cached render IS the skeleton the live fill lands into", but
///         nothing ever seeded a live open from the record: opening a demo you had just been previewing blanked
///         the page and made you watch a multi-second skeleton rebuild facts that were sitting in a sidecar
///         file. These tests hold that seam shut.
///     </para>
/// </summary>
public class MatchOverviewSeedTests
{
    private const string Demo = "/demos/seed.dem";

    private static DemoCacheRecord Indexed(bool withHighlights = true, bool withScoreboard = false) =>
        new()
        {
            Path = Demo,
            Size = 10,
            ModifiedTicks = 20,
            Map = "de_dust2",
            Server = "FACEIT",
            Parse = new TierStamp
            {
                Schema = DemoCacheRecord.ParseSchema,
                ComputedAtTicks = 1
            },
            DurationSeconds = 2298,
            TickRate = 64,
            Players =
            [
                new CachedPlayerInfo
                {
                    Slot = 1,
                    Name = "s1mple",
                    SteamId64 = "765",
                    Team = 3
                },
                new CachedPlayerInfo
                {
                    Slot = 2,
                    Name = "ZywOo",
                    SteamId64 = "766",
                    Team = 2
                }
            ],
            Rounds =
            [
                new CachedRound
                {
                    Number = 1,
                    StartTickFrameClock = 5000
                }
            ],
            CtScore = 13,
            TScore = 9,
            CtClan = "NAVI",
            TClan = "FaZe",
            Analysis = withHighlights || withScoreboard
                ? new TierStamp
                {
                    Schema = DemoCacheRecord.AnalysisSchema,
                    ComputedAtTicks = 1
                }
                : new TierStamp(),
            AnalysisState = withHighlights || withScoreboard
                ? DemoAnalysisState.Indexed
                : DemoAnalysisState.Pending,
            Highlights = withHighlights
                ?
                [
                    new CachedHighlightEvent
                    {
                        RulesetId = "clutch",
                        HighlightId = "ace",
                        Tick = 54_000,
                        PlayerSlot = 1,
                        RoundNumber = 7,
                        RenderedTitle = "s1mple — ace"
                    }
                ]
                : [],
            Scoreboard = withScoreboard
                ?
                [
                    new CachedStatRow
                    {
                        Slot = 1,
                        Team = 3,
                        Kills = 24,
                        Deaths = 14,
                        Assists = 5,
                        Adr = 92.5,
                        Rating = 1.34
                    }
                ]
                : []
        };

    /// <summary>
    ///     The reported symptom: a demo with a cache record showed nothing until its parse finished.
    /// </summary>
    [Test]
    public async Task OpeningAnIndexedDemo_ShowsItsCachedFactsBeforeTheParse()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("seed.dem", null, null, Demo);
        vm.SeedFromCache(Demo, Indexed());

        using (Assert.Multiple())
        {
            await Assert.That(vm.CounterTerrorists.Count).IsEqualTo(1);
            await Assert.That(vm.Terrorists.Count).IsEqualTo(1);
            await Assert.That(vm.HasScore).IsTrue();
            await Assert.That(vm.CtTeamLabel).IsEqualTo("NAVI");
            await Assert.That(vm.HighlightGroups.Count).IsEqualTo(1)
                .Because("highlights are the one section a finished open cannot fill by itself");
            await Assert.That(vm.TickRateDisplay).IsEqualTo("64");
        }
    }

    /// <summary>
    ///     The seam that makes seeding safe: it fills VALUES without changing MODE. Routing it through
    ///     <c>SetCachedRecord</c> would flip the page to Cached, and every keyed live fill, all of which
    ///     require Live, would be dropped silently for the rest of the load.
    /// </summary>
    [Test]
    public async Task Seeding_LeavesThePageLive_SoThePipelineCanStillFillIt()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("seed.dem", null, null, Demo);
        vm.SeedFromCache(Demo, Indexed());

        using (Assert.Multiple())
        {
            await Assert.That(vm.Mode).IsEqualTo(OverviewMode.Live);
            await Assert.That(vm.IsLoading).IsTrue().Because("a parse genuinely is running");
            await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Live);
        }

        // A later pipeline push must still be accepted.
        vm.SetTeamScores(Demo, 16, 14);

        using (Assert.Multiple())
        {
            await Assert.That(vm.CtTeamScoreDisplay).IsEqualTo("16");
            await Assert.That(vm.TTeamScoreDisplay).IsEqualTo("14");
        }
    }

    /// <summary>A seed presenting another demo's key is a late arrival from a previous open.</summary>
    [Test]
    public async Task ASeedForADifferentDemo_IsDropped()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("other.dem", null, null, "/demos/other.dem");
        vm.SeedFromCache(Demo, Indexed());

        using (Assert.Multiple())
        {
            await Assert.That(vm.HighlightGroups.Count).IsEqualTo(0);
            await Assert.That(vm.HasScore).IsFalse();
        }
    }

    /// <summary>
    ///     Tier 3's halves are independent, and the page must say so. A scanned-but-not-stats-computed demo
    ///     used to render a FULL chip directly above "Analysis produced no per-player stats for this demo.",
    ///     on a page whose whole job is reporting which tiers are actually complete, contradicting itself on one screen.
    /// </summary>
    [Test]
    public async Task HighlightsWithoutAScoreboard_RenderTheMoments_ButDoNotClaimFull()
    {
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(Indexed(true));

        using (Assert.Multiple())
        {
            await Assert.That(vm.HighlightGroups.Count).IsEqualTo(1)
                .Because("the highlights really are there and really are current");
            await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Indexed);
            await Assert.That(vm.CompletenessActionLabel).IsEqualTo("Compute full stats")
                .Because("the action that fills the missing half must stay offered");
            await Assert.That(vm.PlayerStatsMessage).IsEqualTo("Player stats need a full analysis pass.");
        }
    }

    /// <summary>
    ///     The open's own highlight harvest reaches the page it belongs to.
    ///     <para>
    ///         This is the second half of the reported symptom, and the ordering is the whole problem: opening
    ///         a demo DOES harvest highlights (<c>OnOpenDemoEvaluated</c>), but off-thread, completing after
    ///         <c>SetAnalysis</c>, the page's last fill point. Seeding cannot cover it either, because at
    ///         seed time the harvest has not started. So a demo you just opened and watched finish showed an
    ///         empty moments column until you navigated away and came back.
    ///     </para>
    /// </summary>
    [Test]
    public async Task HighlightsHarvestedDuringAnOpen_ReachTheLivePage()
    {
        MatchOverviewTabViewModel vm = new();

        // Open a demo whose cache record has no highlights yet.
        vm.BeginOpening("seed.dem", null, null, Demo);
        vm.SeedFromCache(Demo, Indexed(false));
        await Assert.That(vm.HighlightGroups.Count).IsEqualTo(0);

        // The pipeline finishes. The harvest is still running.
        vm.SetAnalysis(Demo, null, null, 24);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HighlightGroups.Count).IsEqualTo(0);
            await Assert.That(vm.HighlightsMessage).IsEqualTo("Harvesting highlights…")
                .Because("an open really does harvest them — the honest reading of empty here is 'not yet'");
        }

        // The harvest lands and writes the record.
        vm.RefreshHighlightsFromCache(Demo, Indexed());

        using (Assert.Multiple())
        {
            await Assert.That(vm.HighlightGroups.Count).IsEqualTo(1);
            await Assert.That(vm.HighlightsMessage).IsEmpty();
            await Assert.That(vm.Mode).IsEqualTo(OverviewMode.Live)
                .Because("filling one section must not flip the page out of Live");
            await Assert.That(vm.HighlightGroups[0].Highlights[0].CanVerify).IsTrue()
                .Because("this demo IS the open one, so Verify is genuinely available");
        }
    }

    /// <summary>
    ///     A scoreboard arriving must not make the page speak for the highlights scan.
    ///     <para>
    ///         The two halves have different producers, and on an open the scoreboard write ALWAYS precedes
    ///         the harvest completing. When the scoreboard writer also set <c>AnalysisState = Indexed</c>, the
    ///         highlight section read that as "the scan is done" and asserted "No highlights fired for this
    ///         demo" about a harvest still in flight, and would have overridden the failure copy if that
    ///         harvest then threw. Each half now reads its own evidence.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AScoreboardArrivingMidOpen_DoesNotClaimTheScanIsDone()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("seed.dem", null, null, Demo);
        vm.SeedFromCache(Demo, Indexed(false));
        vm.SetAnalysis(Demo, null, null, 24);

        // The scoreboard lands first: stamped analysis tier, real rows, but the scan has NOT run.
        DemoCacheRecord scoreboardOnly = Indexed(false, true);
        scoreboardOnly.AnalysisState = DemoAnalysisState.Pending;
        vm.RefreshHighlightsFromCache(Demo, scoreboardOnly);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HighlightsMessage).IsEqualTo("Harvesting highlights…")
                .Because("the harvest is still running — 'no highlights fired' is a claim we cannot make");
            await Assert.That(vm.HasHighlightsAction).IsFalse()
                .Because("a [Compute full stats] button under 'Harvesting…' contradicts it, and pressing it "
                         + "would queue a redundant heavy pass over the demo already being harvested");
        }

        // Now the harvest lands.
        vm.RefreshHighlightsFromCache(Demo, Indexed());

        await Assert.That(vm.HighlightGroups.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A demo that has only ever been OPENED has a real scoreboard and no scan. Its stats must render:
    ///     gating them on the scanner's state field would hide numbers that are sitting right there.
    /// </summary>
    [Test]
    public async Task AnOpenedButNeverScannedDemo_StillShowsItsScoreboard()
    {
        DemoCacheRecord record = Indexed(false, true);
        record.AnalysisState = DemoAnalysisState.Pending;

        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(record);

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(1)
                .Because("the scoreboard's producer never touches AnalysisState");
            await Assert.That(vm.HighlightsMessage).IsEqualTo("Highlights need a full analysis pass.");
            await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Indexed);
        }
    }

    /// <summary>A refresh naming a different demo is dropped.</summary>
    [Test]
    public async Task AHighlightRefreshForAnotherDemo_IsDropped()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("other.dem", null, null, "/demos/other.dem");
        vm.RefreshHighlightsFromCache(Demo, Indexed());

        await Assert.That(vm.HighlightGroups.Count).IsEqualTo(0);
    }

    /// <summary>Both halves present is the only thing that earns FULL.</summary>
    [Test]
    public async Task BothHalvesPresent_EarnFull()
    {
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(Indexed(true, true));

        using (Assert.Multiple())
        {
            await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Full);
            await Assert.That(vm.HighlightGroups.Count).IsEqualTo(1);
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(1);
            await Assert.That(vm.PlayerStatsMessage).IsEmpty();
            await Assert.That(vm.CompletenessActionLabel).IsNull();
        }
    }
}

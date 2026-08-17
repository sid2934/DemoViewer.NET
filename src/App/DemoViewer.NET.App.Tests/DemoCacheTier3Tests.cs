#region

using Cs2DemoKit.Analysis;
using Cs2DemoKit.Analysis.Abstractions;
using Cs2DemoKit.Analysis.Output;
using Cs2DemoKit.Analysis.Profiles;
using DemoViewer.NET.Modules.Highlights;
using Cs2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Tier 3 of the unified demo cache — the producer half of the redesign's step 4, which the migration
///     landed without.
///     <para>
///         The gap these tests close: every reader built in steps 5–9 projects
///         <see cref="DemoCacheRecord.Highlights" />, but the only thing that ever WROTE it was the one-shot
///         legacy migration. The scanner kept writing <c>highlights.json</c> alone, so from the moment the
///         migration ran, a scanned demo had highlights the Reels tab could see and Match Overview could not —
///         and Match Overview is the page whose whole job is showing them.
///     </para>
///     <para>
///         Tier 3 has TWO producers because it has two halves with different costs: the scanner supplies
///         highlights from a snapshot-free run (what makes a library sweep affordable), and a real open
///         supplies the scoreboard from its snapshot-bearing run (the only mode that can produce per-player
///         stats at all). Both are asserted here, along with the invariant that neither may forge the other.
///     </para>
/// </summary>
public class DemoCacheTier3Tests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-tier3-{Guid.NewGuid():N}");

    private static ParsedDemo SyntheticDemo() => new(
        [], [], new Dictionary<int, PlayerInfo>(), null, "de_test",
        0, 1f / 64, "test", "test", "csgo", 0, 0, 0, "valve_demo_2", "", "", DemoProfile.Unknown);

    private sealed class NoopHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
            => ("fp-A@64", new Dictionary<string, string> { ["clutch.ace"] = "h1" });

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) =>
            throw new NotSupportedException("these tests supply rows via processorOverride");

        public void InvalidateRules()
        {
        }
    }

    // Records WHICH mode the scanner asked for. Both throw: the point is the choice, not the run.
    private sealed class ModeRecordingHarvester : IHighlightHarvester
    {
        public List<string> Calls { get; } = [];

        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
            => ("fp-A@64", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo)
        {
            Calls.Add("bare");
            throw new NotSupportedException("stop here — the mode is what is under test");
        }

        public AnalysisRun RunFullAnalysis(ParsedDemo demo)
        {
            Calls.Add("full");
            throw new NotSupportedException("stop here — the mode is what is under test");
        }

        public void InvalidateRules()
        {
        }
    }

    private static List<HighlightFired> Harvest(params int[] ticks) =>
    [
        .. ticks.Select(t => new HighlightFired(
            "clutch", "ace", 0, t, 1, "s1mple", 7, $"s1mple — ace @{t}", 50, HighlightKind.Highlight))
    ];

    // A record as the LIBRARY writes it: tier 2 filled, identity stamped in LOCAL ticks.
    private static DemoCacheRecord LibraryIndexed(string path, long localTicks) => new()
    {
        Path = path,
        Size = 4242,
        ModifiedTicks = localTicks,
        Map = "de_dust2",
        Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 },
        DurationSeconds = 2298,
        TickRate = 64,
        Players =
        [
            new CachedPlayerInfo { Slot = 1, Name = "s1mple", SteamId64 = "765", Team = 3 },
            new CachedPlayerInfo { Slot = 2, Name = "ZywOo", SteamId64 = "766", Team = 2 }
        ],
        Rounds = [new Services.DemoCache.CachedRound { Number = 1, StartTickFrameClock = 5000 }],
        CtScore = 13,
        TScore = 9
    };

    private static HighlightScanService Scanner(
        DemoCacheStore cache,
        Func<string, ParsedDemo, IReadOnlyList<HighlightFired>?>? processor = null) =>
        new(cache, new NoopHarvester(), () => [], () => true, a => a(), processor);

    /// <summary>
    ///     The headline fix: a completed scan puts highlights where Match Overview reads them.
    /// </summary>
    [Test]
    public async Task ACompletedScan_WritesHighlightsIntoTheUnifiedRecord()
    {
        string root = TempRoot();
        try
        {
            const string demo = "/demos/tier3.dem";
            DemoCacheStore cache = new(root);
            using HighlightScanService scanner = Scanner(cache, (_, _) => Harvest(54_000, 61_000));

            scanner.Evaluate(demo, SyntheticDemo());

            DemoCacheRecord? record = cache.TryLoadRecord(demo);

            using (Assert.Multiple())
            {
                await Assert.That(record).IsNotNull();
                await Assert.That(record!.Highlights.Count).IsEqualTo(2)
                    .Because("this is the write whose absence emptied the whole highlight section");
                await Assert.That(record.Highlights[0].TypeKey).IsEqualTo("clutch.ace");
                await Assert.That(record.Highlights[0].Tick).IsEqualTo(54_000);
                await Assert.That(record.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
                await Assert.That(record.Analysis.IsPresent).IsTrue();
                await Assert.That(record.ConfigFingerprint).IsEqualTo("fp-A@64");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     THE trap in this whole change. The library indexer stamps <c>FileInfo.LastWriteTime</c> (local);
    ///     the scanner stamps <c>LastWriteTimeUtc</c>. Routing the tier-3 fill through the identity-asserting
    ///     <c>Update</c> would hand a UTC tick count to a locally-stamped record, <c>MatchesFile</c> would fail
    ///     for every user not on UTC, and "identity drift discards everything" would throw away the tier-2
    ///     roster and score — on EVERY scan. The bug would have looked like the library spontaneously
    ///     forgetting demos it had already indexed.
    /// </summary>
    [Test]
    public async Task MirroringTier3_DoesNotDiscardTier2_WhenTheWritersDisagreeOnMtimeUnits()
    {
        string root = TempRoot();
        try
        {
            const string demo = "/demos/identity.dem";
            long localTicks = DateTime.Now.Ticks; // the library's convention

            DemoCacheStore cache = new(root);
            cache.Upsert(LibraryIndexed(demo, localTicks));

            using HighlightScanService scanner = Scanner(cache, (_, _) => Harvest(54_000));

            scanner.Evaluate(demo, SyntheticDemo());

            DemoCacheRecord? record = cache.TryLoadRecord(demo);

            using (Assert.Multiple())
            {
                await Assert.That(record!.Highlights.Count).IsEqualTo(1);
                await Assert.That(record.Players.Count).IsEqualTo(2)
                    .Because("the tier-2 roster must survive a tier-3 fill");
                await Assert.That(record.CtScore).IsEqualTo(13);
                await Assert.That(record.Parse.IsPresent).IsTrue();
                await Assert.That(record.ModifiedTicks).IsEqualTo(localTicks)
                    .Because("identity belongs to the writer that established it, in ITS units");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A failed scan records the failure but must not blank a previous good harvest — the page has
    ///     distinct copy for "the last pass failed", and showing it with the old highlights still visible is
    ///     strictly more useful than showing it over an empty section.
    /// </summary>
    [Test]
    public async Task AFailedScan_RecordsTheFailure_ButKeepsTheLastGoodHarvest()
    {
        string root = TempRoot();
        try
        {
            const string demo = "/demos/fails.dem";
            DemoCacheStore cache = new(root);

            using (HighlightScanService ok = Scanner(cache, (_, _) => Harvest(54_000)))
            {
                ok.Evaluate(demo, SyntheticDemo());
            }

            await Assert.That(cache.TryLoadRecord(demo)!.Highlights.Count).IsEqualTo(1);

            // A later pass throws.
            using (HighlightScanService bad = Scanner(cache,
                       (_, _) => throw new InvalidOperationException("rules blew up")))
            {
                bad.RequestScan(demo); // forced — a current demo is not in the derived backlog
                bad.Evaluate(demo, SyntheticDemo());
            }

            DemoCacheRecord? record = cache.TryLoadRecord(demo);

            using (Assert.Multiple())
            {
                await Assert.That(record!.AnalysisState).IsEqualTo(DemoAnalysisState.Failed);
                await Assert.That(record.Highlights.Count).IsEqualTo(1)
                    .Because("a failure is not a reason to throw away the last successful harvest");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The scoreboard producer: a real open's table becomes tier 3's stats half.
    /// </summary>
    [Test]
    public async Task TheScoreboardProjection_KeepsTheAnalysisEnginesOwnNumbers()
    {
        MetricTable table = new(
            "player_game_stats",
            ["match_id", "map", "player_slot", "player_name", "team"],
            ["TotalK", "TotalD", "TotalA", "ADR", "HLTV", "CTW", "TW"],
            [
                new MetricRow(
                    new Dictionary<string, object?> { ["player_slot"] = 1, ["player_name"] = "s1mple", ["team"] = 3 },
                    new Dictionary<string, object?>
                    {
                        ["TotalK"] = 24, ["TotalD"] = 14, ["TotalA"] = 5,
                        ["ADR"] = 92.47, ["HLTV"] = 1.34, ["CTW"] = 7, ["TW"] = 6
                    }),
                // A totals row carries no slot and must be dropped rather than rendered as a blank name.
                new MetricRow(
                    new Dictionary<string, object?> { ["player_name"] = "TOTAL" },
                    new Dictionary<string, object?> { ["TotalK"] = 100 })
            ]);

        List<CachedStatRow> rows = DemoCacheAnalysisProjector.ProjectScoreboard(table);
        (int? ct, int? t) = DemoCacheAnalysisProjector.ComputeSideWins(table);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Count).IsEqualTo(1).Because("the slot-less totals row is not a player");
            await Assert.That(rows[0].Slot).IsEqualTo(1);
            await Assert.That(rows[0].Kills).IsEqualTo(24);
            await Assert.That(rows[0].Adr).IsEqualTo(92.47);
            await Assert.That(rows[0].Rating).IsEqualTo(1.34);
            await Assert.That(ct).IsEqualTo(7);
            await Assert.That(t).IsEqualTo(6);
        }
    }

    /// <summary>
    ///     "Compute full stats" has to actually compute stats. The scoreboard is projected from snapshot
    ///     vectors, which the bare run does not produce at all — so a forced scan that stayed bare would
    ///     deliver highlights and leave the stats half of the page reading "needs a full analysis pass" with
    ///     the very button that fixes it having just run. The background sweep must stay bare, because being
    ///     snapshot-free is what makes a library-wide scan affordable.
    /// </summary>
    [Test]
    public async Task AUserRequestRunsTheExpensiveMode_TheBackgroundSweepDoesNot()
    {
        string root = TempRoot();
        try
        {
            const string forcedDemo = "/demos/asked-for.dem";
            const string sweptDemo = "/demos/swept.dem";

            DemoCacheStore cache = new(root);
            ModeRecordingHarvester harvester = new();
            using HighlightScanService scanner =
                new(cache, harvester, () => [forcedDemo, sweptDemo], () => true, a => a());

            // RequestScan is what the completeness chip's action calls — it marks the demo forced.
            scanner.RequestScan(forcedDemo);
            scanner.Evaluate(forcedDemo, SyntheticDemo());

            // A demo the sweep picked up on its own was never forced.
            scanner.Evaluate(sweptDemo, SyntheticDemo());

            using (Assert.Multiple())
            {
                await Assert.That(harvester.Calls.Count).IsEqualTo(2);
                await Assert.That(harvester.Calls[0]).IsEqualTo("full")
                    .Because("the user asked about this demo specifically");
                await Assert.That(harvester.Calls[1]).IsEqualTo("bare")
                    .Because("snapshot-free is what makes a library-wide sweep affordable");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A user's "Compute full stats" survives the Library piggyback beating it to the row.
    ///     <para>
    ///         Both owners coalesce onto ONE parse, and the Library's own evaluate fans out to
    ///         <c>OnParsedOpportunistically</c> — a BARE run that upserts <c>Indexed</c>. The "row is no
    ///         longer Pending, don't waste the slot" skip was sound while every run was equivalent and became
    ///         wrong the moment bare and full stopped being the same thing: the press would be silently
    ///         consumed, the forced flag cleared, and the user left with highlights, no scoreboard, and no
    ///         sign anything was skipped.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AUserRequest_SurvivesThePiggybackWinningTheRace()
    {
        string root = TempRoot();
        try
        {
            const string demo = "/demos/raced-press.dem";
            DemoCacheStore cache = new(root);
            ModeRecordingHarvester harvester = new();
            using HighlightScanService scanner =
                new(cache, harvester, () => [demo], () => true, a => a());

            scanner.RequestScan(demo);

            // The piggyback gets there first on the shared parse and leaves the demo CURRENT.
            cache.Upsert(new DemoCacheRecord
            {
                Path = demo,
                Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
                AnalysisState = DemoAnalysisState.Indexed,
                ConfigFingerprint = "fp-A@64"
            });

            scanner.Evaluate(demo, SyntheticDemo());

            await Assert.That(harvester.Calls.Count).IsEqualTo(1)
                .Because("the press must still run — being skipped is how the scoreboard silently never arrives");
            await Assert.That(harvester.Calls[0]).IsEqualTo("full");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Tier 3's two halves are written by two writers that both fire on an interactive open — the
    ///     highlights mirror (off-thread, from the open-demo harvest) and the scoreboard write. Both are
    ///     read-modify-write cycles on the SAME record, so without serialization both read the pre-write
    ///     state and whichever upserts last erases the other's tier: an open would land highlights or stats,
    ///     never reliably both, and which one you lost would vary run to run.
    /// </summary>
    [Test]
    public async Task ConcurrentTierWrites_DoNotEraseEachOther()
    {
        string root = TempRoot();
        try
        {
            const string demo = "/demos/racy.dem";
            DemoCacheStore cache = new(root);
            cache.Upsert(LibraryIndexed(demo, DateTime.Now.Ticks));

            // Forces the interleaving rather than hoping for it. Each writer announces that it is inside its
            // mutate and waits for the other. Serialized, the second cannot enter until the first has
            // upserted, so the waits simply time out and both tiers survive. UNSERIALIZED, both are inside at
            // once — each holding a copy read before the other's write — and whichever upserts last erases
            // the other's tier, which is exactly the failure this asserts against.
            //
            // (Hammering both in a loop does NOT detect this: every iteration rewrites the same field, so a
            // lost update is restored by the next one and the bug hides. That version passed without the lock.)
            using ManualResetEventSlim highlightsInside = new(false);
            using ManualResetEventSlim scoreboardInside = new(false);
            TimeSpan patience = TimeSpan.FromMilliseconds(750);

            await Task.WhenAll(
                Task.Run(() => cache.UpdateExisting(demo, r =>
                {
                    r.Highlights =
                    [
                        new CachedHighlightEvent
                        {
                            RulesetId = "clutch", HighlightId = "ace", Tick = 54_000, PlayerSlot = 1
                        }
                    ];
                    DemoCacheStore.StampAnalysis(r);
                    highlightsInside.Set();
                    scoreboardInside.Wait(patience);
                })),
                Task.Run(() => cache.UpdateExisting(demo, r =>
                {
                    r.Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 24 }];
                    DemoCacheStore.StampAnalysis(r);
                    scoreboardInside.Set();
                    highlightsInside.Wait(patience);
                })));

            DemoCacheRecord? record = cache.TryLoadRecord(demo);

            using (Assert.Multiple())
            {
                await Assert.That(record!.Highlights.Count).IsEqualTo(1)
                    .Because("the scoreboard writer must not clobber the highlights writer");
                await Assert.That(record.Scoreboard.Count).IsEqualTo(1)
                    .Because("and vice versa");
                await Assert.That(record.Players.Count).IsEqualTo(2)
                    .Because("neither may drop tier 2 on the way past");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Per-side wins are refused rather than guessed when one team's rows disagree — the columns are
    ///     supposed to be team-wide totals, and a demo where they are not is a demo whose split we cannot
    ///     derive. A missing number beats a wrong one on a page that shows a score.
    /// </summary>
    [Test]
    public async Task DisagreeingSideWins_YieldNothingRatherThanAGuess()
    {
        MetricTable table = new(
            "player_game_stats",
            ["player_slot", "team"],
            ["CTW", "TW"],
            [
                new MetricRow(
                    new Dictionary<string, object?> { ["player_slot"] = 1, ["team"] = 3 },
                    new Dictionary<string, object?> { ["CTW"] = 7, ["TW"] = 6 }),
                new MetricRow(
                    new Dictionary<string, object?> { ["player_slot"] = 2, ["team"] = 3 },
                    new Dictionary<string, object?> { ["CTW"] = 5, ["TW"] = 6 })
            ]);

        (int? ct, int? t) = DemoCacheAnalysisProjector.ComputeSideWins(table);

        using (Assert.Multiple())
        {
            await Assert.That(ct).IsNull();
            await Assert.That(t).IsNull();
        }
    }
}

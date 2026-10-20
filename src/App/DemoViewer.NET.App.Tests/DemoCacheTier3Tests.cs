#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Tier 3 of the unified demo cache: the producer half of the redesign's step 4, which the migration
///     landed without.
///     <para>
///         The gap these tests close: every reader built in steps 5–9 projects
///         <see cref="DemoCacheRecord.Highlights" />, but the only thing that ever WROTE it was the one-shot
///         legacy migration. The scanner kept writing <c>highlights.json</c> alone, so from the moment the
///         migration ran, a scanned demo had highlights the Reels tab could see and Match Overview could not,
///         and Match Overview is the page whose whole job is showing them.
///     </para>
///     <para>
///         Tier 3 has two halves with two producers: the scanner supplies highlights, and the facts pass the
///         <c>player_match</c> facet Match Overview projects its scoreboard from
///         (<see cref="MatchOverviewFacetScoreboardTests" />).
///     </para>
/// </summary>
public class DemoCacheTier3Tests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-tier3-{Guid.NewGuid():N}");

    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null, "de_test",
        0, 1f / 64, "test", "test", "csgo", 0, 0, 0, "valve_demo_2", "", "", DemoProfile.Unknown);

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
    ///     roster and score, on EVERY scan. The bug would have looked like the library spontaneously
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
    ///     A failed scan records the failure but must not blank a previous good harvest: the page has
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
                bad.RequestScan(demo); // forced: a current demo is not in the derived backlog
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
    ///     A user's "Compute full stats" survives another channel beating it to the row. Both owners coalesce
    ///     onto ONE parse, and the shell's own harvest of the open demo upserts <c>Indexed</c>; the "row is no
    ///     longer Pending, don't waste the slot" skip must not consume the press.
    /// </summary>
    [Test]
    public async Task AUserRequest_SurvivesThePiggybackWinningTheRace()
    {
        string root = TempRoot();
        try
        {
            const string demo = "/demos/raced-press.dem";
            DemoCacheStore cache = new(root);
            RunRecordingHarvester harvester = new();
            using HighlightScanService scanner =
                new(cache, harvester, () => [demo], () => true, a => a());

            scanner.RequestScan(demo);

            // The piggyback gets there first on the shared parse and leaves the demo CURRENT.
            cache.Upsert(new DemoCacheRecord
            {
                Path = demo,
                Analysis = new TierStamp
                {
                    Schema = DemoCacheRecord.AnalysisSchema,
                    ComputedAtTicks = 1
                },
                AnalysisState = DemoAnalysisState.Indexed,
                ConfigFingerprint = "fp-A@64"
            });

            scanner.Evaluate(demo, SyntheticDemo());

            await Assert.That(harvester.Calls.Count).IsEqualTo(1)
                .Because("the press must still run, not be skipped as already current");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Tier 3's two halves are written by two writers that both fire on an interactive open: the
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
            // once, each holding a copy read before the other's write, and whichever upserts last erases
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
                            RulesetId = "clutch",
                            HighlightId = "ace",
                            Tick = 54_000,
                            PlayerSlot = 1
                        }
                    ];
                    DemoCacheStore.StampAnalysis(r);
                    highlightsInside.Set();
                    scoreboardInside.Wait(patience);
                })),
                Task.Run(() => cache.UpdateExisting(demo, r =>
                {
                    r.Scoreboard =
                    [
                        new CachedStatRow
                        {
                            Slot = 1,
                            Team = 3,
                            Kills = 24
                        }
                    ];
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

    private sealed class NoopHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
            => ("fp-A@64", new Dictionary<string, string>
            {
                ["clutch.ace"] = "h1"
            });

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) =>
            throw new NotSupportedException("these tests supply rows via processorOverride");

        public void InvalidateRules()
        {
        }
    }

    // Records each run the scanner asked for. It throws: the point is that the run was asked for.
    private sealed class RunRecordingHarvester : IHighlightHarvester
    {
        public List<string> Calls { get; } = [];

        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
            => ("fp-A@64", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo)
        {
            Calls.Add("bare");
            throw new NotSupportedException("stop here: the request is what is under test");
        }

        public void InvalidateRules()
        {
        }
    }
}

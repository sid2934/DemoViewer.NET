#region

using System.Text.Json;
using DemoViewer.NET.Modules.Library;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library cache's SELF-HEALING contract. <c>ExtractFinalScore</c> is both-or-nothing, so a row with
///     a CT score and no T score is a state the current code cannot produce — yet the reference library is
///     full of them (555 rows with a CT score, 3 with the T score), left behind when the model's
///     <c>TScore</c>/<c>TClan</c> properties were renamed to <c>Score</c>/<c>Clan</c> and every already-written
///     row silently stopped deserializing its T side. <c>ScoreComputed = true</c> then guaranteed nothing would
///     ever recompute it, so ~the whole library lost its score badge permanently.
///     <para>
///         <b>The risk these tests actually guard is the re-index loop.</b> A repair that cannot tell "never
///         computed" from "computed and legitimately produced nothing" re-parses the same demos on every single
///         launch, forever. Termination here is structural — the repair predicate flags only states the
///         extractor cannot emit, so a re-derived row is never suspect again — and that is only believable if
///         it is measured across THREE launches, not two: two cannot distinguish "terminated" from "terminates
///         one launch later".
///     </para>
///     <para>
///         Driven entirely on synthetic parses through the queue's injected parser, so no real demo is needed
///         and the parse COUNT per launch is the assertion.
///     </para>
/// </summary>
[NotInParallel]
public class DemoLibraryCacheRepairTests
{
    private static readonly Action<Action> _inline = a => a();

    /// <summary>A parse with no frames — <c>ExtractFinalScore</c>'s genuine "produced nothing" case.</summary>
    private static ParsedDemo ScorelessDemo(int rounds = 0)
    {
        List<GameEvent> events = [];
        for (int i = 0; i < rounds; i++)
        {
            events.Add(TestGameEvents.RoundFreezeEnd(i, i * 100, i * 100, 0));
        }

        return SyntheticParsedDemo.Create(
            [], events, new Dictionary<int, PlayerInfo>(), null,
            "de_test", 6400, 1f / 64, "test",
            "test", "csgo", 0, 0, 0,
            "valve_demo_2", "", "", DemoProfile.Unknown);
    }

    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvrepair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Writes a library.json holding the folder plus one cache row per stub demo, keyed on the real
    // (path, size, mtime) so the row is a genuine cache HIT — a mis-keyed row is silently ignored and
    // would make every one of these tests pass for the wrong reason.
    private static string SeedLibrary(string dir, params (string Name, Action<DemoLibraryCacheEntry> Row)[] demos)
    {
        List<DemoLibraryCacheEntry> rows = [];
        foreach ((string name, Action<DemoLibraryCacheEntry> configure) in demos)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            FileInfo fi = new(path);

            DemoLibraryCacheEntry row = new()
            {
                Path = path,
                Size = fi.Length,
                ModifiedTicks = fi.LastWriteTime.Ticks,
                Map = "de_test",
                Server = "seeded",
                Players = ["alpha", "bravo"],
                DurationSeconds = 100,
                FullyIndexed = true,
                ScoreComputed = true
            };
            configure(row);
            rows.Add(row);
        }

        string dataPath = Path.Combine(dir, "library.json");
        File.WriteAllText(dataPath, JsonSerializer.Serialize(new DemoLibraryData
        {
            SchemaVersion = DemoLibraryCacheEntry.CurrentSchema,
            Folders = [dir],
            Cache = rows
        }));
        return dataPath;
    }

    // One app launch: construct the service over the persisted library (folders come from the file, so every
    // launch is identical — no AddFolders rescan racing the explicit one), drain tier-2, return how many
    // demos were PARSED. That count is the whole measurement: a repair that loops shows up as a non-zero
    // count on launch 3.
    private static async Task<int> LaunchAsync(string dataPath, Func<string, ParsedDemo>? parse = null)
    {
        int parses = 0;
        DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), path =>
        {
            Interlocked.Increment(ref parses);
            return parse is null
                ? throw new InvalidOperationException($"unexpected re-parse of {path}")
                : parse(path);
        });

        using DemoLibraryService svc = new(_inline, dataPath);
        using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
        svc.Coordinator = coord;

        await svc.RescanAsync();
        await WaitForAsync(() => svc.Tier2Backlog().Count == 0, "tier-2 backlog drained");

        // Settle: gives a WRONGLY-submitted parse time to land and be counted, so a "0 parses" assertion is
        // evidence rather than a race the test happened to win.
        await Task.Delay(120);
        return parses;
    }

    private static List<DemoLibraryCacheEntry> ReadRows(string dataPath) =>
        JsonSerializer.Deserialize<DemoLibraryData>(File.ReadAllText(dataPath))?.Cache ?? [];

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    ///     A half-resolved row never re-parses on its own, the card never renders the half score, and the row
    ///     on disk is left EXACTLY as written.
    ///     <para>
    ///         NOTE: this test was reversed on 2026-07-29. It previously demanded
    ///         <c>first == 1</c> — the repaired row rejoined the tier-2 backlog and re-derived automatically.
    ///         Correct in the small, ruinous at scale: on the reference library that is 342 demos / ~100 GB of
    ///         background parsing on the first launch after upgrade, on a library that looks unchanged. It is
    ///         an explicit action now (<c>RepairPendingScoresAsync</c>) and the card says so meanwhile.
    ///     </para>
    ///     <para>
    ///         The untouched row is the load-bearing half. The state is re-derived from the row's own data on
    ///         every load, so it cannot be lost — which is what lets this work with no persisted marker.
    ///     </para>
    /// </summary>
    [Test]
    public async Task HalfScore_IsWithheldFromTheCard_ButNeverReIndexesOnItsOwn()
    {
        string dir = TempRoot();
        try
        {
            // Exactly the reference library's shape: the CT side survived the rename, the T side did not.
            string dataPath = SeedLibrary(dir, ("half.dem", r =>
            {
                r.CtScore = 16;
                r.Score = null;
                r.CtClan = "Vitality";
            }));

            // No parse delegate on ANY launch: LaunchAsync THROWS on an unexpected parse, so a single
            // automatic re-derivation fails the test loudly rather than being quietly counted.
            int first = await LaunchAsync(dataPath);
            int second = await LaunchAsync(dataPath);

            DemoLibraryCacheEntry row = ReadRows(dataPath).Single();

            // The card's view of the same demo.
            using DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();
            DemoEntry entry = svc.Entries.Single();

            using (Assert.Multiple())
            {
                await Assert.That(first).IsEqualTo(0)
                    .Because("the repair is on-demand — launching must never spend a parse on it");
                await Assert.That(second).IsEqualTo(0)
                    .Because("two launches cannot tell 'never' from 'starts one launch later'");

                await Assert.That(row.CtScore).IsEqualTo(16)
                    .Because("the row is the evidence — erasing it is what would make the state unrecoverable");
                await Assert.That(row.ScoreComputed).IsTrue()
                    .Because("untouched, and that is what keeps it out of the automatic backlog");

                await Assert.That(entry.CtScore).IsNull()
                    .Because("the half score is refused at the read boundary — the card never renders '16 – —'");
                await Assert.That(entry.NeedsScoreRepair).IsTrue()
                    .Because("absent-because-stale must not render identically to absent-because-unresolvable");
                await Assert.That(entry.State).IsEqualTo(DemoIndexState.Indexed)
                    .Because("players/duration were always correct and must keep showing");
                await Assert.That(svc.ScoreRepairPendingCount).IsEqualTo(1);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The explicit action does what the automatic sweep used to: the flagged row re-derives exactly once,
    ///     the flag clears, and later launches are quiet again.
    /// </summary>
    [Test]
    public async Task RepairPendingScores_ReDerivesOnce_ThenClearsTheFlag()
    {
        string dir = TempRoot();
        try
        {
            string dataPath = SeedLibrary(dir, ("half.dem", r =>
            {
                r.CtScore = 16;
                r.Score = null;
                r.CtClan = "Vitality";
            }));

            // Launch 1: hydrate + mark, no parse.
            await LaunchAsync(dataPath);

            // Launch 2: press the button.
            int parses = 0;
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), path =>
            {
                Interlocked.Increment(ref parses);
                return ScorelessDemo();
            });

            int enlisted;
            using (DemoLibraryService svc = new(_inline, dataPath))
            {
                using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
                svc.Coordinator = coord;
                await svc.RescanAsync();
                await WaitForAsync(() => svc.Tier2Backlog().Count == 0, "initial drain");

                await Assert.That(svc.ScoreRepairPendingCount).IsEqualTo(1)
                    .Because("the count is what the toolbar offers to repair");

                enlisted = await svc.RepairPendingScoresAsync();
                await WaitForAsync(() => svc.Tier2Backlog().Count == 0, "repair drain");
                await Task.Delay(120);
            }

            DemoLibraryCacheEntry row = ReadRows(dataPath).Single();

            // Launch 3: quiet again — the repair must not become a loop.
            int after = await LaunchAsync(dataPath);

            using (Assert.Multiple())
            {
                await Assert.That(enlisted).IsEqualTo(1);
                await Assert.That(parses).IsEqualTo(1)
                    .Because("pressing the button re-derives, once");
                await Assert.That(row.ScoreComputed).IsTrue()
                    .Because("a completed re-derivation is a computed result, whatever it produced");
                await Assert.That(DemoLibraryService.IsScoreResultCoherent(
                        row.CtScore, row.Score, row.CtClan, row.Clan)).IsTrue()
                    .Because("the re-derived row satisfies the contract, so it is never flagged again — "
                             + "this is what makes the repair terminate");
                await Assert.That(after).IsEqualTo(0)
                    .Because("a repaired row must not re-enter the backlog on the next launch");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     THE loop guard. A warmup-only or truncated demo makes <c>ExtractFinalScore</c> return all-nulls by
    ///     design; that outcome must be recorded as computed, or the demo is re-parsed on every launch for the
    ///     life of the install. Starts from an EMPTY cache so the all-null row is produced by a real tier-2
    ///     rather than hand-seeded.
    /// </summary>
    [Test]
    public async Task ScorelessDemo_IsParsedOnce_AndNotReQueuedOnLaterLaunches()
    {
        string dir = TempRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "warmup.dem"), [1, 2, 3]);
            string dataPath = Path.Combine(dir, "library.json");
            File.WriteAllText(dataPath, JsonSerializer.Serialize(new DemoLibraryData
            {
                SchemaVersion = DemoLibraryCacheEntry.CurrentSchema,
                Folders = [dir],
                Cache = []
            }));

            int first = await LaunchAsync(dataPath, _ => ScorelessDemo());
            int second = await LaunchAsync(dataPath);
            int third = await LaunchAsync(dataPath);

            DemoLibraryCacheEntry row = ReadRows(dataPath).Single();

            using (Assert.Multiple())
            {
                await Assert.That(first).IsEqualTo(1).Because("first index of an uncached demo");
                await Assert.That(second).IsEqualTo(0)
                    .Because("a demo that legitimately HAS no score must not re-queue");
                await Assert.That(third).IsEqualTo(0);

                await Assert.That(row.CtScore).IsNull();
                await Assert.That(row.Score).IsNull();
                await Assert.That(row.ScoreComputed).IsTrue()
                    .Because("all-nulls is the extractor's honest answer, and it is a COMPUTED one");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A complete row is left alone — the repair must not become a whole-library re-index. The parser
    ///     THROWS, so any submitted parse fails the test loudly rather than passing quietly.
    /// </summary>
    [Test]
    public async Task CompleteRow_IsNotReIndexed()
    {
        string dir = TempRoot();
        try
        {
            string dataPath = SeedLibrary(dir, ("complete.dem", r =>
            {
                r.CtScore = 13;
                r.Score = 9;
                r.CtClan = "Vitality";
                r.Clan = "FUT";
                r.RoundCount = 22;
            }));

            using (Assert.Multiple())
            {
                await Assert.That(await LaunchAsync(dataPath)).IsEqualTo(0);
                await Assert.That(await LaunchAsync(dataPath)).IsEqualTo(0);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The over-broad-predicate trap: a real HLTV demo can resolve both scores while only ONE side ever set
    ///     a clan tag. That is a legitimate extractor output, so treating "clans half-present" as suspect on
    ///     its own would re-index those demos on every launch, forever.
    /// </summary>
    [Test]
    public async Task BothScoresWithOneClan_IsLegitimate_AndIsNotReIndexed()
    {
        string dir = TempRoot();
        try
        {
            string dataPath = SeedLibrary(dir, ("oneclan.dem", r =>
            {
                r.CtScore = 13;
                r.Score = 11;
                r.CtClan = "Vitality";
                r.Clan = null;
            }));

            await Assert.That(await LaunchAsync(dataPath)).IsEqualTo(0)
                .Because("one side simply had no clan tag — nothing about this row is wrong");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A clan with no score behind it is the same stale half-result: the extractor cannot reach the clan
    ///     reads without having resolved both scores. Repaired once, then quiet.
    /// </summary>
    [Test]
    public async Task ClanWithoutScore_IsRepaired_Once()
    {
        string dir = TempRoot();
        try
        {
            string dataPath = SeedLibrary(dir, ("clanonly.dem", r =>
            {
                r.CtScore = null;
                r.Score = null;
                r.CtClan = "Vitality";
            }));

            int first = await LaunchAsync(dataPath);
            int second = await LaunchAsync(dataPath);

            using (Assert.Multiple())
            {
                await Assert.That(first).IsEqualTo(0)
                    .Because("a clan with no score is still a stale half-result, and re-deriving it is "
                             + "on-demand — launching must not spend a parse on it");
                await Assert.That(second).IsEqualTo(0);
                await Assert.That(ReadRows(dataPath).Single().CtClan).IsEqualTo("Vitality")
                    .Because("the row is left EXACTLY as written — the half data is the evidence the state is "
                             + "re-derived from on every launch, so erasing it is what would lose the state");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The write-time guard, tested at its definition. One predicate backs both the hydrate repair and the
    ///     cache write, so this table IS the both-or-nothing contract: everything it rejects is a state
    ///     <c>ExtractFinalScore</c> cannot emit, which is what makes the repair terminate.
    /// </summary>
    [Test]
    [Arguments(13, 9, null, null, true, "both sides resolved")]
    [Arguments(13, 9, "Vitality", "FUT", true, "both sides, both clans")]
    [Arguments(13, 11, "Vitality", null, true, "one side had no clan tag — legitimate")]
    [Arguments(null, null, null, null, true, "warmup-only / truncated — the honest all-null answer")]
    [Arguments(16, null, null, null, false, "the rename fallout: CT score, no T score")]
    [Arguments(null, 9, null, null, false, "the mirror image")]
    [Arguments(0, 0, null, null, false, "warmup-only sums to zero — the extractor returns nulls instead")]
    [Arguments(null, null, "Vitality", null, false, "a clan is unreachable without a resolved score")]
    [Arguments(16, null, "Vitality", "FUT", false, "clans present, score half-resolved")]
    public async Task ScoreContract_AcceptsOnlyWhatTheExtractorCanProduce(
        int? ctScore, int? tScore, string? ctClan, string? tClan, bool coherent, string why) =>
        await Assert.That(DemoLibraryService.IsScoreResultCoherent(ctScore, tScore, ctClan, tClan))
            .IsEqualTo(coherent).Because(why);

    /// <summary>
    ///     The other half of the stale-data finding: <c>RoundCount</c> was 0 on every row in the reference
    ///     cache because tier-2 never wrote it, and the legacy migration then carried that zero into the
    ///     unified cache. The round boundaries were already being derived — just not for this field.
    /// </summary>
    [Test]
    public async Task Tier2_WritesTheRoundCount()
    {
        string dir = TempRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "rounds.dem"), [1, 2, 3]);
            string dataPath = Path.Combine(dir, "library.json");
            File.WriteAllText(dataPath, JsonSerializer.Serialize(new DemoLibraryData
            {
                SchemaVersion = DemoLibraryCacheEntry.CurrentSchema,
                Folders = [dir],
                Cache = []
            }));

            await LaunchAsync(dataPath, _ => ScorelessDemo(24));

            await Assert.That(ReadRows(dataPath).Single().RoundCount).IsEqualTo(24)
                .Because("CS2 opens a round with round_freeze_end — DeriveRounds is the app's authority");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            /* best-effort */
        }
    }
}

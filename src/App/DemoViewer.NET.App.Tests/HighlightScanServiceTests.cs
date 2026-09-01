#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Scanner queue/staleness battery, now over the GLOBAL demo-
///     processing queue (demo-processing-queue.md). The scanner FEEDS Pending rows into a real
///     <see cref="DemoProcessingQueue" /> (fake parser, no demos), which owns the newest-first drain,
///     the gate yielding, and the one-at-a-time invariant. The behaviours asserted here are the ones
///     that stayed the scanner's: forced-vs-opt-in gating, staleness reconciliation, the piggyback
///     opt-in, and failure marking only the row. Drain-order / concurrency / gate-yield are exercised
///     end-to-end through the shared queue.
/// </summary>
public class HighlightScanServiceTests
{
    /// <summary>
    ///     A minimal synthetic <see cref="ParsedDemo" /> (internal ctor via InternalsVisibleTo) that
    ///     the fake queue parser hands to the scanner's per-row processing.
    /// </summary>
    private static ParsedDemo SyntheticDemo(int tickRate = 64) => SyntheticParsedDemo.Create(
        [],
        [],
        new Dictionary<int, PlayerInfo>(),
        null,
        "de_test",
        0,
        1f / tickRate,
        "test",
        "test",
        "csgo",
        0,
        0,
        0,
        "valve_demo_2",
        "",
        "",
        DemoProfile.Unknown);

    // Builds a scanner wired to a real queue THROUGH the coordinator, the production path (phase 3b): the
    // coordinator submits the scanner's Wants'd rows, the queue (fake parser, no filesystem) drives the
    // drain/gate, and the scanner's Evaluate does the per-row work via its processorOverride. The queue +
    // coordinator are kept alive by scanner.Coordinator; tests dispose only the scanner.
    private static HighlightScanService NewScanner(
        DemoCacheStore store,
        IHighlightHarvester harvester,
        Func<IReadOnlyList<string>> libraryDemoPaths,
        Func<bool> backgroundScanEnabled,
        Func<string, ParsedDemo, IReadOnlyList<HighlightFired>?>? processorOverride = null,
        HeavyJobGate? gate = null)
    {
        DemoProcessingQueue queue = new(gate ?? new HeavyJobGate(), a => a(),
            _ => SyntheticDemo());
        HighlightScanService scanner = new(store, harvester, libraryDemoPaths, backgroundScanEnabled,
            a => a(), processorOverride);
        DemoEvaluationCoordinator coordinator = new([scanner], queue, scanner.PendingPaths);
        scanner.Coordinator = coordinator;
        return scanner;
    }

    private static DemoCacheRecord IndexedRow(string path, long modified, int tickRate = 64,
        string fingerprint = "fp-A@64") => new()
    {
        Path = path,
        ModifiedTicks = modified,
        TickRate = tickRate,
        ConfigFingerprint = fingerprint,
        Analysis = new TierStamp
        {
            Schema = DemoCacheRecord.AnalysisSchema,
            ComputedAtTicks = 1
        },
        AnalysisState = DemoAnalysisState.Indexed
    };

    // One harvested event, so a written record is distinguishable from an untouched one.
    private static List<HighlightFired> Harvest() =>
        [new("clutch", "ace", 0, 5000, 0, "s1mple", 1, "s1mple — ace", 50, HighlightKind.Highlight)];

    private static HighlightFired HF(string id, int slot, int round, int score, string? group) =>
        new("rs", id, 0, 1000 + score, slot, "p", round, id, score, HighlightKind.Highlight, group);

    // Supersession collapses a tiered `group:` family to its top tier per player+round, while leaving
    // other groups, other rounds, other players, ungrouped firings, and same-score peers intact.
    [Test]
    public async Task ApplyGroupSupersession_KeepsTopTierPerGroupPerPlayerRound()
    {
        List<HighlightFired> events =
        [
            HF("triple_kill", 0, 1, 55, "multikill"), // superseded by the 4K
            HF("quad_kill", 0, 1, 88, "multikill"), // top of multikill → kept
            HF("rapid_quad", 0, 1, 92, "rapid_multikill"), // different group → kept alongside quad_kill
            HF("collateral", 0, 1, 95, null), // ungrouped → always kept
            HF("triple_kill", 1, 1, 55, "multikill"), // different PLAYER → kept
            HF("triple_kill", 0, 2, 55, "multikill"), // different ROUND → kept
            HF("rapid_double", 0, 3, 62, "rapid_multikill"), // two distinct same-score moments,
            HF("rapid_double", 0, 3, 62, "rapid_multikill") // same round → both survive (62 >= 62)
        ];

        HashSet<string> kept =
        [
            .. HighlightSurfacing.ApplyGroupSupersession(events)
                .Select(e => $"{e.HighlightId}@s{e.PlayerSlot}r{e.RoundNumber}")
        ];

        await Assert.That(kept.Contains("triple_kill@s0r1")).IsFalse().Because("4K supersedes the 3K");
        await Assert.That(kept.Contains("quad_kill@s0r1")).IsTrue();
        await Assert.That(kept.Contains("rapid_quad@s0r1")).IsTrue().Because("a different group survives");
        await Assert.That(kept.Contains("collateral@s0r1")).IsTrue().Because("ungrouped always survives");
        await Assert.That(kept.Contains("triple_kill@s1r1")).IsTrue().Because("different player");
        await Assert.That(kept.Contains("triple_kill@s0r2")).IsTrue().Because("different round");
        await Assert.That(HighlightSurfacing.ApplyGroupSupersession(events)
            .Count(e => e.HighlightId == "rapid_double")).IsEqualTo(2).Because("same-score peers both survive");
    }

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

    [Test]
    public async Task GotvProfileId_PinsTheBuildersResolvedProfileTypeName()
    {
        // The A2 fingerprint stamps this literal while the real build composes with
        // RuleChainBuilder.Profile.GetType().Name. A profile-class rename must break HERE,
        // loudly, instead of silently invalidating every cached fingerprint.
        await Assert.That(typeof(Cs2GotvProfile).Name)
            .IsEqualTo(RulesHighlightHarvester.GotvProfileId);
    }

    [Test]
    public async Task Backlog_IsDerived_SkipsSidecars_AndNeverPrunesTheSharedCache()
    {
        DemoCacheStore store = new(null);
        FakeHarvester harvester = new();
        store.Upsert(IndexedRow("/demos/fresh.dem", 0));
        store.Upsert(IndexedRow("/demos/stale.dem", 0, fingerprint: "OLD@64"));
        store.Upsert(IndexedRow("/demos/vanished.dem", 0));

        using HighlightScanService scanner = NewScanner(store, harvester,
            () => ["/demos/fresh.dem", "/demos/stale.dem", "/demos/new.dem", "/demos/._junk.dem"],
            () => false,
            (_, _) => null);

        scanner.RefreshStaleness();
        await WaitForAsync(() => scanner.QueueLength == 2, "derived backlog");

        IReadOnlyList<string> queued = scanner.PendingPaths();

        using (Assert.Multiple())
        {
            // THE ONE THAT MATTERS. The old pass deleted every row whose demo was not in the current
            // library paths. That was safe against a highlights-only store and is destructive against the
            // SHARED one: the same call would take the Library's tier-2 roster, score and rounds with it,
            // and a folder on a detached volume enumerates zero files, so it would fire precisely when the
            // demos are still fine. Pruning belongs to the Library, which has the reached-roots guard.
            await Assert.That(store.TryLoadRecord("/demos/vanished.dem")).IsNotNull()
                .Because("the scanner must never prune a cache it shares with the Library");

            await Assert.That(queued).Contains("/demos/stale.dem").Because("fingerprint mismatch = stale");
            await Assert.That(queued).Contains("/demos/new.dem").Because("never scanned");
            await Assert.That(queued).DoesNotContain("/demos/fresh.dem").Because("current under the fingerprint");
            await Assert.That(queued).DoesNotContain("/demos/._junk.dem")
                .Because("AppleDouble sidecars are not demos");
            await Assert.That(scanner.QueueLength).IsEqualTo(2);

            // Derived, not stored: a queued demo keeps whatever tier-3 payload it already had, which is what
            // stops a rules save blanking the highlight section of the whole library.
            await Assert.That(store.TryLoadRecord("/demos/stale.dem")!.AnalysisState)
                .IsEqualTo(DemoAnalysisState.Indexed);
        }
    }

    [Test]
    public async Task Backfill_DrainsNewestFirst_OneAtATime_UnderOptIn()
    {
        DemoCacheStore store = new(null);
        List<string> processed = [];
        int concurrent = 0, maxConcurrent = 0;

        using HighlightScanService scanner = NewScanner(store, new FakeHarvester(),
            () => [],
            () => true,
            (path, _) =>
            {
                int now = Interlocked.Increment(ref concurrent);
                // CAS max: a plain read-modify-write could drop the very violation it exists to record.
                int seen;
                while (now > (seen = Volatile.Read(ref maxConcurrent))
                       && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen)
                {
                }

                Thread.Sleep(20);
                lock (processed)
                {
                    processed.Add(path);
                }

                Interlocked.Decrement(ref concurrent);
                return Harvest();
            });

        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/old.dem",
            ModifiedTicks = 1
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/newest.dem",
            ModifiedTicks = 9
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/mid.dem",
            ModifiedTicks = 5
        });

        scanner.EnsureBackfillRunning();
        await WaitForAsync(() => scanner.QueueLength == 0 && !scanner.IsScanning, "queue drained");

        await Assert.That(processed).IsEquivalentTo(["/d/newest.dem", "/d/mid.dem", "/d/old.dem"])
            .Because("newest-first drain order");
        await Assert.That(maxConcurrent).IsEqualTo(1).Because("never two demos in flight");
        await Assert.That(store.Index.All(r => r.AnalysisState == DemoAnalysisState.Indexed)).IsTrue();
    }

    [Test]
    public async Task Backfill_OptInOff_DoesNothing_ButManualRequestForcesOneDrain()
    {
        DemoCacheStore store = new(null);
        int processedCount = 0;

        using HighlightScanService scanner = NewScanner(store, new FakeHarvester(),
            () => [],
            () => false,
            (path, _) =>
            {
                Interlocked.Increment(ref processedCount);
                return Harvest();
            });

        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/a.dem",
            ModifiedTicks = 1
        });

        scanner.EnsureBackfillRunning();
        await Task.Delay(100);
        await Assert.That(processedCount).IsEqualTo(0).Because("opt-in off — no background churn");

        scanner.RequestScan("/d/a.dem");
        await WaitForAsync(() => processedCount == 1 && !scanner.IsScanning, "forced manual drain");
        await Assert.That(store.TryLoadRecord("/d/a.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
    }

    [Test]
    public async Task OnParsedOpportunistically_RunsAnalysisOnlyWhenOptInOn()
    {
        DemoCacheStore store = new(null);
        FakeHarvester harvester = new();
        bool optIn = false;
        using HighlightScanService scanner = NewScanner(store, harvester,
            () => [],
            () => optIn,
            (_, _) => null);

        ParsedDemo parsed = SyntheticDemo();

        // Opt-in OFF: a missing row does the cheap fingerprint compare but must NOT run the full
        // replay (library indexing stays single-pass), and writes no row.
        scanner.OnParsedOpportunistically("/d/a.dem", parsed);
        await Assert.That(harvester.RunBareAnalysisCalls).IsEqualTo(0)
            .Because("the piggyback analysis is gated behind the D8 opt-in");
        await Assert.That(store.TryLoadRecord("/d/a.dem")).IsNull().Because("no analysis, no row");

        // Opt-in ON: the same missing row now drives the bare analysis (the fake records the call).
        optIn = true;
        scanner.OnParsedOpportunistically("/d/a.dem", parsed);
        await Assert.That(harvester.RunBareAnalysisCalls).IsEqualTo(1)
            .Because("with the opt-in on the piggyback runs a full replay");

        // Fresh matching row (mtime/size 0 == missing-file identity, fingerprint fp-A@64): the fast
        // path returns even with the opt-in on. The counter must not advance.
        store.Upsert(IndexedRow("/d/fresh.dem", 0));
        scanner.OnParsedOpportunistically("/d/fresh.dem", parsed);
        await Assert.That(harvester.RunBareAnalysisCalls).IsEqualTo(1)
            .Because("the fresh-row fast path skips analysis regardless of the opt-in");
    }

    [Test]
    public async Task RequestScan_OptInOff_DrainsOnlyTheRequestedPath()
    {
        DemoCacheStore store = new(null);
        List<string> processed = [];
        using HighlightScanService scanner = NewScanner(store, new FakeHarvester(),
            () => [],
            () => false,
            (path, _) =>
            {
                lock (processed)
                {
                    processed.Add(path);
                }

                return Harvest();
            });

        // A whole queue of Pending skeletons (a fresh library after RefreshStaleness)…
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/other1.dem",
            ModifiedTicks = 9
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/other2.dem",
            ModifiedTicks = 8
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/wanted.dem",
            ModifiedTicks = 1
        });

        // …a manual retry on ONE demo drains exactly that demo, even though it is the OLDEST,
        // and leaves the rest queued (D8: no whole-library marathon from a single retry click).
        scanner.RequestScan("/d/wanted.dem");
        await WaitForAsync(() => processed.Count > 0 && !scanner.IsScanning, "scoped forced drain");
        // Settle: give any (wrongly) enqueued auto rows a chance to run. They must not.
        await Task.Delay(100);

        await Assert.That(processed).IsEquivalentTo(["/d/wanted.dem"]);
        await Assert.That(store.TryLoadRecord("/d/wanted.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
        await Assert.That(store.TryLoadRecord("/d/other1.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Pending);
        await Assert.That(store.TryLoadRecord("/d/other2.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Pending);
    }

    [Test]
    public async Task Backfill_ProcessorFailure_MarksOnlyThatRowFailed()
    {
        DemoCacheStore store = new(null);
        using HighlightScanService scanner = NewScanner(store, new FakeHarvester(),
            () => [],
            () => true,
            (path, _) => path.Contains("bad", StringComparison.Ordinal) ? null : Harvest());

        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/bad.dem",
            ModifiedTicks = 9
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/good.dem",
            ModifiedTicks = 1
        });

        scanner.EnsureBackfillRunning();
        await WaitForAsync(() => scanner.QueueLength == 0 && !scanner.IsScanning, "drain");

        await Assert.That(store.TryLoadRecord("/d/bad.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Failed);
        await Assert.That(store.TryLoadRecord("/d/good.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
    }

    [Test]
    public async Task Backfill_YieldsToInteractiveGateHolder()
    {
        HeavyJobGate gate = new();
        DemoCacheStore store = new(null);
        int processedCount = 0;
        using HighlightScanService scanner = NewScanner(store, new FakeHarvester(),
            () => [],
            () => true,
            (path, _) =>
            {
                Interlocked.Increment(ref processedCount);
                return Harvest();
            },
            gate);

        // An interactive job holds the machine. The scanner's queued work must not process.
        IDisposable interactive = await gate.AcquireInteractiveAsync();
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/a.dem",
            ModifiedTicks = 1
        });
        scanner.EnsureBackfillRunning();
        await Task.Delay(150);
        await Assert.That(processedCount).IsEqualTo(0).Because("background yields to interactive");

        interactive.Dispose();
        await WaitForAsync(() => processedCount == 1, "resumed after interactive release");
    }

    private sealed class FakeHarvester : IHighlightHarvester
    {
        public readonly string CurrentFingerprint = "fp-A";
        public int RunBareAnalysisCalls;

        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate) =>
            ($"{CurrentFingerprint}@{tickRate}", new Dictionary<string, string>());

        // Records the call so the piggyback gate is observable, then throws. The queue/staleness
        // tests still route real work through the processor override, never through here.
        public AnalysisRun RunBareAnalysis(ParsedDemo demo)
        {
            Interlocked.Increment(ref RunBareAnalysisCalls);
            throw new NotSupportedException("unit tests fake the processor");
        }

        public void InvalidateRules()
        {
        }
    }
}

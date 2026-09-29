#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The per-entry choice between one forward pass and one retained parse, the forward entry's
///     cancellation, failure and progress, and the one-time re-evaluation a new round facts fingerprint
///     sends through the queue.
/// </summary>
public class ForwardQueueTests
{
    private static ForwardDemoResult Pass() => new()
    {
        Demo = new DemoDescriptor("de_test", 64, 1f / 64, 1000, 0, "test", "test", 0, DemoProfile.Unknown,
            new Dictionary<int, PlayerInfo>()),
        Rounds = [],
        FrameCount = 10,
        FirstServerTick = 0,
        LastServerTick = 1000
    };

    private static DemoProcessingQueue Queue(
        Func<string, ForwardNeeds, Action<double>, CancellationToken, ForwardDemoResult> forward,
        Func<string, DecodePlan, ParsedDemo>? parse = null,
        Func<ReadOnlyMemory<byte>, ParsedDemo>? parseBytes = null) =>
        new(new HeavyJobGate(), a => a(),
            parseBytes: parseBytes ?? (_ => SyntheticParsedDemo.Create()),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: parse ?? ((_, _) => throw new InvalidOperationException("retained parse")),
            forwardPass: forward);

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

    private static DemoProcessingRequest Forward(string path, string owner, ForwardNeeds needs,
        Action<ForwardDemoResult>? onForward = null, Action<Exception>? onFailed = null) =>
        new(path, owner, DemoJobPriority.Background, 1, _ => { }, onFailed, path, false,
            onForward ?? (_ => { }), needs);

    [Test]
    public async Task ABackgroundForwardPass_StoppedForAUserJob_RestartsAfterIt_AndItsOwnerGetsTheResult()
    {
        int passes = 0;
        using ManualResetEventSlim firstStarted = new();
        List<string> order = [];
        using DemoProcessingQueue queue = Queue((_, _, _, ct) =>
        {
            if (Interlocked.Increment(ref passes) == 1)
            {
                firstStarted.Set();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                ct.ThrowIfCancellationRequested();
            }

            lock (order)
            {
                order.Add("forward");
            }

            return Pass();
        });
        bool delivered = false;
        IDemoQueueHandle forward = queue.SubmitBackground(Forward("a.dem", "roundfacts", ForwardNeeds.Rules, _ => delivered = true));
        firstStarted.Wait(TimeSpan.FromSeconds(5));
        queue.SubmitJob(new QueueJobRequest(QueueJobKind.PackExport, "user", "test", DemoJobPriority.UserRequested, _ =>
        {
            lock (order)
            {
                order.Add("user");
            }

            return Task.CompletedTask;
        }));
        await forward.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", order)).IsEqualTo("user,forward");
            await Assert.That(passes).IsEqualTo(2).Because("a forward pass is not resumable, so it restarts");
            await Assert.That(delivered).IsTrue();
            await Assert.That(forward.State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    [Test]
    public async Task ForwardOnlyEntry_NeverMaterializesAParsedDemo()
    {
        int passes = 0;
        ForwardNeeds seen = ForwardNeeds.None;
        using DemoProcessingQueue queue = Queue((_, needs, _, _) =>
        {
            Interlocked.Increment(ref passes);
            seen = needs;
            return Pass();
        });
        Evaluator library = new("library", ForwardNeeds.FinalState | ForwardNeeds.Rules) { Wanted = { "a.dem" } };
        Evaluator highlights = new("highlights", ForwardNeeds.Rules) { Wanted = { "a.dem" } };
        Evaluator facts = new("roundfacts", ForwardNeeds.Rules) { Wanted = { "a.dem" } };
        using DemoEvaluationCoordinator coordinator = new([library, highlights, facts], queue, () => []);

        queue.Pause();
        coordinator.Consider("a.dem");
        queue.Resume();
        await WaitForAsync(() => facts.Forward + highlights.Forward + library.Forward == 3, "all three owners");
        // The owners run before the entry is marked finished, on the worker.
        await WaitForAsync(() => queue.Snapshot().Single().State == DemoQueueItemState.Completed, "the entry to finish");

        using (Assert.Multiple())
        {
            await Assert.That(passes).IsEqualTo(1);
            await Assert.That(seen).IsEqualTo(ForwardNeeds.FinalState | ForwardNeeds.Rules);
            await Assert.That(library.Retained + highlights.Retained + facts.Retained).IsEqualTo(0);
            await Assert.That(queue.Snapshot().Single().State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    [Test]
    public async Task EntryWithARetainedOwner_ParsesOnce_AndRunsNoForwardPass()
    {
        int passes = 0, parses = 0;
        using DemoProcessingQueue queue = Queue(
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref passes);
                return Pass();
            },
            (_, plan) =>
            {
                Interlocked.Increment(ref parses);
                return SyntheticParsedDemo.Create(plan: plan);
            });
        Evaluator facts = new("roundfacts", ForwardNeeds.Rules) { Wanted = { "a.dem" } };
        Evaluator index = new("roundindex", null) { Wanted = { "a.dem" } };
        using DemoEvaluationCoordinator coordinator = new([facts, index], queue, () => []);

        queue.Pause();
        coordinator.Consider("a.dem");
        queue.Resume();
        await WaitForAsync(() => facts.Retained + index.Retained == 2, "both owners");

        using (Assert.Multiple())
        {
            await Assert.That(parses).IsEqualTo(1);
            await Assert.That(passes).IsEqualTo(0);
            await Assert.That(facts.Forward).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ARunningForwardPass_TakesOwnersItCovers_ButNotAForegroundOpen()
    {
        using ManualResetEventSlim block = new(false);
        int byteParses = 0;
        using DemoProcessingQueue queue = Queue((_, _, _, _) =>
        {
            block.Wait(CancellationToken.None);
            return Pass();
        }, (_, plan) => SyntheticParsedDemo.Create(plan: plan), _ =>
        {
            Interlocked.Increment(ref byteParses);
            return SyntheticParsedDemo.Create();
        });

        IDemoQueueHandle running = queue.SubmitBackground(Forward("a.dem", "library", ForwardNeeds.FinalState | ForwardNeeds.Rules));
        await WaitForAsync(() => queue.RunningCount == 1, "forward running");

        IDemoQueueHandle covered = queue.SubmitBackground(Forward("a.dem", "roundfacts", ForwardNeeds.Rules));
        Task<ParsedDemo> open = queue.RequestForegroundAsync("a.dem", ReadOnlyMemory<byte>.Empty);
        block.Set();
        await open;

        using (Assert.Multiple())
        {
            await Assert.That(covered.Id).IsEqualTo(running.Id);
            await Assert.That(byteParses).IsEqualTo(1).Because("a forward pass holds no frames to hand an open");
        }
    }

    // Runs until its token is cancelled, like the real pass.
    private static ForwardDemoResult UntilCancelled(ManualResetEventSlim started, CancellationToken token)
    {
        started.Set();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(2);
        }
    }

    [Test]
    public async Task ARetainedOwner_StopsTheRunningForwardPass_AndEveryOwnerRunsOnOneParseBeforeOtherDemos()
    {
        using ManualResetEventSlim started = new(false);
        List<string> parses = [];
        int forwardCalls = 0;
        using DemoProcessingQueue queue = Queue(
            (path, _, _, token) =>
            {
                Interlocked.Increment(ref forwardCalls);
                return path == "a.dem" ? UntilCancelled(started, token) : Pass();
            },
            (path, plan) =>
            {
                lock (parses)
                {
                    parses.Add(path);
                }

                return SyntheticParsedDemo.Create(plan: plan);
            });
        List<string> ran = [];
        int forwarded = 0, failed = 0;

        DemoProcessingRequest Owner(string owner, ForwardNeeds? needs) => new("a.dem", owner, DemoJobPriority.Background, 1,
            _ =>
            {
                lock (ran)
                {
                    ran.Add(owner);
                }
            }, _ => failed++, "a.dem", false, needs is null ? null : _ => forwarded++, needs ?? ForwardNeeds.None);

        IDemoQueueHandle library = queue.SubmitBackground(Owner("library", ForwardNeeds.FinalState | ForwardNeeds.Rules));
        queue.SubmitBackground(Owner("roundfacts", ForwardNeeds.Rules));
        started.Wait(TimeSpan.FromSeconds(5));
        // Queued behind, newer: before the fold it would have waited for a.dem's parse to finish.
        queue.SubmitBackground(new DemoProcessingRequest("b.dem", "roundindex", DemoJobPriority.Background, 99, _ => { },
            NeedsUserCommands: false));
        IDemoQueueHandle index = queue.SubmitBackground(Owner("roundindex", null));
        await WaitForAsync(() => ran.Count == 3 && parses.Count == 2 && queue.ActiveWorkerCount == 0, "both demos");

        using (Assert.Multiple())
        {
            await Assert.That(ran).IsEquivalentTo(["library", "roundfacts", "roundindex"]);
            await Assert.That(parses).IsEquivalentTo(["a.dem", "b.dem"]).Because("a.dem is parsed once, and first");
            await Assert.That(parses[0]).IsEqualTo("a.dem");
            await Assert.That(forwarded).IsEqualTo(0);
            await Assert.That(failed).IsEqualTo(0);
            await Assert.That(index.Id).IsNotEqualTo(library.Id);
            await Assert.That(library.State).IsEqualTo(DemoQueueItemState.Cancelled).Because("the stopped pass");
            await Assert.That(forwardCalls).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AWiderForwardNeed_StopsTheNarrowPass_AndOneFullPassServesBoth()
    {
        using ManualResetEventSlim started = new(false);
        List<ForwardNeeds> passes = [];
        using DemoProcessingQueue queue = Queue((_, needs, _, token) =>
        {
            lock (passes)
            {
                passes.Add(needs);
            }

            return needs == ForwardNeeds.Rules ? UntilCancelled(started, token) : Pass();
        });
        int facts = 0, library = 0;

        queue.SubmitBackground(Forward("a.dem", "roundfacts", ForwardNeeds.Rules, _ => facts++));
        started.Wait(TimeSpan.FromSeconds(5));
        queue.SubmitBackground(Forward("a.dem", "library", ForwardNeeds.FinalState | ForwardNeeds.Rules, _ => library++));
        await WaitForAsync(() => facts + library == 2 && queue.ActiveWorkerCount == 0, "both owners");

        using (Assert.Multiple())
        {
            await Assert.That(passes).IsEquivalentTo([ForwardNeeds.Rules, ForwardNeeds.FinalState | ForwardNeeds.Rules]);
            await Assert.That(facts).IsEqualTo(1);
            await Assert.That(library).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ParseReleased_RunsOnceAfterEveryOwner()
    {
        List<string> calls = [];
        ParsedDemo? released = null;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, plan) => SyntheticParsedDemo.Create(plan: plan),
            parseReleased: p =>
            {
                calls.Add("released");
                released = p;
            });
        ParsedDemo? seen = null;
        queue.SubmitBackground(new DemoProcessingRequest("a.dem", "one", DemoJobPriority.Background, 1, p =>
        {
            calls.Add("one");
            seen = p;
        }));
        IDemoQueueHandle two = queue.SubmitBackground(
            new DemoProcessingRequest("a.dem", "two", DemoJobPriority.Background, 1, _ => calls.Add("two")));
        await two.Completion;

        await Assert.That(calls).IsEquivalentTo(["one", "two", "released"]);
        await Assert.That(calls[^1]).IsEqualTo("released");
        await Assert.That(ReferenceEquals(released, seen)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CancelledForwardPass_EndsCancelled_AndCallsNoOwner(bool byOwner)
    {
        using DemoProcessingQueue queue = Queue((_, _, progress, token) =>
        {
            progress(0.1);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(2);
            }
        });
        int forwarded = 0, failed = 0;
        IDemoQueueHandle handle = queue.SubmitBackground(Forward("a.dem", "library", ForwardNeeds.Rules,
            _ => forwarded++, _ => failed++));
        await WaitForAsync(() => queue.RunningCount == 1, "forward running");

        if (byOwner)
        {
            handle.Cancel();
        }
        else
        {
            queue.RemoveByUser(handle.Id);
        }

        await handle.Completion;
        using (Assert.Multiple())
        {
            await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Cancelled);
            await Assert.That(forwarded).IsEqualTo(0);
            await Assert.That(failed).IsEqualTo(0).Because("a cancelled demo is not a failed one");
        }
    }

    [Test]
    public async Task FailedForwardPass_ReachesOnFailed()
    {
        using DemoProcessingQueue queue = Queue((_, _, _, _) => throw new InvalidDataException("not a demo"));
        int failed = 0;
        IDemoQueueHandle handle = queue.SubmitBackground(Forward("a.dem", "library", ForwardNeeds.Rules, onFailed: _ => failed++));
        await handle.Completion;

        await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Failed);
        await Assert.That(failed).IsEqualTo(1);
    }

    [Test]
    public async Task ForwardPass_ReportsProgressOnTheItem()
    {
        using ManualResetEventSlim block = new(false);
        using DemoProcessingQueue queue = Queue((_, _, progress, _) =>
        {
            progress(0.42);
            block.Wait(CancellationToken.None);
            return Pass();
        });
        queue.SubmitBackground(Forward("a.dem", "library", ForwardNeeds.Rules));

        await WaitForAsync(() => queue.Snapshot().SingleOrDefault()?.Progress is > 0.41, "progress");
        block.Set();
    }

    [Test]
    public async Task NewRoundFactsFingerprint_ReEvaluatesEveryDemoOnce_ForwardAtBackgroundPriority_ThroughTheCap()
    {
        const int demos = 7;
        DemoCacheStore store = new(null);
        for (int i = 0; i < demos; i++)
        {
            store.Upsert(new DemoCacheRecord
            {
                Path = $"/d/{i}.dem",
                Size = 10,
                ModifiedTicks = i,
                Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 },
                RoundFacts = new RoundFactsRows(),
                RoundFactsFingerprint = "before-merge",
                AnalysisState = DemoAnalysisState.Indexed,
                ConfigFingerprint = "fp@64",
                Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 }
            });
        }

        List<string> passes = [];
        List<DemoJobPriority> priorities = [];
        int maxQueued = 0;
        DemoProcessingQueue? queueRef = null;
        using DemoProcessingQueue queue = Queue((path, _, _, _) =>
        {
            lock (passes)
            {
                passes.Add(path);
                priorities.AddRange(queueRef!.Snapshot().Where(s => s.Path == path).Select(s => s.Priority));
            }

            return Pass();
        });
        queueRef = queue;
        queue.MaxQueueSize = 3;
        queue.Changed += () => maxQueued = Math.Max(maxQueued,
            queue.Snapshot().Count(s => s.Kind == QueueJobKind.DemoProcessing && s.State is DemoQueueItemState.Queued or DemoQueueItemState.Running));

        RoundFactsEvaluator facts = new(store, new NoRows(), new Identity("after-merge"));
        using HighlightScanService highlights = new(store, new Harvester("fp"), () => [], () => true);
        using DemoEvaluationCoordinator coordinator = new([highlights, facts], queue,
            () => [.. highlights.PendingPaths().Concat(facts.PendingPaths()).Distinct()]);

        await Assert.That(facts.PendingPaths().Count).IsEqualTo(demos);
        await Assert.That(highlights.PendingPaths()).IsEmpty().Because("the highlight fingerprint did not move");

        coordinator.ConsiderAll();
        await WaitForAsync(() => facts.PendingPaths().Count == 0 && queue.ActiveWorkerCount == 0, "every demo re-evaluated");

        using (Assert.Multiple())
        {
            await Assert.That(passes.Order()).IsEquivalentTo(Enumerable.Range(0, demos).Select(i => $"/d/{i}.dem"));
            await Assert.That(priorities.Distinct()).IsEquivalentTo([DemoJobPriority.Background]);
            await Assert.That(maxQueued).IsLessThanOrEqualTo(3).Because("the backlog feeds the cap, it is not submitted at once");
        }
    }

    private sealed class Evaluator(string id, ForwardNeeds? forward) : IDemoEvaluator
    {
        public int Forward;
        public int Retained;
        public HashSet<string> Wanted { get; } = [];
        public string Id => id;
        public bool ReadsUserCommands => false;
        public bool Wants(string path) => Wanted.Contains(path);
        public ForwardNeeds? ForwardFor(string path) => forward;
        public void Evaluate(string path, ParsedDemo parsed) => Interlocked.Increment(ref Retained);
        public void EvaluateForward(string path, ForwardDemoResult pass) => Interlocked.Increment(ref Forward);
    }

    private sealed class NoRows : IRoundFactsRowSource
    {
        public RoundFactsTable Rows(ParsedDemo parsed) => RoundFactsTable.Unavailable("none");
    }

    private sealed class Identity(string fingerprint) : IRoundFactsRulesetIdentity
    {
        public string? Fingerprint(int tickRate) => fingerprint;
    }

    private sealed class Harvester(string fingerprint) : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate) =>
            ($"{fingerprint}@{tickRate}", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) => throw new NotSupportedException();

        public void InvalidateRules()
        {
        }
    }
}

#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The global demo-processing queue (demo-processing-queue.md). A fake parser stands in for the
///     heavy demo parse, no demos, no bytes, so the tests exercise priority ordering, coalescing,
///     the size cap + refeed, max-concurrency, pause/disable, removal, and the awaitable foreground
///     path deterministically. Correctness of the concurrency primitive is the whole point here.
/// </summary>
public class DemoProcessingQueueTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    private static ParsedDemo SyntheticDemo(int tickRate = 64) => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / tickRate, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static DemoProcessingQueue NewQueue(RecordingParser parser, out HeavyJobGate gate)
    {
        gate = new HeavyJobGate();
        return new DemoProcessingQueue(gate, a => a(), parser.ParseFile,
            parser.ParseBytes, NoCompact);
    }

    private static Task NoCompact() => Task.CompletedTask;

    // The stack frame of the mapped entry point. The type name alone also appears in DemoReader's ctor
    // signature on the byte[] path.
    private const string MappedParseFrame = "MemoryMappedDemoSource.ParseFile(";

    private static DemoProcessingRequest Req(string path, string owner, DemoJobPriority priority,
        long orderHint, Action<ParsedDemo>? onParsed = null, Action<Exception>? onFailed = null) =>
        new(path, owner, priority, orderHint, onParsed ?? (_ => { }), onFailed, Path.GetFileName(path));

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

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(TException).Name}, but it did not throw");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Foreground_DuringTheOnParsedWindow_DoesNotHang()
    {
        // Regression for the FinishEntry TOCTOU: the entry stays Running while OnParsed runs, but the
        // waiter/attachment snapshot was already taken. A foreground open that coalesced onto it during
        // that window was appended AFTER the snapshot and never signalled → the open await hung forever.
        // With the Finalizing gate, the finishing entry is no longer coalesceable, so the open runs its
        // own parse and completes.
        RecordingParser parser = new();
        using DemoProcessingQueue queue = NewQueue(parser, out _);

        using ManualResetEventSlim inOnParsed = new(false);
        using ManualResetEventSlim releaseOnParsed = new(false);

        queue.SubmitBackground(Req("/d/x.dem", "library", DemoJobPriority.Background, 1,
            _ =>
            {
                inOnParsed.Set(); // the worker is now INSIDE OnParsed (entry Running + Finalizing)
                releaseOnParsed.Wait(2000); // hold that window open while the foreground races in
            }));

        await Task.Run(() => inOnParsed.Wait(5000));

        // Start the foreground open for the SAME path mid-window, then release OnParsed. The open must
        // complete (WaitAsync throws TimeoutException on the orphaned-waiter hang the fix prevents).
        Task<ParsedDemo> open = queue.RequestForegroundAsync("/d/x.dem", ReadOnlyMemory<byte>.Empty);
        await Task.Delay(50); // give the foreground time to take the (now-excluded) coalesce path
        releaseOnParsed.Set();

        ParsedDemo result = await open.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result).IsNotNull();
        await Assert.That(parser.ByteCalls).IsEqualTo(1).Because("the finalizing entry is not coalesceable — the open parsed its own bytes");
    }


    [Test]
    public async Task Priority_UserRequestedFirst_ThenBackgroundNewestFirst()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            // Pause so every item is queued before any runs → deterministic drain order at max=1.
            q.Pause();
            q.SubmitBackground(Req("/d/a.dem", "lib", DemoJobPriority.Background, 1));
            q.SubmitBackground(Req("/d/c.dem", "lib", DemoJobPriority.Background, 9)); // newest
            q.SubmitBackground(Req("/d/b.dem", "lib", DemoJobPriority.Background, 5));
            q.SubmitBackground(Req("/d/urgent.dem", "hl", DemoJobPriority.UserRequested, 0));
            q.Resume();

            await WaitForAsync(() => parser.Processed.Count == 4, "all four drained");
            await Assert.That(parser.Processed)
                .IsEquivalentTo(["/d/urgent.dem", "/d/c.dem", "/d/b.dem", "/d/a.dem"]);
            await Assert.That(parser.MaxConcurrent).IsEqualTo(1).Because("default max concurrency is 1");
        }
    }

    [Test]
    public async Task Coalesce_TwoOwnersSamePath_OneParse_BothHandlersRun()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            int libRan = 0, hlRan = 0;
            q.Pause();
            q.SubmitBackground(Req("/d/x.dem", "library", DemoJobPriority.Background, 5,
                _ => Interlocked.Increment(ref libRan)));
            q.SubmitBackground(Req("/d/x.dem", "highlights", DemoJobPriority.Background, 5,
                _ => Interlocked.Increment(ref hlRan)));

            // Two owners, one entry.
            await Assert.That(q.Snapshot().Count).IsEqualTo(1).Because("same path coalesces to one item");
            await Assert.That(q.Snapshot()[0].Owners).IsEquivalentTo(["library", "highlights"]);

            q.Resume();
            await WaitForAsync(() => libRan == 1 && hlRan == 1, "both owners' handlers ran");
            await Assert.That(parser.FileCalls).IsEqualTo(1).Because("the demo is parsed exactly once");
        }
    }

    [Test]
    public async Task MaxQueueSize_RejectsOverflow_CapacityAvailableRefeedsAll()
    {
        RecordingParser parser = new()
        {
            SleepMs = 15
        };
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            q.MaxConcurrency = 1;
            q.MaxQueueSize = 2;

            List<string> backlog = new()
            {
                "/d/1.dem",
                "/d/2.dem",
                "/d/3.dem",
                "/d/4.dem",
                "/d/5.dem"
            };
            HashSet<string> done = new();
            HashSet<string> pending = new(backlog);
            object pendingLock = new();

            void Feed()
            {
                string[] toSubmit;
                lock (pendingLock)
                {
                    toSubmit = pending.ToArray();
                }

                foreach (string p in toSubmit)
                {
                    q.SubmitBackground(Req(p, "lib", DemoJobPriority.Background, 0,
                        _ =>
                        {
                            lock (pendingLock)
                            {
                                done.Add(p);
                                pending.Remove(p);
                            }
                        }));
                }
            }

            // The consumer refeeds on capacity, the reject-on-full + idempotent-refeed contract.
            q.CapacityAvailable += Feed;
            Feed(); // initial submit (2 admitted, 3 rejected)

            await WaitForAsync(() =>
            {
                lock (pendingLock)
                {
                    return done.Count == 5;
                }
            }, "all five processed despite the size-2 cap");

            await Assert.That(parser.MaxConcurrent).IsEqualTo(1).Because("cap=2 items in queue, but max=1 in flight");
            await Assert.That(parser.Processed.Count).IsEqualTo(5);
        }
    }

    /// <summary>
    ///     <c>max=2</c> admits exactly two workers at once: a floor as well as a ceiling.
    ///     <para>
    ///         Held on a gate, not a sleep. This used to give each parse a 120 ms <c>Thread.Sleep</c> and
    ///         then assert the observed peak had reached 2, which is a bet that the scheduler overlaps
    ///         them. On a two-core runner executing four batches in parallel the first job finished
    ///         before the second started, the peak was 1, and CI went red on a queue that was behaving
    ///         perfectly ("but found 1", 2026-08-26). Blocking every parse until two are inside tests the
    ///         contract (the queue ADMITS two) rather than the machine's timing.
    ///     </para>
    ///     <para>
    ///         The two failure modes stay distinguishable. A queue that admits only one fails on
    ///         <c>WaitForAsync</c>'s timeout, naming what it waited for; one that admits three is caught
    ///         by the equality below, which still reads the peak.
    ///     </para>
    /// </summary>
    [Test]
    public async Task MaxConcurrency_Two_RunsUpToTwoInFlight()
    {
        using ManualResetEventSlim held = new(false);
        RecordingParser parser = new()
        {
            // Parked INSIDE ParseFile, after the concurrency counter has been incremented, so a blocked
            // worker counts as in flight, which is what makes the peak observable without a race.
            Block = held
        };
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            q.MaxConcurrency = 2;
            for (int i = 0; i < 4; i++)
            {
                q.SubmitBackground(Req($"/d/{i}.dem", "lib", DemoJobPriority.Background, i));
            }

            try
            {
                await WaitForAsync(() => parser.MaxConcurrent >= 2, "two workers in flight at max=2");
            }
            finally
            {
                // Never leave a worker parked, even on the failing path: the queue's Dispose joins its
                // workers, so an unreleased gate would turn a clean assertion failure into a hang.
                held.Set();
            }

            await WaitForAsync(() => parser.Processed.Count == 4, "all four processed at max=2");
            await Assert.That(parser.MaxConcurrent).IsEqualTo(2)
                .Because("max=2 is a ceiling as well as a floor");
        }
    }

    [Test]
    public async Task Pause_StopsNewStarts_Resume_Continues()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            q.Pause();
            q.SubmitBackground(Req("/d/a.dem", "lib", DemoJobPriority.Background, 1));
            await Task.Delay(150);
            await Assert.That(parser.Processed.Count).IsEqualTo(0).Because("paused → no new starts");

            q.Resume();
            await WaitForAsync(() => parser.Processed.Count == 1, "resume drains the queued item");
        }
    }

    [Test]
    public async Task Disabled_NoBackground_ButForegroundStillRuns()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            q.BackgroundEnabled = false;
            q.SubmitBackground(Req("/d/a.dem", "lib", DemoJobPriority.Background, 1));
            await Task.Delay(150);
            await Assert.That(parser.Processed.Count).IsEqualTo(0).Because("background disabled → nothing runs");

            ParsedDemo fg = await q.RequestForegroundAsync("/d/open.dem", new byte[]
            {
                1, 2, 3
            });
            await Assert.That(fg).IsSameReferenceAs(parser.ForegroundDemo)
                .Because("foreground bypasses the disable switch");
        }
    }

    [Test]
    public async Task Foreground_PauseAndDisableOn_QueueFull_StillReturnsPromptly()
    {
        // The discriminating guarantee: a lost interactive-await is worse than any missing UI.
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            q.Pause();
            q.BackgroundEnabled = false;
            q.MaxQueueSize = 2;
            q.SubmitBackground(Req("/d/1.dem", "lib", DemoJobPriority.Background, 1));
            q.SubmitBackground(Req("/d/2.dem", "lib", DemoJobPriority.Background, 2));
            IDemoQueueHandle rejected = q.SubmitBackground(Req("/d/3.dem", "lib", DemoJobPriority.Background, 3));
            await Assert.That(rejected.State).IsEqualTo(DemoQueueItemState.Rejected).Because("tier full");

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
            ParsedDemo fg = await q.RequestForegroundAsync("/d/open.dem", new byte[]
            {
                9
            }, cts.Token);
            await Assert.That(fg).IsSameReferenceAs(parser.ForegroundDemo);
            await Assert.That(parser.Processed.Count).IsEqualTo(0).Because("no background ran; foreground is the fast-path");
        }
    }

    [Test]
    public async Task Foreground_CoalescesOntoInFlightBackgroundParse()
    {
        RecordingParser parser = new();
        using (parser.Block = new ManualResetEventSlim(false))
        {
            using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
            using (gate)
            {
                // Background parse of X begins and blocks (State becomes Running).
                q.SubmitBackground(Req("/d/x.dem", "lib", DemoJobPriority.Background, 1));
                await WaitForAsync(() => q.Snapshot().Any(s => s.State == DemoQueueItemState.Running),
                    "background parse running");

                // A user opens X while it is in flight → coalesces onto that parse (best-effort reuse).
                Task<ParsedDemo> fg = q.RequestForegroundAsync("/d/x.dem", new byte[]
                {
                    1
                });
                await Task.Delay(50);
                await Assert.That(fg.IsCompleted).IsFalse().Because("still waiting on the in-flight parse");

                parser.Block.Set(); // let the background parse finish
                ParsedDemo result = await fg;

                await Assert.That(result).IsSameReferenceAs(parser.LastFileDemo)
                    .Because("foreground reused the background parse result");
                await Assert.That(parser.FileCalls).IsEqualTo(1);
                await Assert.That(parser.ByteCalls).IsEqualTo(0).Because("no redundant foreground parse");
            }
        }
    }

    [Test]
    public async Task PerOwnerRemoval_CoOwnerSurvives()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            int libRan = 0, hlRan = 0;
            q.Pause();
            IDemoQueueHandle libHandle = q.SubmitBackground(Req("/d/x.dem", "library", DemoJobPriority.Background, 5,
                _ => Interlocked.Increment(ref libRan)));
            q.SubmitBackground(Req("/d/x.dem", "highlights", DemoJobPriority.Background, 5,
                _ => Interlocked.Increment(ref hlRan)));

            // The library cancels ITS submission. The item survives for highlights.
            libHandle.Cancel();
            await Assert.That(q.Snapshot().Count).IsEqualTo(1).Because("a co-owner keeps the item alive");
            await Assert.That(q.Snapshot()[0].Owners).IsEquivalentTo(["highlights"]);

            q.Resume();
            await WaitForAsync(() => hlRan == 1, "highlights handler ran");
            await Assert.That(libRan).IsEqualTo(0).Because("the cancelled owner's handler must NOT run");
        }
    }

    [Test]
    public async Task UserRemoval_OfQueuedItem_NeverProcesses()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            q.Pause();
            IDemoQueueHandle handle = q.SubmitBackground(Req("/d/x.dem", "lib", DemoJobPriority.Background, 1));
            q.RemoveByUser(handle.Id);
            await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Cancelled);

            q.Resume();
            await Task.Delay(150);
            await Assert.That(parser.Processed.Count).IsEqualTo(0).Because("a user-removed item never parses");
        }
    }

    [Test]
    public async Task BackfillFailure_MarksOnlyThatItemFailed_OthersProceed()
    {
        RecordingParser parser = new();
        parser.FailPaths.Add("/d/bad.dem");
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            Exception? badError = null;
            q.Pause();
            IDemoQueueHandle bad = q.SubmitBackground(Req("/d/bad.dem", "lib", DemoJobPriority.Background, 9,
                onFailed: ex => badError = ex));
            IDemoQueueHandle good = q.SubmitBackground(Req("/d/good.dem", "lib", DemoJobPriority.Background, 1));
            q.Resume();

            await bad.Completion;
            await good.Completion;
            await Assert.That(bad.State).IsEqualTo(DemoQueueItemState.Failed);
            await Assert.That(good.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(badError).IsNotNull().Because("the owner's OnFailed fired");
        }
    }

    [Test]
    public async Task Foreground_ReturnsActualParsedDemo_AndHonoursCancellation()
    {
        RecordingParser parser = new();
        using DemoProcessingQueue q = NewQueue(parser, out HeavyJobGate gate);
        using (gate)
        {
            ParsedDemo fg = await q.RequestForegroundAsync(null, new byte[]
            {
                1, 2
            });
            await Assert.That(fg).IsSameReferenceAs(parser.ForegroundDemo);

            using CancellationTokenSource cts = new();
            cts.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(async () => await q.RequestForegroundAsync(null, new byte[]
            {
                3
            }, cts.Token));
        }
    }

    // ── History retention + drain compaction ─────────────────────────────────

    [Test]
    public async Task CoalescedForeground_TerminalHistory_DoesNotRootTheParsedDemo()
    {
        using ManualResetEventSlim block = new(false);
        int byteCalls = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            _ =>
            {
                block.Wait();
                return SyntheticDemo();
            },
            _ =>
            {
                Interlocked.Increment(ref byteCalls);
                return SyntheticDemo();
            },
            NoCompact);

        WeakReference demo = await CoalesceOntoRunningParseAsync(queue, block);
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "worker exit");

        await Assert.That(byteCalls).IsEqualTo(0); // the open coalesced; it did not parse on its own
        await Assert.That(queue.Snapshot().Single().State).IsEqualTo(DemoQueueItemState.Completed);

        // Polled: this continuation can run inline inside the helper's completion, before the
        // runtime clears the helper's state machine.
        await WaitForAsync(() =>
        {
            Collect();
            return !demo.IsAlive;
        }, "the coalesced demo to be collected", 2000);
    }

    [Test]
    public async Task Drain_AfterNonParseJobs_CompactsOnce()
    {
        RecordingParser parser = new();
        int compactions = 0;
        using DemoProcessingQueue queue = CountingQueue(parser, new ManualClock(), () => Interlocked.Increment(ref compactions));
        using ManualResetEventSlim block = new(false);

        queue.SubmitJob(Job("a", _ => Task.Run(block.Wait)));
        queue.SubmitJob(Job("b", _ => Task.CompletedTask));
        block.Set();
        await WaitForAsync(() => Volatile.Read(ref compactions) == 1 && queue.ActiveWorkerCount == 0, "drain");
        await Assert.That(queue.Snapshot().All(x => x.State == DemoQueueItemState.Completed)).IsTrue();

        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);
    }

    [Test]
    public async Task EachParse_IsFollowedByACompaction_BeforeTheNextQueuedParse()
    {
        List<string> order = [];
        using ManualResetEventSlim block = new(false);
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            path =>
            {
                block.Wait();
                lock (order)
                {
                    order.Add(Path.GetFileName(path));
                }

                return SyntheticDemo();
            }, _ => SyntheticDemo(), () =>
            {
                lock (order)
                {
                    order.Add("compact");
                }

                return Task.CompletedTask;
            }, new ManualClock());

        queue.SubmitBackground(Req("a.dem", "o", DemoJobPriority.Background, 3));
        queue.SubmitBackground(Req("b.dem", "o", DemoJobPriority.Background, 2));
        queue.SubmitBackground(Req("c.dem", "o", DemoJobPriority.Background, 1));
        block.Set();
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && queue.Snapshot().All(s => s.State == DemoQueueItemState.Completed),
            "drain");

        // No throttle between parses, and the drain adds nothing after the last one.
        await Assert.That(string.Join(",", order)).IsEqualTo("a.dem,compact,b.dem,compact,c.dem,compact");
    }

    [Test]
    public async Task FailedParse_DoesNotCompact_ButAJobThatNotesAParse_Does()
    {
        RecordingParser parser = new();
        parser.FailPaths.Add("bad.dem");
        ManualClock clock = new();
        int compactions = 0;
        using DemoProcessingQueue queue = CountingQueue(parser, clock, () => Interlocked.Increment(ref compactions));

        await RunOneAsync(queue, "ok.dem");
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);

        // Inside the 30 s window: a failed parse is a non-parse job and defers to the window end.
        await RunOneAsync(queue, "bad.dem");
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);
        await Assert.That(clock.PendingTimers).IsEqualTo(1);

        IDemoQueueHandle clips = queue.SubmitJob(Job("clips", ctx =>
        {
            ctx.NoteDemoParsed();
            return Task.CompletedTask;
        }));
        await clips.Completion;
        await WaitForAsync(() => Volatile.Read(ref compactions) == 2 && queue.ActiveWorkerCount == 0, "the clip job's compaction");
        await Assert.That(clock.PendingTimers).IsEqualTo(0);
    }

    [Test]
    public async Task Compaction_OutranksAUserRequestedParse_AndRunsWithBackgroundDisabled()
    {
        List<string> order = [];
        using ManualResetEventSlim block = new(false);
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            path =>
            {
                block.Wait();
                lock (order)
                {
                    order.Add(Path.GetFileName(path));
                }

                return SyntheticDemo();
            }, _ => SyntheticDemo(), () =>
            {
                lock (order)
                {
                    order.Add("compact");
                }

                return Task.CompletedTask;
            }, new ManualClock());

        queue.SubmitBackground(Req("a.dem", "o", DemoJobPriority.UserRequested, 1));
        await WaitForAsync(() => queue.RunningCount == 1, "a running");
        queue.BackgroundEnabled = false;
        queue.SubmitBackground(Req("b.dem", "o", DemoJobPriority.UserRequested, 1));
        block.Set();
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && queue.Snapshot().All(s => s.State == DemoQueueItemState.Completed),
            "drain");

        await Assert.That(string.Join(",", order)).IsEqualTo("a.dem,compact,b.dem,compact");
    }

    [Test]
    public async Task Drain_WithinThrottleWindow_DefersOneCompactionToTheWindowEnd()
    {
        RecordingParser parser = new();
        ManualClock clock = new();
        int compactions = 0;
        using DemoProcessingQueue queue = CountingQueue(parser, clock, () => Interlocked.Increment(ref compactions));

        await RunJobOnceAsync(queue, "a");
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);

        clock.Advance(TimeSpan.FromSeconds(5));
        await RunJobOnceAsync(queue, "b");
        await RunJobOnceAsync(queue, "c");
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);
        await Assert.That(clock.PendingTimers).IsEqualTo(1); // two drains, one scheduled compaction

        clock.Advance(TimeSpan.FromSeconds(24));
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);

        clock.Advance(TimeSpan.FromSeconds(1)); // window ends 30 s after the first compaction
        // The compaction is a queue item, so the timer only submits it.
        await WaitForAsync(() => Volatile.Read(ref compactions) == 2, "the deferred compaction");
        await Assert.That(clock.PendingTimers).IsEqualTo(0);
    }

    [Test]
    public async Task DeferredCompaction_IsCancelledByAJobStart_AndTheNextDrainDecides()
    {
        RecordingParser parser = new();
        ManualClock clock = new();
        int compactions = 0;
        using DemoProcessingQueue queue = CountingQueue(parser, clock, () => Interlocked.Increment(ref compactions));

        await RunJobOnceAsync(queue, "a");
        clock.Advance(TimeSpan.FromSeconds(5));
        await RunJobOnceAsync(queue, "b"); // deferred to T+30
        await Assert.That(clock.PendingTimers).IsEqualTo(1);

        using ManualResetEventSlim block = new(false);
        IDemoQueueHandle c = queue.SubmitJob(Job("c", _ => Task.Run(block.Wait)));
        await WaitForAsync(() => queue.RunningCount == 1, "c running");
        await Assert.That(clock.PendingTimers).IsEqualTo(0);

        clock.Advance(TimeSpan.FromSeconds(30)); // past the old due time; nothing may fire
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(1);

        block.Set();
        await c.Completion;
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "worker exit c");
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(2); // T+35: outside the window, runs now
    }

    // ── Per-entry decode plan ────────────────────────────────────────────────

    private static bool HasUserCommands(DecodePlan plan) => (plan.Categories & MessageCategories.UserCmds) != 0;

    private static DemoProcessingQueue PlanQueue(Dictionary<string, DecodePlan> plans, ManualResetEventSlim? block = null,
        Func<ReadOnlyMemory<byte>, ParsedDemo>? parseBytes = null) =>
        new(new HeavyJobGate(), a => a(), parseBytes: parseBytes ?? (_ => SyntheticDemo()), compactHeap: NoCompact,
            timeProvider: new ManualClock(), parseFileWithPlan: (path, plan) =>
            {
                block?.Wait();
                lock (plans)
                {
                    plans[path] = plan;
                }

                return SyntheticParsedDemo.Create(plan: plan);
            });

    [Test]
    public async Task Plan_DropsUserCommands_OnlyWhenNoOwnerReadsThem()
    {
        Dictionary<string, DecodePlan> plans = [];
        using ManualResetEventSlim block = new(false);
        using DemoProcessingQueue queue = PlanQueue(plans, block);

        queue.SubmitBackground(Req("hold.dem", "o", DemoJobPriority.Background, 9));
        await WaitForAsync(() => queue.RunningCount == 1, "hold running");
        queue.SubmitBackground(Req("narrow.dem", "library", DemoJobPriority.Background, 3) with { NeedsUserCommands = false });
        queue.SubmitBackground(Req("mixed.dem", "library", DemoJobPriority.Background, 2) with { NeedsUserCommands = false });
        queue.SubmitBackground(Req("mixed.dem", "grenades", DemoJobPriority.Background, 2));
        queue.SubmitBackground(Req("full.dem", "grenades", DemoJobPriority.Background, 1));
        block.Set();
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && plans.Count == 4, "drain");

        using (Assert.Multiple())
        {
            await Assert.That(HasUserCommands(plans["narrow.dem"])).IsFalse();
            await Assert.That(plans["narrow.dem"].Categories).IsEqualTo(MessageCategories.All & ~MessageCategories.UserCmds);
            await Assert.That(plans["mixed.dem"].DecodesEverything).IsTrue().Because("a queued entry takes the union");
            await Assert.That(plans["full.dem"].DecodesEverything).IsTrue();
            await Assert.That(plans["hold.dem"].DecodesEverything).IsTrue().Because("requests default to needing them");
        }
    }

    [Test]
    public async Task ARunningNarrowParse_IsNotJoinedByAReaderOrAForegroundOpen()
    {
        Dictionary<string, DecodePlan> plans = [];
        using ManualResetEventSlim block = new(false);
        int byteCalls = 0;
        using DemoProcessingQueue queue = PlanQueue(plans, block, _ =>
        {
            Interlocked.Increment(ref byteCalls);
            return SyntheticDemo();
        });
        List<DecodePlan> seen = [];

        IDemoQueueHandle narrow = queue.SubmitBackground(
            Req("a.dem", "library", DemoJobPriority.Background, 1, p => seen.Add(p.Plan)) with { NeedsUserCommands = false });
        await WaitForAsync(() => queue.RunningCount == 1, "a running");

        IDemoQueueHandle reader = queue.SubmitBackground(
            Req("a.dem", "grenades", DemoJobPriority.Background, 1, p => seen.Add(p.Plan)));
        Task<ParsedDemo> open = queue.RequestForegroundAsync("a.dem", ReadOnlyMemory<byte>.Empty);
        await Assert.That(reader.Id).IsNotEqualTo(narrow.Id);

        block.Set();
        ParsedDemo opened = await open;
        await narrow.Completion;
        await reader.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(byteCalls).IsEqualTo(1).Because("the open parsed its own bytes");
            await Assert.That(opened.Plan.DecodesEverything).IsTrue();
            await Assert.That(seen.Count).IsEqualTo(2);
            await Assert.That(HasUserCommands(seen[0])).IsFalse();
            await Assert.That(seen[1].DecodesEverything).IsTrue();
        }
    }

    [Test]
    public async Task Coordinator_PassesEachEvaluatorsUserCommandNeed()
    {
        Dictionary<string, DecodePlan> plans = [];
        using DemoProcessingQueue queue = PlanQueue(plans);
        PlanEvaluator library = new("library", false) { Wanted = { "a.dem", "b.dem" } };
        PlanEvaluator grenades = new("grenades", true) { Wanted = { "b.dem" } };
        using DemoEvaluationCoordinator coordinator = new([library, grenades], queue, () => []);

        queue.Pause();
        coordinator.Consider("a.dem");
        coordinator.Consider("b.dem");
        queue.Resume();
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && plans.Count == 2, "drain");

        using (Assert.Multiple())
        {
            await Assert.That(HasUserCommands(plans["a.dem"])).IsFalse();
            await Assert.That(plans["b.dem"].DecodesEverything).IsTrue();
        }
    }

    [Test]
    public async Task Coordinator_OnAnIdleQueue_ParsesOnceWithTheUnion()
    {
        Dictionary<string, DecodePlan> plans = [];
        int parses = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), parseBytes: _ => SyntheticDemo(),
            compactHeap: NoCompact, timeProvider: new ManualClock(), parseFileWithPlan: (path, plan) =>
            {
                Interlocked.Increment(ref parses);
                Thread.Sleep(50);
                lock (plans)
                {
                    plans[path] = plan;
                }

                return SyntheticParsedDemo.Create(plan: plan);
            });
        PlanEvaluator library = new("library", false) { Wanted = { "a.dem" } };
        PlanEvaluator grenades = new("grenades", true) { Wanted = { "a.dem" } };
        using DemoEvaluationCoordinator coordinator = new([library, grenades], queue, () => []);

        coordinator.Consider("a.dem");
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && plans.Count == 1 && !coordinator.HasOutstanding("grenades"),
            "drain");

        using (Assert.Multiple())
        {
            await Assert.That(Volatile.Read(ref parses)).IsEqualTo(1);
            await Assert.That(plans["a.dem"].DecodesEverything).IsTrue();
        }
    }

    private sealed class PlanEvaluator(string id, bool readsUserCommands) : IDemoEvaluator
    {
        public HashSet<string> Wanted { get; } = [];
        public string Id => id;
        public bool ReadsUserCommands => readsUserCommands;
        public bool Wants(string path) => Wanted.Contains(path);

        public void Evaluate(string path, ParsedDemo parsed)
        {
        }
    }

    [Test]
    public async Task SettledFile_IsMapped_RecentOrChangingFileIsNot()
    {
        ManualClock clock = new();
        DateTimeOffset now = clock.GetUtcNow();
        FileStat old = new(1000, now - TimeSpan.FromSeconds(61));

        await Assert.That(MappedParsePolicy.IsSettled("x", clock, _ => old)).IsTrue();

        FileStat recent = new(1000, now - TimeSpan.FromSeconds(59));
        await Assert.That(MappedParsePolicy.IsSettled("x", clock, _ => recent)).IsFalse();

        int calls = 0;
        FileStat Growing(string _) => ++calls == 1 ? old : old with { Length = 2000 };
        await Assert.That(MappedParsePolicy.IsSettled("x", clock, Growing)).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    private static DemoProcessingQueue CountingQueue(RecordingParser parser, ManualClock clock, Action onCompact) =>
        new(new HeavyJobGate(), a => a(), parser.ParseFile, parser.ParseBytes, () =>
        {
            onCompact();
            return Task.CompletedTask;
        }, clock);

    private static QueueJobRequest Job(string key, Func<IQueueJobContext, Task> body) =>
        new(QueueJobKind.StratMining, key, "o", DemoJobPriority.Background, body, key);

    private static async Task RunJobOnceAsync(DemoProcessingQueue queue, string key)
    {
        IDemoQueueHandle h = queue.SubmitJob(Job(key, _ => Task.CompletedTask));
        await h.Completion;
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "worker exit " + key);
    }

    private static async Task RunOneAsync(DemoProcessingQueue queue, string path)
    {
        IDemoQueueHandle h = queue.SubmitBackground(Req(path, "o", DemoJobPriority.Background, 1));
        await h.Completion;
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "worker exit " + path);
    }

    [Test]
    public async Task Drain_WithNoJobRun_DoesNotCompact()
    {
        RecordingParser parser = new();
        int compactions = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), parser.ParseFile,
            parser.ParseBytes, () =>
            {
                Interlocked.Increment(ref compactions);
                return Task.CompletedTask;
            });

        queue.Pause();
        IDemoQueueHandle h = queue.SubmitBackground(Req("a.dem", "o", DemoJobPriority.Background, 1));
        queue.RemoveByUser(h.Id);

        await Assert.That(queue.Snapshot().Single().State).IsEqualTo(DemoQueueItemState.Cancelled);
        await Assert.That(Volatile.Read(ref compactions)).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultParse_OnRecentNonDemoFile_ReadsBytes_AndFailsTheEntry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-queue-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        try
        {
            using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), compactHeap: NoCompact);
            Exception? failure = null;
            IDemoQueueHandle h = queue.SubmitBackground(
                Req(path, "o", DemoJobPriority.Background, 1, onFailed: ex => failure = ex));
            await h.Completion;

            await Assert.That(h.State).IsEqualTo(DemoQueueItemState.Failed);
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.ToString()).DoesNotContain(MappedParseFrame);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task DefaultParse_OnSettledNonDemoFile_TakesTheMappedPath_AndFailsTheEntry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-queue-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        try
        {
            using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), compactHeap: NoCompact);
            Exception? failure = null;
            IDemoQueueHandle h = queue.SubmitBackground(
                Req(path, "o", DemoJobPriority.Background, 1, onFailed: ex => failure = ex));
            await h.Completion;

            await Assert.That(h.State).IsEqualTo(DemoQueueItemState.Failed);
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.ToString()).Contains(MappedParseFrame);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task FailedCompaction_DoesNotStartTheThrottle()
    {
        RecordingParser parser = new();
        ManualClock clock = new();
        int attempts = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), parser.ParseFile, parser.ParseBytes,
            () =>
            {
                return Interlocked.Increment(ref attempts) == 1
                    ? Task.FromException(new InvalidOperationException("compaction failed"))
                    : Task.CompletedTask;
            }, clock);

        await RunJobOnceAsync(queue, "a"); // fails
        clock.Advance(TimeSpan.FromSeconds(1));
        await RunJobOnceAsync(queue, "b"); // not throttled: runs now

        await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(2);
        await Assert.That(clock.PendingTimers).IsEqualTo(0);
    }

    // The foreground result lives only in this method's frame, which is gone once it returns.
    private static async Task<WeakReference> CoalesceOntoRunningParseAsync(DemoProcessingQueue queue,
        ManualResetEventSlim block)
    {
        queue.SubmitBackground(Req("a.dem", "o", DemoJobPriority.Background, 1));
        await WaitForAsync(() => queue.RunningCount == 1, "running");
        Task<ParsedDemo> open = queue.RequestForegroundAsync("a.dem", ReadOnlyMemory<byte>.Empty);
        block.Set();
        return new WeakReference(await open);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }
    }

    // One-shot timers only; they fire on the Advance caller's thread.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public int PendingTimers
        {
            get
            {
                lock (_lock)
                {
                    return _timers.Count(t => !t.Disposed);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                ManualTimer t = new(() => callback(state), _now + dueTime);
                _timers.Add(t);
                return t;
            }
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _now += by;
                due = _timers.Where(t => !t.Disposed && t.Due <= _now).ToList();
                _timers.RemoveAll(t => t.Disposed || t.Due <= _now);
            }

            foreach (ManualTimer t in due)
            {
                t.Fire();
            }
        }
    }

    private sealed class ManualTimer(Action fire, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; } = due;
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Fire()
        {
            if (!Disposed)
            {
                fire();
            }
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingParser
    {
        public readonly HashSet<string> FailPaths = new(StringComparer.OrdinalIgnoreCase);
        public readonly ParsedDemo ForegroundDemo = SyntheticDemo();
        public readonly List<string> Processed = [];
        private readonly object _lock = new();
        public ManualResetEventSlim? Block; // if set, ParseFile waits on it
        public int ByteCalls;
        public int FileCalls;
        public ParsedDemo? LastFileDemo;
        public int SleepMs;

        private int _concurrent;
        private int _maxConcurrent;
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public ParsedDemo ParseFile(string path)
        {
            int now = Interlocked.Increment(ref _concurrent);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxConcurrent))
                   && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
            {
            }

            try
            {
                Block?.Wait();
                if (SleepMs > 0)
                {
                    Thread.Sleep(SleepMs);
                }

                lock (_lock)
                {
                    Processed.Add(path);
                    FileCalls++;
                }

                if (FailPaths.Contains(path))
                {
                    throw new InvalidOperationException("boom: " + path);
                }

                ParsedDemo demo = SyntheticDemo();
                LastFileDemo = demo;
                return demo;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public ParsedDemo ParseBytes(ReadOnlyMemory<byte> _)
        {
            Interlocked.Increment(ref ByteCalls);
            return ForegroundDemo;
        }
    }
}

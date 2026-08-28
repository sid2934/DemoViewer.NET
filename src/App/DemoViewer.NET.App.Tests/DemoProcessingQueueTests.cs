#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The global demo-processing queue (demo-processing-queue.md). A fake parser stands in for the
///     heavy demo parse — no demos, no bytes — so the tests exercise priority ordering, coalescing,
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
            parser.ParseBytes);
    }

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

            // The consumer refeeds on capacity — the reject-on-full + idempotent-refeed contract.
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

            // The library cancels ITS submission — the item survives for highlights.
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

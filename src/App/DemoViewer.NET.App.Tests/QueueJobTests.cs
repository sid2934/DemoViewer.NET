#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export.Pack;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Jobs that are not demo parses (lineup clips, strat mining, sidecar migration, pack export, heap
///     compaction) as items of the one processing queue: ordering against demo parses, pause, cancel, the
///     disable switch, and one heavy job at a time across every kind.
/// </summary>
public class QueueJobTests
{
    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static DemoProcessingQueue NewQueue(HeavyJobGate gate, Func<string, ParsedDemo>? parse = null) =>
        new(gate, a => a(), parse ?? (_ => SyntheticDemo()), _ => SyntheticDemo(), () => Task.CompletedTask);

    private static QueueJobRequest Job(QueueJobKind kind, string title, Func<IQueueJobContext, Task> run,
        DemoJobPriority priority = DemoJobPriority.Background, string? key = null, long order = 0) =>
        new(kind, title, "test", priority, run, key, null, order);

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
    public async Task MixedKinds_RunUserWorkFirst_ThenDemoParses_ThenMigrationMiningAndClips()
    {
        using HeavyJobGate gate = new();
        List<string> order = [];
        using DemoProcessingQueue queue = NewQueue(gate);
        queue.Pause();

        Func<IQueueJobContext, Task> Record(string name) => _ =>
        {
            lock (order)
            {
                order.Add(name);
            }

            return Task.CompletedTask;
        };

        queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips", Record("clips")));
        queue.SubmitJob(Job(QueueJobKind.StratMining, "mine", Record("mine")));
        queue.SubmitJob(Job(QueueJobKind.SidecarMigration, "migrate", Record("migrate")));
        queue.SubmitBackground(new DemoProcessingRequest("/d/a.dem", "library", DemoJobPriority.Background, 1,
            _ =>
            {
                lock (order)
                {
                    order.Add("demo");
                }
            }));
        queue.SubmitJob(Job(QueueJobKind.PackExport, "pack", Record("pack"), DemoJobPriority.UserRequested));

        await Assert.That(queue.QueuedCount).IsEqualTo(5);
        queue.Resume();
        await WaitForAsync(() => order.Count == 5 && queue.ActiveWorkerCount == 0, "drain");

        await Assert.That(string.Join(",", order)).IsEqualTo("pack,demo,migrate,mine,clips");
    }

    [Test]
    public async Task APausedQueue_StartsNoJob_OfAnyKind()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        int ran = 0;
        queue.Pause();
        IDemoQueueHandle user = queue.SubmitJob(Job(QueueJobKind.PackExport, "pack", _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        }, DemoJobPriority.UserRequested));
        queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips", _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        }));

        await Task.Delay(200);
        await Assert.That(ran).IsEqualTo(0);
        await Assert.That(user.State).IsEqualTo(DemoQueueItemState.Queued);

        queue.Resume();
        await WaitForAsync(() => Volatile.Read(ref ran) == 2, "both jobs");
    }

    [Test]
    public async Task Cancel_DropsAQueuedJob_AndStopsARunningOneThroughItsToken()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queuedRan = false;

        IDemoQueueHandle running = queue.SubmitJob(Job(QueueJobKind.StratMining, "mine", async ctx =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
        }));
        IDemoQueueHandle queued = queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips", _ =>
        {
            queuedRan = true;
            return Task.CompletedTask;
        }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.RemoveByUser(queued.Id);
        await Assert.That(queued.State).IsEqualTo(DemoQueueItemState.Cancelled);

        running.Cancel();
        await running.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(running.State).IsEqualTo(DemoQueueItemState.Cancelled);
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "worker exit");
        await Assert.That(queuedRan).IsFalse();
        await Assert.That(gate.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task OneHeavyJobAtATime_AcrossDemoParsesAndJobs()
    {
        using HeavyJobGate gate = new();
        int concurrent = 0, peak = 0;

        void Enter()
        {
            int now = Interlocked.Increment(ref concurrent);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
        }

        using DemoProcessingQueue queue = NewQueue(gate, _ =>
        {
            Enter();
            Thread.Sleep(20);
            Interlocked.Decrement(ref concurrent);
            return SyntheticDemo();
        });
        queue.MaxConcurrency = 1;

        List<IDemoQueueHandle> handles = [];
        for (int i = 0; i < 4; i++)
        {
            handles.Add(queue.SubmitBackground(new DemoProcessingRequest($"/d/{i}.dem", "library",
                DemoJobPriority.Background, i, _ => { })));
            handles.Add(queue.SubmitJob(Job(i % 2 == 0 ? QueueJobKind.LineupClips : QueueJobKind.StratMining, $"job {i}",
                async ctx =>
                {
                    Enter();
                    await Task.Delay(20, ctx.CancellationToken);
                    Interlocked.Decrement(ref concurrent);
                }, key: $"k{i}")));
        }

        await Task.WhenAll(handles.Select(h => h.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(peak).IsEqualTo(1);
        await Assert.That(handles.All(h => h.State == DemoQueueItemState.Completed)).IsTrue();
    }

    [Test]
    public async Task AKeyedJob_StillQueued_TakesALaterSubmit_AndCarriesTheNewestTitle()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        queue.Pause();
        IDemoQueueHandle first = queue.SubmitJob(Job(QueueJobKind.StratMining, "Strat mining", _ => Task.CompletedTask,
            key: "mine"));
        IDemoQueueHandle second = queue.SubmitJob(Job(QueueJobKind.StratMining, "Strat mining (user)",
            _ => Task.CompletedTask, DemoJobPriority.UserRequested, "mine"));

        await Assert.That(second.Id).IsEqualTo(first.Id);
        DemoQueueItemSnapshot only = queue.Snapshot().Single();
        await Assert.That(only.DisplayName).IsEqualTo("Strat mining (user)");
        await Assert.That(only.Priority).IsEqualTo(DemoJobPriority.UserRequested);
        await Assert.That(only.Kind).IsEqualTo(QueueJobKind.StratMining);
    }

    [Test]
    public async Task StepAside_LetsADemoOpenIn_BeforeTheJobFinishes()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        TaskCompletionSource inBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IDemoQueueHandle job = queue.SubmitJob(Job(QueueJobKind.StratMining, "mine", async ctx =>
        {
            inBatch.SetResult();
            await proceed.Task;
            await ctx.StepAsideAsync();
            await Task.Delay(300, ctx.CancellationToken);
        }));

        await inBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IDisposable> open = gate.AcquireInteractiveAsync();
        await Task.Delay(150);
        await Assert.That(open.IsCompleted).IsFalse().Because("the job holds the only slot");
        proceed.SetResult();

        using (await open.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Running);
        }

        await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Completed);
    }

    [Test]
    public async Task Disabled_HoldsBackgroundJobs_ButRunsUserRequestedOnes()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        queue.BackgroundEnabled = false;
        IDemoQueueHandle background = queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips", _ => Task.CompletedTask));
        IDemoQueueHandle user = queue.SubmitJob(Job(QueueJobKind.PackExport, "pack", _ => Task.CompletedTask,
            DemoJobPriority.UserRequested));

        await user.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        await Assert.That(background.State).IsEqualTo(DemoQueueItemState.Queued);

        queue.BackgroundEnabled = true;
        await background.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task AFailingJob_IsFailed_WithItsMessage_ProgressIsReported_AndHistoryStaysBounded()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IDemoQueueHandle progress = queue.SubmitJob(Job(QueueJobKind.SidecarMigration, "migrate", async ctx =>
        {
            ctx.Report(3, 12, "3 of 12 demos");
            reported.SetResult();
            await release.Task;
        }));
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DemoQueueItemSnapshot snap = queue.Snapshot().Single(s => s.Id == progress.Id);
        await Assert.That(snap.Progress).IsEqualTo(0.25);
        await Assert.That(snap.Detail).IsEqualTo("3 of 12 demos");
        await Assert.That(queue.Items.Single(i => i.Id == progress.Id).Progress).IsEqualTo(0.25);
        release.SetResult();

        IDemoQueueHandle failing = queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips",
            _ => Task.FromException(new InvalidOperationException("no map bundle"))));
        await failing.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(failing.State).IsEqualTo(DemoQueueItemState.Failed);
        await Assert.That(queue.Snapshot().Single(s => s.Id == failing.Id).Error).IsEqualTo("no map bundle");

        List<IDemoQueueHandle> many = [];
        for (int i = 0; i < 40; i++)
        {
            many.Add(queue.SubmitJob(Job(QueueJobKind.LineupClips, $"clips {i}", _ => Task.CompletedTask)));
        }

        await Task.WhenAll(many.Select(h => h.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "worker exit");
        await Assert.That(queue.Snapshot().Count).IsLessThanOrEqualTo(30);
    }

    [Test]
    public async Task ADemoOpen_OnAPathARunningJobIsAbout_ParsesItsOwnBytes()
    {
        using HeavyJobGate gate = new();
        int byteParses = 0;
        using DemoProcessingQueue queue = new(gate, a => a(), _ => SyntheticDemo(), _ =>
        {
            Interlocked.Increment(ref byteParses);
            return SyntheticDemo();
        }, () => Task.CompletedTask);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.SubmitJob(new QueueJobRequest(QueueJobKind.LineupClips, "clips", "clips", DemoJobPriority.Background,
            async ctx =>
            {
                started.SetResult();
                ctx.ReleaseSlot();
                await release.Task;
            }, Target: "/d/a.dem"));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ParsedDemo opened = await queue.RequestForegroundAsync("/d/a.dem", new byte[] { 1 }).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        await Assert.That(opened).IsNotNull();
        await Assert.That(byteParses).IsEqualTo(1);
    }

    private static async Task<bool> EndsCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        catch (TaskCanceledException)
        {
            return true;
        }
    }

    [Test]
    public async Task APackExport_IsAUserRequestedItem_ThatTakesItsOwnInteractiveSlot_AndCancelsFromEitherSide()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        List<PackProgress> seen = [];
        Progress<PackProgress> sink = new(p =>
        {
            lock (seen)
            {
                seen.Add(p);
            }
        });

        int result = await PackExportQueue.RunAsync(queue, "Pack export: 2 segments to pack.mp4", "/out/pack.mp4",
            async (progress, ct) =>
            {
                using (await gate.AcquireInteractiveAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct))
                {
                    progress.Report(new PackProgress(1, 2, "clip one"));
                }

                return 7;
            }, sink, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result).IsEqualTo(7);
        DemoQueueItemSnapshot item = queue.Snapshot().Single(s => s.Kind == QueueJobKind.PackExport);
        await Assert.That(item.Priority).IsEqualTo(DemoJobPriority.UserRequested);
        await Assert.That(item.State).IsEqualTo(DemoQueueItemState.Completed);
        await Assert.That(item.Progress).IsEqualTo(0.5);
        await Assert.That(item.Path).IsEqualTo("/out/pack.mp4");

        // Cancelled by the caller while queued: the export never starts.
        queue.Pause();
        using CancellationTokenSource cts = new();
        bool started = false;
        Task<int> queued = PackExportQueue.RunAsync(queue, "Pack export", null, (_, _) =>
        {
            started = true;
            return Task.FromResult(1);
        }, null, cts.Token);
        cts.Cancel();
        await Assert.That(await EndsCancelledAsync(queued)).IsTrue();
        queue.Resume();

        // Cancelled from the queue list while running: the export's token fires.
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> removed = PackExportQueue.RunAsync(queue, "Pack export", null, async (_, ct) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, null, CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.RemoveByUser(queue.Snapshot().Single(s => s.State == DemoQueueItemState.Running).Id);
        await Assert.That(await EndsCancelledAsync(removed)).IsTrue();
        await Assert.That(started).IsFalse();
    }
}

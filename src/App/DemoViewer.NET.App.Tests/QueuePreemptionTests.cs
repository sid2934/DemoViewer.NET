#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A user's item stops the background item running in its lane, runs next, and the stopped item goes
///     back in the queue ahead of other background work and still completes. The light lane (saves, loads,
///     section builds, Team Identity commands) runs beside a parse.
/// </summary>
public class QueuePreemptionTests
{
    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static DemoProcessingQueue NewQueue(HeavyJobGate gate, Func<string, ParsedDemo>? parse = null) =>
        new(gate, a => a(), parse ?? (_ => SyntheticDemo()), _ => SyntheticDemo(), () => Task.CompletedTask);

    private static QueueJobRequest Job(QueueJobKind kind, string title, Func<IQueueJobContext, Task> run,
        DemoJobPriority priority = DemoJobPriority.Background, string? key = null) =>
        new(kind, title, "test", priority, run, key);

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
    public async Task AUserItem_StopsTheRunningBackgroundJob_RunsNext_AndTheStoppedJobResumesWhereItWas_BeforeOtherBackgroundWork()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        List<string> order = [];
        int nextChunk = 0; // survives the preemption: the job resumes from here
        int runs = 0;
        using SemaphoreSlim started = new(0);

        void Note(string what)
        {
            lock (order)
            {
                order.Add(what);
            }
        }

        IDemoQueueHandle clips = queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips", async ctx =>
        {
            runs++;
            started.Release();
            while (nextChunk < 10)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                Note($"chunk{nextChunk}");
                nextChunk++;
                await Task.Delay(20, ctx.CancellationToken);
            }
        }));
        await started.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => nextChunk >= 2, "two chunks");
        queue.SubmitJob(Job(QueueJobKind.StratMining, "other background", _ =>
        {
            Note("other");
            return Task.CompletedTask;
        }));
        queue.SubmitJob(Job(QueueJobKind.PackExport, "user", _ =>
        {
            Note("user");
            return Task.CompletedTask;
        }, DemoJobPriority.UserRequested));

        await clips.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => queue.ActiveWorkerCount == 0, "drain");

        List<string> seen = [.. order];
        int user = seen.IndexOf("user");
        int resumedAt = seen.FindIndex(user + 1, s => s.StartsWith("chunk", StringComparison.Ordinal));
        using (Assert.Multiple())
        {
            await Assert.That(runs).IsEqualTo(2).Because("stopped once, run again once");
            await Assert.That(clips.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(seen.Count(s => s.StartsWith("chunk", StringComparison.Ordinal))).IsEqualTo(10)
                .Because("the job resumed where it stopped, no chunk twice");
            await Assert.That(user).IsGreaterThan(0).Because("the user item ran after the stop, not before the job began");
            await Assert.That(resumedAt).IsGreaterThan(user);
            await Assert.That(seen.IndexOf("other")).IsGreaterThan(seen.LastIndexOf("chunk9"))
                .Because("the stopped job goes back ahead of other background work");
        }
    }

    [Test]
    public async Task APreemptedItem_KeepsItsId_ShowsAsQueued_AndCanStillBeRemoved()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        using SemaphoreSlim started = new(0);
        using SemaphoreSlim userGate = new(0);
        IDemoQueueHandle clips = queue.SubmitJob(Job(QueueJobKind.LineupClips, "clips", async ctx =>
        {
            started.Release();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
        }));
        await started.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SubmitJob(Job(QueueJobKind.PackExport, "user", _ => userGate.WaitAsync(), DemoJobPriority.UserRequested));
        await WaitForAsync(() => queue.Snapshot().Any(s => s.Id == clips.Id && s.State == DemoQueueItemState.Queued), "requeue");
        bool completedEarly = clips.Completion.IsCompleted;
        queue.RemoveByUser(clips.Id);
        userGate.Release();
        await clips.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.Multiple())
        {
            await Assert.That(completedEarly).IsFalse().Because("a preempted item has not finished");
            await Assert.That(clips.State).IsEqualTo(DemoQueueItemState.Cancelled);
        }
    }

    [Test]
    public async Task AUserItem_DuringAFullParse_DoesNotStopIt_AndRunsNext()
    {
        using HeavyJobGate gate = new();
        using ManualResetEventSlim parsing = new();
        using ManualResetEventSlim finishParse = new();
        List<string> order = [];
        using DemoProcessingQueue queue = NewQueue(gate, _ =>
        {
            parsing.Set();
            finishParse.Wait(TimeSpan.FromSeconds(10));
            lock (order)
            {
                order.Add("parse");
            }

            return SyntheticDemo();
        });
        queue.SubmitBackground(new DemoProcessingRequest("/d/a.dem", "library", DemoJobPriority.Background, 1, _ => { }));
        queue.SubmitBackground(new DemoProcessingRequest("/d/b.dem", "library", DemoJobPriority.Background, 0, _ => { }));
        parsing.Wait(TimeSpan.FromSeconds(5));
        IDemoQueueHandle user = queue.SubmitJob(Job(QueueJobKind.PackExport, "user", _ =>
        {
            lock (order)
            {
                order.Add("user");
            }

            return Task.CompletedTask;
        }, DemoJobPriority.UserRequested));
        finishParse.Set();
        await user.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && order.Count == 3, "drain");
        await Assert.That(string.Join(",", order)).IsEqualTo("parse,user,parse");
    }

    [Test]
    public async Task TheLightLane_RunsBesideAParse_AndWithTheBackgroundSwitchOff()
    {
        using HeavyJobGate gate = new();
        using ManualResetEventSlim finishParse = new();
        using ManualResetEventSlim parsing = new();
        using DemoProcessingQueue queue = NewQueue(gate, _ =>
        {
            parsing.Set();
            finishParse.Wait(TimeSpan.FromSeconds(10));
            return SyntheticDemo();
        });
        queue.SubmitBackground(new DemoProcessingRequest("/d/a.dem", "library", DemoJobPriority.Background, 1, _ => { }));
        parsing.Wait(TimeSpan.FromSeconds(5));
        queue.BackgroundEnabled = false;
        bool saved = false;
        IDemoQueueHandle save = queue.SubmitJob(Job(QueueJobKind.StoreSave, "save", _ =>
        {
            saved = true;
            return Task.CompletedTask;
        }));
        await save.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        bool parseStillRunning = !finishParse.IsSet;
        finishParse.Set();
        using (Assert.Multiple())
        {
            await Assert.That(saved).IsTrue();
            await Assert.That(parseStillRunning).IsTrue();
            await Assert.That(queue.Snapshot().Any(s => s.Id == save.Id)).IsFalse()
                .Because("a finished light item leaves no history row");
        }
    }

    [Test]
    public async Task WorkSubmittedInAUserAction_GoesFirst_AndRunsBesideABackgroundSaveThatCannotStop()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        using ManualResetEventSlim saving = new();
        using ManualResetEventSlim finishSave = new();
        Task save = QueueWork.Run(queue, QueueJobKind.StoreSave, "save", "t", _ =>
        {
            saving.Set();
            finishSave.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); // a write ignores its token
        });
        saving.Wait(TimeSpan.FromSeconds(5));

        int concurrent = 0, peak = 0;
        using CountdownEvent started = new(4);
        List<Task> sections;
        using (QueueWork.UserAction())
        {
            sections = [.. Enumerable.Range(0, 4).Select(i => QueueWork.Run(queue, QueueJobKind.SectionCompute, "section", "t", _ =>
            {
                int now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref peak, now);
                started.Signal();
                started.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
                Interlocked.Decrement(ref concurrent);
            }, key: "s" + i))];
        }

        Task background = QueueWork.Run(queue, QueueJobKind.SectionCompute, "background", "t", _ => { });
        await Task.WhenAll(sections).WaitAsync(TimeSpan.FromSeconds(10));
        bool saveStillRunning = !save.IsCompleted;
        bool backgroundWaited = !background.IsCompleted;
        finishSave.Set();
        await Task.WhenAll(save, background).WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.Multiple())
        {
            await Assert.That(peak).IsEqualTo(4).Because("the user is waiting on all four, as on the pool");
            await Assert.That(saveStillRunning).IsTrue();
            await Assert.That(backgroundWaited).IsTrue().Because("background light work stays one at a time");
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Test]
    public async Task AKeyedSubmit_WithReplacePending_RunsTheNewestWork()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        queue.Pause();
        string ran = "";
        queue.SubmitJob(new QueueJobRequest(QueueJobKind.SectionCompute, "s", "t", DemoJobPriority.Background,
            _ =>
            {
                ran = "old";
                return Task.CompletedTask;
            }, "section:x", ReplacePending: true));
        IDemoQueueHandle h = queue.SubmitJob(new QueueJobRequest(QueueJobKind.SectionCompute, "s", "t", DemoJobPriority.Background,
            _ =>
            {
                ran = "new";
                return Task.CompletedTask;
            }, "section:x", ReplacePending: true));
        queue.Resume();
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ran).IsEqualTo("new");
    }
}

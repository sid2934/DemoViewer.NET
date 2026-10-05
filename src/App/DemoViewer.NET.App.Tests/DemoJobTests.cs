#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export.Pack;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Work that names a demo joins the demo's visit: one read serves it and the registered passes, it runs
///     after them, it runs on the shell's parse when the demo is loaded, its handle reports its own outcome,
///     and cancelling it takes only it off the visit. A lease holds the visit's slot until it is disposed.
/// </summary>
public class DemoJobTests
{
    private const string Demo = "/d/match.dem";

    private sealed class Rig : IDisposable
    {
        public readonly HeavyJobGate Gate = new();
        public readonly DemoProcessingQueue Queue;
        public int FileParses;

        public Rig() =>
            Queue = new DemoProcessingQueue(Gate, a => a(),
                parseBytes: _ => SyntheticParsedDemo.Create(),
                compactHeap: () => Task.CompletedTask,
                parseFileWithPlan: (_, plan) =>
                {
                    Interlocked.Increment(ref FileParses);
                    return SyntheticParsedDemo.Create(plan: plan);
                });

        public void Dispose()
        {
            Queue.Dispose();
            Gate.Dispose();
        }
    }

    private static DemoJobRequest Request(Func<DemoJobInput, Task> work, PassLevel level = PassLevel.UserRequested,
        string path = Demo) =>
        new(path, "clips", "ext.sample", level, work);

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
    public async Task AJob_JoinsTheQueuedVisit_AndRunsAfterItsPasses_OnOneRead()
    {
        using Rig rig = new();
        List<string> ran = [];
        rig.Queue.Pause();
        DemoJob job = DemoJob.Submit(rig.Queue, Request(_ =>
        {
            lock (ran)
            {
                ran.Add("job");
            }

            return Task.CompletedTask;
        }, PassLevel.Backlog));
        IDemoQueueHandle visit = rig.Queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("library") { Ran = ran }]));
        await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Queued);
        rig.Queue.Resume();
        await Task.WhenAll(job.Completion, visit.Completion);

        using (Assert.Multiple())
        {
            await Assert.That(rig.FileParses).IsEqualTo(1);
            await Assert.That(ran).IsEquivalentTo(["library", "job"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    [Test]
    public async Task AJobThatThrows_EndsFailed_WithItsError_AndThePassesStillRun()
    {
        using Rig rig = new();
        TestPass library = new("library");
        rig.Queue.Pause();
        DemoJob job = DemoJob.Submit(rig.Queue, Request(_ => throw new InvalidOperationException("boom"), PassLevel.Backlog));
        rig.Queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [library]));
        rig.Queue.Resume();
        await job.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Failed);
            await Assert.That(job.Error?.Message).IsEqualTo("boom");
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
        }
    }

    [Test]
    public async Task CancellingAQueuedJob_TakesOnlyItOffTheVisit()
    {
        using Rig rig = new();
        bool jobRan = false;
        TestPass library = new("library");
        rig.Queue.Pause();
        DemoJob job = DemoJob.Submit(rig.Queue, Request(_ =>
        {
            jobRan = true;
            return Task.CompletedTask;
        }, PassLevel.Backlog));
        IDemoQueueHandle visit = rig.Queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [library]));
        job.Cancel();
        await job.Completion;
        rig.Queue.Resume();
        await visit.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Cancelled);
            await Assert.That(jobRan).IsFalse();
            await Assert.That(library.Ran).IsEquivalentTo(["library"]).Because("the co-owner's pass keeps the visit");
            await Assert.That(visit.State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    [Test]
    public async Task CancellingTheOnlyJobOnAVisit_ReadsNothing()
    {
        using Rig rig = new();
        rig.Queue.Pause();
        DemoJob job = DemoJob.Submit(rig.Queue, Request(_ => Task.CompletedTask, PassLevel.Backlog));
        job.Cancel();
        await job.Completion;
        rig.Queue.Resume();

        using (Assert.Multiple())
        {
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Cancelled);
            await Assert.That(rig.FileParses).IsEqualTo(0);
            await Assert.That(rig.Queue.Snapshot().Any(s => s.State == DemoQueueItemState.Queued)).IsFalse();
        }
    }

    [Test]
    public async Task CancellingARunningJob_FiresItsToken()
    {
        using Rig rig = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DemoJob job = DemoJob.Submit(rig.Queue, Request(async input =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(10), input.CancellationToken);
        }));
        await started.Task;
        await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Running);
        job.Cancel();
        await job.Completion;

        await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Cancelled);
    }

    [Test]
    public async Task TheOwnersCancelAll_ReachesItsJobs_AndNoOtherOwnersPasses()
    {
        using Rig rig = new();
        TestPass library = new("library");
        rig.Queue.Pause();
        DemoJob job = DemoJob.Submit(rig.Queue, Request(_ => Task.CompletedTask, PassLevel.Backlog));
        IDemoQueueHandle visit = rig.Queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [library]));
        rig.Queue.CancelOwned("ext.sample");
        await job.Completion;
        rig.Queue.Resume();
        await visit.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Cancelled);
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
        }
    }

    [Test]
    public async Task RemovingTheQueuedVisitFromTheList_EndsTheJob()
    {
        using Rig rig = new();
        rig.Queue.Pause();
        DemoJob job = DemoJob.Submit(rig.Queue, Request(_ => Task.CompletedTask, PassLevel.Backlog));
        rig.Queue.RemoveByUser(rig.Queue.Snapshot().Single(s => s.Kind == QueueJobKind.DemoProcessing).Id);
        await job.Completion;

        await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Cancelled);
    }

    [Test]
    public async Task AJobOnTheLoadedDemo_RunsOnTheShellsParse()
    {
        using Rig rig = new();
        ParsedDemo held = SyntheticParsedDemo.Create();
        rig.Queue.ShellDemo = new Loaded(Demo, held);
        ParsedDemo? seen = null;
        DemoJob job = DemoJob.Submit(rig.Queue, Request(input =>
        {
            seen = input.Parsed;
            return Task.CompletedTask;
        }));
        await job.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(rig.FileParses).IsEqualTo(0);
            await Assert.That(seen).IsSameReferenceAs(held);
        }
    }

    [Test]
    public async Task TheParse_IsGoneFromTheInput_OnceTheTurnEnds()
    {
        using Rig rig = new();
        DemoJobInput? kept = null;
        DemoJob job = DemoJob.Submit(rig.Queue, Request(input =>
        {
            kept = input;
            _ = input.Parsed;
            return Task.CompletedTask;
        }));
        await job.Completion;

        Assert.Throws<ObjectDisposedException>(() => _ = kept!.Parsed);
    }

    [Test]
    public async Task ALease_HoldsTheSlot_UntilDisposed()
    {
        using Rig rig = new();
        DemoLease lease = await DemoJob.LeaseAsync(rig.Queue, Demo, "pack export", "review", PassLevel.UserRequested);
        IDemoQueueHandle other = rig.Queue.SubmitVisit(new DemoVisitRequest("/d/other.dem", PassLevel.UserRequested, [new TestPass("library")]));
        await Task.Delay(100);
        await Assert.That(other.State).IsEqualTo(DemoQueueItemState.Queued).Because("one demo is read at a time");

        lease.Dispose();
        await other.Completion;
        await Assert.That(other.State).IsEqualTo(DemoQueueItemState.Completed);
    }

    [Test]
    public async Task APackExport_ReadsItsDemosThroughLeases_InsideItsSession_WhileOtherWorkWaits()
    {
        using Rig rig = new();
        IDemoQueueHandle? background = null;
        DemoJob? other = null;
        DemoQueueItemState otherWhileExporting = DemoQueueItemState.Completed;
        DemoQueueItemState backgroundWhileExporting = DemoQueueItemState.Completed;
        DemoQueueItemState betweenLeases = DemoQueueItemState.Completed;

        int read = await PackExportQueue.RunAsync(rig.Queue, "Pack export", null, async (_, ct) =>
        {
            using IDisposable session = await rig.Gate.EnterExportSessionAsync(ct);
            background = rig.Queue.SubmitVisit(new DemoVisitRequest("/d/old.dem", PassLevel.Background,
                [new TestPass("library")]));
            int leases = 0;
            using (await DemoJob.LeaseAsync(rig.Queue, "/d/a.dem", "pack export", PackExportQueue.Owner, PassLevel.UserRequested, ct))
            {
                leases++;
            }

            await Task.Delay(100, ct);
            betweenLeases = background!.State;
            using (await DemoJob.LeaseAsync(rig.Queue, "/d/b.dem", "pack export", PackExportQueue.Owner, PassLevel.UserRequested, ct))
            {
                leases++;
                other = DemoJob.Submit(rig.Queue, new DemoJobRequest("/d/c.dem", "clips", "someone-else", PassLevel.UserRequested,
                    _ => Task.CompletedTask));
                await Task.Delay(100, ct);
                otherWhileExporting = other.State;
                backgroundWhileExporting = background!.State;
            }

            return leases;
        }, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await other!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await background!.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.Multiple())
        {
            await Assert.That(read).IsEqualTo(2).Because("the export's own reads start beside the job that runs it");
            await Assert.That(otherWhileExporting).IsEqualTo(DemoQueueItemState.Queued)
                .Because("another owner's read waits for the export, user-requested or not");
            await Assert.That(betweenLeases).IsEqualTo(DemoQueueItemState.Queued)
                .Because("no lease holds the slot here, only the session");
            await Assert.That(backgroundWhileExporting).IsEqualTo(DemoQueueItemState.Queued)
                .Because("background reads hold off for the whole export session");
            await Assert.That(background!.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(other.State).IsEqualTo(DemoQueueItemState.Completed).Because("it runs once the export ends");
        }
    }

    [Test]
    public async Task ALeaseWhoseVisitIsRemoved_Fails_InsteadOfWaitingForever()
    {
        using Rig rig = new();
        rig.Queue.Pause();
        rig.Queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("library")]));
        Task<DemoLease> lease = DemoJob.LeaseAsync(rig.Queue, Demo, "pack export", "review", PassLevel.Backlog);
        rig.Queue.RemoveByUser(rig.Queue.Snapshot().Single(s => s.Kind == QueueJobKind.DemoProcessing).Id);

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await lease.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Loaded(string path, ParsedDemo parsed) : IShellDemoLease
    {
        public IHeldParse? TryHold(string demo) =>
            string.Equals(demo, path, StringComparison.OrdinalIgnoreCase) ? new Hold(parsed) : null;

        public string? LoadedPath => path;

        private sealed class Hold(ParsedDemo parsed) : IHeldParse
        {
            public ParsedDemo Parsed => parsed;

            public void Dispose()
            {
            }
        }
    }
}

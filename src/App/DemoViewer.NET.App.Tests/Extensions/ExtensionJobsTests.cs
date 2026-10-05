#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's jobs through <see cref="IExtensionJobs.Enqueue" />: the handle reports the job's own
///     status and end, Completed is told through the extension's post, a handle and CancelAll reach only the
///     extension's own work, a job naming a demo joins the demo's visit, and the backlog level orders ahead of
///     background work.
/// </summary>
public class ExtensionJobsTests
{
    private const string Demo = "/d/match.dem";

    private sealed class Rig : IDisposable
    {
        public readonly HeavyJobGate Gate = new();
        public readonly DemoProcessingQueue Queue;
        public readonly PostQueue Posts = new();
        public readonly ExtensionFaults Faults;
        public int FileParses;

        public Rig()
        {
            Queue = new DemoProcessingQueue(Gate, a => a(),
                parseBytes: _ => SyntheticParsedDemo.Create(),
                compactHeap: () => Task.CompletedTask,
                parseFileWithPlan: (_, plan) =>
                {
                    Interlocked.Increment(ref FileParses);
                    return SyntheticParsedDemo.Create(plan: plan);
                });
            Faults = ExtensionFaults.For([new Pack("dev.example.one"), new Pack("dev.example.two")], static a => a());
        }

        public ExtensionJobs JobsFor(string id, bool withQueue = true) =>
            new(id, () => withQueue ? Queue : null, () => JobKindRegistry.Default, Faults.GuardFor(new Pack(id)), Posts.Post);

        public void Dispose()
        {
            Queue.Dispose();
            Gate.Dispose();
        }
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
    public async Task AJob_EndsCompleted_AndTellsCompletedThroughThePost_Once()
    {
        using Rig rig = new();
        ExtensionJobs jobs = rig.JobsFor("dev.example.one");
        List<JobStatus> told = [];
        IJobHandle handle = jobs.Enqueue(new JobRequest("work", _ => Task.CompletedTask));
        handle.Completed += r => told.Add(r.Status);
        JobResult result = await handle.Completion;

        await Assert.That(told).IsEmpty().Because("nothing runs until the UI thread drains the post");
        rig.Posts.Drain();
        handle.Completed += r => told.Add(r.Status);
        rig.Posts.Drain();

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(JobStatus.Completed);
            await Assert.That(handle.Status).IsEqualTo(JobStatus.Completed);
            await Assert.That(told).IsEquivalentTo([JobStatus.Completed, JobStatus.Completed])
                .Because("a handler added after the end still hears it, on the post");
        }
    }

    [Test]
    public async Task AJobThatThrows_EndsFailed_WithTheError_AndIsCounted()
    {
        using Rig rig = new();
        IJobHandle handle = rig.JobsFor("dev.example.one").Enqueue(new JobRequest("work", _ => throw new InvalidOperationException("boom")));
        JobResult result = await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(JobStatus.Failed);
            await Assert.That(result.Error?.Message).IsEqualTo("boom");
            await Assert.That(rig.Faults.StateOf("pack.dev.example.one").Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AHandleAndCancelAll_ReachOnlyTheExtensionsOwnJobs()
    {
        using Rig rig = new();
        ExtensionJobs one = rig.JobsFor("dev.example.one");
        ExtensionJobs two = rig.JobsFor("dev.example.two");
        rig.Queue.Pause();
        IJobHandle mine = one.Enqueue(new JobRequest("a", _ => Task.CompletedTask));
        IJobHandle mineToo = one.Enqueue(new JobRequest("b", _ => Task.CompletedTask));
        IJobHandle theirs = two.Enqueue(new JobRequest("c", _ => Task.CompletedTask));

        mine.Cancel();
        await mine.Completion;
        await Assert.That(mineToo.Status).IsEqualTo(JobStatus.Queued).Because("a handle cancels its own job only");
        one.CancelAll();
        await mineToo.Completion;
        rig.Queue.Resume();
        await theirs.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(mine.Status).IsEqualTo(JobStatus.Cancelled);
            await Assert.That(mineToo.Status).IsEqualTo(JobStatus.Cancelled);
            await Assert.That(theirs.Status).IsEqualTo(JobStatus.Completed);
        }
    }

    [Test]
    public async Task AKey_JoinsOnlyTheSameOwnersJob_NeverAnotherExtensionsOrTheHosts()
    {
        using Rig rig = new();
        List<string> ran = [];
        Func<IJobContext, Task> Record(string name) => _ =>
        {
            lock (ran)
            {
                ran.Add(name);
            }

            return Task.CompletedTask;
        };
        rig.Queue.Pause();
        IDemoQueueHandle core = rig.Queue.SubmitJob(new QueueJobRequest(QueueJobKind.StoreSave, "Save: review queue", "review",
            DemoJobPriority.Background, ctx => Record("core")(null!), Key: "save:review-queue", ReplacePending: true));
        JobOptions save = new(BuiltInJobKinds.Save, Key: "save:review-queue");
        IJobHandle one = rig.JobsFor("dev.example.one").Enqueue(new JobRequest("one", Record("one"), save));
        IJobHandle two = rig.JobsFor("dev.example.two").Enqueue(new JobRequest("two", Record("two"), save));
        one.Cancel();
        await one.Completion;
        rig.Queue.Resume();
        await Task.WhenAll(core.Completion, two.Completion);

        using (Assert.Multiple())
        {
            await Assert.That(ran).IsEquivalentTo(["core", "two"]).Because("each owner's keyed job is its own");
            await Assert.That(one.Status).IsEqualTo(JobStatus.Cancelled);
            await Assert.That(two.Status).IsEqualTo(JobStatus.Completed);
        }
    }

    [Test]
    public async Task AJobNamingADemo_JoinsItsVisit_AndReadsTheParse()
    {
        using Rig rig = new();
        TestPass library = new("library");
        ParsedDemo? seen = null;
        rig.Queue.Pause();
        IDemoQueueHandle visit = rig.Queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [library]));
        IJobHandle handle = rig.JobsFor("dev.example.one").Enqueue(JobRequest.OnDemo("clips", Demo, ctx =>
        {
            seen = ctx.Parsed;
            return Task.CompletedTask;
        }, new JobOptions(Priority: JobPriority.Backlog)));
        rig.Queue.Resume();
        JobResult result = await handle.Completion;
        await visit.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(JobStatus.Completed);
            await Assert.That(seen).IsNotNull();
            await Assert.That(rig.FileParses).IsEqualTo(1);
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
        }
    }

    [Test]
    public async Task ADemoJobThatThrows_EndsFailed_AndIsCounted()
    {
        using Rig rig = new();
        IJobHandle handle = rig.JobsFor("dev.example.one")
            .Enqueue(JobRequest.OnDemo("clips", Demo, _ => throw new InvalidOperationException("boom")));
        JobResult result = await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(JobStatus.Failed);
            await Assert.That(result.Error?.Message).IsEqualTo("boom");
            await Assert.That(rig.Faults.StateOf("pack.dev.example.one").Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ABacklogJob_RunsAheadOfABackgroundOne()
    {
        using Rig rig = new();
        ExtensionJobs jobs = rig.JobsFor("dev.example.one");
        List<string> ran = [];
        rig.Queue.Pause();
        IJobHandle background = jobs.Enqueue(new JobRequest("background", _ =>
        {
            ran.Add("background");
            return Task.CompletedTask;
        }, new JobOptions(BuiltInJobKinds.Save)));
        IJobHandle backlog = jobs.Enqueue(new JobRequest("backlog", _ =>
        {
            ran.Add("backlog");
            return Task.CompletedTask;
        }, new JobOptions(BuiltInJobKinds.Save, JobPriority.Backlog)));
        rig.Queue.Resume();
        await Task.WhenAll(background.Completion, backlog.Completion);

        await Assert.That(ran).IsEquivalentTo(["backlog", "background"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithoutAQueue_AJobRunsOnThePool_AndADemoJobIsRejected()
    {
        using Rig rig = new();
        ExtensionJobs jobs = rig.JobsFor("dev.example.one", withQueue: false);
        JobResult plain = await jobs.Enqueue(new JobRequest("work", _ => Task.CompletedTask)).Completion;
        JobResult demo = await jobs.Enqueue(JobRequest.OnDemo("clips", Demo, _ => Task.CompletedTask)).Completion;

        using (Assert.Multiple())
        {
            await Assert.That(plain.Status).IsEqualTo(JobStatus.Completed);
            await Assert.That(demo.Status).IsEqualTo(JobStatus.Rejected);
        }
    }

    [Test]
    public async Task AUserActionScope_PutsItsJobsAtUserPriority()
    {
        using Rig rig = new();
        ExtensionJobs jobs = rig.JobsFor("dev.example.one");
        rig.Queue.Pause();
        IJobHandle handle;
        using (JobScope.UserAction())
        {
            handle = jobs.Enqueue(new JobRequest("work", _ => Task.CompletedTask));
        }

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(handle.Status).IsEqualTo(JobStatus.Completed).Because("a user's job runs while background work is paused");
    }

    [Test]
    public async Task ThrowIfStopped_SeesTheRunningJobsToken()
    {
        using Rig rig = new();
        ExtensionJobs jobs = rig.JobsFor("dev.example.one");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IJobHandle handle = jobs.Enqueue(new JobRequest("work", async ctx =>
        {
            started.TrySetResult();
            while (true)
            {
                JobScope.ThrowIfStopped();
                await Task.Delay(5, CancellationToken.None);
            }
        }));
        await started.Task;
        handle.Cancel();
        JobResult result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(JobStatus.Cancelled);
    }

    private sealed class Pack(string id) : IExtension
    {
        public string Id => id;
        public string FeatureId => "pack." + id;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

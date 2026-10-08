#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's pass on the scheduler and the queue: a throw skips that demo and counts one fault, a pass
///     that keeps overrunning its budget is switched off for the session, it is never asked on the UI thread,
///     its parse is gone once its turn ends, its priority reaches the visit, and switching its extension off
///     takes it off queued visits.
/// </summary>
public class ExtensionPassHostTests
{
    private const string Demo = "/d/match.dem";

    private static DemoProcessingQueue Queue() =>
        new(new HeavyJobGate(), a => a(),
            parseBytes: _ => SyntheticParsedDemo.Create(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, plan) => SyntheticParsedDemo.Create(plan: plan));

    private static (ExtensionFaults Faults, ExtensionGuard Guard) Guard()
    {
        Pack pack = new();
        ExtensionFaults faults = ExtensionFaults.For([pack], static a => a());
        return (faults, faults.GuardFor(pack));
    }

    private static DemoScheduler Scheduler(IDemoProcessingQueue queue, ExtensionGuard guard, params IDemoPass[] passes) =>
        new(() => passes, queue, () => [])
        {
            Faulted = (_, _, ex) => guard.Report("pass", ex)
        };

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
    public async Task APassThatThrows_IsSkippedForThatDemo_AndCountedOnce()
    {
        (ExtensionFaults faults, ExtensionGuard guard) = Guard();
        FakePass inner = new() { Throw = true };
        ExtensionPassHost host = new(inner, [], guard, static () => false);
        using DemoProcessingQueue queue = Queue();
        using DemoScheduler scheduler = Scheduler(queue, guard, host);

        scheduler.DemoChanged(Demo);
        await WaitForAsync(() => scheduler.IsFaulted(host.Id, Demo), "the skip");
        scheduler.DemoChanged(Demo);
        await WaitForAsync(() => queue.ActiveCount(QueueJobKind.Scheduling) == 0 && queue.ActiveCount(QueueJobKind.DemoProcessing) == 0,
            "the second plan");

        using (Assert.Multiple())
        {
            await Assert.That(inner.Runs).IsEqualTo(1).Because("the demo is not offered again this session");
            await Assert.That(faults.StateOf(Pack.Feature).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task APassThatKeepsOverrunningItsBudget_IsSwitchedOffForTheSession_AndCountedOnce()
    {
        (ExtensionFaults faults, ExtensionGuard guard) = Guard();
        FakePass inner = new() { RunFor = TimeSpan.FromMilliseconds(20) };
        ExtensionPassHost host = new(inner, [], guard, static () => false, TimeSpan.FromMilliseconds(1), maxOverruns: 2);
        PassInput input = new(new VisitedDemo(Demo), PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None);

        host.Run(input);
        await Assert.That(host.IsQuarantined).IsFalse();
        host.Run(input);

        using (Assert.Multiple())
        {
            await Assert.That(host.IsQuarantined).IsTrue();
            await Assert.That(host.Interest(new VisitedDemo(Demo), PassLevel.Background)).IsEqualTo(PassInterest.No);
            await Assert.That(faults.StateOf(Pack.Feature).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task APass_IsNeverAskedOnTheUiThread()
    {
        (_, ExtensionGuard guard) = Guard();
        FakePass inner = new();
        ExtensionPassHost host = new(inner, [], guard, static () => true);

        using (Assert.Multiple())
        {
            await Assert.That(host.Interest(new VisitedDemo(Demo), PassLevel.Background)).IsEqualTo(PassInterest.No);
            await Assert.That(inner.Asked).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AfterUpstream_MapsToTheVisitsWaitOnUpstream()
    {
        (_, ExtensionGuard guard) = Guard();
        ExtensionPassHost host = new(new FakePass { Answer = DemoInterest.AfterUpstream }, ["library"], guard, static () => false);

        await Assert.That(host.Interest(new VisitedDemo(Demo), PassLevel.Background)).IsEqualTo(PassInterest.IfUpstreamRuns);
    }

    [Test]
    public async Task TheParse_IsGoneFromTheContext_OnceTheTurnEnds()
    {
        (_, ExtensionGuard guard) = Guard();
        FakePass inner = new();
        ExtensionPassHost host = new(inner, [], guard, static () => false);
        host.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None));

        Assert.Throws<ObjectDisposedException>(() => _ = inner.Kept!.Parsed);
        await Assert.That(inner.Kept!.DemoPath).IsEqualTo(Demo);
    }

    // A pass cannot lift a demo past the backlog itself; the user's own request is what plans one at user level.
    [Test]
    public async Task APassAskingForUserPriority_GetsTheBacklogsLevel_AndARequestGetsTheUsers()
    {
        (_, ExtensionGuard guard) = Guard();
        FakePass inner = new() { Priority = JobPriority.UserRequested };
        ExtensionPassHost host = new(inner, [], guard, static () => false);
        using DemoProcessingQueue queue = Queue();
        using DemoScheduler scheduler = Scheduler(queue, guard, host);

        scheduler.DemoChanged(Demo);
        await WaitForAsync(() => inner.Runs == 1 && queue.Snapshot().Any(s => s.Kind == QueueJobKind.DemoProcessing && s.State == DemoQueueItemState.Completed),
            "the visit");

        await Assert.That(host.LevelFor(new VisitedDemo(Demo))).IsEqualTo(PassLevel.Backlog);
        await Assert.That(queue.Snapshot().Single(s => s.Kind == QueueJobKind.DemoProcessing).Priority)
            .IsNotEqualTo(DemoJobPriority.UserRequested);

        scheduler.Request(Demo);
        await WaitForAsync(() => inner.Runs == 2, "the requested visit");
        await Assert.That(queue.Snapshot().Where(s => s.Kind == QueueJobKind.DemoProcessing).Select(s => s.Priority))
            .Contains(DemoJobPriority.UserRequested);
    }

    [Test]
    public async Task SwitchingTheExtensionOff_TakesItsPassOffQueuedVisits()
    {
        (_, ExtensionGuard guard) = Guard();
        FakePass inner = new();
        ExtensionPassHost host = new(inner, [], guard, static () => false);
        using DemoProcessingQueue queue = Queue();
        queue.Pause();
        IDemoQueueHandle visit = queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [host, new TestPass("library")]));
        queue.CancelOwned(Pack.ExtensionId);
        queue.Resume();
        await visit.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(host.Owner).IsEqualTo(Pack.ExtensionId);
            await Assert.That(inner.Runs).IsEqualTo(0);
        }
    }

    private sealed class FakePass : IExtensionPass
    {
        public bool Throw { get; init; }
        public TimeSpan RunFor { get; init; }
        public DemoInterest Answer { get; init; } = DemoInterest.Yes;
        public JobPriority Priority { get; init; } = JobPriority.Background;
        public int Runs;
        public int Asked;
        public IPassContext? Kept;

        public string Id => "ext.sample.pass";

        public bool ReadsUserCommands => false;

        public DemoInterest Interest(string demoPath)
        {
            Interlocked.Increment(ref Asked);
            return Answer;
        }

        public void Run(IPassContext context)
        {
            Interlocked.Increment(ref Runs);
            Kept = context;
            _ = context.Parsed;
            if (RunFor > TimeSpan.Zero)
            {
                Thread.Sleep(RunFor);
            }

            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }
        }

        public JobPriority PriorityFor(string demoPath) => Priority;
    }

    private sealed class Pack : IExtension
    {
        public const string ExtensionId = "dev.example.passes";
        public const string Feature = "pack.passes";

        public string Id => ExtensionId;
        public string FeatureId => Feature;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The queue counts every read of a demo per content hash for the session: a visit, an open, a forward
///     read the queue stopped, and copies of one demo under two paths counted together.
/// </summary>
public class QueueParseCountTests
{
    private const string Demo = "/d/match.dem";

    private static DemoProcessingQueue Queue(Func<string, string?>? hash = null,
        Func<string, ForwardNeeds, Action<double>, CancellationToken, ForwardDemoResult>? forward = null) =>
        new(new HeavyJobGate(), a => a(),
            parseBytes: _ => SyntheticParsedDemo.Create(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, plan) => SyntheticParsedDemo.Create(plan: plan),
            forwardPass: forward,
            contentHash: hash);

    [Test]
    public async Task AVisit_CountsOneRead_AndAnUnreadDemoCountsNone()
    {
        using DemoProcessingQueue queue = Queue();
        IDemoQueueHandle handle = queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("library")]));
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(queue.ParseCount(Demo)).IsEqualTo(1);
            await Assert.That(queue.ParseCount("/d/other.dem")).IsEqualTo(0);
        }
    }

    [Test]
    public async Task CopiesOfOneDemo_AreCountedTogether_ByContentHash()
    {
        using DemoProcessingQueue queue = Queue(path => path.EndsWith("copy.dem", StringComparison.Ordinal) || path == Demo ? "abc" : null);
        await queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("library")])).Completion;
        await queue.SubmitVisit(new DemoVisitRequest("/e/copy.dem", PassLevel.Backlog, [new TestPass("library")])).Completion;

        using (Assert.Multiple())
        {
            await Assert.That(queue.ParseCount(Demo)).IsEqualTo(2).Because("both paths hold the same content");
            await Assert.That(queue.ParseCounts()["abc"]).IsEqualTo(2);
        }
    }

    [Test]
    public async Task TwoPathsTheCacheHoldsAsOneDemo_AreCountedTogether_ThroughTheCachesLookup()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord { Path = Demo, Size = 1, Sha256 = "sha-m" });
        cache.Upsert(new DemoCacheRecord { Path = "/smb/match.dem", Size = 1, Sha256 = "sha-m" });
        using DemoProcessingQueue queue = Queue(path => cache.TryGetIndex(path)?.Sha256);
        await queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("library")])).Completion;
        await queue.SubmitVisit(new DemoVisitRequest("/SMB/match.dem", PassLevel.Backlog, [new TestPass("library")])).Completion;

        using (Assert.Multiple())
        {
            await Assert.That(queue.ParseCount("/smb/match.dem")).IsEqualTo(2);
            await Assert.That(queue.ParseCounts().Keys).IsEquivalentTo(["sha-m"]);
        }
    }

    [Test]
    public async Task AnOpen_CountsItsRead_AndAVisitParkedBehindItAddsNone()
    {
        using DemoProcessingQueue queue = Queue();
        queue.Pause();
        IDemoQueueHandle visit = queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("library")]));
        using IDemoOpenTicket open = queue.BeginOpen(Demo, "match.dem");
        queue.Resume();
        ParsedDemo parsed = await open.ParseAsync(new byte[] { 1 });
        await open.RunPassesAsync(parsed);
        open.Complete();
        await visit.Completion;

        await Assert.That(queue.ParseCount(Demo)).IsEqualTo(1);
    }

    [Test]
    public async Task AForwardReadStoppedForARetainedPass_CountsAsARead()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using DemoProcessingQueue queue = Queue(forward: (_, _, _, ct) =>
        {
            started.TrySetResult();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("not stopped");
        });
        queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog,
            [new TestPass("library") { NeedsValue = PassNeeds.ForwardRead(ForwardNeeds.FinalState) }]));
        await started.Task;
        IDemoQueueHandle retained = queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog, [new TestPass("grenades")]));
        await retained.Completion;

        await Assert.That(queue.ParseCount(Demo)).IsEqualTo(2).Because("the stopped forward read still read the file");
    }
}

/// <summary>A pass for queue tests: answers a fixed interest and records that it ran.</summary>
internal sealed class TestPass(string id, params string[] after) : IDemoPass
{
    public List<string> Ran { get; init; } = [];
    public PassNeeds NeedsValue { get; init; } = PassNeeds.RetainedWithoutUserCommands;
    public PassInterest Answer { get; set; } = PassInterest.Yes;
    public Action<PassInput>? OnRun { get; init; }

    public string Id => id;
    public IReadOnlyList<string> After => after;

    public PassNeeds Needs(VisitedDemo demo) => NeedsValue;

    public PassInterest Interest(VisitedDemo demo, PassLevel level) => Answer;

    public void Run(PassInput input)
    {
        lock (Ran)
        {
            Ran.Add(id);
        }

        OnRun?.Invoke(input);
    }
}

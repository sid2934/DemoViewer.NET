#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A demo's visit on the queue: passes run in After order whatever order they joined, each is asked
///     again in the slot and sits out when it declines, the read is forward only when every pass takes
///     one, and each pass's submitter hears how its turn ended. The planner's closure rule on its own.
/// </summary>
public class DemoVisitTests
{
    private const string Demo = "/d/match.dem";

    private static ForwardDemoResult ForwardResult() => new()
    {
        Demo = new DemoDescriptor("de_test", 64, 1f / 64, 1000, 0, "test", "test", 0, DemoProfile.Unknown,
            new Dictionary<int, PlayerInfo>()),
        Rounds = [],
        FrameCount = 10,
        FirstServerTick = 0,
        LastServerTick = 1000
    };

    private static DemoProcessingQueue Queue(Action<string>? onParse = null,
        Func<string, ForwardNeeds, Action<double>, CancellationToken, ForwardDemoResult>? forward = null) =>
        new(new HeavyJobGate(), a => a(),
            parseBytes: _ => SyntheticParsedDemo.Create(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (path, plan) =>
            {
                onParse?.Invoke(path);
                return SyntheticParsedDemo.Create(plan: plan);
            },
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

    private static DemoVisitRequest Visit(PassLevel level, Action<IDemoPass, PassOutcome, Exception?>? ended, params IDemoPass[] passes) =>
        new(Demo, level, passes, 0, "match.dem", ended);

    [Test]
    public async Task Passes_RunInAfterOrder_WhateverOrderTheyJoined()
    {
        List<string> ran = [];
        int parses = 0;
        using DemoProcessingQueue queue = Queue(_ => Interlocked.Increment(ref parses));
        FakePass a = new("a") { Ran = ran };
        FakePass b = new("b", "a") { Ran = ran };
        FakePass c = new("c", "b") { Ran = ran };

        queue.Pause();
        queue.SubmitVisit(Visit(PassLevel.Background, null, c));
        queue.SubmitVisit(Visit(PassLevel.Background, null, b));
        IDemoQueueHandle last = queue.SubmitVisit(Visit(PassLevel.Background, null, a));
        queue.Resume();
        await last.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(parses).IsEqualTo(1).Because("three submits for one demo join one visit");
            await Assert.That(ran).IsEquivalentTo(["a", "b", "c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(last.State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    [Test]
    public async Task EachPass_IsAskedAgainInTheSlot_AndSitsOutWhenItDeclines()
    {
        List<string> ran = [];
        List<(string, PassOutcome)> ended = [];
        using DemoProcessingQueue queue = Queue();
        FakePass downstream = new("downstream", "upstream") { Ran = ran };
        // Upstream's run satisfies downstream, the way a written row turns Wants false.
        FakePass upstream = new("upstream") { Ran = ran, OnRun = _ => downstream.Answer = PassInterest.No };

        IDemoQueueHandle handle = queue.SubmitVisit(Visit(PassLevel.Background,
            (p, outcome, _) =>
            {
                lock (ended)
                {
                    ended.Add((p.Id, outcome));
                }
            }, downstream, upstream));
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(ran).IsEquivalentTo(["upstream"]);
            await Assert.That(ended).IsEquivalentTo([("upstream", PassOutcome.Ran), ("downstream", PassOutcome.Skipped)],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task APass_StillWaitingOnUpstreamAfterUpstreamRan_SitsOut()
    {
        List<string> ran = [];
        List<(string, PassOutcome)> ended = [];
        using DemoProcessingQueue queue = Queue();
        FakePass upstream = new("upstream") { Ran = ran };
        FakePass stuck = new("stuck", "upstream") { Ran = ran, Answer = PassInterest.IfUpstreamRuns };

        IDemoQueueHandle handle = queue.SubmitVisit(Visit(PassLevel.Background,
            (p, outcome, _) =>
            {
                lock (ended)
                {
                    ended.Add((p.Id, outcome));
                }
            }, upstream, stuck));
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(ran).IsEquivalentTo(["upstream"]);
            await Assert.That(ended).Contains(("stuck", PassOutcome.Skipped));
        }
    }

    [Test]
    public async Task TheRead_IsForwardOnlyWhenEveryPassTakesOne_AndAForwardPassTakesTheRetainedParseOtherwise()
    {
        int forwardReads = 0, parses = 0;
        List<PassInput> inputs = [];
        using DemoProcessingQueue queue = Queue(_ => Interlocked.Increment(ref parses), (_, _, _, _) =>
        {
            Interlocked.Increment(ref forwardReads);
            return ForwardResult();
        });
        FakePass rules = new("rules") { NeedsValue = PassNeeds.ForwardRead(ForwardNeeds.Rules), OnRun = Record };
        FakePass finalState = new("final") { NeedsValue = PassNeeds.ForwardRead(ForwardNeeds.FinalState), OnRun = Record };
        FakePass walk = new("walk") { NeedsValue = PassNeeds.RetainedParse, OnRun = Record };

        void Record(PassInput input)
        {
            lock (inputs)
            {
                inputs.Add(input);
            }
        }

        queue.Pause();
        queue.SubmitVisit(new DemoVisitRequest("/d/forward.dem", PassLevel.Background, [rules, finalState]));
        queue.SubmitVisit(new DemoVisitRequest("/d/mixed.dem", PassLevel.Background, [rules]));
        IDemoQueueHandle mixed = queue.SubmitVisit(new DemoVisitRequest("/d/mixed.dem", PassLevel.Background, [walk]));
        queue.Resume();
        await mixed.Completion;
        await WaitForAsync(() => queue.ActiveWorkerCount == 0 && inputs.Count == 4, "both visits");

        using (Assert.Multiple())
        {
            await Assert.That(forwardReads).IsEqualTo(1).Because("the all-forward visit reads forward once");
            await Assert.That(parses).IsEqualTo(1).Because("one retained pass puts the mixed visit on the retained parse");
            await Assert.That(inputs.Where(i => i.Demo.Path == "/d/forward.dem").All(i => i.Forward is not null && i.Retained is null)).IsTrue();
            await Assert.That(inputs.Where(i => i.Demo.Path == "/d/mixed.dem").All(i => i.Retained is not null && i.Forward is null)).IsTrue()
                .Because("a forward pass on a retained visit runs on the retained parse");
            await Assert.That(inputs.Where(i => i.Demo.Path == "/d/mixed.dem").All(i => i.Retained!.Plan.DecodesEverything)).IsTrue()
                .Because("the retained pass reads player inputs");
        }
    }

    [Test]
    public async Task EachSubmitter_HearsHowItsPassEnded()
    {
        List<(string, PassOutcome, string?)> ended = [];
        void Ended(IDemoPass p, PassOutcome outcome, Exception? error)
        {
            lock (ended)
            {
                ended.Add((p.Id, outcome, error?.Message));
            }
        }

        using DemoProcessingQueue queue = Queue(path =>
        {
            if (path.Contains("bad", StringComparison.Ordinal))
            {
                throw new InvalidDataException("corrupt");
            }
        });
        FakePass thrower = new("thrower") { OnRun = _ => throw new InvalidOperationException("boom") };
        FakePass stopped = new("stopped") { OnRun = _ => throw new OperationCanceledException() };
        FakePass fine = new("fine");
        FakePass onBad = new("onbad");

        IDemoQueueHandle good = queue.SubmitVisit(Visit(PassLevel.Background, Ended, thrower, stopped, fine));
        IDemoQueueHandle bad = queue.SubmitVisit(new DemoVisitRequest("/d/bad.dem", PassLevel.Background, [onBad], 0, null, Ended));
        await good.Completion;
        await bad.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(ended).Contains(("thrower", PassOutcome.Failed, "boom"));
            await Assert.That(ended.Any(e => e.Item1 == "stopped" && e.Item2 == PassOutcome.Cancelled)).IsTrue();
            await Assert.That(ended).Contains(("fine", PassOutcome.Ran, (string?)null));
            await Assert.That(ended).Contains(("onbad", PassOutcome.ParseFailed, "corrupt"));
            await Assert.That(onBad.Failed).IsEquivalentTo(["corrupt"]);
            await Assert.That(good.State).IsEqualTo(DemoQueueItemState.Completed).Because("one pass's throw never fails the visit");
            await Assert.That(bad.State).IsEqualTo(DemoQueueItemState.Failed);
        }
    }

    [Test]
    public async Task AUserRequestedVisit_RunsAtUserPriority_AndTheRowNamesEveryPass()
    {
        using DemoProcessingQueue queue = Queue();
        queue.Pause();
        IDemoQueueHandle handle = queue.SubmitVisit(Visit(PassLevel.UserRequested, null, new FakePass("a"), new FakePass("b")));

        DemoQueueItemSnapshot row = queue.Snapshot().Single(s => s.Id == handle.Id);

        using (Assert.Multiple())
        {
            await Assert.That(row.Priority).IsEqualTo(DemoJobPriority.UserRequested);
            await Assert.That(row.Owners).IsEquivalentTo(["a", "b"]);
        }

        queue.Resume();
        await handle.Completion;
    }

    [Test]
    public async Task Ordered_IgnoresAnAfterIdNotOnTheVisit_AndNeverAddsTheSameInstanceTwice()
    {
        DemoVisit visit = new(new VisitedDemo(Demo), PassLevel.Background);
        FakePass late = new("late", "absent", "first");
        FakePass first = new("first");

        await Assert.That(visit.Add(late, PassNeeds.RetainedParse, null)).IsTrue();
        await Assert.That(visit.Add(first, PassNeeds.RetainedParse, null)).IsTrue();
        await Assert.That(visit.Add(first, PassNeeds.RetainedParse, null)).IsFalse();

        using (Assert.Multiple())
        {
            await Assert.That(visit.Count).IsEqualTo(2);
            await Assert.That(visit.Ordered().Select(p => p.Pass.Id)).IsEquivalentTo(["first", "late"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Planner_SeedsEveryYes_AndAddsAWaitingPassOnlyWhenAnAncestorIsOnTheVisit()
    {
        FakePass library = new("library");
        FakePass highlights = new("highlights") { Answer = PassInterest.No };
        FakePass facts = new("facts", "library") { Answer = PassInterest.IfUpstreamRuns };
        FakePass index = new("index", "facts") { Answer = PassInterest.IfUpstreamRuns };
        FakePass tags = new("tags", "index") { Answer = PassInterest.IfUpstreamRuns };
        FakePass orphan = new("orphan", "highlights") { Answer = PassInterest.IfUpstreamRuns };
        IDemoPass[] all = [library, highlights, facts, index, tags, orphan];

        List<IDemoPass> plan = VisitPlanner.Plan(all, new VisitedDemo(Demo), PassLevel.Backlog);

        await Assert.That(plan.Select(p => p.Id)).IsEquivalentTo(["library", "facts", "index", "tags"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching)
            .Because("the chain joins link by link; a pass waiting on one that is not running stays out");
    }

    [Test]
    public async Task Planner_ReachesAGrandparent_ThroughAParentThatSitsOut()
    {
        FakePass library = new("library");
        FakePass index = new("index", "library") { Answer = PassInterest.No };
        FakePass tags = new("tags", "index") { Answer = PassInterest.IfUpstreamRuns };

        List<IDemoPass> plan = VisitPlanner.Plan([library, index, tags], new VisitedDemo(Demo), PassLevel.Backlog);

        await Assert.That(plan.Select(p => p.Id)).IsEquivalentTo(["library", "tags"]);
    }

    [Test]
    public async Task Planner_LeavesOutAPassWhoseInterestThrows_AndReportsIt()
    {
        List<string> faulted = [];
        FakePass library = new("library");
        FakePass broken = new("broken") { Interested = (_, _) => throw new InvalidOperationException("boom") };

        List<IDemoPass> plan = VisitPlanner.Plan([library, broken], new VisitedDemo(Demo), PassLevel.Backlog,
            (p, ex) => faulted.Add(p.Id + ": " + ex.Message));

        using (Assert.Multiple())
        {
            await Assert.That(plan.Select(p => p.Id)).IsEquivalentTo(["library"]);
            await Assert.That(faulted).IsEquivalentTo(["broken: boom"]);
        }
    }

    private sealed class FakePass(string id, params string[] after) : IDemoPass
    {
        public List<string> Ran { get; init; } = [];
        public PassNeeds NeedsValue { get; init; } = PassNeeds.RetainedWithoutUserCommands;
        public PassInterest Answer { get; set; } = PassInterest.Yes;
        public Func<VisitedDemo, PassLevel, PassInterest>? Interested { get; init; }
        public Action<PassInput>? OnRun { get; init; }
        public List<string> Failed { get; } = [];

        public string Id => id;
        public IReadOnlyList<string> After => after;

        public PassNeeds Needs(VisitedDemo demo) => NeedsValue;

        public PassInterest Interest(VisitedDemo demo, PassLevel level) => Interested?.Invoke(demo, level) ?? Answer;

        public void Run(PassInput input)
        {
            lock (Ran)
            {
                Ran.Add(id);
            }

            OnRun?.Invoke(input);
        }

        public void OnFailed(VisitedDemo demo, Exception failure)
        {
            lock (Failed)
            {
                Failed.Add(failure.Message);
            }
        }
    }
}

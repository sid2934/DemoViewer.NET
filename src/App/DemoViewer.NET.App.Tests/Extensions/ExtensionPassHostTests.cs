#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's pass on the scheduler and the queue: a throw skips that demo and counts one fault, a pass
///     that keeps overrunning its budget is switched off for the session, it is never asked on the UI thread,
///     its parse is gone once its turn ends, its priority reaches the visit, and switching its extension off
///     takes it off queued visits. A pass that declares a forward read keeps a visit forward and sees no parse.
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

    internal static ExtensionGuard StandaloneGuard() => ExtensionGuard.Standalone(new Pack());

    internal static ForwardDemoResult ForwardResult() => new()
    {
        Demo = new DemoDescriptor("de_test", 64, 1f / 64, 1000, 0, "test", "test", 0, DemoProfile.Unknown,
            new Dictionary<int, PlayerInfo>()),
        Rounds = [new ClipRound(1, 300), new ClipRound(2, 700)],
        FrameCount = 10,
        FirstServerTick = 5,
        LastServerTick = 1000
    };

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

    [Test]
    public async Task AForwardPass_DeclaresAForwardNeed()
    {
        ExtensionPassHost forward = new(new FakePass { Reads = PassRead.Forward }, [], StandaloneGuard(), static () => false);
        ExtensionPassHost retained = new(new FakePass(), [], StandaloneGuard(), static () => false);

        using (Assert.Multiple())
        {
            await Assert.That(forward.Needs(new VisitedDemo(Demo))).IsEqualTo(new PassNeeds(ParseMode.Forward));
            await Assert.That(retained.Needs(new VisitedDemo(Demo))).IsEqualTo(PassNeeds.RetainedWithoutUserCommands);
        }
    }

    // A table rides the rules run, so a forward pass that reads one makes that run; Table reads it off the visit's run.
    [Test]
    public async Task APassThatReadsATable_MakesTheRulesRun_AndReadsItOffTheVisitsRun()
    {
        TableReader inner = new();
        ExtensionPassHost reader = new(inner, [], StandaloneGuard(), static () => false, tableRulesets: ["ext_sample__rows"]);
        ExtensionPassHost plain = new(new TableReader(), [], StandaloneGuard(), static () => false);
        AnalysisRun run = DemoAnalysis.Run(SyntheticParsedDemo.Create(), []);
        List<(AnalysisRun Run, string Table, IReadOnlyList<string> Rulesets)> asked = [];
        FactRow row = new(new Dictionary<string, FactValue>(), new Dictionary<string, FactValue> { ["x"] = FactValue.Of(1L) });

        reader.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None)
        {
            RetainedRun = () => run,
            TableOf = (r, table, rulesets) =>
            {
                asked.Add((r, table, rulesets));
                return [row];
            }
        });
        plain.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(reader.Needs(new VisitedDemo(Demo))).IsEqualTo(new PassNeeds(ParseMode.Forward, ForwardNeeds.Rules, false));
            await Assert.That(plain.Needs(new VisitedDemo(Demo))).IsEqualTo(new PassNeeds(ParseMode.Forward));
            await Assert.That(inner.Rows).IsEquivalentTo([row]);
            await Assert.That(asked.Count).IsEqualTo(1);
            await Assert.That(asked[0].Run).IsSameReferenceAs(run);
            await Assert.That(asked[0].Table).IsEqualTo("rows");
            await Assert.That(asked[0].Rulesets).IsEquivalentTo(["ext_sample__rows"]);
        }
    }

    [Test]
    public async Task APassWithNoTableRulesets_CannotReadATable()
    {
        TableReader inner = new();
        ExtensionPassHost host = new(inner, [], StandaloneGuard(), static () => false);

        host.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None));

        await Assert.That(inner.Failure).IsTypeOf<InvalidOperationException>();
    }

    private sealed class TableReader : IExtensionPass
    {
        public IReadOnlyList<FactRow>? Rows;
        public Exception? Failure;

        public string Id => "ext.sample.reader";

        public PassRead Read => PassRead.Forward;

        public DemoInterest Interest(string demoPath) => DemoInterest.Yes;

        public void Run(IPassContext context)
        {
            try
            {
                Rows = context.Table("rows");
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
        }
    }

    [Test]
    public async Task AForwardPass_RunsOnTheForwardResult_WithANullParse()
    {
        FakePass inner = new() { Reads = PassRead.Forward };
        ExtensionPassHost host = new(inner, [], StandaloneGuard(), static () => false);

        host.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, null, ForwardResult(), CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(inner.Seen!.Parsed).IsNull();
            await Assert.That(inner.Seen.FrameCount).IsEqualTo(10);
            await Assert.That(inner.Seen.FirstServerTick).IsEqualTo(5);
            await Assert.That(inner.Seen.LastServerTick).IsEqualTo(1000);
            await Assert.That(inner.Seen.Map).IsEqualTo("de_test");
            await Assert.That(inner.Seen.Rounds).IsEquivalentTo([new PassRound(1, 300), new PassRound(2, 700)],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }

        Assert.Throws<ObjectDisposedException>(() => _ = inner.Kept!.FrameCount);
    }

    [Test]
    public async Task AForwardPass_OnARetainedVisit_ReadsTheParsesClock_ButNotTheParse()
    {
        FakePass inner = new() { Reads = PassRead.Forward };
        ExtensionPassHost host = new(inner, [], StandaloneGuard(), static () => false);

        host.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(inner.Seen!.Parsed).IsNull();
            await Assert.That(inner.Seen.FrameCount).IsEqualTo(0);
            await Assert.That(inner.Seen.FirstServerTick).IsEqualTo(0);
            await Assert.That(inner.Seen.Map).IsEqualTo("de_test");
            await Assert.That(inner.Seen.Rounds).IsEmpty();
        }
    }

    [Test]
    public async Task ARetainedPass_OnAForwardVisit_IsNotPossible()
    {
        FakePass inner = new();
        ExtensionPassHost host = new(inner, [], StandaloneGuard(), static () => false);
        int forwardReads = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            parseBytes: _ => SyntheticParsedDemo.Create(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, plan) => SyntheticParsedDemo.Create(plan: plan),
            forwardPass: (_, _, _, _) =>
            {
                Interlocked.Increment(ref forwardReads);
                return ForwardResult();
            });

        await queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Backlog,
            [host, new TestPass("library") { NeedsValue = PassNeeds.ForwardRead(ForwardNeeds.FinalState) }])).Completion;

        using (Assert.Multiple())
        {
            await Assert.That(forwardReads).IsEqualTo(0).Because("one retained pass puts the visit on the retained parse");
            await Assert.That(inner.Seen!.Parsed).IsNotNull();
        }

        Assert.Throws<InvalidOperationException>(() =>
            host.Run(new PassInput(new VisitedDemo(Demo), PassLevel.Background, null, ForwardResult(), CancellationToken.None)));
    }

    [Test]
    public async Task AForwardPass_ReadsUserCommandsOnlyThroughACollectorThatDeclaresThem()
    {
        ExtensionGuard guard = StandaloneGuard();
        CollectorContribution reading = CollectorKit.Counting("reads.cmds", FakePass.PassId) with { ReadsUserCommands = true };
        CollectorContribution silent = CollectorKit.Counting("no.cmds", FakePass.PassId);
        ExtensionPassHost alone = new(new FakePass { Reads = PassRead.Forward, UserCommands = true }, [], guard, static () => false);
        ExtensionPassHost withSilent = new(new FakePass { Reads = PassRead.Forward, UserCommands = true }, [], guard, static () => false,
            collectors: CollectorSet.Of([new RegisteredCollector(silent, guard)]));
        ExtensionPassHost withReading = new(new FakePass { Reads = PassRead.Forward }, [], guard, static () => false,
            collectors: CollectorSet.Of([new RegisteredCollector(reading, guard)]));
        ExtensionPassHost retained = new(new FakePass(), [], guard, static () => false,
            collectors: CollectorSet.Of([new RegisteredCollector(reading, guard)]));

        using (Assert.Multiple())
        {
            await Assert.That(alone.Needs(new VisitedDemo(Demo))).IsEqualTo(new PassNeeds(ParseMode.Forward))
                .Because("a forward pass sees no commands itself, so its own answer declares nothing");
            await Assert.That(withSilent.Needs(new VisitedDemo(Demo))).IsEqualTo(new PassNeeds(ParseMode.Forward, ForwardNeeds.Rules));
            await Assert.That(withReading.Needs(new VisitedDemo(Demo)))
                .IsEqualTo(new PassNeeds(ParseMode.Forward, ForwardNeeds.Rules | ForwardNeeds.UserCommands, true))
                .Because("the collector reads them on the rules run, so the forward read decodes them");
            await Assert.That(retained.Needs(new VisitedDemo(Demo))).IsEqualTo(new PassNeeds(ParseMode.Retained, ForwardNeeds.None, true))
                .Because("a retained visit's parse keeps them for the run's lane");
        }
    }

    /// <summary>What a pass read from its context during its turn.</summary>
    internal sealed record SeenContext(ParsedDemo? Parsed, int FrameCount, int FirstServerTick, int LastServerTick, string Map,
        IReadOnlyList<PassRound> Rounds);

    internal sealed class FakePass : IExtensionPass
    {
        public const string PassId = "ext.sample.pass";

        public bool Throw { get; init; }
        public PassRead Reads { get; init; } = PassRead.Retained;
        public bool UserCommands { get; init; }
        public string PassName { get; init; } = PassId;
        public List<string>? Ran { get; init; }
        public SeenContext? Seen;
        public TimeSpan RunFor { get; init; }
        public DemoInterest Answer { get; init; } = DemoInterest.Yes;
        public JobPriority Priority { get; init; } = JobPriority.Background;
        public int Runs;
        public int Asked;
        public IPassContext? Kept;

        public string Id => PassName;

        public bool ReadsUserCommands => UserCommands;

        public PassRead Read => Reads;

        public DemoInterest Interest(string demoPath)
        {
            Interlocked.Increment(ref Asked);
            return Answer;
        }

        public void Run(IPassContext context)
        {
            Interlocked.Increment(ref Runs);
            Kept = context;
            Seen = new SeenContext(context.Parsed, context.FrameCount, context.FirstServerTick, context.LastServerTick,
                context.Demo.MapName, context.Rounds);
            if (Ran is { } ran)
            {
                lock (ran)
                {
                    ran.Add(Id);
                }
            }
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

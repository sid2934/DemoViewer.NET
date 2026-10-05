#region

using System.Collections.Concurrent;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="DemoEvaluationCoordinator" /> parity battery: proves the "one parse, many
///     evaluators" contract against the REAL <see cref="DemoProcessingQueue" /> with a fake parser:
///     interested evaluators all run on a single parse, uninterested ones don't submit, failures are
///     isolated, parse failures reach <c>OnFailed</c>, and re-considering a processed demo is a no-op.
///     Pure logic; no filesystem (the fake parser ignores the path).
/// </summary>
[NotInParallel]
public class DemoEvaluationCoordinatorTests
{
    private static readonly Action<Action> _inline = a => a();
    private static readonly string[] _oneDemo = ["/x/demo.dem"];

    private static ParsedDemo Synthetic() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "s", "c",
        "csgo", 0, 0, 0,
        "v", "", "", DemoProfile.Unknown);

    private static async Task WaitFor(Func<bool> cond, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    [Test]
    public async Task Consider_ParsesOnce_FansOutToAllInterested()
    {
        int parses = 0;
        // Block the parse until BOTH evaluators' submissions are in, so the two coalesce onto one entry
        // deterministically (a real multi-second parse always outlasts the coordinator's tight submit
        // loop; an instant synthetic parse could otherwise finish between the two submits).
        using ManualResetEventSlim release = new(false);
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                release.Wait(2000);
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake a = new("a");
        Fake b = new("b");
        using DemoEvaluationCoordinator coord = new([a, b], queue, () => Array.Empty<string>());

        coord.Consider("/x/demo.dem"); // submits a then b (both before the blocked parse completes)
        release.Set();

        await WaitFor(() => a.Count == 1 && b.Count == 1, "both evaluators ran");
        await Assert.That(parses).IsEqualTo(1).Because("one parse fans out to every interested evaluator");
    }

    [Test]
    public async Task Consider_SkipsUninterestedEvaluator()
    {
        int parses = 0;
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake wants = new("wants");
        Fake skips = new("skips", _ => false);
        using DemoEvaluationCoordinator coord = new([wants, skips], queue, () => Array.Empty<string>());

        coord.Consider("/x/demo.dem");

        await WaitFor(() => wants.Count == 1, "interested evaluator ran");
        await Task.Delay(50);
        await Assert.That(skips.Count).IsEqualTo(0).Because("Wants=false means no submission for it");
        await Assert.That(parses).IsEqualTo(1);
    }

    [Test]
    public async Task Evaluate_FailureIsolated_OtherEvaluatorStillRuns()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        Fake ok = new("ok");
        using DemoEvaluationCoordinator coord = new([thrower, ok], queue, () => Array.Empty<string>());

        coord.Consider("/x/demo.dem");

        await WaitFor(() => ok.Count == 1, "the healthy evaluator still ran despite a sibling throw");
    }

    [Test]
    public async Task Evaluate_Throws_DemoIsNotReparsedOnLaterPolls()
    {
        int parses = 0;
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        // Wants stays true forever, as it does for an evaluator that throws before recording its result.
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        using DemoEvaluationCoordinator coord = new([thrower], queue, () => _oneDemo);

        coord.Consider("/x/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !coord.HasOutstanding("thrower"), "the throwing evaluation");

        coord.ConsiderAll();
        coord.ConsiderAll();
        await Task.Delay(80);

        await Assert.That(coord.IsFaulted("thrower", "/x/demo.dem")).IsTrue();
        await Assert.That(parses).IsEqualTo(1).Because("a throwing evaluator is skipped for that demo");
        await Assert.That(thrower.Count).IsEqualTo(1);
    }

    // Every throw out of an evaluator reaches the Faulted callback with the evaluator and the demo, which
    // the composition root counts against the extension; the parse count still stops at one.
    [Test]
    public async Task Evaluate_Throws_IsReportedOnce_WithTheEvaluatorAndTheDemo()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        ConcurrentQueue<(string Id, string? Path)> reported = new();
        using DemoEvaluationCoordinator coord = new([thrower], queue, () => _oneDemo)
        {
            Faulted = (id, path, _) => reported.Enqueue((id, path))
        };

        coord.Consider("/x/demo.dem");
        await WaitFor(() => !reported.IsEmpty && !coord.HasOutstanding("thrower"), "the throwing evaluation");
        coord.ConsiderAll();
        await Task.Delay(80);

        await Assert.That(reported.ToArray()).IsEquivalentTo([("thrower", (string?)"/x/demo.dem")]);
    }

    [Test]
    public async Task Evaluate_Cancelled_IsNotAFault()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake cancelled = new("cancelled", onEvaluate: _ => throw new OperationCanceledException());
        using DemoEvaluationCoordinator coord = new([cancelled], queue, () => Array.Empty<string>());

        coord.Consider("/x/demo.dem");
        await WaitFor(() => cancelled.Count == 1 && !coord.HasOutstanding("cancelled"), "the cancelled evaluation");

        await Assert.That(coord.IsFaulted("cancelled", "/x/demo.dem")).IsFalse()
            .Because("a preempted or removed item is requeued, not broken");
    }

    [Test]
    public async Task ForgetFaults_OffersTheDemoAgain()
    {
        int parses = 0;
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        using DemoEvaluationCoordinator coord = new([thrower], queue, () => _oneDemo);

        coord.Consider("/x/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !coord.HasOutstanding("thrower"), "the throwing evaluation");

        coord.ForgetFaults("/x/demo.dem");
        coord.ConsiderAll();
        await WaitFor(() => thrower.Count == 2, "the demo offered again after its faults were cleared");
        await Assert.That(parses).IsEqualTo(2);
    }

    [Test]
    public async Task Evaluate_Throws_OtherDemosAndEvaluatorsStillConsidered()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: p =>
        {
            if (p == "/x/bad.dem")
            {
                throw new InvalidOperationException("boom");
            }
        });
        using DemoEvaluationCoordinator coord = new([thrower], queue, () => Array.Empty<string>());

        coord.Consider("/x/bad.dem");
        await WaitFor(() => thrower.Count == 1 && !coord.HasOutstanding("thrower"), "the throwing evaluation");
        coord.Consider("/x/good.dem");
        await WaitFor(() => thrower.Count == 2, "the same evaluator on another demo");

        await Assert.That(coord.IsFaulted("thrower", "/x/good.dem")).IsFalse();
    }

    [Test]
    public async Task ParseFailure_CallsOnFailed()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ => throw new InvalidOperationException("corrupt"));
        Fake a = new("a");
        using DemoEvaluationCoordinator coord = new([a], queue, () => Array.Empty<string>());

        coord.Consider("/x/demo.dem");

        await WaitFor(() => a.Failed.Count == 1, "OnFailed fired on a parse failure");
        await Assert.That(a.Count).IsEqualTo(0).Because("Evaluate does not run when the parse failed");
    }

    [Test]
    public async Task ConsiderAll_AfterProcessing_DoesNotReparse()
    {
        int parses = 0;
        HashSet<string> evaluatedOnce = new();
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        // Wants only while not yet evaluated, mirrors a real staleness gate.
        Fake a = new("a", p =>
            {
                lock (evaluatedOnce)
                {
                    return !evaluatedOnce.Contains(p);
                }
            },
            p =>
            {
                lock (evaluatedOnce)
                {
                    evaluatedOnce.Add(p);
                }
            });
        using DemoEvaluationCoordinator coord = new([a], queue, () => _oneDemo);

        coord.Consider("/x/demo.dem");
        await WaitFor(() => a.Count == 1, "first evaluation");

        coord.ConsiderAll(); // re-poll: Wants is now false → no second submit
        await Task.Delay(80);

        await Assert.That(parses).IsEqualTo(1).Because("a processed demo is not re-parsed");
        await Assert.That(a.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     FanOutParsed (Phase 4a) hands an ALREADY-parsed demo to every evaluator's
    ///     OnParsedOpportunistically EXCEPT those named in the skip set, no queue submit, no second parse.
    /// </summary>
    [Test]
    public async Task FanOutParsed_HandsToAllExceptSkipped()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake a = new("a");
        Fake b = new("b");
        using DemoEvaluationCoordinator coord = new([a, b], queue, () => Array.Empty<string>());

        coord.FanOutParsed("/x/demo.dem", Synthetic(),
            new HashSet<string>(StringComparer.Ordinal)
            {
                "b"
            });

        await Assert.That(a.Opportunistic).Contains("/x/demo.dem")
            .Because("an unskipped evaluator gets the held parse");
        await Assert.That(b.OpportunisticCount).IsEqualTo(0).Because("a skipped evaluator is not re-fed");
    }

    /// <summary>
    ///     A throwing OnParsedOpportunistically handler is isolated. The trigger never throws and the
    ///     other evaluators are still fed.
    /// </summary>
    [Test]
    public async Task FanOutParsed_FailureIsolated_OtherStillFed()
    {
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onOpportunistic: _ => throw new InvalidOperationException("boom"));
        Fake ok = new("ok");
        using DemoEvaluationCoordinator coord = new([thrower, ok], queue, () => Array.Empty<string>());

        coord.FanOutParsed("/x/demo.dem", Synthetic()); // must not throw

        await Assert.That(ok.OpportunisticCount).IsEqualTo(1).Because("a sibling throw is isolated");
    }

    [Test]
    public async Task Consider_APassWaitingOnUpstream_JoinsTheUpstreamsVisit_AndRunsAfterItOnTheSameParse()
    {
        int parses = 0;
        List<string> order = [];
        bool libraryWrote = false;
        DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ =>
        {
            Interlocked.Increment(ref parses);
            return Synthetic();
        });
        Fake library = new("library", _ => !libraryWrote, _ =>
        {
            libraryWrote = true;
            lock (order)
            {
                order.Add("library");
            }
        });
        // Wanted only once the library has written and until it has itself; before that it can only wait.
        bool factsWrote = false;
        UpstreamFake facts = new("facts", _ => libraryWrote && !factsWrote, _ => !libraryWrote, _ =>
        {
            factsWrote = true;
            lock (order)
            {
                order.Add("facts");
            }
        });
        PassRegistry registry = new();
        registry.AddCoreEvaluator("library", () => library);
        registry.AddCoreEvaluator("facts", () => facts, "library");
        using DemoEvaluationCoordinator coord = new(registry.Resolve, queue, () => _oneDemo);

        coord.Consider("/x/demo.dem");
        await WaitFor(() => order.Count == 2 && !coord.HasOutstanding("facts"), "both passes on one visit");

        // A demo the library does not want has no upstream on the visit, so the waiting pass stays off it.
        coord.Consider("/x/other.dem");
        await Task.Delay(80);

        using (Assert.Multiple())
        {
            await Assert.That(parses).IsEqualTo(1).Because("the waiting pass rides the upstream parse");
            await Assert.That(order).IsEquivalentTo(["library", "facts"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(coord.HasOutstanding("facts")).IsFalse();
        }
    }

    private sealed class UpstreamFake(string id, Func<string, bool> wants, Func<string, bool> afterUpstream, Action<string> onEvaluate)
        : IDemoEvaluator
    {
        public string Id => id;

        public bool Wants(string path) => wants(path);

        public bool WantsAfterUpstream(string path) => afterUpstream(path);

        public void Evaluate(string path, ParsedDemo parsed) => onEvaluate(path);
    }

    private sealed class Fake(
        string id,
        Func<string, bool>? wants = null,
        Action<string>? onEvaluate = null,
        Action<string>? onOpportunistic = null)
        : IDemoEvaluator
    {
        private readonly object _gate = new();
        private readonly Func<string, bool> _wants = wants ?? (_ => true);
        public List<string> Evaluated { get; } = [];
        public List<string> Failed { get; } = [];
        public List<string> Opportunistic { get; } = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return Evaluated.Count;
                }
            }
        }

        public int OpportunisticCount
        {
            get
            {
                lock (_gate)
                {
                    return Opportunistic.Count;
                }
            }
        }

        public string Id { get; } = id;

        public bool Wants(string path)
        {
            lock (_gate)
            {
                return _wants(path);
            }
        }

        public void Evaluate(string path, ParsedDemo parsed)
        {
            lock (_gate)
            {
                Evaluated.Add(path);
            }

            onEvaluate?.Invoke(path);
        }

        public void OnFailed(string path)
        {
            lock (_gate)
            {
                Failed.Add(path);
            }
        }

        public void OnParsedOpportunistically(string path, ParsedDemo parsed)
        {
            lock (_gate)
            {
                Opportunistic.Add(path);
            }

            onOpportunistic?.Invoke(path);
        }
    }
}

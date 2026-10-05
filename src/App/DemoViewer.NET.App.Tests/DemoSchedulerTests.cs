#region

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="DemoScheduler" /> against the REAL <see cref="DemoProcessingQueue" /> with a fake parser: a
///     changed demo is planned off the caller's thread as a queue item, every interested pass runs on one
///     parse, uninterested passes stay off the visit, a throwing pass is skipped for that demo for the
///     session, parse failures reach <c>OnFailed</c>, a refused visit is planned again when the queue has
///     room, and the whole-library re-check plans every known demo once. Pure logic; no filesystem (the fake
///     parser ignores the path).
/// </summary>
[NotInParallel]
public class DemoSchedulerTests
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
    public async Task DemoChanged_ParsesOnce_RunsEveryInterestedPass()
    {
        int parses = 0;
        using ManualResetEventSlim release = new(false);
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                release.Wait(2000);
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake a = new("a");
        Fake b = new("b");
        using DemoScheduler scheduler = new([a, b], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");
        release.Set();

        await WaitFor(() => a.Count == 1 && b.Count == 1, "both passes ran");
        await Assert.That(parses).IsEqualTo(1).Because("one parse serves every interested pass");
    }

    [Test]
    public async Task DemoChanged_SkipsAPassThatDoesNotWantTheDemo()
    {
        int parses = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake wants = new("wants");
        Fake skips = new("skips", _ => false);
        using DemoScheduler scheduler = new([wants, skips], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");

        await WaitFor(() => wants.Count == 1, "interested pass ran");
        await Task.Delay(50);
        await Assert.That(skips.Count).IsEqualTo(0).Because("Wants=false keeps the pass off the visit");
        await Assert.That(parses).IsEqualTo(1);
    }

    [Test]
    public async Task DemoChanged_PlansAsAQueueItem_NotOnTheCallersThread()
    {
        RecordingQueue queue = new() { Defer = true };
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");

        await Assert.That(queue.Visits).IsEmpty().Because("nothing is planned until the queue runs the planning item");
        await Assert.That(queue.Jobs.Select(j => j.Kind)).IsEquivalentTo([QueueJobKind.Scheduling]);

        await queue.RunDeferredAsync();
        await Assert.That(queue.Visits.Select(v => v.Path)).IsEquivalentTo(["/x/demo.dem"]);
    }

    [Test]
    public async Task Request_PlansAtUserPriority_AndTheOpenDemoAtTheOpenLevel()
    {
        RecordingQueue queue = new();
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/backlog.dem");
        scheduler.Request("/x/forced.dem");
        scheduler.PlanOpenDemo("/x/open.dem");

        using (Assert.Multiple())
        {
            await Assert.That(queue.Visits.Single(v => v.Path == "/x/backlog.dem").Level).IsEqualTo(PassLevel.Backlog);
            await Assert.That(queue.Visits.Single(v => v.Path == "/x/forced.dem").Level).IsEqualTo(PassLevel.UserRequested);
            await Assert.That(queue.Visits.Single(v => v.Path == "/x/open.dem").Level).IsEqualTo(PassLevel.OpenDemo);
            await Assert.That(queue.Jobs.Where(j => j.Kind == QueueJobKind.Scheduling).Select(j => j.Priority))
                .Contains(DemoJobPriority.UserRequested).Because("a user's request plans ahead of background planning");
        }
    }

    [Test]
    public async Task Run_FailureIsolated_OtherPassStillRuns()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        Fake ok = new("ok");
        using DemoScheduler scheduler = new([thrower, ok], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");

        await WaitFor(() => ok.Count == 1, "the healthy pass still ran despite a sibling throw");
    }

    [Test]
    public async Task Run_Throws_DemoIsNotReparsedOnLaterRechecks()
    {
        int parses = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        // Wants stays true forever, as it does for a pass that throws before recording its result.
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        using DemoScheduler scheduler = new([thrower], queue, () => _oneDemo);

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !scheduler.HasOutstanding("thrower"), "the throwing run");

        scheduler.RecheckAll();
        scheduler.RecheckAll();
        await Task.Delay(120);

        await Assert.That(scheduler.IsFaulted("thrower", "/x/demo.dem")).IsTrue();
        await Assert.That(parses).IsEqualTo(1).Because("a throwing pass is skipped for that demo");
        await Assert.That(thrower.Count).IsEqualTo(1);
    }

    // Every throw out of a pass reaches the Faulted callback with the pass and the demo, which the
    // composition root counts against the extension; the parse count still stops at one.
    [Test]
    public async Task Run_Throws_IsReportedOnce_WithThePassAndTheDemo()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        ConcurrentQueue<(string Id, string? Path)> reported = new();
        using DemoScheduler scheduler = new([thrower], queue, () => _oneDemo)
        {
            Faulted = (id, path, _) => reported.Enqueue((id, path))
        };

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => !reported.IsEmpty && !scheduler.HasOutstanding("thrower"), "the throwing run");
        scheduler.RecheckAll();
        await Task.Delay(120);

        await Assert.That(reported.ToArray()).IsEquivalentTo([("thrower", (string?)"/x/demo.dem")]);
    }

    [Test]
    public async Task Run_Cancelled_IsNotAFault()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake cancelled = new("cancelled", onEvaluate: _ => throw new OperationCanceledException());
        using DemoScheduler scheduler = new([cancelled], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => cancelled.Count == 1 && !scheduler.HasOutstanding("cancelled"), "the cancelled run");

        await Assert.That(scheduler.IsFaulted("cancelled", "/x/demo.dem")).IsFalse()
            .Because("a preempted or removed item is requeued, not broken");
    }

    [Test]
    public async Task ForgetFaults_OffersTheDemoAgain()
    {
        int parses = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        using DemoScheduler scheduler = new([thrower], queue, () => _oneDemo);

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !scheduler.HasOutstanding("thrower"), "the throwing run");

        scheduler.ForgetFaults("/x/demo.dem");
        scheduler.RecheckAll();
        await WaitFor(() => thrower.Count == 2, "the demo offered again after its faults were cleared");
        await Assert.That(parses).IsEqualTo(2);
    }

    [Test]
    public async Task Run_Throws_OtherDemosStillPlanned()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: p =>
        {
            if (p == "/x/bad.dem")
            {
                throw new InvalidOperationException("boom");
            }
        });
        using DemoScheduler scheduler = new([thrower], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/bad.dem");
        await WaitFor(() => thrower.Count == 1 && !scheduler.HasOutstanding("thrower"), "the throwing run");
        scheduler.DemoChanged("/x/good.dem");
        await WaitFor(() => thrower.Count == 2, "the same pass on another demo");

        await Assert.That(scheduler.IsFaulted("thrower", "/x/good.dem")).IsFalse();
    }

    [Test]
    public async Task ParseFailure_CallsOnFailed()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ => throw new InvalidOperationException("corrupt"));
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");

        await WaitFor(() => a.Failed.Count == 1, "OnFailed fired on a parse failure");
        await Assert.That(a.Count).IsEqualTo(0).Because("Evaluate does not run when the parse failed");
    }

    [Test]
    public async Task RecheckAll_AfterProcessing_DoesNotReparse()
    {
        int parses = 0;
        HashSet<string> evaluatedOnce = new();
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
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
        using DemoScheduler scheduler = new([a], queue, () => _oneDemo);

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => a.Count == 1, "first run");

        scheduler.RecheckAll(); // Wants is now false → no second visit
        await Task.Delay(120);

        await Assert.That(parses).IsEqualTo(1).Because("a processed demo is not re-parsed");
        await Assert.That(a.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RecheckAll_PlansEveryKnownDemo_Once()
    {
        ConcurrentBag<string> parsed = [];
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, path =>
        {
            parsed.Add(path);
            return Synthetic();
        });
        HashSet<string> done = new(StringComparer.Ordinal);
        Fake a = new("a", p =>
        {
            lock (done)
            {
                return !done.Contains(p);
            }
        }, p =>
        {
            lock (done)
            {
                done.Add(p);
            }
        });
        string[] library = ["/x/1.dem", "/x/2.dem", "/x/3.dem"];
        using DemoScheduler scheduler = new([a], queue, () => library);

        scheduler.RecheckAll();
        await WaitFor(() => a.Count == 3 && queue.ActiveWorkerCount == 0, "every known demo planned");
        scheduler.RecheckAll();
        await Task.Delay(120);

        await Assert.That(parsed.Order()).IsEquivalentTo(library);
    }

    [Test]
    public async Task ARefusedVisit_StaysDirty_AndIsPlannedWhenTheQueueHasRoom()
    {
        ConcurrentBag<string> parsed = [];
        using ManualResetEventSlim release = new(false);
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, path =>
        {
            parsed.Add(path);
            release.Wait(3000);
            return Synthetic();
        })
        {
            MaxQueueSize = 1
        };
        HashSet<string> done = new(StringComparer.Ordinal);
        Fake a = new("a", p =>
        {
            lock (done)
            {
                return !done.Contains(p);
            }
        }, p =>
        {
            lock (done)
            {
                done.Add(p);
            }
        });
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/first.dem");
        await WaitFor(() => parsed.Count == 1, "the first demo occupies the worker");
        scheduler.DemoChanged("/x/second.dem");
        await Task.Delay(80);
        await Assert.That(parsed.Count).IsEqualTo(1).Because("the full tier refused the second demo");
        await Assert.That(scheduler.HasOutstanding("a")).IsTrue();

        release.Set();
        await WaitFor(() => a.Count == 2, "the refused demo planned once the tier had room");
        await Assert.That(parsed.Order()).IsEquivalentTo(["/x/first.dem", "/x/second.dem"]);
    }

    [Test]
    public async Task DemoChanged_APassWaitingOnUpstream_JoinsTheUpstreamsVisit_AndRunsAfterItOnTheSameParse()
    {
        int parses = 0;
        List<string> order = [];
        bool libraryWrote = false;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ =>
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
        using DemoScheduler scheduler = new(registry.Resolve, queue, () => _oneDemo);

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => order.Count == 2 && !scheduler.HasOutstanding("facts"), "both passes on one visit");

        // A demo the library does not want has no upstream on the visit, so the waiting pass stays off it.
        scheduler.DemoChanged("/x/other.dem");
        await Task.Delay(120);

        using (Assert.Multiple())
        {
            await Assert.That(parses).IsEqualTo(1).Because("the waiting pass rides the upstream parse");
            await Assert.That(order).IsEquivalentTo(["library", "facts"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(scheduler.HasOutstanding("facts")).IsFalse();
        }
    }

    [Test]
    public async Task DemoChanged_SubmitsOneVisitThroughSubmitVisit_WithAPassPerWantedEvaluator_KeyedByItsId()
    {
        RecordingQueue queue = new();
        Fake a = new("a");
        Fake b = new("b");
        Fake c = new("c", _ => false);
        using DemoScheduler scheduler = new([a, b, c], queue, () => _oneDemo);

        scheduler.DemoChanged("/x/demo.dem");

        using (Assert.Multiple())
        {
            await Assert.That(queue.Visits).HasCount().EqualTo(1).Because("one visit carries every wanted pass");
            await Assert.That(queue.Visits[0].Path).IsEqualTo("/x/demo.dem");
            await Assert.That(queue.Visits[0].Passes.Select(p => p.Id)).IsEquivalentTo(["a", "b"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("a pass is keyed by its evaluator's id");
            await Assert.That(queue.Backgrounds).IsEqualTo(0).Because("the scheduler never submits per evaluator");
        }
    }

    // A queue that records what the scheduler submits and runs its planning item inline, or holds it when
    // Defer is set, as the extension test doubles do.
    private sealed class RecordingQueue : IDemoProcessingQueue
    {
        private readonly List<QueueJobRequest> _deferred = [];
        public List<DemoVisitRequest> Visits { get; } = [];
        public List<QueueJobRequest> Jobs { get; } = [];
        public int Backgrounds { get; private set; }
        public bool Defer { get; init; }

        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; } = new([]);
        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused => false;
        public int QueuedCount => 0;
        public int RunningCount => 0;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public event Action? CapacityAvailable
        {
            add { }
            remove { }
        }

        public int ActiveCount(QueueJobKind kind) => 0;

        public Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request)
        {
            Backgrounds++;
            return new RecordedHandle();
        }

        public IDemoQueueHandle SubmitVisit(DemoVisitRequest request)
        {
            lock (Visits)
            {
                Visits.Add(request);
            }

            return new RecordedHandle();
        }

        public IDemoQueueHandle SubmitJob(QueueJobRequest request)
        {
            lock (Jobs)
            {
                Jobs.Add(request);
            }

            if (Defer)
            {
                lock (_deferred)
                {
                    _deferred.Add(request);
                }

                return new RecordedHandle();
            }

            request.RunAsync(new InlineContext()).GetAwaiter().GetResult();
            return new RecordedHandle(DemoQueueItemState.Completed);
        }

        public async Task RunDeferredAsync()
        {
            List<QueueJobRequest> jobs;
            lock (_deferred)
            {
                jobs = [.. _deferred];
                _deferred.Clear();
            }

            foreach (QueueJobRequest job in jobs)
            {
                await job.RunAsync(new InlineContext());
            }
        }

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void RemoveByUser(Guid itemId)
        {
        }

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void CancelOwned(string ownerTag)
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        private sealed class InlineContext : IQueueJobContext
        {
            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, int total, string? detail = null)
            {
            }

            public Task StepAsideAsync() => Task.CompletedTask;

            public void ReleaseSlot()
            {
            }
        }

        private sealed class RecordedHandle(DemoQueueItemState state = DemoQueueItemState.Queued) : IDemoQueueHandle
        {
            public Guid Id { get; } = Guid.NewGuid();
            public DemoQueueItemState State => state;
            public Task Completion => new TaskCompletionSource().Task;

            public void Cancel()
            {
            }
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
        Action<string>? onEvaluate = null)
        : IDemoEvaluator
    {
        private readonly object _gate = new();
        private readonly Func<string, bool> _wants = wants ?? (_ => true);
        public List<string> Evaluated { get; } = [];
        public List<string> Failed { get; } = [];

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
    }
}

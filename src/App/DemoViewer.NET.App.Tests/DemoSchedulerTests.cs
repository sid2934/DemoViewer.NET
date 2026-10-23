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
///     room, the whole-library re-check plans every known demo once, and a planning item stopped mid-batch
///     leaves the unplanned demos dirty. Pure logic; no filesystem (the fake parser ignores the path).
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
    public async Task APlanningItemRemovedBeforeItStarts_DoesNotStopTheNextChangeFromPlanning()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        queue.Pause();
        scheduler.DemoChanged("/x/one.dem");
        await WaitFor(() => queue.Snapshot().Any(i => i.Kind == QueueJobKind.Scheduling), "the queued planning item");
        Guid removed = queue.Snapshot().Single(i => i.Kind == QueueJobKind.Scheduling).Id;
        queue.RemoveByUser(removed);
        await WaitFor(() => queue.Snapshot().All(i => i.Id != removed || i.State != DemoQueueItemState.Queued),
            "the planning item's removal");
        queue.Resume();

        scheduler.DemoChanged("/x/two.dem");
        await WaitFor(() => a.Count == 2, "both demos, the removed item's included");
        await Assert.That(a.Evaluated.Order()).IsEquivalentTo(["/x/one.dem", "/x/two.dem"]);
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
        await queue.IdleAsync("every known demo planned", () => a.Count == 3);
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

    // A full background tier refuses visits. The planner must stop and wait for the capacity event, not re-plan
    // the refused demos until a visit ends: that held the planning item Running and starved the light lane.
    [Test]
    public async Task AFullQueue_StopsThePlanner_UntilAVisitEnds()
    {
        using ManualResetEventSlim release = new(false);
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ =>
        {
            release.Wait(5000);
            return Synthetic();
        })
        {
            MaxQueueSize = 1
        };
        int asks = 0;
        HashSet<string> done = new(StringComparer.Ordinal);
        Fake a = new("a", p =>
        {
            Interlocked.Increment(ref asks);
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
        await WaitFor(() => queue.RunningCount == 1 && queue.ActiveCount(QueueJobKind.Scheduling) == 0, "the first demo running");
        scheduler.DemoChanged("/x/second.dem");
        scheduler.DemoChanged("/x/third.dem");
        await WaitFor(() => queue.ActiveCount(QueueJobKind.Scheduling) == 0, "the planner to give up on a full tier");
        int before = Volatile.Read(ref asks);
        await Task.Delay(300);

        using (Assert.Multiple())
        {
            await Assert.That(Volatile.Read(ref asks) - before).IsEqualTo(0).Because("nothing re-plans while the tier stays full");
            await Assert.That(queue.ActiveCount(QueueJobKind.Scheduling)).IsEqualTo(0);
            await Assert.That(scheduler.Check.IsChecking).IsFalse();
            await Assert.That(scheduler.Check.Waiting).IsGreaterThan(0).Because("the flyout says demos wait for room");
        }

        release.Set();
        await WaitFor(() => a.Count == 3, "every demo planned as visits end");
        await WaitFor(() => scheduler.Check is { IsChecking: false, Waiting: 0, LastChecked: not null }, "a clean check stamped");
    }

    // A user's request bypasses the background cap, so it must not wait behind a backlog demo the tier refused.
    [Test]
    public async Task AUserRequest_IsPlanned_WhileTheBackgroundTierIsFull()
    {
        using ManualResetEventSlim release = new(false);
        ConcurrentBag<string> parsed = [];
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, path =>
        {
            parsed.Add(path);
            release.Wait(5000);
            return Synthetic();
        })
        {
            MaxQueueSize = 1
        };
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/first.dem");
        await WaitFor(() => queue.RunningCount == 1, "the first demo running");
        scheduler.DemoChanged("/x/backlog.dem");
        scheduler.Request("/x/asked.dem");

        await WaitFor(() => queue.Snapshot().Any(s => s.Path == "/x/asked.dem"), "the user's demo queued behind the running one");
        await Assert.That(queue.Snapshot().Any(s => s.Path == "/x/backlog.dem")).IsFalse()
            .Because("the backlog demo waits for room; the request did not");
        release.Set();
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
            await Assert.That(queue.Visits).Count().IsEqualTo(1).Because("one visit carries every wanted pass");
            await Assert.That(queue.Visits[0].Path).IsEqualTo("/x/demo.dem");
            await Assert.That(queue.Visits[0].Passes.Select(p => p.Id)).IsEquivalentTo(["a", "b"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("a pass is keyed by its evaluator's id");
            await Assert.That(queue.Backgrounds).IsEqualTo(0).Because("the scheduler never submits per evaluator");
        }
    }

    // The user stops the planning item part-way through a batch. The demo it was about to plan and every
    // one after it stay dirty for the next event; the ones it already submitted are not planned again.
    [Test]
    public async Task Drain_StoppedMidBatch_KeepsTheUnplannedDemosDirty_AndDoesNotReplanTheSubmittedOnes()
    {
        using CancellationTokenSource stop = new();
        RecordingQueue queue = new() { Defer = true, Token = stop.Token, EndPasses = true };
        int asked = 0;
        // The stop lands while the second demo of the batch is being planned: its visit still goes out.
        Fake a = new("a", _ =>
        {
            if (Interlocked.Increment(ref asked) == 2)
            {
                stop.Cancel();
            }

            return true;
        });
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/1.dem");
        scheduler.DemoChanged("/x/2.dem");
        scheduler.DemoChanged("/x/3.dem");
        await queue.RunDeferredAsync();

        await Assert.That(queue.Visits).Count().IsEqualTo(2).Because("the stop lands before the third demo is planned");

        queue.Token = CancellationToken.None;
        scheduler.DemoChanged("/x/4.dem");
        await queue.RunDeferredAsync();

        await Assert.That(queue.Visits.Select(v => v.Path).Order())
            .IsEquivalentTo(["/x/1.dem", "/x/2.dem", "/x/3.dem", "/x/4.dem"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
            .Because("the demo left unplanned is planned on the next event, and no submitted demo is planned twice");
    }

    private static string SameBytes(string path) =>
        path.EndsWith("demo.dem", StringComparison.OrdinalIgnoreCase) ? "content-1" : path;

    [Test]
    public async Task TwoPathsOfOneDemo_MarkedTogether_AreOneVisit_OnThePathMarkedLast()
    {
        RecordingQueue queue = new() { Defer = true };
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>(), SameBytes);

        scheduler.DemoChanged("/nfs/demo.dem");
        scheduler.DemoChanged("/smb/demo.dem");
        await queue.RunDeferredAsync();

        await Assert.That(queue.Visits.Select(v => v.Path)).IsEquivalentTo(["/smb/demo.dem"]);
    }

    [Test]
    public async Task ASecondPathOfADemoInFlight_IsNotPlannedAgain_AndARecheckListingBothPlansOnce()
    {
        RecordingQueue queue = new();
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => ["/nfs/demo.dem", "/smb/demo.dem", "/nfs/other.dem"], SameBytes);

        scheduler.DemoChanged("/nfs/demo.dem");
        scheduler.Request("/smb/demo.dem");
        await Assert.That(queue.Visits).Count().IsEqualTo(1).Because("the pass is already out on that demo");

        scheduler.RecheckAll();
        await Assert.That(queue.Visits.Select(v => v.Path))
            .IsEquivalentTo(["/nfs/demo.dem", "/nfs/other.dem"], TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    [Test]
    public async Task AFaultThroughOnePath_SkipsTheOther_AndForgettingAChangedPathClearsOnlyIt()
    {
        int parses = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        using DemoScheduler scheduler = new([thrower], queue, () => Array.Empty<string>(), SameBytes);

        scheduler.DemoChanged("/nfs/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !scheduler.HasOutstanding("thrower"), "the throwing run");
        scheduler.DemoChanged("/smb/demo.dem");
        await Task.Delay(120);

        using (Assert.Multiple())
        {
            await Assert.That(scheduler.IsFaulted("thrower", "/smb/demo.dem")).IsTrue();
            await Assert.That(scheduler.IsFaulted("thrower", "/NFS/DEMO.dem")).IsTrue();
            await Assert.That(parses).IsEqualTo(1).Because("the other path holds the bytes the pass threw on");
        }

        scheduler.ForgetFaults("/smb/demo.dem");
        using (Assert.Multiple())
        {
            await Assert.That(scheduler.IsFaulted("thrower", "/smb/demo.dem")).IsFalse();
            await Assert.That(scheduler.IsFaulted("thrower", "/nfs/demo.dem")).IsTrue()
                .Because("the file that changed was the other one; these bytes are the ones the pass threw on");
        }
    }

    [Test]
    public async Task ADemoHashedDuringItsVisit_KeepsItsFault_AndIsNotReplanned()
    {
        int parses = 0;
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline,
            _ =>
            {
                Interlocked.Increment(ref parses);
                return Synthetic();
            });
        ConcurrentDictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        Fake thrower = new("thrower", onEvaluate: p =>
        {
            hashes[p] = "content-1";
            throw new InvalidOperationException("boom");
        });
        using DemoScheduler scheduler = new([thrower], queue, () => ["/x/demo.dem"],
            path => hashes.TryGetValue(path, out string? key) ? key : path);

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !scheduler.HasOutstanding("thrower"), "the throwing run");
        scheduler.RecheckAll();
        await Task.Delay(120);

        using (Assert.Multiple())
        {
            await Assert.That(scheduler.IsFaulted("thrower", "/x/demo.dem")).IsTrue();
            await Assert.That(parses).IsEqualTo(1).Because("the fault holds under the key the hash gave the path");
        }
    }

    [Test]
    public async Task WithoutAKeyResolver_APathDifferingOnlyInCase_IsTheSameDemo()
    {
        using DemoProcessingQueue queue = new(new HeavyJobGate(), _inline, _ => Synthetic());
        Fake thrower = new("thrower", onEvaluate: _ => throw new InvalidOperationException("boom"));
        using DemoScheduler scheduler = new([thrower], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");
        await WaitFor(() => thrower.Count == 1 && !scheduler.HasOutstanding("thrower"), "the throwing run");

        await Assert.That(scheduler.IsFaulted("thrower", "/X/Demo.DEM")).IsTrue();
        scheduler.ForgetFaults("/X/DEMO.dem");
        await Assert.That(scheduler.IsFaulted("thrower", "/x/demo.dem")).IsFalse();
    }

    [Test]
    public async Task WithoutAKeyResolver_APathDifferingOnlyInCase_IsNotPlannedWhileItsPassIsOut()
    {
        RecordingQueue queue = new();
        Fake a = new("a");
        using DemoScheduler scheduler = new([a], queue, () => Array.Empty<string>());

        scheduler.DemoChanged("/x/demo.dem");
        scheduler.Request("/X/Demo.DEM");

        await Assert.That(queue.Visits.Select(v => v.Path)).IsEquivalentTo(["/x/demo.dem"]);
    }

    // A queue that records what the scheduler submits and runs its planning item inline, or holds it when
    // Defer is set, as the extension test doubles do. Token is the planning item's cancellation token;
    // EndPasses reports every submitted pass as run at once, so a demo can be planned again.
    [Test]
    public async Task ARider_JoinsAVisitThatRunsRules_AndIsNeverSubmittedAlone()
    {
        RecordingQueue queue = new();
        RulesFake grenades = new("grenades", path => path == "/x/demo.dem");
        bool riderWants = false;
        RulesFake highlights = new("highlights", _ => false, _ => riderWants);
        using DemoScheduler scheduler = new([highlights, grenades], queue, () => _oneDemo);

        // Grenades is out on the first visit; the rider turns stale only after.
        scheduler.DemoChanged("/x/demo.dem");
        riderWants = true;
        scheduler.DemoChanged("/x/demo.dem");
        scheduler.DemoChanged("/x/other.dem");
        scheduler.DemoChanged("/x/third.dem");

        using (Assert.Multiple())
        {
            await Assert.That(queue.Visits).Count().IsEqualTo(1)
                .Because("the rider whose rules pass is out, and the rider on a demo nothing else wants, pay for no read");
            await Assert.That(queue.Visits[0].Passes.Select(p => p.Id)).IsEquivalentTo(["grenades"]);
        }

        // A rider never upgrades a read that needs no rules.
        RecordingQueue noRules = new();
        using DemoScheduler finalStateOnly = new([highlights, new FinalStateFake("library")], noRules, () => _oneDemo);
        finalStateOnly.DemoChanged("/x/demo.dem");
        await Assert.That(noRules.Visits.Single().Passes.Select(p => p.Id)).IsEquivalentTo(["library"]);

        RecordingQueue second = new();
        using DemoScheduler fresh = new([highlights, grenades], second, () => _oneDemo);
        fresh.DemoChanged("/x/demo.dem");

        using (Assert.Multiple())
        {
            await Assert.That(second.Visits[0].Passes.Select(p => p.Id)).IsEquivalentTo(["highlights", "grenades"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(second.Visits[0].Riders).IsEquivalentTo(["highlights"]);
        }
    }

    private sealed class FinalStateFake(string id) : IDemoEvaluator
    {
        public string Id => id;

        public bool Wants(string path) => true;

        public ForwardNeeds? ForwardFor(string path) => ForwardNeeds.FinalState;

        public void Evaluate(string path, ParsedDemo parsed)
        {
        }
    }

    private sealed class RulesFake(string id, Func<string, bool> wants, Func<string, bool>? ifRulesRun = null) : IDemoEvaluator
    {
        public string Id => id;

        public bool Wants(string path) => wants(path);

        public bool WantsIfRulesRun(string path) => ifRulesRun?.Invoke(path) ?? false;

        public ForwardNeeds? ForwardFor(string path) => ForwardNeeds.Rules;

        public void Evaluate(string path, ParsedDemo parsed)
        {
        }
    }

    private sealed class RecordingQueue : IDemoProcessingQueue
    {
        private readonly List<QueueJobRequest> _deferred = [];
        public List<DemoVisitRequest> Visits { get; } = [];
        public List<QueueJobRequest> Jobs { get; } = [];
        public int Backgrounds { get; private set; }
        public bool Defer { get; init; }
        public bool EndPasses { get; init; }
        public CancellationToken Token { get; set; }

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

            if (EndPasses)
            {
                foreach (IDemoPass pass in request.Passes)
                {
                    request.PassEnded?.Invoke(pass, PassOutcome.Ran, null);
                }
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

            request.RunAsync(new InlineContext(Token)).GetAwaiter().GetResult();
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
                await job.RunAsync(new InlineContext(Token));
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

        private sealed class InlineContext(CancellationToken token) : IQueueJobContext
        {
            public CancellationToken CancellationToken => token;

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

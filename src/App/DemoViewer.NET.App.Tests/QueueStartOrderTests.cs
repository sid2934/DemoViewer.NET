#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The start order the queue reports in its snapshot, checked against what then starts, and the user's
///     promotion of a queued item to the top of its lane.
/// </summary>
public class QueueStartOrderTests
{
    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

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

    // Records each item as it starts. A parse records from the parse step, a job from its body.
    private sealed class Rig : IDisposable
    {
        private readonly List<string> _started = [];
        private readonly Dictionary<string, ManualResetEventSlim> _holds = new(StringComparer.Ordinal);

        public Rig()
        {
            Queue = new DemoProcessingQueue(Gate, a => a(), path =>
            {
                string name = Path.GetFileName(path);
                Note(name);
                ManualResetEventSlim? hold;
                lock (_holds)
                {
                    _holds.TryGetValue(name, out hold);
                }

                hold?.Wait(TimeSpan.FromSeconds(10));
                return SyntheticDemo();
            }, _ => SyntheticDemo(), () => Task.CompletedTask);
        }

        public HeavyJobGate Gate { get; } = new();
        public DemoProcessingQueue Queue { get; }

        public IReadOnlyList<string> Started
        {
            get
            {
                lock (_started)
                {
                    return [.. _started];
                }
            }
        }

        public void Dispose()
        {
            lock (_holds)
            {
                foreach (ManualResetEventSlim hold in _holds.Values)
                {
                    hold.Set();
                }
            }

            Queue.Dispose();
            Gate.Dispose();
        }

        public void Note(string name)
        {
            lock (_started)
            {
                _started.Add(name);
            }
        }

        // The parse of this file waits until Release.
        public void Hold(string name)
        {
            lock (_holds)
            {
                _holds[name] = new ManualResetEventSlim();
            }
        }

        public void Release(string name)
        {
            lock (_holds)
            {
                _holds[name].Set();
            }
        }

        public IDemoQueueHandle Parse(string name, long order, DemoJobPriority priority = DemoJobPriority.Background) =>
            Queue.SubmitBackground(new DemoProcessingRequest("/d/" + name, "library", priority, order, _ => { }, null, name));

        // A heavy job that runs until the returned source completes; it holds the heavy lane.
        public (IDemoQueueHandle Handle, TaskCompletionSource Release) Blocker(DemoJobPriority priority)
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IDemoQueueHandle handle = Queue.SubmitJob(new QueueJobRequest(QueueJobKind.SidecarMigration, "blocker", "test",
                priority, async job =>
                {
                    Note("blocker");
                    await release.Task.WaitAsync(job.CancellationToken);
                }));
            return (handle, release);
        }

        public Guid Id(string name) => Queue.Snapshot().Single(s => s.DisplayName == name).Id;

        // The queued heavy-lane items by the rank the queue reports.
        public List<string> Ranked() => Queue.Snapshot()
            .Where(s => s.State == DemoQueueItemState.Queued && !s.Light && s.Kind != QueueJobKind.HeapCompaction)
            .OrderBy(s => s.StartRank)
            .Select(s => s.DisplayName!)
            .ToList();

        public DemoQueueItemSnapshot Item(string name) => Queue.Snapshot().Single(s => s.DisplayName == name);
    }

    [Test]
    public async Task TheReportedStartOrder_IsTheOrderTheyStart_IncludingGroupingByDemo()
    {
        using Rig rig = new();
        (IDemoQueueHandle blocker, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");

        rig.Parse("a.dem", 3);
        rig.Parse("b.dem", 1);
        // A job about a.dem: kind rank puts it after every parse, but once a.dem starts it groups with it.
        rig.Queue.SubmitJob(new QueueJobRequest(QueueJobKind.SidecarMigration, "a-job", "test",
            DemoJobPriority.Background, _ =>
            {
                rig.Note("a-job");
                return Task.CompletedTask;
            }, Target: "/d/a.dem"));
        rig.Parse("c.dem", 0, DemoJobPriority.UserRequested);

        List<string> ranked = rig.Ranked();
        await Assert.That(string.Join(",", ranked)).IsEqualTo("c.dem,a.dem,a-job,b.dem");

        release.SetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => rig.Started.Count == 5 && rig.Queue.QueuedCount + rig.Queue.RunningCount == 0, "drain");

        await Assert.That(string.Join(",", rig.Started.Skip(1))).IsEqualTo(string.Join(",", ranked));
    }

    [Test]
    public async Task TheRank_FollowsAPriorityChange_WhileQueued()
    {
        using Rig rig = new();
        (_, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        rig.Parse("a.dem", 2);
        rig.Parse("b.dem", 1);
        await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("a.dem,b.dem");

        // A user's request for b.dem joins the queued item and lifts it.
        rig.Parse("b.dem", 1, DemoJobPriority.UserRequested);
        await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("b.dem,a.dem");
        release.SetResult();
    }

    [Test]
    public async Task APausedQueue_RanksWhatMayStartFirst_AndSaysWhatHoldsTheRest()
    {
        using Rig rig = new();
        (_, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        rig.Queue.Pause();
        rig.Parse("a.dem", 5);
        rig.Parse("u.dem", 1, DemoJobPriority.UserRequested);

        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("u.dem,a.dem");
            await Assert.That(rig.Item("a.dem").Hold).IsEqualTo(DemoQueueHold.Paused);
            await Assert.That(rig.Item("u.dem").Hold).IsEqualTo(DemoQueueHold.None);
        }

        rig.Queue.Resume();
        rig.Queue.BackgroundEnabled = false;
        await Assert.That(rig.Item("a.dem").Hold).IsEqualTo(DemoQueueHold.BackgroundOff);
        release.SetResult();
    }

    [Test]
    public async Task APromotedItem_StartsNext_WithoutStoppingTheRunningOne_AndTheNewestPromotionGoesFirst()
    {
        using Rig rig = new();
        // A Background job: a user's item would stop it, a promotion must not.
        (IDemoQueueHandle blocker, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.Background);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        rig.Hold("b.dem");
        rig.Parse("a.dem", 3);
        rig.Parse("b.dem", 2);
        rig.Parse("c.dem", 1);

        await Assert.That(rig.Queue.Promote(rig.Id("c.dem"))).IsTrue();
        await Assert.That(rig.Queue.Promote(rig.Id("b.dem"))).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("b.dem,c.dem,a.dem");
            await Assert.That(rig.Item("b.dem").Promoted).IsTrue();
            await Assert.That(rig.Item("c.dem").Promoted).IsTrue();
            await Assert.That(rig.Item("a.dem").Promoted).IsFalse();
        }

        await Task.Delay(50);
        using (Assert.Multiple())
        {
            await Assert.That(blocker.State).IsEqualTo(DemoQueueItemState.Running).Because("a promotion preempts nothing");
            await Assert.That(string.Join(",", rig.Started)).IsEqualTo("blocker");
        }

        release.SetResult();
        await WaitForAsync(() => rig.Item("b.dem").State == DemoQueueItemState.Running, "b.dem to start");
        using (Assert.Multiple())
        {
            await Assert.That(blocker.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(rig.Item("b.dem").Promoted).IsFalse().Because("a promotion ends when the item starts");
            await Assert.That(rig.Item("c.dem").Promoted).IsTrue();
            await Assert.That(rig.Queue.Promote(rig.Id("b.dem"))).IsFalse().Because("only a queued item is promoted");
        }

        rig.Release("b.dem");
        await WaitForAsync(() => rig.Started.Count == 4 && rig.Queue.QueuedCount + rig.Queue.RunningCount == 0, "drain");
        await Assert.That(string.Join(",", rig.Started)).IsEqualTo("blocker,b.dem,c.dem,a.dem");
    }

    [Test]
    public async Task AUserRequest_AfterAPromotion_DoesNotStopTheRunningItem()
    {
        using Rig rig = new();
        // A Background job: a user's item alone would stop it. With a promotion ahead it must not.
        (IDemoQueueHandle blocker, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.Background);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        rig.Parse("a.dem", 1);
        rig.Queue.Promote(rig.Id("a.dem"));
        rig.Parse("u.dem", 9, DemoJobPriority.UserRequested);

        await Task.Delay(50);
        await Assert.That(blocker.State).IsEqualTo(DemoQueueItemState.Running);
        release.SetResult();
        await WaitForAsync(() => rig.Started.Count == 3 && rig.Queue.QueuedCount + rig.Queue.RunningCount == 0, "drain");
        using (Assert.Multiple())
        {
            await Assert.That(blocker.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(string.Join(",", rig.Started)).IsEqualTo("blocker,a.dem,u.dem");
        }
    }

    [Test]
    public async Task Promote_RefusesALightItem()
    {
        using Rig rig = new();
        rig.Queue.Pause();
        IDemoQueueHandle save = rig.Queue.SubmitJob(new QueueJobRequest(QueueJobKind.StoreSave, "save", "store",
            DemoJobPriority.Background, _ => Task.CompletedTask));
        await Assert.That(rig.Queue.Promote(save.Id)).IsFalse();
        await Assert.That(rig.Item("save").Light).IsTrue();
        rig.Queue.Resume();
    }

    [Test]
    public async Task APromotion_SurvivesALaterUserRequest_ThatWouldOtherwiseOutrankIt()
    {
        using Rig rig = new();
        (_, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        rig.Parse("a.dem", 1);
        rig.Queue.Promote(rig.Id("a.dem"));
        rig.Parse("u.dem", 9, DemoJobPriority.UserRequested);
        await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("a.dem,u.dem");

        release.SetResult();
        await WaitForAsync(() => rig.Started.Count == 3 && rig.Queue.QueuedCount + rig.Queue.RunningCount == 0, "drain");
        await Assert.That(string.Join(",", rig.Started)).IsEqualTo("blocker,a.dem,u.dem");
    }

    [Test]
    public async Task APromotedBackgroundItem_StillWaitsForTheBackgroundSwitch_AndForResume()
    {
        using Rig rig = new();
        rig.Queue.BackgroundEnabled = false;
        rig.Parse("a.dem", 1);
        await Assert.That(rig.Queue.Promote(rig.Id("a.dem"))).IsTrue();
        rig.Parse("u.dem", 0, DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("u.dem") && rig.Queue.RunningCount == 0, "the user's parse");
        await Task.Delay(50);
        using (Assert.Multiple())
        {
            await Assert.That(rig.Item("a.dem").State).IsEqualTo(DemoQueueItemState.Queued);
            await Assert.That(rig.Item("a.dem").Promoted).IsTrue();
            await Assert.That(rig.Item("a.dem").Hold).IsEqualTo(DemoQueueHold.BackgroundOff);
        }

        rig.Queue.Pause();
        rig.Queue.BackgroundEnabled = true;
        await Task.Delay(50);
        await Assert.That(rig.Item("a.dem").State).IsEqualTo(DemoQueueItemState.Queued).Because("paused holds it too");
        await Assert.That(rig.Item("a.dem").Hold).IsEqualTo(DemoQueueHold.Paused);

        rig.Queue.Resume();
        await WaitForAsync(() => rig.Started.Contains("a.dem"), "a.dem after Resume");
    }

    [Test]
    public async Task ACancelledPromotedItem_IsDropped_AndTheOrderFallsBack()
    {
        using Rig rig = new();
        (_, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        rig.Parse("a.dem", 2);
        rig.Parse("b.dem", 1);
        Guid b = rig.Id("b.dem");
        rig.Queue.Promote(b);
        await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("b.dem,a.dem");

        rig.Queue.RemoveByUser(b);
        using (Assert.Multiple())
        {
            await Assert.That(rig.Item("b.dem").State).IsEqualTo(DemoQueueItemState.Cancelled);
            await Assert.That(rig.Item("b.dem").Promoted).IsFalse();
            await Assert.That(rig.Item("b.dem").StartRank).IsNull();
            await Assert.That(rig.Queue.Promote(b)).IsFalse();
            await Assert.That(string.Join(",", rig.Ranked())).IsEqualTo("a.dem");
        }

        release.SetResult();
        await WaitForAsync(() => rig.Started.Count == 2 && rig.Queue.QueuedCount + rig.Queue.RunningCount == 0, "drain");
        await Assert.That(string.Join(",", rig.Started)).IsEqualTo("blocker,a.dem");
    }

    [Test]
    public async Task Promote_RefusesAnUnknownOrRunningItem()
    {
        using Rig rig = new();
        (IDemoQueueHandle blocker, TaskCompletionSource release) = rig.Blocker(DemoJobPriority.UserRequested);
        await WaitForAsync(() => rig.Started.Contains("blocker"), "the blocker to start");
        using (Assert.Multiple())
        {
            await Assert.That(rig.Queue.Promote(Guid.NewGuid())).IsFalse();
            await Assert.That(rig.Queue.Promote(blocker.Id)).IsFalse();
        }

        release.SetResult();
    }
}

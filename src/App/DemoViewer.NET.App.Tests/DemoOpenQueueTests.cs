#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A user's demo open as a queue item: first in the list, ignores pause, stops background work that can
///     stop, waits for a parse that cannot, joins a parse of the same demo, and gives way to a newer open.
/// </summary>
public class DemoOpenQueueTests
{
    private static readonly byte[] Bytes = [1];

    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private sealed class Rig : IDisposable
    {
        public readonly HeavyJobGate Gate = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ParseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ParsedDemo FileDemo = SyntheticDemo();
        public int ByteParses;
        public readonly DemoProcessingQueue Queue;

        public Rig() =>
            Queue = new DemoProcessingQueue(Gate, a => a(), parseFile: _ =>
            {
                ParseStarted.TrySetResult();
                Release.Task.Wait();
                return FileDemo;
            }, parseBytes: _ =>
            {
                Interlocked.Increment(ref ByteParses);
                return SyntheticDemo();
            }, compactHeap: () => Task.CompletedTask);

        public DemoQueueItemSnapshot Open => Queue.Snapshot().First(s => s.Kind == QueueJobKind.DemoOpen);

        public void Dispose()
        {
            Release.TrySetResult();
            Queue.Dispose();
            Gate.Dispose();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    private static async Task<bool> EndsCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    [Test]
    public async Task AnOpen_GoesFirst_IgnoresPause_AndHoldsBackgroundWorkUntilItEnds()
    {
        using Rig rig = new();
        rig.Release.SetResult();
        int ran = 0;
        rig.Queue.Pause();
        rig.Queue.SubmitJob(new QueueJobRequest(QueueJobKind.StratMining, "mine", "test", DemoJobPriority.Background,
            _ =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            }));

        using IDemoOpenTicket open = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        ParsedDemo parsed = await open.ParseAsync(Bytes).WaitAsync(TimeSpan.FromSeconds(5));
        rig.Queue.Resume();
        await Task.Delay(100);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsNotNull();
            await Assert.That(rig.Queue.Snapshot()[0].Kind).IsEqualTo(QueueJobKind.DemoOpen);
            await Assert.That(rig.Open.DisplayName).IsEqualTo("Open demo: a.dem");
            await Assert.That(rig.Open.Priority).IsEqualTo(DemoJobPriority.Foreground);
            await Assert.That(ran).IsEqualTo(0);
        }

        open.Complete();
        await WaitForAsync(() => Volatile.Read(ref ran) == 1, "the mine to run after the open");
        await Assert.That(rig.Open.State).IsEqualTo(DemoQueueItemState.Completed);
    }

    [Test]
    public async Task AnOpen_StopsARunningBackgroundJob_WhichRunsAgainAfterIt()
    {
        using Rig rig = new();
        int starts = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDemoQueueHandle job = rig.Queue.SubmitJob(new QueueJobRequest(QueueJobKind.StratMining, "mine", "test",
            DemoJobPriority.Background, async ctx =>
            {
                if (Interlocked.Increment(ref starts) == 1)
                {
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
            }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IDemoOpenTicket open = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        await open.ParseAsync(Bytes).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Queued);
        open.Complete();

        await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.Multiple())
        {
            await Assert.That(starts).IsEqualTo(2);
            await Assert.That(job.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(rig.ByteParses).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AnOpen_WaitsForARetainedParse_AndSaysWhichFile()
    {
        using Rig rig = new();
        rig.Queue.SubmitBackground(new DemoProcessingRequest("/d/b.dem", "library", DemoJobPriority.Background, 0,
            _ => { }, DisplayName: "b.dem"));
        await rig.ParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using IDemoOpenTicket open = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        Task<ParsedDemo> parse = open.ParseAsync(Bytes);
        await WaitForAsync(() => rig.Open.Detail == "Waiting for b.dem to finish parsing", "the waiting detail");

        using (Assert.Multiple())
        {
            await Assert.That(rig.Open.State).IsEqualTo(DemoQueueItemState.Queued);
            await Assert.That(rig.Queue.Snapshot()[0].Kind).IsEqualTo(QueueJobKind.DemoOpen);
            await Assert.That(parse.IsCompleted).IsFalse();
            await Assert.That(rig.ByteParses).IsEqualTo(0);
        }

        rig.Release.SetResult();
        ParsedDemo parsed = await parse.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsNotSameReferenceAs(rig.FileDemo);
            await Assert.That(rig.ByteParses).IsEqualTo(1);
            await Assert.That(rig.Open.State).IsEqualTo(DemoQueueItemState.Running);
        }
    }

    [Test]
    public async Task AnOpen_JoinsARunningParseOfTheSameDemo()
    {
        using Rig rig = new();
        rig.Queue.SubmitBackground(new DemoProcessingRequest("/d/a.dem", "library", DemoJobPriority.Background, 0,
            _ => { }, DisplayName: "a.dem"));
        await rig.ParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using IDemoOpenTicket open = rig.Queue.BeginOpen("/D/A.dem", "a.dem");
        Task<ParsedDemo> parse = open.ParseAsync(Bytes);
        await Assert.That(rig.Open.State).IsEqualTo(DemoQueueItemState.Running);
        rig.Release.SetResult();

        ParsedDemo parsed = await parse.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsSameReferenceAs(rig.FileDemo);
            await Assert.That(rig.ByteParses).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AnOpen_DoesNotJoinAParseWithoutUserCommands()
    {
        using Rig rig = new();
        rig.Queue.SubmitBackground(new DemoProcessingRequest("/d/a.dem", "library", DemoJobPriority.Background, 0,
            _ => { }, DisplayName: "a.dem", NeedsUserCommands: false));
        await rig.ParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using IDemoOpenTicket open = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        Task<ParsedDemo> parse = open.ParseAsync(Bytes);
        await WaitForAsync(() => rig.Open.Detail == "Waiting for a.dem to finish parsing", "the waiting detail");
        rig.Release.SetResult();

        ParsedDemo parsed = await parse.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsNotSameReferenceAs(rig.FileDemo);
            await Assert.That(rig.ByteParses).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ASecondOpen_ReplacesAWaitingOne()
    {
        using Rig rig = new();
        rig.Queue.SubmitBackground(new DemoProcessingRequest("/d/b.dem", "library", DemoJobPriority.Background, 0,
            _ => { }, DisplayName: "b.dem"));
        await rig.ParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using IDemoOpenTicket first = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        Task<ParsedDemo> firstParse = first.ParseAsync(Bytes);
        using IDemoOpenTicket second = rig.Queue.BeginOpen("/d/c.dem", "c.dem");

        using (Assert.Multiple())
        {
            await Assert.That(await EndsCancelledAsync(firstParse)).IsTrue();
            await Assert.That(first.IsSuperseded).IsTrue();
            await Assert.That(rig.Queue.Snapshot()[0].DisplayName).IsEqualTo("Open demo: c.dem");
            await Assert.That(rig.Queue.Snapshot().Single(s => s.DisplayName == "Open demo: a.dem").State)
                .IsEqualTo(DemoQueueItemState.Cancelled);
        }

        rig.Release.SetResult();
        await second.ParseAsync(Bytes).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(rig.ByteParses).IsEqualTo(1);
    }

    [Test]
    public async Task ASecondOpen_CancelsARunningOne_WhichEndsCancelled()
    {
        using Rig rig = new();
        rig.Release.SetResult();
        IDemoOpenTicket first = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        await first.ParseAsync(Bytes).WaitAsync(TimeSpan.FromSeconds(5));
        first.Report(0.6, "Analysing");

        using IDemoOpenTicket second = rig.Queue.BeginOpen("/d/c.dem", "c.dem");
        using (Assert.Multiple())
        {
            await Assert.That(first.CancellationToken.IsCancellationRequested).IsTrue();
            await Assert.That(first.IsSuperseded).IsTrue();
        }

        first.Complete();
        DemoQueueItemSnapshot ended = rig.Queue.Snapshot().Single(s => s.DisplayName == "Open demo: a.dem");
        await Assert.That(ended.State).IsEqualTo(DemoQueueItemState.Cancelled);
    }

    [Test]
    public async Task RemovingAnOpen_CancelsItsWait()
    {
        using Rig rig = new();
        rig.Queue.SubmitBackground(new DemoProcessingRequest("/d/b.dem", "library", DemoJobPriority.Background, 0,
            _ => { }, DisplayName: "b.dem"));
        await rig.ParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using IDemoOpenTicket open = rig.Queue.BeginOpen("/d/a.dem", "a.dem");
        Task<ParsedDemo> parse = open.ParseAsync(Bytes);
        rig.Queue.RemoveByUser(rig.Open.Id);

        using (Assert.Multiple())
        {
            await Assert.That(await EndsCancelledAsync(parse)).IsTrue();
            await Assert.That(open.IsSuperseded).IsFalse();
            await Assert.That(rig.Open.State).IsEqualTo(DemoQueueItemState.Cancelled);
        }
    }
}

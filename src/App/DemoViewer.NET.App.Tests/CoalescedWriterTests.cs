#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

public class CoalescedWriterTests
{
    [Test]
    public async Task AWriteThatThrowsAnything_IsReported_AndLaterSavesStillWrite()
    {
        List<string> written = [];
        List<Exception> failures = [];
        CoalescedWriter<string> writer = new(s =>
        {
            if (s == "bad")
            {
                throw new InvalidOperationException("not an IO error");
            }

            written.Add(s);
        }, failures.Add);

        writer.Post("bad");
        writer.Post("good");
        using (Assert.Multiple())
        {
            await Assert.That(failures.Count).IsEqualTo(1);
            await Assert.That(written).IsEquivalentTo(["good"]);
            await Assert.That(writer.Flush()).IsTrue();
        }
    }

    [Test]
    public async Task ADrainThatNeverRuns_DoesNotStopTheNextPost_AndFlushWritesTheNewest()
    {
        List<string> written = [];
        Queue<Action> scheduled = [];
        bool drop = true;
        CoalescedWriter<string> writer = new(written.Add, _ => { }, drain =>
        {
            if (drop)
            {
                // Refused or cancelled before it ran: completes without draining.
                return Task.CompletedTask;
            }

            scheduled.Enqueue(drain);
            return Task.CompletedTask;
        });

        writer.Post("a");
        drop = false;
        writer.Post("b");
        int queued = scheduled.Count;
        writer.Post("c");
        bool pendingBeforeFlush = writer.HasPending;
        bool flushed = writer.Flush();
        while (scheduled.Count > 0)
        {
            scheduled.Dequeue()();
        }

        using (Assert.Multiple())
        {
            await Assert.That(queued).IsEqualTo(1).Because("the first drain was dropped, so the next Post schedules again");
            await Assert.That(pendingBeforeFlush).IsTrue();
            await Assert.That(flushed).IsTrue();
            await Assert.That(written).IsEquivalentTo(["c"]).Because("the newest snapshot wins and nothing is written twice");
        }
    }

    [Test]
    public async Task Flush_GivesUpAfterItsTimeout_WhileAWriteIsStuck_AndNeverThrows()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        CoalescedWriter<string> writer = new(_ =>
        {
            entered.Set();
            release.Wait();
        }, _ => { }, drain => Task.Run(drain));

        writer.Post("slow");
        entered.Wait(TimeSpan.FromSeconds(5));
        writer.Post("next");
        bool flushed = writer.Flush(TimeSpan.FromMilliseconds(50));
        release.Set();
        await Assert.That(flushed).IsFalse();
        await Assert.That(writer.Flush(TimeSpan.FromSeconds(5))).IsTrue();
    }
}

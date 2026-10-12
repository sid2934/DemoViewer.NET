#region

using System.Net;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="HttpExtensionFeedClient" /> over a fake handler: a body that keeps arriving completes however
///     long it takes, since only the headers have a whole-call budget; a body that stops arriving is a stalled
///     transfer within the stall budget; a cap is a cap. No request leaves the process.
/// </summary>
public class HttpExtensionFeedClientTests
{
    private static readonly Uri Url = new("https://example.invalid/pack.zip");

    [Test]
    public async Task Download_ThatKeepsArriving_CompletesPastTheHeaderBudget()
    {
        // Each chunk moves the manual clock 50 ms after its read is pending: no single read nears the 400 ms stall
        // budget, but the 600 ms of body passes the header budget and the stall budget taken as one deadline.
        ManualTimers clock = new();
        DateTimeOffset started = clock.GetUtcNow();
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 12, chunk, pace: TimeSpan.FromMilliseconds(1), stallAfter: null,
            onChunk: () => clock.Advance(TimeSpan.FromMilliseconds(50)));
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), clock);
        using MemoryStream destination = new();
        List<long> progress = [];

        await client.DownloadAsync(Url, destination, 12 * 1024, new InlineProgress(progress), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.Multiple())
        {
            await Assert.That(destination.Length).IsEqualTo(12 * 1024);
            await Assert.That(progress[^1]).IsEqualTo(12 * 1024);
            await Assert.That(clock.GetUtcNow() - started).IsEqualTo(TimeSpan.FromMilliseconds(600));
        }
    }

    [Test]
    public async Task Download_ThatStopsArriving_IsAStalledTransfer_NotACancellation()
    {
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 20, chunk, pace: TimeSpan.FromMilliseconds(10), stallAfter: 3);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300));
        using MemoryStream destination = new();

        ExtensionDownloadException? ex = await Assert.ThrowsAsync<ExtensionDownloadException>(
            () => client.DownloadAsync(Url, destination, 20 * 1024, null, CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(ex!.Message).IsEqualTo("the transfer stalled");
            await Assert.That(destination.Length).IsEqualTo(3 * 1024);
        }
    }

    [Test]
    public async Task Download_RefusesMoreBytesThanTheFeedSaid_AndSlowHeaders()
    {
        byte[] chunk = new byte[1024];
        using PacedHandler over = new(chunks: 4, chunk, pace: TimeSpan.Zero, stallAfter: null);
        using HttpExtensionFeedClient client = new(over, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using MemoryStream destination = new();
        ExtensionDownloadException? tooMany = await Assert.ThrowsAsync<ExtensionDownloadException>(
            () => client.DownloadAsync(Url, destination, 2 * 1024, null, CancellationToken.None));

        using PacedHandler slow = new(chunks: 1, chunk, pace: TimeSpan.Zero, stallAfter: null, headerDelay: TimeSpan.FromSeconds(2));
        using HttpExtensionFeedClient impatient = new(slow, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        ExtensionDownloadException? noHeaders = await Assert.ThrowsAsync<ExtensionDownloadException>(
            () => impatient.GetFeedAsync(Url, CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(tooMany!.Message).Contains("larger than the 2048 bytes");
            await Assert.That(noHeaders!.Message).IsEqualTo("the server did not answer in time");
        }
    }

    [Test]
    public async Task Download_CancelledByTheCaller_IsACancellation()
    {
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 100, chunk, pace: TimeSpan.FromMilliseconds(20), stallAfter: null);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using MemoryStream destination = new();
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(120));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.DownloadAsync(Url, destination, 100 * 1024, null, cts.Token));
        await Assert.That(destination.Length).IsLessThan(100 * 1024);
    }

    // Serves chunks at a fixed pace from a stream that waits between them, after an optional header delay;
    // with stallAfter set, the read after that many chunks never returns. onChunk runs after the pace wait, so
    // the caller's read is already pending when it does.
    private sealed class PacedHandler(int chunks, byte[] chunk, TimeSpan pace, int? stallAfter, TimeSpan? headerDelay = null,
        Action? onChunk = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (headerDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PacedStream(chunks, chunk, pace, stallAfter, onChunk))
            };
        }
    }

    private sealed class PacedStream(int chunks, byte[] chunk, TimeSpan pace, int? stallAfter, Action? onChunk) : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= chunks)
            {
                return 0;
            }

            if (stallAfter is { } stall && _served >= stall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (pace > TimeSpan.Zero)
            {
                await Task.Delay(pace, cancellationToken);
            }

            onChunk?.Invoke();
            int n = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            _served++;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Timers fire only when the test advances the clock, on the advancing thread.
    private sealed class ManualTimers : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.DueAt <= _now)];
                foreach (Timer t in due)
                {
                    _timers.Remove(t);
                }
            }

            foreach (Timer t in due)
            {
                t.Fire();
            }
        }

        private sealed class Timer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new NotSupportedException("a periodic timer");
                }

                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueAt = owner._now + dueTime;
                        owner._timers.Add(this);
                    }
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class InlineProgress(List<long> sink) : IProgress<long>
    {
        public void Report(long value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}

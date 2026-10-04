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
        // 12 chunks, 50 ms apart: 600 ms of body against a 100 ms header budget and a 400 ms stall budget.
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 12, chunk, pace: TimeSpan.FromMilliseconds(50), stallAfter: null);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400));
        using MemoryStream destination = new();
        List<long> progress = [];

        await client.DownloadAsync(Url, destination, 12 * 1024, new InlineProgress(progress), CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(destination.Length).IsEqualTo(12 * 1024);
            await Assert.That(progress[^1]).IsEqualTo(12 * 1024);
            await Assert.That(handler.Elapsed).IsGreaterThan(TimeSpan.FromMilliseconds(100)).Because("the body outlived the header budget");
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
    // with stallAfter set, the read after that many chunks never returns.
    private sealed class PacedHandler(int chunks, byte[] chunk, TimeSpan pace, int? stallAfter, TimeSpan? headerDelay = null) : HttpMessageHandler
    {
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

        public TimeSpan Elapsed => DateTimeOffset.UtcNow - _started;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (headerDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PacedStream(chunks, chunk, pace, stallAfter))
            };
        }
    }

    private sealed class PacedStream(int chunks, byte[] chunk, TimeSpan pace, int? stallAfter) : Stream
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

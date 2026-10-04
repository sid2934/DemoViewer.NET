#region

using System.Buffers;
using System.Net.Http.Headers;
using System.Text;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     <see cref="IExtensionFeedClient" /> over one shared <see cref="HttpClient" />. The feed is a small JSON
///     asset; the zip is streamed to the caller's file with a per-read stall timeout, since the client's own
///     timeout covers only the headers once the body is read as a stream.
/// </summary>
public sealed class HttpExtensionFeedClient : IExtensionFeedClient
{
    private static readonly TimeSpan _readTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient _http = CreateClient();

    /// <summary>The process-wide instance.</summary>
    public static HttpExtensionFeedClient Shared { get; } = new();

    /// <inheritdoc />
    public async Task<string> GetFeedAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] buffer = ArrayPool<char>.Shared.Rent(16 * 1024);
        try
        {
            StringBuilder text = new();
            while (true)
            {
                int read = await ReadWithStallTimeout(() => reader.ReadAsync(buffer, ct).AsTask(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return text.ToString();
                }

                if (text.Length + read > ExtensionFeed.MaxLength)
                {
                    throw new ExtensionDownloadException("the feed is too large");
                }

                text.Append(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(destination);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new ExtensionDownloadException($"the server offers {declared} bytes; the feed said {maxBytes}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                int read = await ReadWithStallTimeout(() => stream.ReadAsync(buffer, ct).AsTask(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new ExtensionDownloadException($"the download is larger than the {maxBytes} bytes the feed said");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A read that makes no progress for _readTimeout is a stalled transfer, not a cancellation.
    private static async Task<int> ReadWithStallTimeout(Func<Task<int>> read, CancellationToken ct)
    {
        using CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<int> pending = read();
        Task finished = await Task.WhenAny(pending, Task.Delay(_readTimeout, delay.Token)).ConfigureAwait(false);
        if (finished != pending)
        {
            ct.ThrowIfCancellationRequested();
            throw new ExtensionDownloadException("the transfer stalled");
        }

        await delay.CancelAsync().ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // GitHub rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DemoViewer.NET", "1.0"));
        return client;
    }
}

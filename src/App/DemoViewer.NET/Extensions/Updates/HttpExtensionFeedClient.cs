#region

using System.Buffers;
using System.Net.Http.Headers;
using System.Text;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     <see cref="IExtensionFeedClient" /> over one <see cref="HttpClient" />. The client's own timeout is
///     off: it would span the whole transfer, body included, and abort a slow download that is making
///     progress. The headers get <see cref="HeaderTimeout" />; the body is governed by a per-read stall
///     timeout (<see cref="StallTimeout" />) and the caller's byte cap.
/// </summary>
public sealed class HttpExtensionFeedClient : IExtensionFeedClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _stallTimeout;

    /// <summary>The process-wide instance over the shared handler and the default budgets.</summary>
    public static HttpExtensionFeedClient Shared { get; } = new();

    /// <summary>How long the response headers may take; 30 s by default.</summary>
    public static TimeSpan HeaderTimeout => TimeSpan.FromSeconds(30);

    /// <summary>How long one body read may make no progress before the transfer counts as stalled; 60 s by default.</summary>
    public static TimeSpan StallTimeout => TimeSpan.FromSeconds(60);

    /// <summary>The default budgets over the default handler.</summary>
    public HttpExtensionFeedClient() : this(null, null, null)
    {
    }

    /// <param name="handler">The message handler, or null for the default; a test hands in a fake.</param>
    /// <param name="headerTimeout">Overrides <see cref="HeaderTimeout" />.</param>
    /// <param name="stallTimeout">Overrides <see cref="StallTimeout" />.</param>
    public HttpExtensionFeedClient(HttpMessageHandler? handler, TimeSpan? headerTimeout = null, TimeSpan? stallTimeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        // GitHub rejects requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DemoViewer.NET", "1.0"));
        _headerTimeout = headerTimeout ?? HeaderTimeout;
        _stallTimeout = stallTimeout ?? StallTimeout;
    }

    /// <inheritdoc />
    public async Task<string> GetFeedAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        using HttpResponseMessage response = await SendForHeaders(url, ct).ConfigureAwait(false);
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
        using HttpResponseMessage response = await SendForHeaders(url, ct).ConfigureAwait(false);
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

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    // The header budget applies to this call alone; the body stream it returns is read under the stall budget.
    private async Task<HttpResponseMessage> SendForHeaders(Uri url, CancellationToken ct)
    {
        using CancellationTokenSource headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(_headerTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ExtensionDownloadException("the server did not answer in time");
        }

        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // A read that makes no progress for the stall budget is a stalled transfer, not a cancellation.
    private async Task<int> ReadWithStallTimeout(Func<Task<int>> read, CancellationToken ct)
    {
        using CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<int> pending = read();
        Task finished = await Task.WhenAny(pending, Task.Delay(_stallTimeout, delay.Token)).ConfigureAwait(false);
        if (finished != pending)
        {
            ct.ThrowIfCancellationRequested();
            throw new ExtensionDownloadException("the transfer stalled");
        }

        await delay.CancelAsync().ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }
}

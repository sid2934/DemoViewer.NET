namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     The network half of <see cref="ExtensionUpdateService" />: fetches a feed and downloads a zip.
///     <see cref="HttpExtensionFeedClient" /> in production; a fake in tests, which never reach the network.
///     Both methods throw on failure (<see cref="HttpRequestException" />, <see cref="IOException" />,
///     <see cref="ExtensionDownloadException" />, <see cref="OperationCanceledException" />); the service turns
///     every one into a state.
/// </summary>
public interface IExtensionFeedClient
{
    /// <summary>The feed text at <paramref name="url" />, at most <see cref="ExtensionFeed.MaxLength" /> characters.</summary>
    Task<string> GetFeedAsync(Uri url, CancellationToken ct);

    /// <summary>
    ///     Copies the body at <paramref name="url" /> into <paramref name="destination" />, reporting the bytes
    ///     written so far. More than <paramref name="maxBytes" /> is an <see cref="ExtensionDownloadException" />,
    ///     so a server that lies about a zip cannot fill the disk.
    /// </summary>
    Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>A download that did not match what the feed promised, or a transfer that stalled.</summary>
public sealed class ExtensionDownloadException : Exception
{
    public ExtensionDownloadException()
    {
    }

    public ExtensionDownloadException(string message) : base(message)
    {
    }

    public ExtensionDownloadException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

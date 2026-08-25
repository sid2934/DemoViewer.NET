#region

using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core.Export;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     A decorator that hashes every pre-encode RGBA frame and forwards it.
///     <para>
///         <b>This is what "deterministic export" is asserted on</b> (plan D13). <c>libvpx-vp9</c> and
///         <c>libx264</c> are not bit-reproducible across thread counts, builds or versions, so
///         comparing encoded files would test ffmpeg's determinism rather than the renderer's. The
///         contract is that two runs of the same request produce the same <b>pixels</b>; the encoder is
///         free to be its own kind of nondeterministic.
///     </para>
///     <para>
///         With a null inner sink it is also the cheapest way to run the whole pipeline with no
///         encoder — what the CPU-throughput measurement uses to isolate render cost from encode cost.
///     </para>
/// </summary>
public sealed class HashingFrameSink : IFrameSink
{
    private readonly List<string> _hashes = [];
    private readonly IFrameSink? _inner;
    private readonly byte[] _digest = new byte[32];
    private bool _disposed;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The sink to forward to, or null to hash and discard.</param>
    public HashingFrameSink(IFrameSink? inner = null) => _inner = inner;

    /// <summary>One lower-case hex SHA-256 per frame, in write order.</summary>
    public IReadOnlyList<string> FrameHashes => _hashes;

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SHA256.HashData(rgba.Span, _digest);
        _hashes.Add(Convert.ToHexString(_digest).ToLowerInvariant());

        if (_inner is not null)
        {
            await _inner.WriteAsync(rgba, width, height, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     Where rendered frames go: design §5.7, verbatim.
///     <para>
///         One implementation pipes raw RGBA to an ffmpeg subprocess (<c>FfmpegFrameSink</c>), one
///         accumulates an animated GIF with no ffmpeg at all (<c>ManagedGifSink</c>), and one hashes and
///         forwards (<c>HashingFrameSink</c>, the determinism harness). All three live in Pipeline, which
///         is why this seam is the whole contract Core knows about encoding.
///     </para>
///     <para>
///         <b>The buffer is borrowed.</b> <see cref="WriteAsync" />'s span is pooled and reused by the
///         session on the very next frame: a sink that needs the bytes past the returned task must copy
///         them. <see cref="IAsyncDisposable.DisposeAsync" /> is where a sink finalises its output, so it
///         must be awaited, and it is called exactly once, including on cancellation.
///     </para>
/// </summary>
public interface IFrameSink : IAsyncDisposable
{
    /// <summary>Accepts one rendered frame.</summary>
    /// <param name="rgba">Row-major RGBA8888, <c>width * height * 4</c> bytes. Valid only until the task completes.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="ct">Cancels the write; the sink is still disposed afterwards.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
}

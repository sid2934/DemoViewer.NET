#region

using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Channels;
using FFMpegCore.Pipes;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     The push→pull bridge between <c>IFrameSink.WriteAsync</c> and FFMpegCore's
///     <c>RawVideoPipeSource</c>, which is a <b>pull</b> source: it takes an
///     <see cref="IEnumerable{T}" /> of frames and drains it on its own pump task.
///     <para>
///         A bounded <see cref="Channel{T}" /> (capacity 4, single reader, single writer, waiting writes)
///         is what makes the two shapes meet <b>and</b> supplies the backpressure the export loop needs:
///         the renderer cannot outrun the encoder by more than four frames, so a 1080p export's peak
///         memory is four staging buffers rather than however many frames the encoder is behind.
///     </para>
///     <para>
///         The block in <see cref="MoveNext" /> is deliberate and is on FFMpegCore's own pump task, whose
///         entire job is to sit on this enumerator. It is never the UI thread and never the render loop.
///     </para>
///     <para>
///         Buffers are rented from <see cref="ArrayPool{T}" /> by the writer and returned by the reader
///         once <c>Serialize</c> has copied them into the pipe, so a steady-state export allocates
///         nothing here (design §6).
///     </para>
/// </summary>
internal sealed class ChannelVideoFrameSource : IEnumerable<IVideoFrame>, IEnumerator<IVideoFrame>
{
    /// <summary>Frames in flight between the renderer and ffmpeg. Four is a frame of slack, not a queue.</summary>
    public const int DefaultCapacity = 4;

    private readonly Channel<PooledRgbaFrame> _channel;
    private readonly ConcurrentBag<PooledRgbaFrame> _spare = [];
    private PooledRgbaFrame? _current;
    private int _enumerated;

    /// <summary>Creates the bridge.</summary>
    /// <param name="capacity">Frames the channel holds before a write waits.</param>
    public ChannelVideoFrameSource(int capacity = DefaultCapacity) =>
        _channel = Channel.CreateBounded<PooledRgbaFrame>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

    /// <summary>Frames the reader has pulled. Diagnostics and tests.</summary>
    public int FramesRead { get; private set; }

    /// <inheritdoc />
    public IEnumerator<IVideoFrame> GetEnumerator()
    {
        // RawVideoPipeSource enumerates exactly once; a second pass would silently produce an empty
        // stream, which is a much worse failure than saying so.
        if (Interlocked.Exchange(ref _enumerated, 1) == 1)
        {
            throw new InvalidOperationException("A ChannelVideoFrameSource can only be enumerated once.");
        }

        return this;
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public IVideoFrame Current => _current!;

    /// <inheritdoc />
    object IEnumerator.Current => Current;

    /// <inheritdoc />
    public bool MoveNext()
    {
        RecycleCurrent();

        // Sync-over-async on purpose: see the type comment. WaitToRead + TryRead rather than ReadAsync so
        // a completed-and-drained channel returns false instead of throwing.
        while (_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            if (!_channel.Reader.TryRead(out PooledRgbaFrame? frame))
            {
                continue;
            }

            _current = frame;
            FramesRead++;
            return true;
        }

        _current = null;
        return false;
    }

    /// <inheritdoc />
    public void Reset() => throw new NotSupportedException("A frame stream cannot be rewound.");

    /// <inheritdoc />
    public void Dispose()
    {
        RecycleCurrent();

        // Anything still queued when the pump stops early (a killed ffmpeg) belongs back in the pool.
        while (_channel.Reader.TryRead(out PooledRgbaFrame? frame))
        {
            Recycle(frame);
        }
    }

    /// <summary>Copies one frame in, waiting while the encoder is four frames behind.</summary>
    /// <param name="rgba">The borrowed staging buffer; copied before this returns.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="ct">Cancels the wait.</param>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        PooledRgbaFrame frame = Rent(rgba.Length);
        rgba.Span.CopyTo(frame.Buffer.AsSpan(0, rgba.Length));
        frame.Set(width, height, rgba.Length);

        try
        {
            await _channel.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        catch
        {
            // A refused write must not leak the rented array: a cancelled 1080p export would otherwise
            // drop 8 MB on the floor per frame in flight.
            Recycle(frame);
            throw;
        }
    }

    /// <summary>Signals end-of-stream. ffmpeg sees EOF on the pipe and exits normally.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Signals end-of-stream with a fault, so a waiting reader stops rather than hanging.</summary>
    /// <param name="error">The reason.</param>
    public void Fault(Exception error) => _channel.Writer.TryComplete(error);

    private void RecycleCurrent()
    {
        if (_current is null)
        {
            return;
        }

        Recycle(_current);
        _current = null;
    }

    private PooledRgbaFrame Rent(int length)
    {
        if (!_spare.TryTake(out PooledRgbaFrame? frame))
        {
            frame = new PooledRgbaFrame();
        }

        frame.EnsureCapacity(length);
        return frame;
    }

    private void Recycle(PooledRgbaFrame frame)
    {
        frame.ReleaseBuffer();
        _spare.Add(frame);
    }
}

/// <summary>
///     One RGBA frame on loan from <see cref="ArrayPool{T}" />. The object itself is recycled through a
///     small free list, so a long export allocates a handful of these during warm-up and none afterwards.
/// </summary>
internal sealed class PooledRgbaFrame : IVideoFrame
{
    private int _length;
    private bool _rented;

    /// <summary>The rented backing array. Only the first <c>Length</c> bytes are meaningful.</summary>
    public byte[] Buffer { get; private set; } = [];

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>rgba</c> is what <c>RawVideoPipeSource</c> turns into <c>-pix_fmt rgba</c>, and it is the
    ///     byte order <c>SKColorType.Rgba8888</c> hands back: the two must agree or every frame comes out
    ///     with red and blue swapped.
    /// </remarks>
    public string Format => "rgba";

    /// <inheritdoc />
    public void Serialize(Stream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        pipe.Write(Buffer, 0, _length);
    }

    /// <inheritdoc />
    public Task SerializeAsync(Stream pipe, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return pipe.WriteAsync(Buffer.AsMemory(0, _length), token).AsTask();
    }

    /// <summary>Grows the rented buffer if needed.</summary>
    /// <param name="length">Bytes this frame will carry.</param>
    public void EnsureCapacity(int length)
    {
        if (_rented && Buffer.Length >= length)
        {
            return;
        }

        ReleaseBuffer();
        Buffer = ArrayPool<byte>.Shared.Rent(length);
        _rented = true;
    }

    /// <summary>Stamps the frame's dimensions and payload length.</summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="length">Payload bytes.</param>
    public void Set(int width, int height, int length)
    {
        Width = width;
        Height = height;
        _length = length;
    }

    /// <summary>Returns the rented array to the pool. Idempotent.</summary>
    public void ReleaseBuffer()
    {
        if (!_rented)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(Buffer);
        Buffer = [];
        _rented = false;
        _length = 0;
    }
}

#region

using System.Globalization;
using ChannelClosedException = System.Threading.Channels.ChannelClosedException;
using DemoViewer.NET.Playback2D.Core.Export;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>How an <see cref="FfmpegFrameSink" /> is configured.</summary>
/// <param name="OutputPath">Where the encoded file goes. Overwritten.</param>
/// <param name="FormatId">One of <see cref="ExportFormats" />.</param>
/// <param name="Width">Frame width; must match what the session renders.</param>
/// <param name="Height">Frame height.</param>
/// <param name="Fps">Output frame rate.</param>
/// <param name="BinaryFolder">
///     The directory holding <c>ffmpeg</c>, from <see cref="Ffmpeg.FfmpegLocator" />. Null relies on
///     <c>PATH</c>. Passed <b>per invocation</b>, never through <c>GlobalFFOptions</c> — that is
///     process-global mutable state, and a CLI export and an in-app export must be able to disagree.
/// </param>
/// <param name="Crf">Constant-rate factor. 30 is a good VP9 default; 20 suits H.264.</param>
/// <param name="H264Preset">x264 speed preset. Ignored for the other formats.</param>
/// <param name="DeletePartialOnCancel">Remove a half-written file when the export is cancelled.</param>
/// <param name="Log">Optional line sink for ffmpeg's stderr.</param>
public sealed record FfmpegSinkOptions(
    string OutputPath,
    string FormatId,
    int Width,
    int Height,
    int Fps,
    string? BinaryFolder = null,
    int Crf = 30,
    string H264Preset = "medium",
    bool DeletePartialOnCancel = true,
    Action<string>? Log = null);

/// <summary>
///     Encodes rendered frames by piping raw RGBA to an <b>ffmpeg subprocess</b>.
///     <para>
///         FFMpegCore builds the argument list and owns the process; no ffmpeg code is linked into this
///         program, which is what keeps the licence posture clean (see <c>THIRD-PARTY-NOTICES.md</c> §e).
///         The transport is FFMpegCore's named pipe rather than literal stdin — same mechanism, same
///         separateness, and it is what the library supports.
///     </para>
///     <para>
///         <b>The process starts on the first frame</b>, not in the constructor: an export cancelled
///         before it renders anything then leaves no process and no file at all.
///     </para>
///     <para>
///         <b>Cancellation kills ffmpeg.</b> The first write links the caller's token into the sink's own
///         source, which is what <c>CancellableThrough</c> watches; tripping it terminates the process,
///         and <see cref="DisposeAsync" /> then removes the partial output.
///     </para>
/// </summary>
public sealed class FfmpegFrameSink : IFrameSink
{
    /// <summary>How long <see cref="DisposeAsync" /> waits for ffmpeg to drain before giving up.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many trailing stderr lines are kept to explain a failure.</summary>
    private const int StderrTailLines = 6;

    private readonly ChannelVideoFrameSource _frames = new();
    private readonly FfmpegSinkOptions _options;
    private readonly Queue<string> _stderrTail = new(StderrTailLines);
    private CancellationTokenSource? _kill;
    private bool _disposed;
    private Task<bool>? _encoder;

    /// <summary>Creates the sink. No process is started until the first frame arrives.</summary>
    /// <param name="options">Output path, format and encoder settings.</param>
    public FfmpegFrameSink(FfmpegSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Frames handed to the encoder so far.</summary>
    public int FramesWritten { get; private set; }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width != _options.Width || height != _options.Height)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"The sink was built for {_options.Width}x{_options.Height} but got {width}x{height}; a rawvideo stream carries no frame header, so a size change mid-stream is undecodable."));
        }

        // Queue the frame BEFORE starting ffmpeg: FFMpegCore builds the input arguments by pulling the
        // first frame off this enumerator (that is how it learns the pixel format and the size), so a
        // start with an empty channel would block the argument build until the next write.
        try
        {
            await _frames.WriteAsync(rgba, width, height, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (_encoder is not null)
        {
            // The stream was ended under us, which only EndFrameStream does, and only because the
            // encoder stopped. Surface why it stopped rather than the channel's own
            // "the channel has been closed", which names the symptom and not one cause.
            await AwaitEncoderAsync().ConfigureAwait(false);
            throw;
        }

        FramesWritten++;

        if (_encoder is null)
        {
            _kill = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _encoder = BuildProcessor(_kill.Token).ProcessAsynchronously(true, BuildOptions(_options));

            // The pump task is the channel's ONLY reader. If ffmpeg exits early — a bad output path, a
            // full disk, a codec the located build does not carry — nothing will ever drain the queue
            // again, and a writer parked on a full one would wait forever. That is risk R2's deadlock,
            // and it is not covered by DisposeAsync's timeout because disposal is never reached. Ending
            // the encoder therefore ends the frame stream, which turns that wait into the encoder's own
            // exception at the very next write.
            _ = _encoder.ContinueWith(EndFrameStream, _frames, CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }

        if (_encoder.IsFaulted)
        {
            // Surface an encoder that died (a bad codec name, a full disk) at the next write instead of
            // rendering another 1700 frames into a pipe nobody is reading.
            await AwaitEncoderAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Awaits the encoder, turning a failure into one that names the cause.
    ///     <para>
    ///         When ffmpeg refuses its output the pipe breaks before FFMpegCore observes the exit, so the
    ///         raw fault is <c>IOException: Pipe is broken</c> — true, and useless. ffmpeg has already
    ///         said what was wrong on stderr, which <see cref="_stderrTail" /> is holding; a failure that
    ///         reads "Error opening output …: No such file or directory" is one a user can act on.
    ///     </para>
    /// </summary>
    private async Task AwaitEncoderAsync()
    {
        try
        {
            await _encoder!.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string tail = StderrTail();
            throw tail.Length == 0
                ? new FfmpegEncodeException($"ffmpeg failed: {ex.Message}", ex)
                : new FfmpegEncodeException($"ffmpeg failed: {tail}", ex);
        }
    }

    private string StderrTail()
    {
        lock (_stderrTail)
        {
            // Newest first: the last thing ffmpeg said before it died is the thing that explains it, and
            // the lines before it are the banner.
            return _stderrTail.Count == 0 ? string.Empty : string.Join(" | ", _stderrTail.Reverse());
        }
    }

    private void OnStderr(string line)
    {
        _options.Log?.Invoke(line);

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > StderrTailLines)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    /// <summary>
    ///     Ends the frame stream when the encoder task ends, whatever ended it.
    ///     <para>
    ///         A cancelled encoder is left alone: the token that killed it is the caller's own, so the
    ///         writer is already observing it, and faulting here would race a clean
    ///         <see cref="OperationCanceledException" /> into a <see cref="ChannelClosedException" />.
    ///     </para>
    /// </summary>
    private static void EndFrameStream(Task encoder, object? state)
    {
        ChannelVideoFrameSource frames = (ChannelVideoFrameSource)state!;

        if (encoder.IsFaulted)
        {
            frames.Fault(encoder.Exception!.GetBaseException());
            return;
        }

        if (encoder.IsCompletedSuccessfully)
        {
            // Normally this is redundant — DisposeAsync completes the stream and only then awaits the
            // encoder. It matters when ffmpeg exits 0 on its own, before the render loop is finished.
            frames.Complete();
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

        bool cancelled = _kill?.IsCancellationRequested ?? false;
        _frames.Complete();

        if (_encoder is not null)
        {
            try
            {
                // A timeout rather than an unbounded await: R2's failure mode is a deadlock between the
                // pump and the writer, and a diagnosable "ffmpeg did not exit in 30 s" beats a hang.
                await _encoder.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (TimeoutException)
            {
                _options.Log?.Invoke("ffmpeg did not exit within 30 s; the output may be truncated.");
                cancelled = true;
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"ffmpeg failed: {ex.Message}");
                throw;
            }
            finally
            {
                _frames.Dispose();
                _kill?.Dispose();

                if (cancelled && _options.DeletePartialOnCancel)
                {
                    TryDeleteOutput();
                }
            }

            return;
        }

        _frames.Dispose();
        _kill?.Dispose();
    }

    /// <summary>
    ///     The exact ffmpeg argument line this sink would run, without starting anything. What the
    ///     argument tests assert against, and what a <c>--verbose</c> export logs.
    /// </summary>
    /// <param name="options">The settings to describe.</param>
    public static string DescribeArguments(FfmpegSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // One stub frame so RawVideoPipeSource can read the format and size it stamps into the input
        // arguments; it is never serialized because no process is started.
        StubFrame stub = new(options.Width, options.Height);
        return Build(options, new RawVideoPipeSource([stub]) { FrameRate = options.Fps }).Arguments;
    }

    private FFMpegArgumentProcessor BuildProcessor(CancellationToken ct) =>
        Build(_options, new RawVideoPipeSource(_frames) { FrameRate = _options.Fps })
            .CancellableThrough(ct)
            .NotifyOnError(OnStderr);

    private static FFMpegArgumentProcessor Build(FfmpegSinkOptions options, RawVideoPipeSource source) =>
        FFMpegArguments
            .FromPipeInput(source)
            .OutputToFile(options.OutputPath, true, arguments => Configure(arguments, options));

    private static void Configure(FFMpegArgumentOptions arguments, FfmpegSinkOptions options)
    {
        switch (options.FormatId)
        {
            case ExportFormats.Mp4:
                arguments
                    .WithVideoCodec("libx264")
                    .WithCustomArgument($"-preset {options.H264Preset}")
                    .WithConstantRateFactor(options.Crf)
                    .ForcePixelFormat("yuv420p")
                    .WithFastStart();
                break;

            case ExportFormats.Gif:
                // Plan D6: the standard SINGLE-input equivalent of the two-pass palettegen/paletteuse
                // recipe. A literal two-pass would need the input twice, and over a pipe that means
                // spilling a multi-gigabyte rawvideo temp file.
                arguments
                    .WithCustomArgument(GifFilter(options))
                    .Loop(0);
                break;

            default:
                arguments
                    .WithVideoCodec("libvpx-vp9")
                    .WithCustomArgument("-b:v 0")
                    .WithConstantRateFactor(options.Crf)
                    .ForcePixelFormat("yuv420p")
                    .WithCustomArgument("-row-mt 1");
                break;
        }

        // Every invocation, every format: this pipeline has no audio to carry, and letting ffmpeg guess
        // is how a container ends up with an empty audio stream.
        arguments.DisableChannel(Channel.Audio);
    }

    private static string GifFilter(FfmpegSinkOptions options)
    {
        string chain = string.Create(CultureInfo.InvariantCulture,
            $"[0:v]fps={options.Fps},scale={options.Width}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
        return "-filter_complex \"" + chain + "\"";
    }

    private static FFOptions BuildOptions(FfmpegSinkOptions options) =>
        string.IsNullOrEmpty(options.BinaryFolder)
            ? new FFOptions()
            : new FFOptions { BinaryFolder = options.BinaryFolder };

    private void TryDeleteOutput()
    {
        try
        {
            if (File.Exists(_options.OutputPath))
            {
                File.Delete(_options.OutputPath);
            }
        }
        catch (IOException ex)
        {
            _options.Log?.Invoke($"could not remove the partial export: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _options.Log?.Invoke($"could not remove the partial export: {ex.Message}");
        }
    }

    /// <summary>A frame that exists only to tell FFMpegCore the stream's shape. Never serialized.</summary>
    private sealed class StubFrame(int width, int height) : IVideoFrame
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public string Format => "rgba";

        public void Serialize(Stream pipe) =>
            throw new NotSupportedException("The argument-describing stub frame is never written.");

        public Task SerializeAsync(Stream pipe, CancellationToken token) =>
            throw new NotSupportedException("The argument-describing stub frame is never written.");
    }
}

/// <summary>
///     An ffmpeg subprocess failed. The message carries ffmpeg's own last words rather than the pipe
///     error that usually races ahead of them.
/// </summary>
public sealed class FfmpegEncodeException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing copy, normally ffmpeg's stderr tail.</param>
    /// <param name="inner">The raw failure — usually the broken pipe, not the reason for it.</param>
    public FfmpegEncodeException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FfmpegEncodeException() : base("The ffmpeg subprocess failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing copy.</param>
    public FfmpegEncodeException(string message) : base(message)
    {
    }
}

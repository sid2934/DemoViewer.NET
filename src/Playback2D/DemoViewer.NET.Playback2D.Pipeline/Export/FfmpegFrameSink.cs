#region

using System.Globalization;
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

    private readonly ChannelVideoFrameSource _frames = new();
    private readonly FfmpegSinkOptions _options;
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
        await _frames.WriteAsync(rgba, width, height, ct).ConfigureAwait(false);
        FramesWritten++;

        if (_encoder is null)
        {
            _kill = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _encoder = BuildProcessor(_kill.Token).ProcessAsynchronously(true, BuildOptions(_options));
        }

        if (_encoder.IsFaulted)
        {
            // Surface an encoder that died (a bad codec name, a full disk) at the next write instead of
            // rendering another 1700 frames into a pipe nobody is reading.
            await _encoder.ConfigureAwait(false);
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
            .NotifyOnError(line => _options.Log?.Invoke(line));

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

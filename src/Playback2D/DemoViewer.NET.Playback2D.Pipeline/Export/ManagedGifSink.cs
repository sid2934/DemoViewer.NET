#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Export;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     The bottom rung of the encoder ladder: an animated GIF written with <b>no ffmpeg at all</b>.
///     <para>
///         It exists so "export" is never a dead end. A machine with no ffmpeg on <c>PATH</c>, on a
///         platform with no pinned download, whose user declined the download anyway, can still produce a
///         shareable file — and the dialog can say so honestly instead of greying everything out.
///     </para>
///     <para>
///         <b>It buffers every frame in memory</b>, which is why the caps exist (plan D7): a GIF is a
///         palette-per-frame format and a global palette needs the whole animation before it can be
///         chosen. <see cref="SceneExportSession.Validate" /> enforces the same ceilings on the request,
///         so a user is refused before rendering rather than after.
///     </para>
///     <para>
///         <b>Frame delays are integer centiseconds</b>, so only frame rates dividing 100 are exact.
///         The constructor refuses anything else rather than silently exporting a 30 fps request at
///         33.3 fps.
///     </para>
/// </summary>
public sealed class ManagedGifSink : IFrameSink
{
    private readonly int _frameDelayCentiseconds;
    private readonly int _maxFrames;
    private readonly string _outputPath;
    private bool _cancelled;
    private bool _disposed;
    private Image<Rgba32>? _image;

    /// <summary>Creates the sink.</summary>
    /// <param name="outputPath">Where the GIF goes. Overwritten on success.</param>
    /// <param name="fps">Frame rate; must divide 100 exactly (10, 20, 25 or 50).</param>
    /// <param name="maxFrames">Frame ceiling before the sink refuses.</param>
    /// <exception cref="ExportValidationException">The frame rate cannot be expressed, or the cap is nonsense.</exception>
    public ManagedGifSink(string outputPath, int fps, int maxFrames = SceneExportSession.GifMaxFrames)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (fps <= 0 || 100 % fps != 0)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"A GIF frame delay is a whole number of centiseconds, so {fps} fps cannot be expressed " +
                $"exactly. Use {string.Join(", ", SceneExportSession.SupportedFps(ExportFormats.Gif))}."));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);

        _outputPath = outputPath;
        _maxFrames = maxFrames;
        _frameDelayCentiseconds = 100 / fps;
    }

    /// <summary>Frames accumulated so far.</summary>
    public int FramesWritten { get; private set; }

    /// <summary>The centisecond delay stamped on every frame. Test hook for the D7 arithmetic.</summary>
    public int FrameDelayCentiseconds => _frameDelayCentiseconds;

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ct.IsCancellationRequested)
        {
            _cancelled = true;
            ct.ThrowIfCancellationRequested();
        }

        if (FramesWritten >= _maxFrames)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"A GIF is capped at {_maxFrames} frames. Shorten the range or export WebM."));
        }

        Image<Rgba32> frame = Image.LoadPixelData<Rgba32>(rgba.Span, width, height);

        if (_image is null)
        {
            _image = frame;
            GifMetadata metadata = _image.Metadata.GetGifMetadata();
            metadata.RepeatCount = 0; // loop forever
            Stamp(_image.Frames.RootFrame.Metadata);
        }
        else
        {
            if (frame.Width != _image.Width || frame.Height != _image.Height)
            {
                frame.Dispose();
                throw new ExportValidationException(
                    "Every GIF frame must be the same size; the render changed size mid-export.");
            }

            using (frame)
            {
                Stamp(_image.Frames.AddFrame(frame.Frames.RootFrame).Metadata);
            }
        }

        FramesWritten++;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        try
        {
            if (_image is null || _cancelled || FramesWritten == 0)
            {
                return ValueTask.CompletedTask;
            }

            // A global palette: one Wu-quantized table for the whole animation. Per-frame palettes would
            // be sharper but roughly triple the file, and a shareable size is the point of this rung.
            GifEncoder encoder = new()
            {
                Quantizer = new WuQuantizer(),
                ColorTableMode = GifColorTableMode.Global
            };

            _image.Save(_outputPath, encoder);
        }
        finally
        {
            _image?.Dispose();
            _image = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Marks the export cancelled so <see cref="DisposeAsync" /> writes nothing.</summary>
    public void Cancel() => _cancelled = true;

    private void Stamp(SixLabors.ImageSharp.Metadata.ImageFrameMetadata metadata) =>
        metadata.GetGifMetadata().FrameDelay = _frameDelayCentiseconds;
}

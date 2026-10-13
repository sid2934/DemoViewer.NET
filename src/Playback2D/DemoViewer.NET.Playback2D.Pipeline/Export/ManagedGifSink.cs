#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export.Gif;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     The bottom rung of the encoder ladder: an animated GIF written with <b>no ffmpeg at all</b>.
///     <para>
///         It exists so "export" is never a dead end. A machine with no ffmpeg on <c>PATH</c>, on a
///         platform with no pinned download, whose user declined the download anyway, can still produce a
///         shareable file, and the dialog can say so honestly instead of greying everything out.
///     </para>
///     <para>
///         <b>It buffers every frame in memory</b> (as RGB; alpha is dropped, export frames are
///         opaque), which is why the caps exist: the one global palette needs the whole animation
///         before any frame can be mapped to it. <see cref="SceneExportSession.Validate" /> enforces the same ceilings on the request,
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
    private readonly int _maxFrames;
    private readonly string _outputPath;
    private bool _cancelled;
    private bool _disposed;
    private readonly List<byte[]> _frames = [];
    private WuColorQuantizer? _quantizer;
    private int _width;
    private int _height;

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

        // Same reason as FfmpegFrameSink: the file is only opened at Dispose, and a missing directory
        // found then would discard every frame accumulated in memory.
        _outputPath = ExportOutputPath.EnsureDirectory(outputPath);
        _maxFrames = maxFrames;
        FrameDelayCentiseconds = 100 / fps;
    }

    /// <summary>Frames accumulated so far.</summary>
    public int FramesWritten { get; private set; }

    /// <summary>The centisecond delay stamped on every frame. Test hook for the delay arithmetic.</summary>
    public int FrameDelayCentiseconds { get; }

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

        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("The frame is smaller than width * height * 4 bytes.", nameof(rgba));
        }

        if (_quantizer is null)
        {
            _quantizer = new WuColorQuantizer();
            _width = width;
            _height = height;
        }
        else if (width != _width || height != _height)
        {
            throw new ExportValidationException(
                "Every GIF frame must be the same size; the render changed size mid-export.");
        }

        byte[] rgb = new byte[width * height * 3];
        ReadOnlySpan<byte> source = rgba.Span;
        for (int s = 0, d = 0; d < rgb.Length; s += 4, d += 3)
        {
            rgb[d] = source[s];
            rgb[d + 1] = source[s + 1];
            rgb[d + 2] = source[s + 2];
        }

        _quantizer.Add(rgb);
        _frames.Add(rgb);
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
            if (_quantizer is null || _cancelled || FramesWritten == 0)
            {
                return ValueTask.CompletedTask;
            }

            // One global palette for the whole animation, with unchanged pixels left transparent over
            // the previous frame. Per-frame palettes would be sharper but several times the size, and a
            // shareable size is the point of this rung.
            Write(_quantizer.BuildPalette(GifAnimationWriter.MaxPaletteColors));
        }
        finally
        {
            _frames.Clear();
            _quantizer = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Marks the export cancelled so <see cref="DisposeAsync" /> writes nothing.</summary>
    public void Cancel() => _cancelled = true;

    private void Write(byte[] palette)
    {
        try
        {
            using FileStream file = new(_outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            GifAnimationWriter writer = new(file, _width, _height, palette);

            for (int i = 0; i < _frames.Count; i++)
            {
                writer.WriteFrame(_frames[i], i == 0 ? default : _frames[i - 1], FrameDelayCentiseconds);

                // Encoded frames are only needed as the next frame's reference.
                if (i > 0)
                {
                    _frames[i - 1] = [];
                }
            }

            writer.Finish();
        }
        catch
        {
            File.Delete(_outputPath);
            throw;
        }
    }
}

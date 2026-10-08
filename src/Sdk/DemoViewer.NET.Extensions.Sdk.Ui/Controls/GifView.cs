#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     Plays an animated GIF from a file, looping, scaled to fit. Nothing is read until the control is in
///     the tree with a <see cref="Source" />, and everything is released when it leaves or the source
///     changes.
///     <para>
///         The file is read into memory and closed at once, so the clip sweep can delete it while it plays.
///         Frames are decoded one at a time into a single bitmap: a 12 s clip decoded whole is about 220 MB.
///     </para>
/// </summary>
public sealed class GifView : Control, IDisposable
{
    /// <summary>Defines the <see cref="Source" /> property.</summary>
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<GifView, string?>(nameof(Source));

    private SKCodec? _codec;
    private SKCodecFrameInfo[] _frames = [];
    private SKData? _data;
    private WriteableBitmap? _bitmap;
    private int _frame = -1;
    private int _generation;
    private DispatcherTimer? _timer;

    static GifView()
    {
        AffectsRender<GifView>(SourceProperty);
    }

    /// <summary>The GIF's path, or null for nothing.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>How many frames the loaded GIF has; 0 before one is loaded.</summary>
    public int FrameCount => _codec?.FrameCount ?? 0;

    /// <summary>The frame on screen, or -1.</summary>
    public int CurrentFrame => _frame;

    /// <summary>Completes when the current source has been read and its first frame decoded.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && VisualRoot is not null)
        {
            Stop();
            Start();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_bitmap is null)
        {
            return default;
        }

        Size size = _bitmap.Size;
        double scale = Math.Min(double.IsInfinity(availableSize.Width) ? 1 : availableSize.Width / size.Width, 1);
        return new Size(size.Width * scale, size.Height * scale);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (_bitmap is null || _frame < 0)
        {
            return;
        }

        Size size = _bitmap.Size;
        double scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
        Size drawn = new(size.Width * scale, size.Height * scale);
        Rect target = new(new Point((Bounds.Width - drawn.Width) / 2, (Bounds.Height - drawn.Height) / 2), drawn);
        context.DrawImage(_bitmap, new Rect(size), target);
    }

    private void Start()
    {
        if (Source is not { Length: > 0 } path)
        {
            return;
        }

        int generation = ++_generation;
        Loading = LoadAsync(path, generation);
    }

    private async Task LoadAsync(string path, int generation)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        _data = SKData.CreateCopy(bytes);
        _codec = SKCodec.Create(_data);
        if (_codec is null || _codec.FrameCount == 0 && _codec.Info.Width == 0)
        {
            Stop();
            return;
        }

        _frames = _codec.FrameInfo;
        _bitmap = new WriteableBitmap(new PixelSize(_codec.Info.Width, _codec.Info.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        Advance();
        InvalidateMeasure();
        if (_codec.FrameCount > 1)
        {
            _timer = new DispatcherTimer { Interval = Delay(0) };
            _timer.Tick += (_, _) => Advance();
            _timer.Start();
        }
    }

    // Decodes the next frame over the one on screen. The export writes whole frames, so a frame depends at
    // most on the one before it; a GIF whose frame restores an older one would draw that frame over the last.
    private void Advance()
    {
        if (_codec is null || _bitmap is null)
        {
            return;
        }

        int count = Math.Max(1, _codec.FrameCount);
        int next = (_frame + 1) % count;
        SKImageInfo info = new(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (ILockedFramebuffer buffer = _bitmap.Lock())
        {
            int prior = next == 0 ? -1 : next - 1;
            SKCodecOptions options = count > 1 ? new SKCodecOptions(next, prior) : new SKCodecOptions();
            _codec.GetPixels(info, buffer.Address, buffer.RowBytes, options);
        }

        _frame = next;
        if (_timer is not null)
        {
            _timer.Interval = Delay(next);
        }

        InvalidateVisual();
    }

    private TimeSpan Delay(int frame)
    {
        int ms = frame < _frames.Length ? _frames[frame].Duration : 0;
        return TimeSpan.FromMilliseconds(ms > 10 ? ms : 100);
    }

    private void Stop()
    {
        _generation++;
        _timer?.Stop();
        _timer = null;
        _codec?.Dispose();
        _codec = null;
        _frames = [];
        _data?.Dispose();
        _data = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _frame = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }
}

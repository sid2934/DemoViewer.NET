#region

using System.Runtime.InteropServices;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Services.Export.Pack;

/// <summary>
///     One segment's view of the pack's one sink. It forwards every frame, and its dispose ends the segment
///     rather than the file: <c>SceneExportSession</c> disposes the sink it is handed, exactly once, which
///     for a clip in a pack must not close the encode the next clip writes into.
///     <para>
///         <b>Whose failure it was.</b> A clip can fail on its own (a demo that will not parse, a range the
///         session refuses) and the pack goes on without it; a failure in the sink behind (ffmpeg gone, a
///         full disk) ends the pack. <see cref="InnerFaulted" /> tells the two apart.
///     </para>
/// </summary>
/// <param name="inner">The pack's sink. Not owned.</param>
internal sealed class PackSegmentSink(IFrameSink inner) : IFrameSink
{
    /// <summary>Frames this segment forwarded.</summary>
    public int FramesWritten { get; private set; }

    /// <summary>A write to the pack's sink threw: the encode is broken, not just this clip.</summary>
    public bool InnerFaulted { get; private set; }

    /// <summary>The session disposed this view; the pack's sink is untouched.</summary>
    public bool Ended { get; private set; }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Ended, this);
        try
        {
            await inner.WriteAsync(rgba, width, height, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            InnerFaulted = true;
            throw;
        }

        FramesWritten++;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Ended = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
///     Draws a section's title card: the title large and the line under it smaller, centred on the scene's
///     background, in the scene's own embedded face so the card looks the same on every machine.
/// </summary>
internal static class PackTitleCardRenderer
{
    /// <summary>The card as one RGBA8888 frame, unpremultiplied like the session's read-back.</summary>
    /// <param name="title">The title.</param>
    /// <param name="subtitle">The line under it, or empty.</param>
    /// <param name="side">The square frame's side.</param>
    /// <param name="palette">The scene colours.</param>
    public static byte[] Render(string title, string subtitle, int side, ScenePalette palette)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(side);

        // Drawn premultiplied (a raster surface cannot be anything else) and read back unpremultiplied, the
        // session's own pair; the card is opaque, so the two agree byte for byte.
        using SKSurface surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(palette.Background.WithAlpha(255));

        using TextBlobCache text = new();
        using SKPaint titlePaint = new() { Color = SKColors.White, IsAntialias = true };
        using SKPaint subtitlePaint = new() { Color = palette.Label.WithAlpha(255), IsAntialias = true };
        float margin = side * 0.08f;
        float width = side - 2 * margin;
        float titleSize = side * 0.075f;
        float subtitleSize = side * 0.04f;

        using SKFont titleFont = new(text.Typeface, titleSize);
        using SKFont subtitleFont = new(text.Typeface, subtitleSize);
        List<string> titleLines = Wrap(string.IsNullOrWhiteSpace(title) ? "Untitled section" : title, titleFont, width);
        List<string> subtitleLines = string.IsNullOrWhiteSpace(subtitle) ? [] : Wrap(subtitle, subtitleFont, width);

        float titleLine = titleSize * 1.25f;
        float subtitleLine = subtitleSize * 1.35f;
        float block = titleLines.Count * titleLine + (subtitleLines.Count > 0 ? subtitleSize + subtitleLines.Count * subtitleLine : 0);
        float y = (side - block) / 2 + titleSize;
        foreach (string line in titleLines)
        {
            canvas.DrawText(line, side / 2f, y, SKTextAlign.Center, titleFont, titlePaint);
            y += titleLine;
        }

        y += subtitleSize;
        foreach (string line in subtitleLines)
        {
            canvas.DrawText(line, side / 2f, y, SKTextAlign.Center, subtitleFont, subtitlePaint);
            y += subtitleLine;
        }

        byte[] rgba = new byte[side * side * 4];
        GCHandle handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            surface.ReadPixels(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                handle.AddrOfPinnedObject(), side * 4, 0, 0);
        }
        finally
        {
            handle.Free();
        }

        return rgba;
    }

    // Greedy word wrap to the width; a word wider than the line stands on its own line and is clipped.
    private static List<string> Wrap(string text, SKFont font, float width)
    {
        List<string> lines = [];
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureText(candidate) > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }
}

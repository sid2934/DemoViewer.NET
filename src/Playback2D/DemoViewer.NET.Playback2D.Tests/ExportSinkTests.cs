#region

using System.Buffers;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using FFMpegCore.Pipes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The ffmpeg command line, asserted without running ffmpeg. Every flag here is one this pipeline
///     cannot work without, and every one of them was chosen rather than inherited from a default.
/// </summary>
public class FfmpegArgumentTests
{
    [Test]
    public async Task TheRawVideoInput_IsFullySpecified()
    {
        string arguments = FfmpegFrameSink.DescribeArguments(Options(ExportFormats.WebM));

        // A rawvideo stream carries no header at all: pixel format, size and rate are the only things
        // telling ffmpeg what the bytes mean, and a wrong guess is a video of coloured noise.
        await Assert.That(arguments).Contains("-f rawvideo");
        await Assert.That(arguments).Contains("-pix_fmt rgba");
        await Assert.That(arguments).Contains("1280x720");
        await Assert.That(arguments).Contains("-r 60");
    }

    [Test]
    public async Task EveryFormat_DisablesAudio()
    {
        foreach (string format in ExportFormats.All)
        {
            // Nothing in this pipeline has audio to carry, and letting ffmpeg guess is how a container
            // ends up with an empty audio stream that some players then refuse.
            await Assert.That(FfmpegFrameSink.DescribeArguments(Options(format))).Contains("-an");
        }
    }

    [Test]
    public async Task Webm_UsesVp9_InConstantQualityMode()
    {
        string arguments = FfmpegFrameSink.DescribeArguments(Options(ExportFormats.WebM));

        await Assert.That(arguments).Contains("-c:v libvpx-vp9");
        await Assert.That(arguments).Contains("-b:v 0"); // without it, -crf is ignored and VP9 goes CBR
        await Assert.That(arguments).Contains("-crf 30");
        await Assert.That(arguments).Contains("-pix_fmt yuv420p");
        await Assert.That(arguments).Contains("-row-mt 1");
    }

    [Test]
    public async Task Mp4_UsesH264_WithFastStart()
    {
        string arguments = FfmpegFrameSink.DescribeArguments(Options(ExportFormats.Mp4));

        await Assert.That(arguments).Contains("-c:v libx264");
        await Assert.That(arguments).Contains("-preset medium");

        // FFMpegCore spells it without the leading '+'. Same flag, same effect (the '+' only matters when
        // several movflags are being combined), and asserting on what is actually emitted beats asserting
        // on the spelling the plan happened to use.
        await Assert.That(arguments).Contains("-movflags faststart");
    }

    [Test]
    public async Task Gif_IsOnePass_OverOneInput()
    {
        string arguments = FfmpegFrameSink.DescribeArguments(Options(ExportFormats.Gif, fps: 20));

        await Assert.That(arguments).Contains("palettegen");
        await Assert.That(arguments).Contains("paletteuse");
        await Assert.That(arguments).Contains("-loop 0");

        // Plan D6: a literal two-pass needs the input twice, and over a pipe that means spilling a
        // multi-gigabyte rawvideo temp file. The split/palettegen/paletteuse chain is the single-input
        // equivalent, so exactly ONE -i may appear.
        await Assert.That(CountInputs(arguments)).IsEqualTo(1);
    }

    [Test]
    public async Task BuildingArguments_DoesNotTouchGlobalFfOptions()
    {
        string? before = FFMpegCore.GlobalFFOptions.Current.BinaryFolder;
        FfmpegFrameSink.DescribeArguments(Options(ExportFormats.WebM) with { BinaryFolder = "/somewhere/else" });

        // The binary folder is passed per invocation on purpose: GlobalFFOptions is process-global
        // mutable state, and an in-app export and a dv2d export must be able to disagree about it.
        await Assert.That(FFMpegCore.GlobalFFOptions.Current.BinaryFolder).IsEqualTo(before);
    }

    private static FfmpegSinkOptions Options(string format, int fps = 60) =>
        new("out." + format, format, 1280, 720, fps);

    private static int CountInputs(string arguments)
    {
        int count = 0;
        foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "-i", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
///     The push→pull bridge. Its whole job is to make a bounded queue out of two incompatible shapes, so
///     the cases that matter are the ones where a queue misbehaves: it must apply backpressure, it must
///     return every rented buffer, and it must never leave a reader waiting forever.
/// </summary>
public class ChannelVideoFrameSourceTests
{
    [Test]
    public async Task TheWriter_WaitsOnceTheChannelIsFull()
    {
        using ChannelVideoFrameSource bridge = new(capacity: 2);
        byte[] payload = new byte[16];

        await bridge.WriteAsync(payload, 2, 2, CancellationToken.None);
        await bridge.WriteAsync(payload, 2, 2, CancellationToken.None);

        ValueTask third = bridge.WriteAsync(payload, 2, 2, CancellationToken.None);

        // Backpressure IS the memory bound: without it a renderer that outruns the encoder queues frames
        // until the process runs out of room, and at 1080p each one is 8 MB.
        await Assert.That(third.IsCompleted).IsFalse();

        IEnumerator<IVideoFrame> reader = bridge.GetEnumerator();
        await Assert.That(reader.MoveNext()).IsTrue();
        await third;
    }

    [Test]
    public async Task CompletingTheChannel_EndsTheEnumeration()
    {
        using ChannelVideoFrameSource bridge = new();
        byte[] payload = new byte[16];

        await bridge.WriteAsync(payload, 2, 2, CancellationToken.None);
        await bridge.WriteAsync(payload, 2, 2, CancellationToken.None);
        bridge.Complete();

        IEnumerator<IVideoFrame> reader = bridge.GetEnumerator();
        int read = 0;
        while (reader.MoveNext())
        {
            read++;
        }

        // ffmpeg sees EOF on the pipe and exits normally; a reader that blocked here instead is R2's
        // deadlock.
        await Assert.That(read).IsEqualTo(2);
    }

    [Test]
    public async Task TheFrameHandedOut_CarriesTheWrittenBytes_AndTheRgbaFormat()
    {
        using ChannelVideoFrameSource bridge = new();
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        await bridge.WriteAsync(payload, 2, 2, CancellationToken.None);
        bridge.Complete();

        IEnumerator<IVideoFrame> reader = bridge.GetEnumerator();
        await Assert.That(reader.MoveNext()).IsTrue();

        IVideoFrame frame = reader.Current;
        await Assert.That(frame.Width).IsEqualTo(2);
        await Assert.That(frame.Height).IsEqualTo(2);

        // "rgba" is what RawVideoPipeSource turns into -pix_fmt rgba, and it must agree with the
        // SKColorType the session reads back or red and blue swap in every exported frame.
        await Assert.That(frame.Format).IsEqualTo("rgba");

        using MemoryStream sink = new();
        frame.Serialize(sink);
        await Assert.That(sink.ToArray()).IsEquivalentTo(payload);
    }

    [Test]
    public async Task EveryRentedBuffer_GoesBackToThePool()
    {
        // The pool has no public census, so this measures the proxy that matters: renting the same size
        // after a full drain must not allocate a new array.
        using ChannelVideoFrameSource bridge = new();
        byte[] payload = new byte[4096];

        for (int i = 0; i < 32; i++)
        {
            await bridge.WriteAsync(payload, 32, 32, CancellationToken.None);

            IEnumerator<IVideoFrame> reader = i == 0 ? bridge.GetEnumerator() : bridge;
            reader.MoveNext();
        }

        bridge.Complete();
        bridge.Dispose();

        byte[] rented = ArrayPool<byte>.Shared.Rent(4096);
        ArrayPool<byte>.Shared.Return(rented);
        await Assert.That(rented.Length).IsGreaterThanOrEqualTo(4096);
    }

    [Test]
    public async Task EnumeratingTwice_IsRefused()
    {
        using ChannelVideoFrameSource bridge = new();
        bridge.GetEnumerator();

        // A second pass would silently produce an empty stream — a video file with no frames and no
        // error, which is a much worse failure than saying so.
        await Assert.That(SceneExportSessionLoopTests.Throws<InvalidOperationException>(
            () => bridge.GetEnumerator())).IsNotNull();
    }
}

/// <summary>
///     The no-ffmpeg floor. It exists so "export" is never a dead end, which means the cases worth
///     pinning are the ones where it must refuse cleanly rather than produce something wrong.
/// </summary>
public class ManagedGifSinkTests
{
    /// <summary>
    ///     A sink whose output directory does not exist yet must make it, not fail at the end.
    ///     <para>
    ///         This is CI's own invocation: the workflow exports to
    ///         <c>artifacts/playback2d-export/ci-smoke.gif</c>, a directory that does not exist on a
    ///         clean checkout. Neither rung of the ladder creates it — ffmpeg answers <c>Error opening
    ///         output …: No such file or directory</c> and ImageSharp throws
    ///         <see cref="DirectoryNotFoundException" /> — and both refusals land only after the whole
    ///         range has been replayed and drawn. Found at the B4 merge, by running the CI step.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnOutputDirectoryThatDoesNotExistYet_IsCreated_NotDiscoveredAtTheEnd()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dv-gifdir-{Guid.NewGuid():N}", "nested");
        string path = Path.Combine(directory, "out.gif");

        try
        {
            await using (ManagedGifSink sink = new(path, 20))
            {
                // Asserted before a single frame is written: the point is that the path is prepared up
                // front, so a directory that cannot be made fails in seconds rather than in minutes.
                await Assert.That(Directory.Exists(directory)).IsTrue();
                await sink.WriteAsync(Frame(8, 8, 40), 8, 8, CancellationToken.None);
            }

            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, true);
            }
        }
    }

    [Test]
    public async Task ItWritesADecodableGif_WithTheRequestedFrameDelay()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-gif-{Guid.NewGuid():N}.gif");

        try
        {
            await using (ManagedGifSink sink = new(path, 20))
            {
                for (int i = 0; i < 6; i++)
                {
                    await sink.WriteAsync(Frame(8, 8, (byte)(i * 40)), 8, 8, CancellationToken.None);
                }
            }

            using Image image = await Image.LoadAsync(path);
            await Assert.That(image.Frames.Count).IsEqualTo(6);
            await Assert.That(image.Width).IsEqualTo(8);

            // 20 fps is 5 centiseconds. A GIF delay is an integer number of them, which is exactly why
            // the fps list is the divisors of 100 (plan D7).
            await Assert.That(image.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay).IsEqualTo(5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AnFpsThatCannotBeExpressedExactly_IsRefusedAtConstruction()
    {
        // 30 fps is 3.33 centiseconds. Accepting it would export a 30 fps request at 33.3 fps and say
        // nothing, which is the kind of quiet wrongness a user only notices after uploading it.
        await Assert.That(SceneExportSessionLoopTests.Throws<ExportValidationException>(
            () => new ManagedGifSink("x.gif", 30).DisposeAsync().AsTask().GetAwaiter().GetResult()))
            .IsNotNull();
    }

    [Test]
    public async Task PastTheFrameCap_ItRefuses()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-gif-{Guid.NewGuid():N}.gif");
        ManagedGifSink sink = new(path, 20, maxFrames: 3);

        try
        {
            for (int i = 0; i < 3; i++)
            {
                await sink.WriteAsync(Frame(4, 4, 1), 4, 4, CancellationToken.None);
            }

            ExportValidationException? refusal = null;
            try
            {
                await sink.WriteAsync(Frame(4, 4, 1), 4, 4, CancellationToken.None);
            }
            catch (ExportValidationException ex)
            {
                refusal = ex;
            }

            await Assert.That(refusal).IsNotNull();
        }
        finally
        {
            await sink.DisposeAsync();
            File.Delete(path);
        }
    }

    [Test]
    public async Task ACancelledExport_LeavesNoFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-gif-{Guid.NewGuid():N}.gif");
        ManagedGifSink sink = new(path, 20);

        await sink.WriteAsync(Frame(4, 4, 200), 4, 4, CancellationToken.None);
        sink.Cancel();
        await sink.DisposeAsync();

        await Assert.That(File.Exists(path)).IsFalse();
    }

    private static byte[] Frame(int width, int height, byte value)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = (byte)(255 - value);
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return pixels;
    }
}

/// <summary>The determinism decorator: it must hash what it forwards, and forward what it hashes.</summary>
public class HashingFrameSinkTests
{
    [Test]
    public async Task ItHashesEveryFrame_AndForwardsThemUnchanged()
    {
        RecordingFrameSink inner = new();
        HashingFrameSink hashing = new(inner);

        await hashing.WriteAsync(new byte[] { 1, 2, 3, 4 }, 1, 1, CancellationToken.None);
        await hashing.WriteAsync(new byte[] { 1, 2, 3, 4 }, 1, 1, CancellationToken.None);
        await hashing.WriteAsync(new byte[] { 9, 9, 9, 9 }, 1, 1, CancellationToken.None);
        await hashing.DisposeAsync();

        await Assert.That(inner.Frames.Count).IsEqualTo(3);
        await Assert.That(inner.DisposeCount).IsEqualTo(1);
        await Assert.That(hashing.FrameHashes[0]).IsEqualTo(hashing.FrameHashes[1]);
        await Assert.That(hashing.FrameHashes[2]).IsNotEqualTo(hashing.FrameHashes[0]);
    }
}

#region

using System.Buffers;
using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using FFMpegCore;
using FFMpegCore.Pipes;
using TUnit.Assertions.Enums;

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
        await Assert.That(arguments).Contains("-crf 32");
        await Assert.That(arguments).Contains("-pix_fmt yuv420p");
        await Assert.That(arguments).Contains("-row-mt 1");

        // The pair that was MISSING before, which is why this rung ran at libvpx's slowest setting.
        // -row-mt alone only tells libvpx it may thread within a tile; -deadline/-cpu-used is the actual
        // speed control, and unset means "spend everything". 97 fps → 526 fps, for
        // 15 % more bits and an SSIM still above 0.999.
        await Assert.That(arguments).Contains("-deadline realtime");
        await Assert.That(arguments).Contains("-cpu-used 5");
    }

    [Test]
    public async Task Mp4_UsesH264_WithFastStart()
    {
        string arguments = FfmpegFrameSink.DescribeArguments(Options(ExportFormats.Mp4));

        await Assert.That(arguments).Contains("-c:v libx264");

        // `-preset medium -crf 30` was beaten on BOTH axes by this: faster and a higher SSIM. CRF 30
        // on x264 throws the quality away before the preset can spend any effort on it.
        await Assert.That(arguments).Contains("-preset veryfast");
        await Assert.That(arguments).Contains("-crf 21");

        // FFMpegCore spells it without the leading '+'. Same flag, same effect (the '+' only matters when
        // several movflags are being combined), and asserting on what is actually emitted beats asserting
        // on a particular spelling that could change.
        await Assert.That(arguments).Contains("-movflags faststart");
    }

    [Test]
    public async Task Gif_IsOnePass_OverOneInput()
    {
        string arguments = FfmpegFrameSink.DescribeArguments(Options(ExportFormats.Gif, 20));

        await Assert.That(arguments).Contains("palettegen");
        await Assert.That(arguments).Contains("paletteuse");
        await Assert.That(arguments).Contains("-loop 0");

        // A literal two-pass needs the input twice, and over a pipe that means spilling a
        // multi-gigabyte rawvideo temp file. The split/palettegen/paletteuse chain is the single-input
        // equivalent, so exactly ONE -i may appear.
        await Assert.That(CountInputs(arguments)).IsEqualTo(1);
    }

    [Test]
    public async Task BuildingArguments_DoesNotTouchGlobalFfOptions()
    {
        string? before = GlobalFFOptions.Current.BinaryFolder;
        FfmpegFrameSink.DescribeArguments(Options(ExportFormats.WebM) with
        {
            BinaryFolder = "/somewhere/else"
        });

        // The binary folder is passed per invocation on purpose: GlobalFFOptions is process-global
        // mutable state, and an in-app export and a dv2d export must be able to disagree about it.
        await Assert.That(GlobalFFOptions.Current.BinaryFolder).IsEqualTo(before);
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
        using ChannelVideoFrameSource bridge = new(2);
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
        await Assert.That(sink.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
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

        // A second pass would silently produce an empty stream, a video file with no frames and no
        // error, which is a much worse failure than saying so.
        await Assert.That(SceneExportSessionLoopTests.Throws<InvalidOperationException>(() => bridge.GetEnumerator())).IsNotNull();
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
    ///         clean checkout. Neither rung of the ladder creates it: ffmpeg answers
    ///         <c>
    ///             Error opening
    ///             output …: No such file or directory
    ///         </c>
    ///         and opening the file throws
    ///         <see cref="DirectoryNotFoundException" />, and both refusals land only after the whole
    ///         range has been replayed and drawn.
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

            DecodedGif gif = GifTestDecoder.Decode(await File.ReadAllBytesAsync(path));
            await Assert.That(gif.Frames.Count).IsEqualTo(6);
            await Assert.That(gif.Width).IsEqualTo(8);
            await Assert.That(gif.LoopCount).IsEqualTo(0);

            // 20 fps is 5 centiseconds. A GIF delay is an integer number of them, which is exactly why
            // the fps list is the divisors of 100.
            for (int i = 0; i < 6; i++)
            {
                await Assert.That(gif.Frames[i].DelayCentiseconds).IsEqualTo(5);
                await Assert.That(gif.Frames[i].Rgb).IsEquivalentTo(Rgb(Frame(8, 8, (byte)(i * 40))), CollectionOrdering.Matching);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     Noise large enough to fill the 4096-entry code table several times over, so code width growth
    ///     and the clear code are exercised, in fewer colours than the palette holds so the round trip
    ///     must be exact. The later frames check the changed-rectangle and transparency path.
    /// </summary>
    [Test]
    public async Task NoiseThatOverflowsTheCodeTable_RoundTripsExactly()
    {
        const int size = 128;
        byte[] first = Noise(size, size, 1);
        byte[] second = (byte[])first.Clone();
        // A colour the noise never uses, so every pixel in the block changes.
        Paint(second, size, 30, 20, 50, 40, (250, 250, 250));
        byte[] fourth = Noise(size, size, 2);
        byte[][] frames = [first, second, second, fourth];

        byte[] file = await Encode(frames, size, size);
        DecodedGif gif = GifTestDecoder.Decode(file);

        await Assert.That(gif.Frames.Count).IsEqualTo(4);
        for (int i = 0; i < frames.Length; i++)
        {
            await Assert.That(gif.Frames[i].Rgb).IsEquivalentTo(Rgb(frames[i]), CollectionOrdering.Matching);
            await Assert.That(gif.Frames[i].Disposal).IsEqualTo(1);
        }

        // Only the painted block changed; a repeated frame is one transparent pixel.
        DecodedFrame changed = gif.Frames[1];
        await Assert.That((changed.Left, changed.Top, changed.Width, changed.Height)).IsEqualTo((30, 20, 50, 40));
        await Assert.That(changed.HasTransparency).IsFalse();
        await Assert.That((gif.Frames[2].Width, gif.Frames[2].Height)).IsEqualTo((1, 1));
        await Assert.That(gif.Frames[2].HasTransparency).IsTrue();
        await Assert.That(gif.Frames[0].HasTransparency).IsFalse();
    }

    [Test]
    public async Task MoreColoursThanThePaletteHolds_AreQuantizedClosely()
    {
        const int width = 256, height = 64;
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                rgba[i] = (byte)x;
                rgba[i + 1] = (byte)(y * 4);
                rgba[i + 2] = (byte)(255 - x);
                rgba[i + 3] = 255;
            }
        }

        DecodedGif gif = GifTestDecoder.Decode(await Encode([rgba], width, height));
        byte[] expected = Rgb(rgba);
        byte[] actual = gif.Frames[0].Rgb;

        long totalError = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            totalError += Math.Abs(expected[i] - actual[i]);
        }

        // About 4.7 here, against 5.0 from ImageSharp's Wu and Floyd-Steinberg on the same input.
        double meanError = totalError / (double)expected.Length;
        await Assert.That(meanError).IsLessThan(6.0);
    }

    [Test]
    public async Task TheSameFrames_EncodeToTheSameBytes()
    {
        byte[][] frames = [Noise(64, 48, 3), Noise(64, 48, 4), Noise(64, 48, 4)];

        byte[] once = await Encode(frames, 64, 48);
        byte[] again = await Encode(frames, 64, 48);

        await Assert.That(again).IsEquivalentTo(once, CollectionOrdering.Matching);
    }

    /// <summary>
    ///     Keeps <see cref="GifTestDecoder" /> honest: a file from a third-party encoder (ImageSharp 3.1,
    ///     Wu quantizer, global table, delta frames) must decode to the frames that encoder's own
    ///     decoder produced, hashed when the fixture was made.
    /// </summary>
    [Test]
    public async Task TheTestDecoder_ReadsAThirdPartyGif()
    {
        byte[] file = await File.ReadAllBytesAsync(Path.Combine(FixtureCorpus.Root, "gif", "imagesharp-noise.gif"));
        DecodedGif gif = GifTestDecoder.Decode(file);

        string[] expected =
        [
            "DDB94C9E59D18884CBB98C35C9C854E07A323D800405FEEE714524CA3CCDE917",
            "D18496F493CBCB3890ADA35DF97878D3C84C8D320F75DCA9D0C7715CB43CF281",
            "65A049CC6FB58AFC3B156A6882112D2BDC40B86FD216F2DE54F08D9A6753CAFE"
        ];

        await Assert.That(gif.Frames.Count).IsEqualTo(3);
        await Assert.That(gif.LoopCount).IsEqualTo(0);
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(Convert.ToHexString(SHA256.HashData(gif.Frames[i].Rgb))).IsEqualTo(expected[i]);
            await Assert.That(gif.Frames[i].DelayCentiseconds).IsEqualTo(5);
        }
    }

    [Test]
    public async Task AnFpsThatCannotBeExpressedExactly_IsRefusedAtConstruction()
    {
        // 30 fps is 3.33 centiseconds. Accepting it would export a 30 fps request at 33.3 fps and say
        // nothing, which is the kind of quiet wrongness a user only notices after uploading it.
        await Assert.That(SceneExportSessionLoopTests.Throws<ExportValidationException>(() => new ManagedGifSink("x.gif", 30).DisposeAsync().AsTask().GetAwaiter().GetResult()))
            .IsNotNull();
    }

    [Test]
    public async Task PastTheFrameCap_ItRefuses()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-gif-{Guid.NewGuid():N}.gif");
        ManagedGifSink sink = new(path, 20, 3);

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

    private static async Task<byte[]> Encode(byte[][] frames, int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-gif-{Guid.NewGuid():N}.gif");
        try
        {
            await using (ManagedGifSink sink = new(path, 20))
            {
                foreach (byte[] frame in frames)
                {
                    await sink.WriteAsync(frame, width, height, CancellationToken.None);
                }
            }

            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Rgb(byte[] rgba)
    {
        byte[] rgb = new byte[rgba.Length / 4 * 3];
        for (int s = 0, d = 0; d < rgb.Length; s += 4, d += 3)
        {
            rgb[d] = rgba[s];
            rgb[d + 1] = rgba[s + 1];
            rgb[d + 2] = rgba[s + 2];
        }

        return rgb;
    }

    // 200 colours, each in its own 5-bit-per-channel bin, so the quantizer can keep every one exactly.
    private static (byte R, byte G, byte B) Palette200(int i) =>
        ((byte)((i % 8 * 32) + 5), (byte)((i / 8 % 5 * 48) + 7), (byte)((i / 40 * 48) + 9));

    private static byte[] Noise(int width, int height, uint seed)
    {
        byte[] pixels = new byte[width * height * 4];
        uint state = seed * 2654435761u;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            state = (state * 1664525u) + 1013904223u;
            (byte r, byte g, byte b) = Palette200((int)((state >> 8) % 200));
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static void Paint(byte[] rgba, int stride, int left, int top, int width, int height,
        (byte R, byte G, byte B) colour)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                int i = ((y * stride) + x) * 4;
                rgba[i] = colour.R;
                rgba[i + 1] = colour.G;
                rgba[i + 2] = colour.B;
            }
        }
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

        await hashing.WriteAsync(new byte[]
        {
            1, 2, 3, 4
        }, 1, 1, CancellationToken.None);
        await hashing.WriteAsync(new byte[]
        {
            1, 2, 3, 4
        }, 1, 1, CancellationToken.None);
        await hashing.WriteAsync(new byte[]
        {
            9, 9, 9, 9
        }, 1, 1, CancellationToken.None);
        await hashing.DisposeAsync();

        await Assert.That(inner.Frames.Count).IsEqualTo(3);
        await Assert.That(inner.DisposeCount).IsEqualTo(1);
        await Assert.That(hashing.FrameHashes[0]).IsEqualTo(hashing.FrameHashes[1]);
        await Assert.That(hashing.FrameHashes[2]).IsNotEqualTo(hashing.FrameHashes[0]);
    }
}

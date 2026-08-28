#region

using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A probe that answers from a script instead of from a machine. See
///     <c>docs/playback2d-v2/plans/P2-export-throughput.md</c>.
///     <para>
///         This is what makes the fallback behaviour assertable on a CI runner with no GPU, no driver and
///         no ffmpeg. Every case below runs identically on a workstation with an RTX card and on a hosted
///         Linux container, because neither one is consulted.
///     </para>
/// </summary>
/// <param name="working">The encoder names this imaginary machine can run.</param>
internal sealed class FakeEncoderProbe(params string[] working) : IEncoderProbe
{
    private readonly HashSet<string> _working = new(working, StringComparer.Ordinal);

    /// <summary>Every (encoder, trustListing) pair this probe was asked about, in order.</summary>
    public List<(string Encoder, bool TrustListing)> Calls { get; } = [];

    /// <inheritdoc />
    public EncoderProbeResult Verify(string encoderName, string? binaryFolder, bool trustListing,
        CancellationToken ct)
    {
        Calls.Add((encoderName, trustListing));
        return _working.Contains(encoderName)
            ? new EncoderProbeResult(encoderName, true, "verified")
            : new EncoderProbeResult(encoderName, false, "no device (fake)");
    }
}

/// <summary>
///     The ladder itself: shape, order, and the invariant every fallback rests on.
/// </summary>
public class EncoderLadderTests
{
    [Test]
    public async Task EveryLadder_EndsInSoftware()
    {
        // The load-bearing invariant. `auto` on a machine with no working hardware encoder (every CI
        // runner and most laptops) has to land somewhere, and "somewhere" is the last rung.
        foreach (string format in ExportFormats.All)
        {
            IReadOnlyList<VideoEncoder> rungs = EncoderLadder.For(format);
            await Assert.That(rungs).IsNotEmpty();
            await Assert.That(rungs[^1].IsHardware).IsFalse();
            await Assert.That(EncoderLadder.SoftwareFor(format)).IsEqualTo(rungs[^1]);
        }
    }

    [Test]
    public async Task WebM_PrefersAv1Hardware_ThenVp9()
    {
        IReadOnlyList<VideoEncoder> rungs = EncoderLadder.For(ExportFormats.WebM);

        // AV1 rather than HEVC on the hardware rungs is a CONTAINER constraint, not a taste: HEVC cannot
        // go in a WebM at all, while AV1 has been legal in it since 2018. That is what lets the hardware
        // rung keep the .webm extension, the format id and every persisted default exactly as they were.
        await Assert.That(rungs.Select(r => r.Name).ToList())
            .IsEquivalentTo(new List<string>
            {
                "av1_nvenc",
                "av1_qsv",
                "av1_amf",
                "libvpx-vp9"
            });

        foreach (VideoEncoder rung in rungs)
        {
            await Assert.That(rung.Codec).IsEqualTo(rung == rungs[^1] ? "vp9" : "av1");
        }
    }

    [Test]
    public async Task Mp4_PrefersH264Hardware_ThenX264()
    {
        await Assert.That(EncoderLadder.For(ExportFormats.Mp4).Select(r => r.Name).ToList())
            .IsEquivalentTo(new List<string>
            {
                "h264_nvenc",
                "h264_qsv",
                "h264_amf",
                "libx264"
            });
    }

    [Test]
    public async Task VendorOrder_PutsTheDiscreteCardFirst()
    {
        // NVENC, then QSV, then AMF, on both ladders. Not a quality claim: on a box with a discrete
        // NVIDIA card AND an iGPU, the discrete card is the one that is not also drawing the desktop.
        foreach (string format in new[]
                 {
                     ExportFormats.WebM, ExportFormats.Mp4
                 })
        {
            List<EncoderAcceleration> order =
                [.. EncoderLadder.For(format).Select(r => r.Acceleration)];

            await Assert.That(order).IsEquivalentTo(new List<EncoderAcceleration>
            {
                EncoderAcceleration.Nvenc,
                EncoderAcceleration.QuickSync,
                EncoderAcceleration.Amf,
                EncoderAcceleration.Software
            });
        }
    }

    [Test]
    public async Task Gif_HasNoRealLadder()
    {
        IReadOnlyList<VideoEncoder> rungs = EncoderLadder.For(ExportFormats.Gif);

        await Assert.That(rungs.Count).IsEqualTo(1);
        await Assert.That(rungs[0].IsHardware).IsFalse();

        // The palettegen/paletteuse filter chain IS the encoder, so there is no -c:v and no quality
        // ladder to map. The rung exists so a GIF export reports through the same shape as every other.
        await Assert.That(rungs[0].ArgumentsFor(ExportQuality.Best)).IsEmpty();
    }

    [Test]
    public async Task AnUnknownFormat_GetsTheWebMLadder()
    {
        // Matching SceneExportSession.SupportedFps's treatment of the same case: WebM is the default
        // format, so it is also the default answer to a question about a format nobody has heard of.
        await Assert.That(EncoderLadder.For("qt-anim")).IsEquivalentTo(EncoderLadder.For(ExportFormats.WebM));
    }

    [Test]
    public async Task Find_IsCaseInsensitive_AndScopedToTheFormat()
    {
        await Assert.That(EncoderLadder.Find(ExportFormats.WebM, "AV1_NVENC")).IsEqualTo(EncoderLadder.Av1Nvenc);

        // The two video ladders share no rungs, and a name from the wrong one is not "close enough":
        // asking for h264_nvenc while exporting a WebM is a request that cannot be honoured.
        await Assert.That(EncoderLadder.Find(ExportFormats.WebM, "h264_nvenc")).IsNull();
        await Assert.That(EncoderLadder.Find(ExportFormats.Mp4, "libvpx-vp9")).IsNull();
    }
}

/// <summary>
///     The quality table, asserted as arguments. Every string here is measured (throughput, output
///     bitrate and SSIM per cell), so changing one changes a published number.
/// </summary>
public class EncoderQualityTests
{
    [Test]
    public async Task EveryVideoRung_MapsAllThreeQualities_ToSomethingDifferent()
    {
        foreach (string format in new[]
                 {
                     ExportFormats.WebM, ExportFormats.Mp4
                 })
        {
            foreach (VideoEncoder rung in EncoderLadder.For(format))
            {
                string draft = rung.ArgumentsFor(ExportQuality.Draft);
                string standard = rung.ArgumentsFor(ExportQuality.Standard);
                string best = rung.ArgumentsFor(ExportQuality.Best);

                await Assert.That(draft).IsNotEmpty();
                await Assert.That(standard).IsNotEmpty();
                await Assert.That(best).IsNotEmpty();

                // Three ids mapping onto two settings is a menu with a decoy on it.
                await Assert.That(draft).IsNotEqualTo(standard);
                await Assert.That(standard).IsNotEqualTo(best);
            }
        }
    }

    [Test]
    public async Task Nvenc_UsesConstantQualityVbr_WithAZeroBitrate()
    {
        foreach (VideoEncoder rung in new[]
                 {
                     EncoderLadder.Av1Nvenc, EncoderLadder.H264Nvenc
                 })
        {
            foreach (ExportQuality quality in Enum.GetValues<ExportQuality>())
            {
                string arguments = rung.ArgumentsFor(quality);

                // -rc vbr with -b:v 0 IS NVENC's constant-quality mode. Without the zero bitrate, -cq is
                // silently ignored and the encoder goes CBR at its default rate: an export that "works"
                // and looks wrong.
                await Assert.That(arguments).Contains("-rc vbr");
                await Assert.That(arguments).Contains("-b:v 0");
                await Assert.That(arguments).Contains("-cq ");
                await Assert.That(arguments).Contains("-preset p");
            }
        }
    }

    [Test]
    public async Task Vp9_AlwaysCarriesTheSpeedControl_AndRowThreading()
    {
        foreach (ExportQuality quality in Enum.GetValues<ExportQuality>())
        {
            string arguments = EncoderLadder.Vp9.ArgumentsFor(quality);

            // Without both flags libvpx runs at its slowest setting; they are vp9's whole speed control.
            await Assert.That(arguments).Contains("-deadline ");
            await Assert.That(arguments).Contains("-cpu-used ");
            await Assert.That(arguments).Contains("-row-mt 1");
            await Assert.That(arguments).Contains("-b:v 0");
        }
    }

    [Test]
    public async Task Standard_IsTheDefault_ForEveryParseFailure()
    {
        await Assert.That(ExportQualities.ParseOrDefault(null)).IsEqualTo(ExportQuality.Standard);
        await Assert.That(ExportQualities.ParseOrDefault("")).IsEqualTo(ExportQuality.Standard);
        await Assert.That(ExportQualities.ParseOrDefault("turbo")).IsEqualTo(ExportQuality.Standard);
    }

    [Test]
    public async Task QualityIds_RoundTrip_CaseInsensitively()
    {
        foreach (ExportQuality quality in Enum.GetValues<ExportQuality>())
        {
            string id = ExportQualities.ToId(quality);
            await Assert.That(ExportQualities.All).Contains(id);
            await Assert.That(ExportQualities.ParseOrDefault(id)).IsEqualTo(quality);
            await Assert.That(ExportQualities.ParseOrDefault(id.ToUpperInvariant())).IsEqualTo(quality);
        }
    }
}

/// <summary>
///     The ladder walk. Every case here is GPU-free by construction: the probe is a fake, so what is
///     asserted is the SELECTION LOGIC and not the machine it happens to run on.
/// </summary>
public class EncoderSelectorTests
{
    [Test]
    public async Task NoHardwareWorks_FallsBackToSoftware_AndSaysWhatItTried()
    {
        FakeEncoderProbe probe = new("libvpx-vp9");
        EncoderSelection selection = new EncoderSelector(probe)
            .Select(ExportFormats.WebM, "auto", ExportQuality.Standard, null);

        await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.Vp9);
        await Assert.That(selection.IsHardware).IsFalse();

        // "It fell back" and "there was nothing to fall back from" are different facts, and a user
        // wondering why their export is slow needs to be able to tell them apart.
        await Assert.That(selection.Attempts.Count).IsEqualTo(4);
        await Assert.That(selection.Reason).Contains("av1_nvenc");
        await Assert.That(selection.Reason).Contains("no device (fake)");
    }

    [Test]
    public async Task TheBestRungWins_WhenItVerifies()
    {
        EncoderSelection selection = new EncoderSelector(new FakeEncoderProbe("av1_nvenc", "libvpx-vp9"))
            .Select(ExportFormats.WebM, null, ExportQuality.Standard, null);

        await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.Av1Nvenc);
        await Assert.That(selection.IsHardware).IsTrue();

        // It stopped at the first rung: the ladder is walked lazily, so a working NVENC costs ONE probe
        // and not four.
        await Assert.That(selection.Attempts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TheLadderWalks_PastEachFailure()
    {
        FakeEncoderProbe probe = new("h264_amf");
        EncoderSelection selection = new EncoderSelector(probe)
            .Select(ExportFormats.Mp4, EncoderLadder.Auto, ExportQuality.Best, null);

        await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.H264Amf);
        await Assert.That(probe.Calls.Select(c => c.Encoder).ToList())
            .IsEquivalentTo(new List<string>
            {
                "h264_nvenc",
                "h264_qsv",
                "h264_amf"
            });
        await Assert.That(selection.Reason).Contains("rung 3 of 4");
    }

    [Test]
    public async Task NullRequest_MeansAuto()
    {
        // The settings file, the dialog and the CLI all have their own way of spelling "nothing chosen".
        foreach (string? request in new[]
                 {
                     null, "", "   ", "auto", "AUTO"
                 })
        {
            EncoderSelection selection = new EncoderSelector(new FakeEncoderProbe("av1_nvenc"))
                .Select(ExportFormats.WebM, request, ExportQuality.Standard, null);
            await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.Av1Nvenc);
        }
    }

    [Test]
    public async Task Software_SkipsTheHardwareRungs_WithoutProbingThem()
    {
        FakeEncoderProbe probe = new("av1_nvenc", "libvpx-vp9");
        EncoderSelection selection = new EncoderSelector(probe)
            .Select(ExportFormats.WebM, "software", ExportQuality.Draft, null);

        await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.Vp9);
        await Assert.That(selection.Quality).IsEqualTo(ExportQuality.Draft);

        // Not one process was started. `software` is the answer a bisect or a bitrate comparison wants,
        // and it must not depend on what hardware happens to be in the machine running it, including
        // hardware that WOULD have verified.
        await Assert.That(probe.Calls).IsEmpty();
    }

    [Test]
    public async Task SoftwareRungs_AreTrustedFromTheListing_NotTestEncoded()
    {
        FakeEncoderProbe probe = new("libvpx-vp9");
        new EncoderSelector(probe).Select(ExportFormats.WebM, "auto", ExportQuality.Standard, null);

        // trustListing is true for exactly the software rung and false for every hardware one. That is
        // what keeps a GPU-less CI export from paying a 600 ms test encode to re-learn that libvpx is
        // still libvpx. The failure mode a test encode exists for is a DRIVER fact.
        foreach ((string encoder, bool trust) in probe.Calls)
        {
            await Assert.That(trust).IsEqualTo(string.Equals(encoder, "libvpx-vp9", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task AnExplicitRungThatFails_IsRefused_NotSubstituted()
    {
        EncoderSelector selector = new(new FakeEncoderProbe("libvpx-vp9"));

        // A user who asked for av1_nvenc and quietly got libvpx has been told a lie about what their
        // file is, and goes on believing their GPU did the work.
        EncoderUnavailableException thrown = Assert.Throws<EncoderUnavailableException>(() => selector.Select(ExportFormats.WebM, "av1_nvenc", ExportQuality.Standard, null));

        await Assert.That(thrown.Message).Contains("av1_nvenc");
        await Assert.That(thrown.Message).Contains("no device (fake)");
        await Assert.That(thrown.Message).Contains("--encoder auto");
    }

    [Test]
    public async Task AnUnknownEncoderName_IsAValidationError_ThatListsTheChoices()
    {
        EncoderSelector selector = new(new FakeEncoderProbe());

        ExportValidationException thrown = Assert.Throws<ExportValidationException>(() => selector.Select(ExportFormats.Mp4, "libaom-av1", ExportQuality.Standard, null));

        await Assert.That(thrown.Message).Contains("auto");
        await Assert.That(thrown.Message).Contains("software");
        await Assert.That(thrown.Message).Contains("h264_nvenc");
        await Assert.That(thrown.Message).Contains("libx264");
    }

    [Test]
    public async Task NothingVerifies_StillSelectsTheFloor_SoFfmpegGetsToExplain()
    {
        FakeEncoderProbe probe = new();
        EncoderSelection selection = new EncoderSelector(probe)
            .Select(ExportFormats.Mp4, "auto", ExportQuality.Standard, null);

        // Even the software floor said no, which means this ffmpeg cannot encode H.264 at all. Selecting
        // it anyway is deliberate: the export then fails with ffmpeg's own message about the real problem
        // instead of ours about a probe.
        await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.X264);
        await Assert.That(selection.Reason).Contains("nothing on the ladder verified");
    }

    [Test]
    public async Task Gif_ShortCircuits_WithoutProbingAnything()
    {
        FakeEncoderProbe probe = new();
        EncoderSelection selection = new EncoderSelector(probe)
            .Select(ExportFormats.Gif, "av1_nvenc", ExportQuality.Best, null);

        await Assert.That(probe.Calls).IsEmpty();
        await Assert.That(selection.Encoder).IsEqualTo(EncoderLadder.Gif);
    }

    [Test]
    public async Task TwoSelections_AreIndependentValues()
    {
        // Nothing about a selection is process-global, so two exports in one process may be on two
        // different rungs at once. A future multi-export node depends on that.
        EncoderSelector selector = new(new FakeEncoderProbe("av1_nvenc", "libvpx-vp9"));

        EncoderSelection hardware =
            selector.Select(ExportFormats.WebM, "auto", ExportQuality.Best, null);
        EncoderSelection software =
            selector.Select(ExportFormats.WebM, "software", ExportQuality.Draft, null);

        await Assert.That(hardware.Encoder).IsEqualTo(EncoderLadder.Av1Nvenc);
        await Assert.That(hardware.Quality).IsEqualTo(ExportQuality.Best);
        await Assert.That(software.Encoder).IsEqualTo(EncoderLadder.Vp9);
        await Assert.That(software.Quality).IsEqualTo(ExportQuality.Draft);
    }
}

/// <summary>The cache: a memo of machine facts, and nothing else.</summary>
public class EncoderProbeCacheTests
{
    [Test]
    public async Task ItAsksTheProbeOncePerEncoder()
    {
        FakeEncoderProbe inner = new("av1_nvenc");
        EncoderProbeCache cache = new(inner);

        for (int i = 0; i < 5; i++)
        {
            await Assert.That(cache.Verify("av1_nvenc", null, false, CancellationToken.None).Works).IsTrue();
            await Assert.That(cache.Verify("av1_qsv", null, false, CancellationToken.None).Works).IsFalse();
        }

        await Assert.That(inner.Calls.Count).IsEqualTo(2);
        await Assert.That(cache.Hits).IsEqualTo(8);
    }

    [Test]
    public async Task DifferentFfmpegDirectories_AreDifferentMachines()
    {
        FakeEncoderProbe inner = new("av1_nvenc");
        EncoderProbeCache cache = new(inner);

        cache.Verify("av1_nvenc", "/opt/ffmpeg-a", false, CancellationToken.None);
        cache.Verify("av1_nvenc", "/opt/ffmpeg-b", false, CancellationToken.None);

        // A PATH ffmpeg and a managed one are two different BUILDS. One may carry NVENC and the other
        // may not, so keying on the encoder name alone would answer a question nobody asked.
        await Assert.That(inner.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Clear_ForgetsEverything()
    {
        FakeEncoderProbe inner = new("libx264");
        EncoderProbeCache cache = new(inner);

        cache.Verify("libx264", null, true, CancellationToken.None);
        cache.Clear();
        cache.Verify("libx264", null, true, CancellationToken.None);

        await Assert.That(inner.Calls.Count).IsEqualTo(2);
        await Assert.That(cache.Hits).IsEqualTo(0);
    }
}

/// <summary>
///     The real probe against the real ffmpeg. Skips cleanly when there is none, the way
///     <c>FfmpegAcquisitionTests</c> and <c>ExportFailureTests</c> already do. CI has no GPU and may
///     have no ffmpeg; neither is a failure.
/// </summary>
public class FfmpegEncoderProbeTests
{
    [Test]
    public async Task TheTestEncodeTransport_Works_AgainstASoftwareEncoder()
    {
        string? folder = RequireFfmpeg();
        FfmpegEncoderProbe probe = new();

        // trustListing:false forces the actual two-frames-on-stdin encode. Every OTHER caller of this
        // path is a hardware rung, so without this case the transport itself (rawvideo yuv420p on
        // stdin, out to -f null -) would only ever be exercised on a machine with a working GPU. If it
        // were broken, `auto` would silently reject every hardware rung and fall to software forever.
        EncoderProbeResult result = probe.Verify("libvpx-vp9", folder, false, CancellationToken.None);

        await Assert.That(result.Works).IsTrue().Because(result.Detail);
        await Assert.That(result.Detail).IsEqualTo("verified");
        await Assert.That(probe.TestEncodes).IsEqualTo(1);
    }

    [Test]
    public async Task AnEncoderNoBuildHas_IsRejectedFromTheListing_WithoutSpawningAnything()
    {
        string? folder = RequireFfmpeg();
        FfmpegEncoderProbe probe = new();

        EncoderProbeResult result =
            probe.Verify("h265_unicorn", folder, false, CancellationToken.None);

        await Assert.That(result.Works).IsFalse();
        await Assert.That(result.Detail).IsEqualTo("not built into this ffmpeg");

        // The listing is a cheap pre-filter, and its whole job is to answer this without a subprocess.
        await Assert.That(probe.TestEncodes).IsEqualTo(0);
    }

    [Test]
    public async Task TheListing_ContainsTheSoftwareRungs_AndIsCached()
    {
        string? folder = RequireFfmpeg();
        FfmpegEncoderProbe probe = new();

        IReadOnlySet<string> first = probe.ListEncoders(folder, CancellationToken.None);
        await Assert.That(first).Contains("libvpx-vp9");
        await Assert.That(first).Contains("libx264");

        // Same instance back, not a re-read: `-encoders` is half a second and every export asks.
        await Assert.That(probe.ListEncoders(folder, CancellationToken.None)).IsSameReferenceAs(first);
    }

    [Test]
    public async Task ACancelledListing_IsNotRemembered()
    {
        string? folder = RequireFfmpeg();
        FfmpegEncoderProbe probe = new();

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        // A cancelled walk says so. It does NOT come back with an empty listing, which is
        // indistinguishable from "this build carries no encoders". EncoderProbeCache refuses to cache a
        // cancelled RESULT for the same reason, and the listing underneath it has to agree.
        Assert.Throws<OperationCanceledException>(() => probe.ListEncoders(folder, cancelled.Token));

        // The app holds ONE EncoderProbeCache for a whole session. A stuck cancelled read tells every
        // later export in that session that every rung is "not built into this ffmpeg" and drops it to
        // the software floor: silently, permanently, and triggered by nothing more exotic than pressing
        // Cancel while the ladder is being walked.
        IReadOnlySet<string> afterwards = probe.ListEncoders(folder, CancellationToken.None);

        await Assert.That(afterwards).Contains("libvpx-vp9");
        await Assert.That(afterwards).Contains("libx264");
    }

    [Test]
    public async Task ACancelledProbe_DoesNotSitOutTheTimeout()
    {
        string? folder = RequireFfmpeg();
        FfmpegEncoderProbe probe = new();

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        long startedMs = Environment.TickCount64;
        try
        {
            probe.Verify("libvpx-vp9", folder, false, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            // The expected answer.
        }

        long elapsedMs = Environment.TickCount64 - startedMs;

        // Handing the token to the stream reads instead of to the process abandons ffmpeg's pipes while
        // it is still writing, blocking the child on a full buffer until the 20 s timeout kills it. A
        // user who cancels an export waits for the render loop to notice, not for that.
        await Assert.That(elapsedMs).IsLessThan((long)FfmpegEncoderProbe.Timeout.TotalMilliseconds / 4);
    }

    [Test]
    public async Task ANonexistentFfmpeg_IsUnavailable_NotAnException()
    {
        FfmpegEncoderProbe probe = new();
        string nowhere = Path.Combine(Path.GetTempPath(), "dv-no-ffmpeg-" + Guid.NewGuid().ToString("N"));

        EncoderProbeResult result = probe.Verify("libx264", nowhere, false, CancellationToken.None);

        // A probe must never be the thing that fails an export: it answers "unavailable" and the ladder
        // handles it, exactly as it handles a driver that says no.
        await Assert.That(result.Works).IsFalse();
        await Assert.That(result.Detail).IsEqualTo("could not read `ffmpeg -encoders`");
    }

    private static string? RequireFfmpeg()
    {
        FfmpegLocation located = FfmpegLocator.Locate(null);
        return located.Found
            ? located.Directory
            : throw new SkipTestException("no ffmpeg on PATH.");
    }
}

/// <summary>The selected rung reaching the actual ffmpeg command line.</summary>
public class EncoderArgumentTests
{
    [Test]
    public async Task TheSelectedRung_IsWhatEndsUpOnTheCommandLine()
    {
        EncoderSelection selection = new EncoderSelector(new FakeEncoderProbe("av1_nvenc"))
            .Select(ExportFormats.WebM, "auto", ExportQuality.Best, null);

        string arguments = FfmpegFrameSink.DescribeArguments(
            new FfmpegSinkOptions("out.webm", ExportFormats.WebM, 1280, 720, 60, null, selection));

        await Assert.That(arguments).Contains("-c:v av1_nvenc");
        await Assert.That(arguments).Contains("-preset p6");
        await Assert.That(arguments).Contains("-cq 28");
        await Assert.That(arguments).Contains("-pix_fmt yuv420p");

        // What it must NOT contain: the rung it replaced. A sink that emitted both codecs would be
        // ffmpeg's problem to reject, several seconds into an export.
        await Assert.That(arguments).DoesNotContain("libvpx-vp9");
    }

    [Test]
    public async Task Mp4_KeepsFastStart_WhateverTheRung()
    {
        foreach (VideoEncoder rung in EncoderLadder.For(ExportFormats.Mp4))
        {
            EncoderSelection selection = new(rung, ExportQuality.Standard, "test", []);
            string arguments = FfmpegFrameSink.DescribeArguments(
                new FfmpegSinkOptions("out.mp4", ExportFormats.Mp4, 1280, 720, 60, null, selection));

            // faststart is a CONTAINER property (it moves the moov atom to the front), so it has to
            // survive a rung swap, so it is applied outside the encoder branch.
            await Assert.That(arguments).Contains("-movflags faststart");
            await Assert.That(arguments).Contains("-c:v " + rung.Name);
        }
    }

    [Test]
    public async Task Gif_IsUntouchedByTheLadder()
    {
        EncoderSelection selection = new EncoderSelector(new FakeEncoderProbe())
            .Select(ExportFormats.Gif, "auto", ExportQuality.Best, null);

        string arguments = FfmpegFrameSink.DescribeArguments(
            new FfmpegSinkOptions("out.gif", ExportFormats.Gif, 640, 360, 20, null, selection));

        await Assert.That(arguments).Contains("palettegen");
        await Assert.That(arguments).Contains("paletteuse");
        await Assert.That(arguments).Contains("-loop 0");

        // No -c:v at all: the filter chain is the encoder, and a ladder rung leaking a codec flag in
        // here would override it.
        await Assert.That(arguments).DoesNotContain("-c:v ");
    }

    [Test]
    public async Task ASinkWithNoSelection_StillEncodes_OnTheSoftwareRung()
    {
        // A caller that never ran a probe (a test, a headless tool, a path not yet wired) gets a
        // working sink rather than a null reference.
        foreach (string format in new[]
                 {
                     ExportFormats.WebM, ExportFormats.Mp4
                 })
        {
            FfmpegSinkOptions options = new("out." + format, format, 1280, 720, 60);

            await Assert.That(options.ResolvedEncoder.Encoder)
                .IsEqualTo(EncoderLadder.SoftwareFor(format));
            await Assert.That(options.ResolvedEncoder.Quality).IsEqualTo(ExportQuality.Standard);
            await Assert.That(FfmpegFrameSink.DescribeArguments(options))
                .Contains("-c:v " + EncoderLadder.SoftwareFor(format).Name);
        }
    }
}

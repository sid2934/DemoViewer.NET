#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What <see cref="SceneExportSession.Validate" /> refuses, and why each refusal is worth its own
///     rule. Every one of these is a request a user could build in the dialog or type on the command
///     line, and each would otherwise fail somewhere far less legible — inside ffmpeg, or in an
///     out-of-memory during a GIF quantise.
/// </summary>
public class ExportRequestValidationTests
{
    private static readonly int[] _gifFps = [10, 20, 25, 50];

    [Test]
    public async Task OddDimensions_AreRefusedForYuvFormats_AndAcceptedForGif()
    {
        SKSizeI odd = new(1281, 720);

        await Assert.That(Refusal(ExportFixtures.Request(10, ExportFormats.WebM, odd))).IsNotNull();
        await Assert.That(Refusal(ExportFixtures.Request(10, ExportFormats.Mp4, odd))).IsNotNull();

        // GIF has a palette, not a chroma plane, so odd is simply odd. (Under the width cap, so the
        // only rule in play here is the even-dimension one.)
        await Assert.That(Refusal(ExportFixtures.Request(10, ExportFormats.Gif, odd, fps: 20))).IsNull();
    }

    [Test]
    public async Task AnEmptyRange_IsRefused()
    {
        ExportRequest request = ExportFixtures.Request(10) with { StartFrame = 8, EndFrame = 4 };
        await Assert.That(Refusal(request)).IsNotNull();
    }

    [Test]
    public async Task AnFpsTheFormatCannotExpress_IsRefused()
    {
        // 30 does not divide 100, so a GIF at 30 fps would silently become 33.3 (plan D7).
        await Assert.That(Refusal(ExportFixtures.Request(10, ExportFormats.Gif, fps: 30))).IsNotNull();
        await Assert.That(Refusal(ExportFixtures.Request(10, ExportFormats.Gif, fps: 20))).IsNull();
        await Assert.That(Refusal(ExportFixtures.Request(10, ExportFormats.WebM, fps: 30))).IsNull();
    }

    [Test]
    public async Task SupportedFps_ForGif_IsTheDivisorsOfOneHundred()
    {
        await Assert.That(SceneExportSession.SupportedFps(ExportFormats.Gif)).IsEquivalentTo(_gifFps);

        foreach (int fps in SceneExportSession.SupportedFps(ExportFormats.Gif))
        {
            await Assert.That(100 % fps).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AGifOverTheFrameCap_IsRefusedBeforeAnythingRenders()
    {
        ExportRequest request = ExportFixtures.Request(SceneExportSession.GifMaxFrames + 1,
            ExportFormats.Gif, new SKSizeI(640, 360), fps: 20);

        string? refusal = Refusal(request);
        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!).Contains(SceneExportSession.GifMaxFrames.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task AGifWiderThanTheCap_IsRefused()
    {
        ExportRequest request = ExportFixtures.Request(10, ExportFormats.Gif,
            new SKSizeI(SceneExportSession.GifMaxWidth + 2, 1080), fps: 20);
        await Assert.That(Refusal(request)).IsNotNull();
    }

    private static string? Refusal(ExportRequest request)
    {
        try
        {
            SceneExportSession.Validate(request);
            return null;
        }
        catch (ExportValidationException ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>
///     The export loop itself: how many frames come out, how big they are, which layers drew, and what
///     the compositor is left in afterwards.
/// </summary>
public class SceneExportSessionLoopTests
{
    [Test]
    public async Task EveryFrameInTheRange_ReachesTheSink_AtTheRequestedSize()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        SKSizeI size = new(96, 64);
        ExportRequest request = ExportFixtures.Request(12, size: size);

        await new SceneExportSession(compositor)
            .RunAsync(request, ExportFixtures.Source(12), sink, surfaces, null, CancellationToken.None);

        await Assert.That(sink.Frames.Count).IsEqualTo(12);
        foreach ((int length, int width, int height) in sink.Frames)
        {
            await Assert.That(length).IsEqualTo(size.Width * size.Height * 4);
            await Assert.That(width).IsEqualTo(size.Width);
            await Assert.That(height).IsEqualTo(size.Height);
        }

        await Assert.That(sink.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task LayerIds_FilterTheStack_AndTheCompositorIsPutBackAfterwards()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        HashSet<string> only = new(StringComparer.Ordinal) { SceneLayerIds.Markers };
        ExportRequest request = ExportFixtures.Request(2, layerIds: only);

        bool[] before = [.. compositor.Layers.Select(l => l.IsEnabled)];

        await new SceneExportSession(compositor)
            .RunAsync(request, ExportFixtures.Source(2), sink, surfaces, null, CancellationToken.None);

        // The window's own layer stack is the one an in-app export borrows. Leaving the radar switched
        // off after the file finished writing would be a visible bug with no obvious cause.
        for (int i = 0; i < compositor.Layers.Count; i++)
        {
            await Assert.That(compositor.Layers[i].IsEnabled).IsEqualTo(before[i]);
        }
    }

    [Test]
    public async Task AnEmptyLayerSet_LeavesTheHudOff()
    {
        StubHudDataSource hud = new(ExportFixtures.Hud(3));
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. SceneLayerCatalog.SceneStackIds], null, null, hud);
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        // Empty LayerIds = "every enabled layer" — but the two HUD layers are opt-in by name.
        await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(2),
            ExportFixtures.Source(2), sink, surfaces, null, CancellationToken.None);

        await Assert.That(compositor.Find(SceneLayerIds.HudClock)).IsNotNull();
        await Assert.That(hud.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task NamingTheHudLayers_TurnsThemOn()
    {
        StubHudDataSource hud = new(ExportFixtures.Hud(3));
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. SceneLayerCatalog.SceneStackIds], null, null, hud);
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        HashSet<string> ids = new(StringComparer.Ordinal)
        {
            SceneLayerIds.Markers, SceneLayerIds.HudClock, SceneLayerIds.HudKillFeed
        };

        await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(3, layerIds: ids),
            ExportFixtures.Source(3), sink, surfaces, null, CancellationToken.None);

        await Assert.That(hud.Reads).IsGreaterThan(0);
    }

    [Test]
    public async Task ARangePastTheSourcesEnd_IsRefused()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        ExportRequest request = ExportFixtures.Request(50);

        await Assert.That(Throws<ExportValidationException>(() =>
                new SceneExportSession(compositor)
                    .RunAsync(request, ExportFixtures.Source(4), sink, surfaces, null, CancellationToken.None)
                    .GetAwaiter().GetResult()))
            .IsNotNull();
    }

    internal static T? Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return null;
        }
        catch (T expected)
        {
            return expected;
        }
    }
}

/// <summary>
///     Cancellation, from every point it can arrive. The contract is the same each time: the run throws
///     <see cref="OperationCanceledException" />, the sink is disposed <b>exactly once</b> so ffmpeg is
///     killed and the partial file removed, and the terminal progress report says
///     <see cref="ExportPhase.Cancelled" />.
/// </summary>
public class SceneExportSessionCancellationTests
{
    [Test]
    public async Task AnAlreadyCancelledToken_StopsBeforeTheFirstFrame_AndStillDisposesTheSink()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();
        using CancellationTokenSource source = new();
        source.Cancel();

        List<ExportProgress> reports = [];

        await Assert.That(await Caught(() => new SceneExportSession(compositor).RunAsync(
                ExportFixtures.Request(8), ExportFixtures.Source(8), sink, surfaces,
                new Progress<ExportProgress>(reports.Add), source.Token)))
            .IsTrue();

        await Assert.That(sink.Frames.Count).IsEqualTo(0);
        await Assert.That(sink.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task CancellingMidRender_StopsPromptly_AndReportsCancelled()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        using CancellationTokenSource source = new();
        CancellingFrameSink sink = new(source, 3);

        List<ExportProgress> reports = [];

        await Assert.That(await Caught(() => new SceneExportSession(compositor).RunAsync(
                ExportFixtures.Request(40), ExportFixtures.Source(40), sink, surfaces,
                new Progress<ExportProgress>(reports.Add), source.Token)))
            .IsTrue();

        await Assert.That(sink.Written).IsEqualTo(3);
        await Assert.That(sink.DisposeCount).IsEqualTo(1);

        // Progress<T> marshals through the synchronization context, so the terminal report may still be
        // in flight; what must be true is that no report claims completion.
        foreach (ExportProgress report in reports)
        {
            await Assert.That(report.Phase).IsNotEqualTo(ExportPhase.Completed);
        }
    }

    [Test]
    public async Task AFailingSink_SurfacesItsOwnError_AndTheSinkIsStillDisposedOnce()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new(throwOnFrame: 4);

        InvalidOperationException? failure = null;
        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(20),
                ExportFixtures.Source(20), sink, surfaces, null, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(sink.DisposeCount).IsEqualTo(1);
    }

    private static async Task<bool> Caught(Func<Task> run)
    {
        try
        {
            await run();
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}

/// <summary>Progress reporting: monotone, bounded, and honest about what it does not know yet.</summary>
public class SceneExportSessionProgressTests
{
    [Test]
    public async Task FramesDone_IsMonotone_TotalIsConstant_AndTheRunEndsCompleted()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        List<ExportProgress> reports = [];
        IProgress<ExportProgress> progress = new SynchronousProgress(reports.Add);

        await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(10),
            ExportFixtures.Source(10), sink, surfaces, progress, CancellationToken.None);

        await Assert.That(reports.Count).IsGreaterThan(10);

        int previous = -1;
        foreach (ExportProgress report in reports)
        {
            await Assert.That(report.FramesDone).IsGreaterThanOrEqualTo(previous);
            await Assert.That(report.FramesTotal).IsEqualTo(10);
            previous = report.FramesDone;
        }

        ExportProgress terminal = reports[^1];
        await Assert.That(terminal.Phase).IsEqualTo(ExportPhase.Completed);
        await Assert.That(terminal.FramesDone).IsEqualTo(10);
    }

    [Test]
    public async Task Eta_IsNullUntilTwoFramesHaveBeenMeasured()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new();

        List<ExportProgress> reports = [];
        IProgress<ExportProgress> progress = new SynchronousProgress(reports.Add);

        await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(6),
            ExportFixtures.Source(6), sink, surfaces, progress, CancellationToken.None);

        // One frame's throughput is dominated by JIT and the first surface touch; an ETA built from it
        // is a number that immediately halves, which reads as a broken estimate rather than an early one.
        foreach (ExportProgress report in reports)
        {
            if (report.FramesDone < 2)
            {
                await Assert.That(report.Eta).IsNull();
            }
        }
    }

    /// <summary>
    ///     <see cref="Progress{T}" /> posts to a synchronization context, which a direct-execution test
    ///     does not pump. This calls straight through, so the assertions above see every report.
    /// </summary>
    private sealed class SynchronousProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}

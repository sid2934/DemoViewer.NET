#region

using System.Collections.Concurrent;
using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What <see cref="SceneExportSession.Validate" /> refuses, and why each refusal is worth its own
///     rule. Every one of these is a request a user could build in the dialog or type on the command
///     line, and each would otherwise fail somewhere far less legible: inside ffmpeg, or in an
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
        ExportRequest request = ExportFixtures.Request(10) with
        {
            StartFrame = 8,
            EndFrame = 4
        };
        await Assert.That(Refusal(request)).IsNotNull();
    }

    [Test]
    public async Task AnFpsTheFormatCannotExpress_IsRefused()
    {
        // 30 does not divide 100, so a GIF at 30 fps would silently become 33.3.
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
            CultureInfo.InvariantCulture));
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

        HashSet<string> only = new(StringComparer.Ordinal)
        {
            SceneLayerIds.Markers
        };
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

        // Empty LayerIds = "every enabled layer", but the two HUD layers are opt-in by name.
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
            SceneLayerIds.Markers,
            SceneLayerIds.HudClock,
            SceneLayerIds.HudKillFeed
        };

        await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(3, layerIds: ids),
            ExportFixtures.Source(3), sink, surfaces, null, CancellationToken.None);

        await Assert.That(hud.Reads).IsGreaterThan(0);
    }

    /// <summary>
    ///     A non-CPU surface provider is refused up front, before anything is replayed.
    ///     <para>
    ///         The loop awaits the sink between frames, so it resumes on whatever pool thread the
    ///         continuation lands on, while <c>GpuSurfaceProvider</c> is bound to the thread that made
    ///         its EGL context. Export could originally only ever be handed
    ///         <see cref="CpuSurfaceProvider" />; once the auto-probe could find ANGLE, <c>dv2d export</c>
    ///         on any GPU machine died mid-run with a thread-affinity message about an internal invariant,
    ///         found by running CI's own export step.
    ///     </para>
    ///     <para>
    ///         Making GPU export work requires pinning the loop to one thread. Until then the contract is
    ///         a refusal in the caller's vocabulary, and the CLI defaults to CPU so only an explicit
    ///         <c>--gpu</c> reaches it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ANonCpuSurfaceProvider_IsRefused_BeforeAnythingIsRendered()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using MislabelledBackendProvider surfaces = new(RenderBackend.Angle);
        RecordingFrameSink sink = new();

        ExportValidationException? refusal = Throws<ExportValidationException>(() =>
            new SceneExportSession(compositor)
                .RunAsync(ExportFixtures.Request(4), ExportFixtures.Source(4), sink, surfaces, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult());

        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!.Message).Contains("Angle");

        // Before anything, not part-way through: nothing was drawn and no file was opened.
        await Assert.That(sink.Frames.Count).IsEqualTo(0);
        await Assert.That(surfaces.SurfacesCreated).IsEqualTo(0);
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

        // Same reason as the mid-render case below: the callback runs on the pool, unsynchronized.
        ConcurrentQueue<ExportProgress> reports = [];

        await Assert.That(await Caught(() => new SceneExportSession(compositor).RunAsync(
                ExportFixtures.Request(8), ExportFixtures.Source(8), sink, surfaces,
                new Progress<ExportProgress>(reports.Enqueue), source.Token)))
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

        // Thread-safe, and NOT a plain List: with no synchronization context under the test runner,
        // Progress<T> posts every callback to the thread pool, so a report still in flight lands
        // while the assertions below read the collection. A List here is a ~30 % flaky
        // "Collection was modified", found by re-running this suite repeatedly.
        ConcurrentQueue<ExportProgress> reports = [];

        await Assert.That(await Caught(() => new SceneExportSession(compositor).RunAsync(
                ExportFixtures.Request(40), ExportFixtures.Source(40), sink, surfaces,
                new Progress<ExportProgress>(reports.Enqueue), source.Token)))
            .IsTrue();

        await Assert.That(sink.Written).IsEqualTo(3);
        await Assert.That(sink.DisposeCount).IsEqualTo(1);

        // Progress<T> marshals through the synchronization context, so the terminal report may still be
        // in flight; what must be true is that no report claims completion.
        foreach (ExportProgress report in reports.ToArray())
        {
            await Assert.That(report.Phase).IsNotEqualTo(ExportPhase.Completed);
        }
    }

    [Test]
    public async Task AFailingSink_SurfacesItsOwnError_AndTheSinkIsStillDisposedOnce()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        RecordingFrameSink sink = new(4);

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

/// <summary>Progress reporting: monotone, bounded, and null where the total is not yet known.</summary>
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

/// <summary>
///     <b>Which layers an export has to be asked for by name</b>, and the single list that decides.
///     <para>
///         There were three of those lists, <c>CreateSceneStack</c>'s <c>isHud</c> pair, the session's
///         <c>OptInLayerIds</c>, and <c>ExportRequest.LayerIds</c>' prose, and a layer that reached two of
///         them was force-enabled on every export by the third. These cases drive off
///         <see cref="SceneLayerIds.OptIn" /> itself rather than naming ids, so a future opt-in layer like
///         <c>hud.roster</c> is
///         covered by them the moment it is added.
///     </para>
/// </summary>
public class ExportOptInLayerTests
{
    [Test]
    public async Task TheOptInSet_HasOneOwner_AndEveryIdInItIsBuildable()
    {
        await Assert.That(SceneExportSession.OptInLayerIds).IsSameReferenceAs(SceneLayerIds.OptIn)
            .Because("an alias keeps the request rule and the stack rule from disagreeing");

        foreach (string id in SceneLayerIds.OptIn)
        {
            // Opt-in without being registrable is the shape of a real crash: the dialog named
            // playback2d.annotations, CreateSceneStack had never heard of it, and every export under
            // shipped defaults died on "unknown layer id(s)" before rendering a frame.
            await Assert.That(SceneLayerCatalog.SceneStackIds.Contains(id, StringComparer.Ordinal))
                .IsTrue().Because($"{id} is offered but not buildable");
        }
    }

    [Test]
    public async Task ANullIncludeSet_RegistersNothingOptIn_EvenWithEverySourceOnHand()
    {
        StubHudDataSource hud = new(ExportFixtures.Hud(3));
        AnnotationSession ink = Ink();

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(null, null, null, hud, ink);

        foreach (string id in SceneLayerIds.OptIn)
        {
            await Assert.That(compositor.Find(id)).IsNull().Because($"{id} is opt-in by name");
        }

        await Assert.That(compositor.Find(SceneLayerIds.Markers)).IsNotNull()
            .Because("null include still means the whole scene");
    }

    [Test]
    public async Task AnOptInLayerAskedForWithNoSource_IsSkipped_NotBuilt()
    {
        // Every source withheld: each opt-in layer is named and starved, and the answer is an absent
        // layer rather than an empty scoreboard box, or, before the source was threaded through at all,
        // a NullReferenceException out of the layer's own constructor.
        using SceneCompositor compositor =
            SceneLayerCatalog.CreateSceneStack([.. SceneLayerCatalog.SceneStackIds]);

        foreach (string id in SceneLayerIds.OptIn)
        {
            await Assert.That(compositor.Find(id)).IsNull();
        }
    }

    [Test]
    public async Task NamingTheInk_WithADocument_ChangesTheExportedFrames()
    {
        IReadOnlyList<string> blank = await Export(Ink(0));
        IReadOnlyList<string> drawn = await Export(Ink());

        // The end of the promise design §1 goal 2 makes: not "the id is accepted" but "the ink is in the
        // file". Hashes of the rendered RGBA, so nothing short of pixels satisfies it.
        await Assert.That(blank.Count).IsEqualTo(drawn.Count);

        bool anyDifference = false;
        for (int i = 0; i < blank.Count; i++)
        {
            anyDifference |= !string.Equals(blank[i], drawn[i], StringComparison.Ordinal);
        }

        await Assert.That(anyDifference).IsTrue().Because("the stroke has to reach the exported pixels");
    }

    private static async Task<IReadOnlyList<string>> Export(AnnotationSession ink)
    {
        HashSet<string> ids = new(StringComparer.Ordinal)
        {
            SceneLayerIds.Markers,
            SceneLayerIds.Annotations
        };

        using SceneCompositor compositor =
            SceneLayerCatalog.CreateSceneStack([.. ids], null, null, null, ink);
        using CpuSurfaceProvider surfaces = new();
        HashingFrameSink sink = new();

        await new SceneExportSession(compositor).RunAsync(
            ExportFixtures.Request(4, size: new SKSizeI(64, 48), layerIds: ids),
            ExportFixtures.Source(4), sink, surfaces, null, CancellationToken.None);

        return sink.FrameHashes;
    }

    // Ink anchored to one of the budget scene's live players rather than to a world level: an entity
    // anchor resolves against whatever pane that player's Z lands in, so the stroke is visible without
    // the test having to guess which quantized level the export derived from the fixture.
    private static AnnotationSession Ink(int strokes = 1)
    {
        AnnotationDocument document = new();
        List<AnnotationElement> elements = new(strokes);
        for (int i = 0; i < strokes; i++)
        {
            elements.Add(new AnnotationElement(
                Guid.NewGuid(),
                AnnotationKind.Freehand,
                new AnnotationStyle(0xFFFF0000, 240f, 1f),
                new SpaceRef.Entity(76561190000000001, 0, 0),
                TimeEnvelope.Static,
                [new InkPoint(0, 0, 0.5f), new InkPoint(600, 400, 0.5f)],
                null));
        }

        document.Reset(elements);
        return new AnnotationSession(document);
    }
}

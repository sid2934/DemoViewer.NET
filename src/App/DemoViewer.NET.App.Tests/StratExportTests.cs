#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.ViewModels.StratBook;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratCanvasTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Strat Export (step-authoring.md §3.6, §7): the Strat Book's export pane opens on GIF, 20 fps, 640 square,
///     first step to last plus 2 s; the job renders the strat through <c>SceneExportSession</c> with no demo; and the
///     plan's criterion, a GIF of a five-step strat, is produced with its frame count and one frame pinned.
/// </summary>
[NotInParallel]
public class StratExportTests
{
    /// <summary>
    ///     The five-step strat's steps sit at ticks 320 to 2880; the default range runs to 2880 + 128, and at 20 fps
    ///     one output frame is 3.2 ticks: 1 + floor(2688 / 3.2).
    /// </summary>
    private const int FiveStepFrames = 841;

    /// <summary>The golden frame: step 3's tick, (1600 - 320) / 3.2.</summary>
    private const int GoldenFrame = 400;

    private const string GoldenName = "strat-export-five-steps-f400@640x640.png";

    [Test]
    public async Task TheTab_OpensTheDialog_OnTheStratDefaults()
    {
        (StratBookTabViewModel vm, List<Playback2DExportStatusViewModel> chips) = OpenFiveSteps();
        using (vm)
        {
            vm.OpenExportCommand.Execute(null);
            Playback2DExportDialogViewModel dialog = vm.ExportDialog!;
            ExportRequest request = dialog.BuildRequest(dialog.SelectedRange!);

            using (Assert.Multiple())
            {
                await Assert.That(dialog.Title).IsEqualTo("Export strat");
                await Assert.That(dialog.ShowDemoLayers).IsFalse();
                await Assert.That(dialog.SelectedFormat).IsEqualTo(ExportFormats.Gif);
                await Assert.That(dialog.SelectedFps).IsEqualTo(20);
                await Assert.That(dialog.ResolvedSize).IsEqualTo(new SKSizeI(640, 640));
                await Assert.That(dialog.SelectedRange!.StartFrame).IsEqualTo(320).Because("the first step");
                await Assert.That(dialog.SelectedRange.EndFrame).IsEqualTo(2880 + 128).Because("the last step plus 2 s");
                await Assert.That(dialog.EstimatedFrameCount).IsEqualTo(FiveStepFrames);
                await Assert.That(Path.GetFileName(dialog.OutputPath)).IsEqualTo("A exec.gif");
                await Assert.That(dialog.CanStart).IsTrue();

                // The seven scene layers, the ink and the clock, named: the killfeed and roster have no source.
                await Assert.That(request.LayerIds).IsEquivalentTo(StratExportJob.LayerIds(true, true));
                await Assert.That(request.LayerIds.Count).IsEqualTo(9);
                await Assert.That(request.LayerIds).DoesNotContain(SceneLayerIds.HudKillFeed);

                // The chip is mounted through the host, once, on the first Export.
                await Assert.That(chips.Count).IsEqualTo(1);
                await Assert.That(vm.ExportStatus).IsSameReferenceAs(chips[0]);
            }
        }
    }

    /// <summary>
    ///     The plan's "done": a GIF of a five-step strat, through the tab's dialog defaults and the job the tab builds,
    ///     on the managed GIF floor so the bytes do not depend on which ffmpeg this machine has. Its frame count is
    ///     pinned, and the frame at step 3 is held to a committed golden (<c>STRAT_GOLDEN_UPDATE=1</c> rewrites it).
    /// </summary>
    [Test]
    public async Task AFiveStepStrat_ExportsAGif_WithAPinnedFrameCountAndGoldenFrame()
    {
        (StratBookTabViewModel vm, _) = OpenFiveSteps();
        string dir = Path.Combine(Path.GetTempPath(), "dv-strat-export-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(dir, "five-steps.gif");
        try
        {
            using (vm)
            {
                vm.OpenExportCommand.Execute(null);
                Playback2DExportDialogViewModel dialog = vm.ExportDialog!;
                ExportRangeOption range = dialog.SelectedRange!;

                // What the dialog's Start hands the job: its request, the range in strat ticks, and the capture
                // riding the ink session.
                Scene2DExportRequest request = new(dialog.BuildRequest(range), output, string.Empty,
                    range.StartFrame, range.EndFrame, dialog.SelectedEncoder, dialog.SelectedQuality,
                    StratExportJob.Register(vm.Canvas.CaptureForExport()!));

                await NoFfmpegJob(vm).RunAsync(request, new Progress<ExportProgress>(), CancellationToken.None);
            }

            byte[] gif = await File.ReadAllBytesAsync(output);
            await Assert.That(System.Text.Encoding.ASCII.GetString(gif, 0, 6)).IsEqualTo("GIF89a");

            using SKCodec codec = SKCodec.Create(new MemoryStream(gif));
            await Assert.That(codec.FrameCount).IsEqualTo(FiveStepFrames);
            await Assert.That(StratFrameSource.OutputFrameCount(320, 3008, 20, 1.0)).IsEqualTo(FiveStepFrames);
            await Assert.That(codec.Info.Width).IsEqualTo(640);
            await Assert.That(codec.Info.Height).IsEqualTo(640);

            byte[] actual = DecodeFrame(codec, GoldenFrame);
            string golden = GoldenPath();
            if (Environment.GetEnvironmentVariable("STRAT_GOLDEN_UPDATE") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
                await File.WriteAllBytesAsync(golden, actual);
                Console.WriteLine($"[golden] wrote {golden}");
            }

            byte[] expected = await File.ReadAllBytesAsync(golden);

            // Six token labels, the step-3 label and the clock: the glyph tier's count off the scene, never tuned.
            GoldenComparison result = GoldenImageComparer.Compare(expected, actual,
                GoldenTolerance.ForLabelledFrame(640, 640, 8));
            Console.WriteLine($"[golden] {GoldenName} {result.Summary}");
            if (!result.Match)
            {
                string artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
                Directory.CreateDirectory(artifacts);
                await File.WriteAllBytesAsync(Path.Combine(artifacts, "strat-export-five-steps.actual.png"), actual);
            }

            await Assert.That(result.FailureReason).IsNull();
            await Assert.That(result.Match).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task TheJob_RefusesARequestWithNoCapture()
    {
        StratExportJob job = new(_ => null, locateFfmpeg: _ => FfmpegLocation.NotFound);
        ExportRequest core = new(0, 0, 20, new SKSizeI(64, 64), 1.0, ExportFormats.Gif,
            StratExportJob.LayerIds(false, false), new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));

        ExportRefusedException? refused = null;
        try
        {
            await job.RunAsync(new Scene2DExportRequest(core, "unused.gif", string.Empty), new Progress<ExportProgress>(),
                CancellationToken.None);
        }
        catch (ExportRefusedException ex)
        {
            refused = ex;
        }

        await Assert.That(refused?.Message).IsEqualTo(StratExportJob.NoStratRefusal);
    }

    /// <summary>
    ///     §7's validation rows: 1:55 to 0:55 at 20 fps (1,201 frames) fits the GIF cap, a whole 115 s round (2,301)
    ///     is refused with the cap message and the dialog names 10 fps (1,151 frames), and WebM has no cap.
    /// </summary>
    [Test]
    public async Task Validation_TheCapFitsAnExecute_RefusesARound_AndOffersTheRateThatFits()
    {
        await Assert.That(StratFrameSource.OutputFrameCount(320, 320 + 60 * 64, 20, 1.0)).IsEqualTo(1201);
        SceneExportSession.Validate(Request(1201, ExportFormats.Gif));

        ExportValidationException? capped = null;
        try
        {
            SceneExportSession.Validate(Request(2301, ExportFormats.Gif));
        }
        catch (ExportValidationException ex)
        {
            capped = ex;
        }

        await Assert.That(capped?.Message).Contains("capped at 1800 frames");
        SceneExportSession.Validate(Request(2301, ExportFormats.WebM, 25));

        using Playback2DExportDialogViewModel dialog = new(
            [new ExportRangeOption("whole round", 0, 115 * 64)],
            new Playback2DSettings { ExportFormatId = ExportFormats.Gif, ExportFps = 20, ExportWidth = 640, ExportHeight = 640 },
            outputFrameCount: StratFrameSource.OutputFrameCount,
            fileExists: _ => false,
            scene: new ExportDialogScene("Export strat", "strat", StratBookTabViewModel.ExportSizes,
                StratExportJob.LayerIds));

        await Assert.That(dialog.EstimatedFrameCount).IsEqualTo(2301);
        await Assert.That(dialog.CanStart).IsFalse();
        await Assert.That(dialog.ErrorBanner).Contains("At 10 fps it is 1151 frames and fits.");

        dialog.SelectedFps = 10;
        await Assert.That(dialog.CanStart).IsTrue();
    }

    [Test]
    public async Task TheGate_IsASubFeatureOfTheStratBook_AndDesktopOnly()
    {
        FeatureDescriptor descriptor = FeatureCatalog.All.Single(d => d.Id == StratBookTabViewModel.ExportFeatureId);
        using ShellModuleFeatureGate onBrowser = new(null, static () => true);
        using ShellModuleFeatureGate onDesktop = new(null, static () => false);

        using (Assert.Multiple())
        {
            await Assert.That(descriptor.Id).IsEqualTo("stratbook.export");
            await Assert.That(descriptor.ParentId).IsEqualTo("tab.stratbook");
            await Assert.That(descriptor.Scope).IsEqualTo(FeatureScope.SubFeature);
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).Contains("stratbook.export");
            await Assert.That(onBrowser.IsEnabled("stratbook.export")).IsFalse();
            await Assert.That(onDesktop.IsEnabled("stratbook.export")).IsTrue();
        }
    }

    [Test]
    public async Task TheBrowserTab_SaysExportIsUnavailable_AndOffersNoButton()
    {
        using StratBookTabViewModel vm = new(new StratStore(null), null, null, true, canvasMapLoader: _ => null);
        ModuleContext context = new(new PlaybackController(), () => null);
        context.SetFeatures(new ShellModuleFeatureGate(null, static () => true));
        vm.OnActivated(context);

        await Assert.That(vm.CanExport).IsFalse();
        await Assert.That(vm.ExportUnavailableNote).IsEqualTo("Export strat: unavailable in the browser");
    }

    [Test]
    [Arguments("A exec", "A exec")]
    [Arguments("mid/to:B?", "mid-to-B-")]
    [Arguments("  ", "strat")]
    public async Task TheFileStem_IsTheStratName_MadeSafe(string name, string expected)
    {
        string stem = StratBookTabViewModel.ExportFileStem(name);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            await Assert.That(stem).DoesNotContain(c);
        }

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(stem).IsEqualTo(expected);
        }
    }

    // A Strat Book tab with the five-step strat open, activated on a real ModuleContext carrying a strat export host
    // whose job never finds ffmpeg; the list collects the chips the host is asked to mount.
    private static (StratBookTabViewModel Vm, List<Playback2DExportStatusViewModel> Chips) OpenFiveSteps()
    {
        StratStore store = new(null);
        StratDocument document = FiveSteps();
        StratSaveResult saved = store.Save(document, [], "created");
        if (!saved.Saved)
        {
            throw new InvalidOperationException(saved.Reason);
        }

        StratBookTabViewModel vm = new(store, null, null, false, canvasMapLoader: _ => null);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.ExportJobFactory = _ => NoFfmpegJob(vm);

        List<Playback2DExportStatusViewModel> chips = [];
        ModuleContext context = new(new PlaybackController(), () => null);
        context.SetStratExportHost(new StratExportHost(null, null, null, () => new AppSettings(), _ => { },
            chips.Add, null));
        vm.OnActivated(context);
        vm.Session.Open(document.Id);
        return (vm, chips);
    }

    private static StratExportJob NoFfmpegJob(StratBookTabViewModel vm) =>
        new(vm.Canvas.MapLoader, locateFfmpeg: _ => FfmpegLocation.NotFound);

    private static ExportRequest Request(int frames, string format, int fps = 20) =>
        new(0, frames - 1, fps, new SKSizeI(640, 640), 1.0, format, StratExportJob.LayerIds(true, true),
            new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));

    // A GIF frame may be stored as a delta on the one before it, so frames are decoded in order into one bitmap,
    // each on top of the frame it names as its base.
    private static byte[] DecodeFrame(SKCodec codec, int index)
    {
        SKImageInfo info = new(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap bitmap = new(info);
        SKCodecFrameInfo[] frames = codec.FrameInfo;
        for (int i = 0; i <= index; i++)
        {
            SKCodecOptions options = new(i, frames[i].RequiredFrame);
            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), options);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidOperationException($"GIF frame {i}: {result}");
            }
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static string GoldenPath()
    {
        string repo = DemoTestHelper.FindRepoRoot()
                      ?? throw new InvalidOperationException("repo root not found from the test output directory");
        return Path.Combine(repo, "tests", "fixtures", "strats", "export", GoldenName);
    }
}

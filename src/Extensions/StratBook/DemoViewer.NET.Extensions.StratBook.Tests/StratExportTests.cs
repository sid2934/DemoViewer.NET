#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Export;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using SkiaSharp;
using TUnit.Assertions.Enums;
using static DemoViewer.NET.AppTests.StratCanvasTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Strat Export: the Strat Book's export pane opens on GIF, 20 fps, 640 square,
///     first step to last plus 2 s; the job renders the strat through <c>SceneExportSession</c> with no demo; and
///     a GIF of a five-step strat is produced with its frame count and one frame pinned.
/// </summary>
[NotInParallel(nameof(StratExportTests))]
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
        await HeadlessSession.RunOnUi(async () =>
        {
            (StratBookTabViewModel vm, List<Playback2DExportStatusViewModel> chips, ExtensionSceneExport export) = OpenFiveSteps();
            using (export)
            using (vm)
            {
                vm.OpenExportCommand.Execute(null);
                Playback2DExportDialogViewModel dialog = (Playback2DExportDialogViewModel)vm.ExportDialog!;
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
                    await Assert.That(request.LayerIds).IsEquivalentTo(StratExportJob.LayerIds(true, true), CollectionOrdering.Matching);
                    await Assert.That(request.LayerIds.Count).IsEqualTo(9);
                    await Assert.That(request.LayerIds).DoesNotContain(SceneLayerIds.HudKillFeed);

                    // The chip is the host's, mounted once, on the first Export.
                    await Assert.That(chips.Count).IsEqualTo(1);
                }
            }
        });
    }

    /// <summary>
    ///     A GIF of a five-step strat, through the tab's dialog defaults and the job the tab builds,
    ///     on the managed GIF floor so the bytes do not depend on which ffmpeg this machine has. Its frame count is
    ///     pinned, and the frame at step 3 is held to a committed golden (<c>STRAT_GOLDEN_UPDATE=1</c> rewrites it).
    /// </summary>
    [Test]
    public async Task AFiveStepStrat_ExportsAGif_WithAPinnedFrameCountAndGoldenFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (StratBookTabViewModel vm, _, ExtensionSceneExport export) = OpenFiveSteps();
            string dir = Path.Combine(Path.GetTempPath(), "dv-strat-export-" + Guid.NewGuid().ToString("N"));
            string output = Path.Combine(dir, "five-steps.gif");
            string silencedOutput = Path.Combine(dir, "five-steps-silenced.gif");
            try
            {
                using (export)
                using (vm)
                {
                    vm.OpenExportCommand.Execute(null);
                    Playback2DExportDialogViewModel dialog = (Playback2DExportDialogViewModel)vm.ExportDialog!;
                    ExportRangeOption range = dialog.SelectedRange!;

                    // What the pane's Start hands the host: its request and the range in strat ticks, over the
                    // scene the tab captures. On the managed GIF floor, so the bytes do not depend on which
                    // ffmpeg this machine has, and with no map art, as the canvas has none here.
                    await RenderAsync(vm, dialog, range, output, null);

                    // The same export with every text layer silenced: the pixels that differ between the two
                    // frames are the glyph mask the golden is judged under.
                    await RenderAsync(vm, dialog, range, silencedOutput, SceneLayerCatalog.SilenceText);
                }

                byte[] gif = await File.ReadAllBytesAsync(output);
                await Assert.That(System.Text.Encoding.ASCII.GetString(gif, 0, 6)).IsEqualTo("GIF89a");

                using SKCodec codec = SKCodec.Create(new MemoryStream(gif));
                await Assert.That(codec.FrameCount).IsEqualTo(FiveStepFrames);
                await Assert.That(StratFrameSource.OutputFrameCount(320, 3008, 20, 1.0)).IsEqualTo(FiveStepFrames);
                await Assert.That(codec.Info.Width).IsEqualTo(640);
                await Assert.That(codec.Info.Height).IsEqualTo(640);

                byte[] actual = DecodeFrame(codec, GoldenFrame);
                using SKCodec silencedCodec = SKCodec.Create(new MemoryStream(await File.ReadAllBytesAsync(silencedOutput)));
                byte[] silenced = DecodeFrame(silencedCodec, GoldenFrame);
                string golden = GoldenPath();
                if (Environment.GetEnvironmentVariable("STRAT_GOLDEN_UPDATE") == "1")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
                    await File.WriteAllBytesAsync(golden, actual);
                    Console.WriteLine($"[golden] wrote {golden}");
                }

                byte[] expected = await File.ReadAllBytesAsync(golden);
                // Text is judged under its own ink, as dv2d golden verify does: the golden stands in under the
                // mask. The GIF's one global palette moves with the text pixels, so every colour outside the
                // mask shifts by a few levels on another rasteriser (16 measured on ubuntu) and the 11x11
                // window rule reads that as structure on the near-black grid. This tolerance keeps the mean
                // SSIM floor and a 32-level ceiling, which a missing token, a wrong colour or a moved marker
                // still breaks; the strat canvas goldens pin the pixels on the uncompressed path.
                GlyphAttribution ink = GlyphAttribution.Measure(expected, actual, silenced);
                GoldenComparison result = GoldenImageComparer.Compare(expected, ink.GlyphPatchedPng,
                    GoldenTolerance.DefaultPerceptual with { MaxChannelDelta = 32, MinWindowSsim = 0 });
                Console.WriteLine($"[golden] {ink.Describe(GoldenName, 8)}; patched: {result.Summary}");
                if (!result.Match)
                {
                    string artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
                    Directory.CreateDirectory(artifacts);
                    await File.WriteAllBytesAsync(Path.Combine(artifacts, "strat-export-five-steps.actual.png"), actual);
                    await File.WriteAllBytesAsync(Path.Combine(artifacts, "strat-export-five-steps.silenced.png"), silenced);
                }

                await Assert.That(result.FailureReason).IsNull().Because(result.FailureReason ?? "the export succeeded");
                await Assert.That(result.Match).IsTrue();
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        });
    }

    /// <summary>
    ///     A pane opened over a strat that is then closed captures nothing, and the host refuses the run with
    ///     the copy the pane named rather than with its own.
    /// </summary>
    [Test]
    public async Task TheHost_RefusesAStartWithNoCapture_WithTheStratsOwnCopy()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (StratBookTabViewModel vm, _, ExtensionSceneExport export) = OpenFiveSteps();
            using (export)
            using (vm)
            {
                vm.OpenExportCommand.Execute(null);
                Playback2DExportDialogViewModel dialog = (Playback2DExportDialogViewModel)vm.ExportDialog!;
                ExportRangeOption range = dialog.SelectedRange!;
                vm.Session.Close();

                ExportSceneSetup? setup = null;
                ExportRefusedException? refused = null;
                try
                {
                    setup = Capture(vm, dialog, range);
                }
                catch (ExportRefusedException ex)
                {
                    refused = ex;
                }

                await Assert.That(setup).IsNull();
                await Assert.That(refused?.Message).IsEqualTo(StratExportJob.NoStratRefusal);
            }
        });
    }

    /// <summary>
    ///     Validation: 1:55 to 0:55 at 20 fps (1,201 frames) fits the GIF cap, a whole 115 s round (2,301)
    ///     is refused with the cap message and the dialog names 10 fps (1,151 frames), and WebM has no cap.
    /// </summary>
    [Test]
    public async Task Validation_TheCapFitsAnExecute_RefusesARound_AndOffersTheRateThatFits()
    {
        await HeadlessSession.RunOnUi(async () =>
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
                scene: new ExportDialogScene("Export strat", "strat", FirstPartySceneExport.SizePresets,
                    StratExportJob.LayerIds));

            await Assert.That(dialog.EstimatedFrameCount).IsEqualTo(2301);
            await Assert.That(dialog.CanStart).IsFalse();
            await Assert.That(dialog.ErrorBanner).Contains("At 10 fps it is 1151 frames and fits.");

            dialog.SelectedFps = 10;
            await Assert.That(dialog.CanStart).IsTrue();
        });
    }

    [Test]
    public async Task TheGate_IsASubFeatureOfTheStratBook_AndDesktopOnly()
    {
        await HeadlessSession.RunOnUi(async () =>
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
        });
    }

    [Test]
    public async Task TheBrowserTab_SaysExportIsUnavailable_AndOffersNoButton()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = new(new StratStore(null), null, null, true, canvasMapLoader: _ => null);
            ModuleContext context = new(new PlaybackController(), () => null);
            context.SetFeatures(new ShellModuleFeatureGate(null, static () => true));
            vm.OnActivated(context);

            await Assert.That(vm.CanExport).IsFalse();
            await Assert.That(vm.ExportUnavailableNote).IsEqualTo("Export strat: unavailable in the browser");
        });
    }

    [Test]
    [Arguments("A exec", "A exec")]
    [Arguments("mid/to:B?", "mid-to-B-")]
    [Arguments("  ", "strat")]
    public async Task TheFileStem_IsTheStratName_MadeSafe(string name, string expected)
    {
        await HeadlessSession.RunOnUi(async () =>
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
        });
    }

    // A Strat Book tab with the five-step strat open, activated on a real ModuleContext carrying a strat export host
    // whose job never finds ffmpeg; the list collects the chips the host is asked to mount.
    // The gate and the queue are the machine's, one per process: nothing here starts an export, and a fresh
    // pair per test would leave a queue running for every case in the suite.
    private static readonly HeavyJobGate _gate = new();

    private static readonly FirstPartyExports _exports = new(_gate, new DemoProcessingQueue(_gate, a => a()));

    private static (StratBookTabViewModel Vm, List<Playback2DExportStatusViewModel> Chips, ExtensionSceneExport Export)
        OpenFiveSteps()
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

        List<Playback2DExportStatusViewModel> chips = [];
        ExtensionSceneExport export = new(StratBookPack.PackId, StratBookPack.PackFeatureId, _exports, null,
            new ChipCollector(chips), ExtensionGuard.Standalone(new StratBookPack()), null);

        ModuleContext context = new(new PlaybackController(), () => null);
        context.RegisterService<IExtensionSceneExport>(() => export);
        vm.OnActivated(context);
        vm.Session.Open(document.Id);
        return (vm, chips, export);
    }

    // The scene the pane's Start would hand over, as the host builds it: the tab's capture, no map art (the
    // canvas has none here, and the golden is recorded on the grid), and the range in strat ticks.
    private static ExportSceneSetup? Capture(StratBookTabViewModel vm, Playback2DExportDialogViewModel dialog,
        ExportRangeOption range)
    {
        if (vm.Canvas.CaptureForExport() is not { } capture)
        {
            throw new ExportRefusedException(StratExportJob.NoStratRefusal);
        }

        // Dark, not the session's theme: the pane resolves the theme at Start and puts it on the request,
        // and a request with no palette falls back to Dark, which is what the golden was recorded under.
        return SceneExportScenes.ToSetup(StratExportJob.Scene(capture), ScenePalette.Dark,
            new SceneFramesRequest(null, range.StartFrame, range.EndFrame, dialog.SelectedFps, 1.0), null);
    }

    // One strat export on the managed GIF floor, through the one runner, with the text layers optionally
    // silenced. locateFfmpeg is pinned so the bytes do not depend on which ffmpeg this machine has.
    private static async Task RenderAsync(StratBookTabViewModel vm, Playback2DExportDialogViewModel dialog,
        ExportRangeOption range, string output, Action<SceneCompositor>? configureLayers)
    {
        ExportSceneSetup setup = Capture(vm, dialog, range)! with { ConfigureLayers = configureLayers };
        Scene2DExportRequest request = new(dialog.BuildRequest(range), output, string.Empty,
            range.StartFrame, range.EndFrame, dialog.SelectedEncoder, dialog.SelectedQuality,
            setup.Annotations);

        // Every seam named, the 2D tab's rule: an omitted optional is invisible at the call site.
        SceneExportRunner job = new(_ => setup, surfaces: RenderSurfaceProviderFactory.CreateCpu,
            managedFfmpegDirectory: static () => null, log: null, encoderProbe: EncoderProbeCache.Shared,
            locateFfmpeg: _ => FfmpegLocation.NotFound, noSetupRefusal: StratExportJob.NoStratRefusal);
        await job.RunAsync(request, new Progress<ExportProgress>(), CancellationToken.None);
    }

    private sealed class ChipCollector(List<Playback2DExportStatusViewModel> chips) : IFirstPartyExportChips
    {
        public void Mount(string chipId, string featureId, Playback2DExportStatusViewModel status) =>
            chips.Add(status);
    }

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

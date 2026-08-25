#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The export dialog, as a pure view-model: no window, no filesystem, no ffmpeg on the machine. Every
///     environment dependency is an injected delegate, which is the same seam
///     <c>HighlightReelDialogViewModel</c> uses and the reason these cases are machine-independent.
///     <para>
///         The rules being checked belong to <c>SceneExportSession</c>. What is asserted here is that the
///         dialog <b>routes</b> them — a rule re-implemented in the VM is a rule <c>dv2d export</c> would
///         not have.
///     </para>
/// </summary>
public class Playback2DExportDialogTests
{
    [Test]
    public async Task WithoutAnOutputPath_ItCannotStart()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        await Assert.That(vm.CanStart).IsTrue();

        vm.OutputPath = "   ";
        await Assert.That(vm.CanStart).IsFalse();
        await Assert.That(vm.ErrorBanner).IsNotNull();
    }

    [Test]
    public async Task WithNoFfmpeg_VideoIsBlocked_AndGifStaysAvailable()
    {
        Playback2DExportDialogViewModel vm = Dialog(ffmpeg: () => FfmpegLocation.NotFound);

        await Assert.That(vm.ShowFfmpegStrip).IsTrue();
        await Assert.That(vm.CanStart).IsFalse();

        // The managed GIF floor is exactly why "no ffmpeg" is not a dead end.
        vm.SelectedFormat = ExportFormats.Gif;
        await Assert.That(vm.CanStart).IsTrue();
    }

    [Test]
    public async Task SwitchingFormat_RelistsTheFrameRates()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.SelectedFps = 60;

        vm.SelectedFormat = ExportFormats.Gif;

        // GIF frame delays are whole centiseconds, so 60 does not exist there. The list is
        // SceneExportSession's, and the nearest supported rate is chosen rather than resetting to the head.
        await Assert.That(vm.AvailableFps).IsEquivalentTo(SceneExportSession.SupportedFps(ExportFormats.Gif));
        await Assert.That(vm.AvailableFps.Contains(vm.SelectedFps)).IsTrue();
        await Assert.That(vm.SelectedFps).IsEqualTo(50);
    }

    [Test]
    public async Task ACustomSize_SnapsDownToEven()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.UseCustomSize = true;
        vm.CustomWidthText = "1921";
        vm.CustomHeightText = "1081";

        // A user typing 1921 meant 1920. yuv420p's chroma subsampling is not something they should have
        // to know about, so the dialog snaps instead of refusing (plan D8).
        await Assert.That(vm.ResolvedSize.Width).IsEqualTo(1920);
        await Assert.That(vm.ResolvedSize.Height).IsEqualTo(1080);
        await Assert.That(vm.CanStart).IsTrue();
    }

    [Test]
    public async Task WhileLiveSyncIsActive_ItCannotStart_AndSaysWhy()
    {
        Playback2DExportDialogViewModel vm = Dialog(liveSync: () => true);

        await Assert.That(vm.CanStart).IsFalse();
        await Assert.That(vm.ErrorBanner).IsEqualTo(Services.Export.ExportJobService.LiveSyncRefusal);
    }

    [Test]
    public async Task AGifOverTheFrameCap_IsRefusedByTheSessionsOwnValidator()
    {
        Playback2DExportDialogViewModel vm = Dialog(
            ranges: [new ExportRangeOption("huge", 0, 100_000)],
            outputFrameCount: (start, end, _, _) => end - start + 1);

        vm.SelectedFormat = ExportFormats.Gif;

        await Assert.That(vm.CanStart).IsFalse();
        await Assert.That(vm.ErrorBanner!).Contains("GIF");
    }

    [Test]
    public async Task IncludingTheHud_AddsBothHudLayerIds()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.IncludeHud = true;

        ExportRequest request = vm.BuildRequest(vm.Ranges[0]);

        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudClock)).IsTrue();
        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudKillFeed)).IsTrue();
    }

    [Test]
    public async Task VisionIsOffByDefault()
    {
        Playback2DExportDialogViewModel vm = Dialog();

        // R3's first lever: solving line of sight is the most expensive thing in the frame, so it is the
        // one overlay a user opts into rather than out of.
        await Assert.That(vm.IncludeVision).IsFalse();
        await Assert.That(vm.BuildRequest(vm.Ranges[0]).LayerIds.Contains(SceneLayerIds.Vision)).IsFalse();
    }

    [Test]
    public async Task TheLiveCamera_IsCapturedOnStart_NotOnSelection()
    {
        int captures = 0;
        Playback2DExportDialogViewModel vm = Dialog(captureCamera: () =>
        {
            captures++;
            return new CameraScript.FollowPlayer(42);
        });

        // Plan D12: mirroring the live view is a CAPTURE. Taking it when the user picked the option would
        // mean panning between then and Start silently changed the export.
        vm.SelectedFormat = ExportFormats.Mp4;
        vm.SelectedFps = 30;
        await Assert.That(captures).IsEqualTo(0);

        vm.BuildRequest(vm.Ranges[0]);
        await Assert.That(captures).IsEqualTo(1);
    }

    [Test]
    public async Task TheDefaultSize_Is720p()
    {
        Playback2DExportDialogViewModel vm = Dialog();

        // Measured, not preferred: 1280x720 exports at 1.83x realtime on a CPU where 1920x1080 manages
        // 0.97x. 1080p is still one click away.
        await Assert.That(vm.ResolvedSize.Width).IsEqualTo(1280);
        await Assert.That(vm.ResolvedSize.Height).IsEqualTo(720);
    }

    [Test]
    public async Task TheDefaultEncoder_IsAuto_AtStandardQuality()
    {
        Playback2DExportDialogViewModel vm = Dialog();

        // P2 D4. `auto` is the only value that cannot fail for an environment reason: it walks the
        // ladder and lands on tuned software where no hardware verifies. A named rung is taken literally
        // and refused if this machine cannot run it, which is honest but is not a default.
        await Assert.That(vm.SelectedEncoder).IsEqualTo(EncoderLadder.Auto);
        await Assert.That(vm.SelectedQuality).IsEqualTo(ExportQualities.Standard);
    }

    [Test]
    public async Task TheEncoderList_TracksTheFormatsLadder()
    {
        Playback2DExportDialogViewModel vm = Dialog();

        await Assert.That(vm.AvailableEncoders).Contains("av1_nvenc");
        await Assert.That(vm.AvailableEncoders.Contains("h264_nvenc")).IsFalse();

        vm.SelectedFormat = ExportFormats.Mp4;

        await Assert.That(vm.AvailableEncoders).Contains("h264_nvenc");
        await Assert.That(vm.AvailableEncoders.Contains("av1_nvenc")).IsFalse();

        // Every list offers the two format-independent answers, and offers the software rung ONLY under
        // that name — listing libvpx-vp9 as well would make one choice look like two.
        await Assert.That(vm.AvailableEncoders).Contains(EncoderLadder.Auto);
        await Assert.That(vm.AvailableEncoders).Contains(EncoderLadder.Software);
        await Assert.That(vm.AvailableEncoders.Contains("libx264")).IsFalse();
    }

    [Test]
    public async Task ChangingFormat_DropsARungTheNewLadderDoesNotHave()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.SelectedEncoder = "av1_nvenc";

        vm.SelectedFormat = ExportFormats.Mp4;

        // The two ladders share no rung names, so a carried-over av1_nvenc would be a value the selector
        // then refuses — an export that fails because the user changed container. It degrades to `auto`,
        // which is what picking a hardware rung meant in the first place.
        await Assert.That(vm.SelectedEncoder).IsEqualTo(EncoderLadder.Auto);
    }

    [Test]
    public async Task ASavedRungFromTheOtherLadder_SeedsAsAuto()
    {
        Playback2DExportDialogViewModel vm = new(
            [new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings { ExportFormatId = ExportFormats.WebM, ExportEncoder = "h264_nvenc" },
            job: null,
            ffmpegLocator: () => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath),
            fileExists: _ => false);

        // A hand-edited settings file is the other way this happens, and it must not throw at startup.
        await Assert.That(vm.SelectedEncoder).IsEqualTo(EncoderLadder.Auto);
    }

    [Test]
    public async Task AnUnknownSavedQuality_SeedsAsStandard()
    {
        Playback2DExportDialogViewModel vm = new(
            [new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings { ExportQuality = "insane" },
            job: null,
            ffmpegLocator: () => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath),
            fileExists: _ => false);

        await Assert.That(vm.SelectedQuality).IsEqualTo(ExportQualities.Standard);
    }

    [Test]
    public async Task Start_PersistsTheEncoderAndQuality()
    {
        AppSettings persisted = new();
        Playback2DExportDialogViewModel vm = new(
            [new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings(),
            new StubExportJobService(),
            ffmpegLocator: () => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath),
            persistDefaults: mutate => mutate(persisted),
            fileExists: _ => false);

        vm.SelectedQuality = ExportQualities.Best;
        vm.SelectedEncoder = "av1_nvenc";
        vm.StartCommand.Execute(null);

        await Assert.That(persisted.Playback2D.ExportQuality).IsEqualTo(ExportQualities.Best);
        await Assert.That(persisted.Playback2D.ExportEncoder).IsEqualTo("av1_nvenc");
    }

    [Test]
    public async Task Start_CarriesTheChoicesOntoTheRequest()
    {
        StubExportJobService job = new();
        Playback2DExportDialogViewModel vm = new(
            [new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings(),
            job,
            ffmpegLocator: () => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath),
            fileExists: _ => false);

        vm.SelectedQuality = ExportQualities.Draft;
        vm.SelectedEncoder = EncoderLadder.Software;
        vm.StartCommand.Execute(null);

        // They ride the REQUEST, not the runner — plan D5's per-session shape, and what lets two exports
        // in one process disagree about which rung they are on.
        await Assert.That(job.Started).IsNotNull();
        await Assert.That(job.Started!.Quality).IsEqualTo(ExportQualities.Draft);
        await Assert.That(job.Started.EncoderOverride).IsEqualTo(EncoderLadder.Software);
    }

    private static Playback2DExportDialogViewModel Dialog(
        IReadOnlyList<ExportRangeOption>? ranges = null,
        Func<FfmpegLocation>? ffmpeg = null,
        Func<bool>? liveSync = null,
        Func<CameraScript>? captureCamera = null,
        Func<int, int, int, double, int>? outputFrameCount = null) =>
        new(ranges ?? [new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings(),
            job: null,
            captureLiveCamera: captureCamera,
            outputFrameCount: outputFrameCount,
            ffmpegLocator: ffmpeg ?? (() => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath)),
            isLiveSyncSessionActive: liveSync,
            persistDefaults: null,
            fileExists: _ => false);

    /// <summary>Records the one request the dialog hands off, and nothing else.</summary>
    private sealed class StubExportJobService : IExportJobService
    {
        public Scene2DExportRequest? Started { get; private set; }

        public ExportJobStatus Status => ExportJobStatus.Idle;

        public event EventHandler<ExportJobStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Start(Scene2DExportRequest request) => Started = request;

        public Task CancelAsync() => Task.CompletedTask;
    }
}

/// <summary>
///     The <c>playback2d.export</c> gate. Its id is a persisted override key, and it is the first entry in
///     <see cref="ShellModuleFeatureGate.DesktopOnlyIds" /> — both are locks, not incidental facts.
/// </summary>
public class Playback2DExportFeatureGateTests
{
    [Test]
    public async Task TheIdIsExactlyPlayback2DExport_AndItCascadesFromTheTab()
    {
        FeatureDescriptor descriptor = FeatureCatalog.All.Single(d => d.Id == "playback2d.export");

        // Persisted-key lock: settings write Features:Overrides:{id}, so renaming this silently discards
        // every user's choice.
        await Assert.That(descriptor.Id).IsEqualTo("playback2d.export");
        await Assert.That(descriptor.ParentId).IsEqualTo("tab.playback2d");
        await Assert.That(descriptor.Scope).IsEqualTo(FeatureScope.SubFeature);
        await Assert.That(descriptor.GroupId).IsNull();
    }

    [Test]
    public async Task ItIsTheOnlyDesktopOnlyModuleId_AndTheAndLivesInOnePlace()
    {
        await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds.Contains("playback2d.export")).IsTrue();

        // B5 D4: the !OperatingSystem.IsBrowser() AND for module-facing ids lives here and nowhere else.
        // A second shim would be a second answer to the same question.
        foreach (string id in ShellModuleFeatureGate.DesktopOnlyIds)
        {
            await Assert.That(FeatureCatalog.All.Any(d => d.Id == id)).IsTrue();
        }
    }

    [Test]
    public async Task ItSitsInTheContiguousPlayback2DBlock()
    {
        List<string> ids = [.. FeatureCatalog.All.Select(d => d.Id)];
        int first = ids.IndexOf("playback2d.timeline");
        int export = ids.IndexOf("playback2d.export");

        // The block's order is documented in plans/00-overview.md §3.10 and read as one group in
        // Settings; the leader-lock tests above it depend on nothing here moving.
        await Assert.That(export).IsGreaterThan(first);
        for (int i = first; i <= export; i++)
        {
            await Assert.That(ids[i].StartsWith("playback2d.", StringComparison.Ordinal)).IsTrue();
        }
    }
}

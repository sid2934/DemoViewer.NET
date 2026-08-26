#region

using System.ComponentModel;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
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
    public async Task IncludingTheHud_AddsEveryHudLayerId()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.IncludeHud = true;

        ExportRequest request = vm.BuildRequest(vm.Ranges[0]);

        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudClock)).IsTrue();
        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudKillFeed)).IsTrue();
        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudRoster)).IsTrue()
            .Because("a saved ExportIncludeHud=true asked for the HUD, and the HUD now has three parts");
    }

    /// <summary>
    ///     One checkbox for three layers was too coarse the moment D3b added the third: a user who wants
    ///     the score strip but not a scoreboard down both edges of a 720p clip could only have both or
    ///     neither.
    /// </summary>
    [Test]
    public async Task EachHudLayer_HasItsOwnToggle()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.IncludeHudKillFeed = false;
        vm.IncludeHudRoster = false;

        ExportRequest request = vm.BuildRequest(vm.Ranges[0]);

        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudClock)).IsTrue();
        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudKillFeed)).IsFalse();
        await Assert.That(request.LayerIds.Contains(SceneLayerIds.HudRoster)).IsFalse();
    }

    [Test]
    public async Task TheHudMaster_TurnsOffEveryPart_WhateverTheThreeSay()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.IncludeHud = false;

        // ExportIncludeHud is the key already in users' files, so "off" has to keep meaning "no HUD"
        // rather than becoming an override the three sub-toggles can win against.
        IReadOnlySet<string> ids = vm.BuildRequest(vm.Ranges[0]).LayerIds;

        foreach (string id in new[]
                 {
                     SceneLayerIds.HudClock, SceneLayerIds.HudKillFeed, SceneLayerIds.HudRoster
                 })
        {
            await Assert.That(ids.Contains(id)).IsFalse().Because($"{id} is under the master switch");
        }
    }

    [Test]
    public async Task TheHudToggles_SeedFromSettings_AndPersistBack()
    {
        Playback2DSettings saved = new()
        {
            ExportIncludeHudClock = false,
            ExportIncludeHudRoster = false
        };

        AppSettings written = new();
        Playback2DExportDialogViewModel vm = Dialog(
            defaults: saved,
            job: new StubExportJobService(),
            persist: mutate => mutate(written));

        await Assert.That(vm.IncludeHudClock).IsFalse();
        await Assert.That(vm.IncludeHudKillFeed).IsTrue();
        await Assert.That(vm.IncludeHudRoster).IsFalse();

        vm.IncludeHudRoster = true;
        vm.StartCommand.Execute(null);

        await Assert.That(written.Playback2D.ExportIncludeHudClock).IsFalse();
        await Assert.That(written.Playback2D.ExportIncludeHudRoster).IsTrue();
        await Assert.That(written.Playback2D.ExportIncludeHudKillFeed).IsTrue();
    }

    /// <summary>
    ///     The whole point of the roster being an id rather than a flag: naming it, with a HUD source on
    ///     hand, produces a registered layer. D3a's <c>Starved()</c> routes it — no new source kind, no new
    ///     line in the catalog.
    /// </summary>
    [Test]
    public async Task TheRosterId_BuildsALayer_WhenAHudSourceIsSupplied()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        ExportRequest request = vm.BuildRequest(vm.Ranges[0]);

        using SceneCompositor fed = SceneLayerCatalog.CreateSceneStack(
            [.. request.LayerIds], null, null, new EmptyHud(), EmptyInk());
        await Assert.That(fed.Find(SceneLayerIds.HudRoster)).IsNotNull();

        using SceneCompositor starved = SceneLayerCatalog.CreateSceneStack(
            [.. request.LayerIds], null, null, null, EmptyInk());
        await Assert.That(starved.Find(SceneLayerIds.HudRoster)).IsNull()
            .Because("asked for with nothing to feed it, a HUD layer is skipped, not an empty box");
    }

    /// <summary>A HUD source with nothing in it — enough to prove a layer was registered.</summary>
    private sealed class EmptyHud : Playback2D.Core.Hud.IHudDataSource
    {
        public Playback2D.Core.Hud.HudSnapshot At(int tick) =>
            Playback2D.Core.Hud.HudSnapshot.Empty with { Tick = tick };
    }

    /// <summary>
    ///     <b>The defect, at the exact seam it bit.</b> <c>ExportIncludeAnnotations</c> ships true, so this
    ///     is the id set every first export produces — and <c>CreateSceneStack</c> threw
    ///     <c>ArgumentException: unknown layer id(s): playback2d.annotations</c> on it, killing the export
    ///     before its first frame. The comment on the offending line claimed unknown ids were ignored.
    /// </summary>
    [Test]
    public async Task TheShippedDefaultIdSet_BuildsAStack_WithTheInkInIt()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        await Assert.That(vm.IncludeAnnotations).IsTrue().Because("the box ships checked");

        ExportRequest request = vm.BuildRequest(vm.Ranges[0]);

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. request.LayerIds], null, null, null, EmptyInk());

        await Assert.That(compositor.Find(SceneLayerIds.Annotations)).IsNotNull()
            .Because("a checkbox that stops throwing but still draws nothing is only half the fix");
    }

    [Test]
    public async Task WithAnnotationsUnchecked_TheStackHasNoInk()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.IncludeAnnotations = false;

        ExportRequest request = vm.BuildRequest(vm.Ranges[0]);

        // The document is supplied either way: unchecked has to mean "not drawn", not "nothing to draw
        // with", or the setting would be honoured only by accident on a tab with no strokes on it.
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. request.LayerIds], null, null, null, EmptyInk());

        await Assert.That(request.LayerIds.Contains(SceneLayerIds.Annotations)).IsFalse();
        await Assert.That(compositor.Find(SceneLayerIds.Annotations)).IsNull();
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

    /// <summary>
    ///     <b>The second export ever attempted.</b> The overwrite remark was returned from
    ///     <c>Validate()</c> as the <c>ErrorBanner</c>, and <c>CanStart</c> is <c>ErrorBanner is null</c> —
    ///     so naming a path that already exists produced a red banner and a dead Export button. The
    ///     default path is a constant, which means every export after the first landed on exactly that.
    /// </summary>
    [Test]
    public async Task WhenTheOutputFileExists_ItStillStarts_AndSaysItWillOverwrite()
    {
        Playback2DExportDialogViewModel vm = Dialog(fileExists: _ => true);

        await Assert.That(vm.CanStart).IsTrue()
            .Because("overwriting is what naming an existing path MEANS, not a reason to refuse");
        await Assert.That(vm.StartCommand.CanExecute(null)).IsTrue();
        await Assert.That(vm.ErrorBanner).IsNull();

        // The remark still has to be made — it just belongs in the channel that does not gate the button.
        await Assert.That(vm.NoticeBanner).IsNotNull();
        await Assert.That(vm.NoticeBanner!).Contains("already exists");
    }

    [Test]
    public async Task ARealRefusal_StillClearsTheNotice_OutOfTheErrorChannel()
    {
        Playback2DExportDialogViewModel vm = Dialog(fileExists: _ => true, liveSync: () => true);

        // Both banners are live at once and they say different things: one is why it will not start, the
        // other is what will happen when it does.
        await Assert.That(vm.CanStart).IsFalse();
        await Assert.That(vm.ErrorBanner).IsEqualTo(Services.Export.ExportJobService.LiveSyncRefusal);
        await Assert.That(vm.NoticeBanner!).Contains("already exists");
    }

    /// <summary>
    ///     ffmpeg infers the container from the extension, and nothing downstream overrides it — so a
    ///     stale extension is not cosmetic. Picking MP4 over a path still ending <c>.webm</c> produced a
    ///     WebM, named <c>.webm</c>, encoded under MP4's settings.
    /// </summary>
    [Test]
    public async Task ChangingTheFormat_RewritesTheOutputExtension()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.OutputPath = Path.Combine("videos", "round-7.webm");

        vm.SelectedFormat = ExportFormats.Mp4;
        await Assert.That(Path.GetExtension(vm.OutputPath)).IsEqualTo(".mp4");

        vm.SelectedFormat = ExportFormats.Gif;
        await Assert.That(Path.GetExtension(vm.OutputPath)).IsEqualTo(".gif");

        // The directory and the stem are the user's; only the container is the format's.
        await Assert.That(Path.GetFileNameWithoutExtension(vm.OutputPath)).IsEqualTo("round-7");
        await Assert.That(Path.GetDirectoryName(vm.OutputPath)).IsEqualTo("videos");
    }

    /// <summary>
    ///     <b>Two Starts cannot trade documents.</b> The ink used to be a field on the tab, written by the
    ///     dialog's Start and read by the runner's setup closure — but the job awaits the heavy-job gate
    ///     BEFORE that closure runs, so a second Start (even one the gate then refused) replaced the
    ///     document the first, still-parked export was going to burn in. It rides the request now, and a
    ///     request is one-per-run by construction.
    /// </summary>
    [Test]
    public async Task EachStart_CarriesItsOwnInk()
    {
        RecordingExportJobService job = new();
        int strokes = 0;
        Playback2DExportDialogViewModel vm = Dialog(job: job, captureInk: () => InkWith(++strokes));

        vm.StartCommand.Execute(null);
        vm.StartCommand.Execute(null);

        await Assert.That(job.Requests.Count).IsEqualTo(2);
        await Assert.That(job.Requests[0].Ink!.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(job.Requests[1].Ink!.Document.Elements.Count).IsEqualTo(2)
            .Because("the second Start's snapshot must not have replaced the first's");
        await Assert.That(ReferenceEquals(job.Requests[0].Ink, job.Requests[1].Ink)).IsFalse();
    }

    [Test]
    public async Task WithNoInkCapture_TheRequestSimplyCarriesNone()
    {
        RecordingExportJobService job = new();
        Playback2DExportDialogViewModel vm = Dialog(job: job);

        vm.StartCommand.Execute(null);

        // A tab with the annotations feature off hands back null, and null must mean "no ink layer fed",
        // never "reuse whatever was there last time".
        await Assert.That(job.Requests[0].Ink).IsNull();
    }

    /// <summary>
    ///     <c>ExportIncludeVision</c> was the one export check box with no settings key at all — absent
    ///     from the class, from the seed and from the write-back — so a user who wanted cones re-ticked
    ///     the box for every single export.
    /// </summary>
    [Test]
    public async Task IncludeVision_SeedsFromSettings_AndPersistsBack()
    {
        AppSettings written = new();
        Playback2DExportDialogViewModel seeded = Dialog(
            defaults: new Playback2DSettings { ExportIncludeVision = true });

        await Assert.That(seeded.IncludeVision).IsTrue();
        await Assert.That(seeded.BuildRequest(seeded.Ranges[0]).LayerIds.Contains(SceneLayerIds.Vision))
            .IsTrue();

        Playback2DExportDialogViewModel vm = Dialog(
            job: new RecordingExportJobService(), persist: mutate => mutate(written));
        vm.IncludeVision = true;
        vm.StartCommand.Execute(null);

        await Assert.That(written.Playback2D.ExportIncludeVision).IsTrue();
    }

    /// <summary>
    ///     The fileless branch, explicitly. <c>SettingsWasmRoundTripTests</c> reflects over the whole class
    ///     and would catch a missing <c>WriteInMemory</c> row too, but a key that only ever travels is
    ///     half a setting: this one asserts the round trip AND the dialog reading it back.
    /// </summary>
    [Test]
    public async Task IncludeVision_SurvivesTheFilelessSettingsPath()
    {
        SettingsService settings = new(null); // the WASM branch — no file, only the in-memory provider
        settings.Write(s => s.Playback2D.ExportIncludeVision = true);

        await Assert.That(settings.Current.Playback2D.ExportIncludeVision).IsTrue()
            .Because("a Playback2DSettings property with no WriteInMemory row forgets itself on WASM");

        Playback2DExportDialogViewModel vm = Dialog(defaults: settings.Current.Playback2D);
        await Assert.That(vm.IncludeVision).IsTrue();
    }

    /// <summary>
    ///     <b>Cross-surface layer parity, the app half.</b> Its counterpart is
    ///     <c>ExportLayerParityTests</c> in the dv2d suite, which pins <c>ExportCommand.BuildLayerIds</c>
    ///     to the same two Core-derived expressions from the other side. Adding a layer to
    ///     <c>SceneStackIds</c> moves both; changing one front end's defaults moves only one, and that is
    ///     the drift these exist to catch.
    /// </summary>
    [Test]
    public async Task TheShippedIncludeSet_IsTheCliesFullOverlaySet()
    {
        Playback2DExportDialogViewModel vm = Dialog();

        // HUD on, annotations on, vision off — the dialog's shipped defaults, and `dv2d export --hud
        // --annotations`.
        await Assert.That(vm.IncludeHud).IsTrue();
        await Assert.That(vm.IncludeAnnotations).IsTrue();
        await Assert.That(vm.IncludeVision).IsFalse();

        await Assert.That(vm.BuildRequest(vm.Ranges[0]).LayerIds.Order())
            .IsEquivalentTo(FullOverlaySet().Order());
    }

    [Test]
    public async Task WithEveryOverlayOff_ItIsTheCliesBareDefaultSet()
    {
        Playback2DExportDialogViewModel vm = Dialog();
        vm.IncludeHud = false;
        vm.IncludeAnnotations = false;

        // The bare `dv2d export` set. Vision used to be in the CLI's and never in the app's, which is one
        // request producing two different videos depending on which front end ran it.
        await Assert.That(vm.BuildRequest(vm.Ranges[0]).LayerIds.Order())
            .IsEquivalentTo(BareSceneSet().Order());
    }

    /// <summary>Every scene-stack id except vision — `dv2d export --hud --annotations`.</summary>
    internal static IEnumerable<string> FullOverlaySet() =>
        Playback2D.Pipeline.Headless.SceneLayerCatalog.SceneStackIds
            .Where(id => !string.Equals(id, SceneLayerIds.Vision, StringComparison.Ordinal));

    /// <summary>Every non-opt-in scene-stack id except vision — bare `dv2d export`.</summary>
    internal static IEnumerable<string> BareSceneSet() =>
        Playback2D.Pipeline.Headless.SceneLayerCatalog.SceneStackIds
            .Where(id => !SceneLayerIds.OptIn.Contains(id) &&
                         !string.Equals(id, SceneLayerIds.Vision, StringComparison.Ordinal));

    private static AnnotationSession EmptyInk() => new(new AnnotationDocument());

    private static AnnotationSession InkWith(int strokes)
    {
        AnnotationDocument document = new();
        document.Reset([.. Enumerable.Range(0, strokes).Select(_ => new AnnotationElement(
            Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default,
            new SpaceRef.World(0), TimeEnvelope.Static,
            [new InkPoint(0, 0, 0.5f), new InkPoint(10, 10, 0.5f)], null))]);
        return new AnnotationSession(document);
    }

    private static Playback2DExportDialogViewModel Dialog(
        IReadOnlyList<ExportRangeOption>? ranges = null,
        Func<FfmpegLocation>? ffmpeg = null,
        Func<bool>? liveSync = null,
        Func<CameraScript>? captureCamera = null,
        Func<int, int, int, double, int>? outputFrameCount = null,
        Playback2DSettings? defaults = null,
        IExportJobService? job = null,
        Action<Action<AppSettings>>? persist = null,
        Func<string, bool>? fileExists = null,
        Func<AnnotationSession?>? captureInk = null,
        FfmpegAcquire? acquireFfmpeg = null) =>
        new(ranges ?? [new ExportRangeOption("Current round", 100, 400)],
            defaults ?? new Playback2DSettings(),
            job: job,
            captureLiveCamera: captureCamera,
            outputFrameCount: outputFrameCount,
            ffmpegLocator: ffmpeg ?? (() => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath)),
            isLiveSyncSessionActive: liveSync,
            persistDefaults: persist,
            fileExists: fileExists ?? (_ => false),
            captureInk: captureInk,
            acquireFfmpeg: acquireFfmpeg);

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

    /// <summary>Every request, in order — what a single-slot stub cannot show about two Starts.</summary>
    private sealed class RecordingExportJobService : IExportJobService
    {
        public List<Scene2DExportRequest> Requests { get; } = [];

        public ExportJobStatus Status => ExportJobStatus.Idle;

        public event EventHandler<ExportJobStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Start(Scene2DExportRequest request) => Requests.Add(request);

        public Task CancelAsync() => Task.CompletedTask;
    }
}

/// <summary>
///     <b>The pinned ffmpeg download, which for the whole life of the feature could not run.</b> The
///     runner's <c>consent</c> was an optional constructor parameter its one production caller omitted,
///     so <c>ResolveFfmpegAsync</c> short-circuited before <c>FfmpegAcquisition</c> was ever reached —
///     while the pane showed a check box, <b>ticked by default</b>, offering exactly that download, and
///     the refusal text advertised it too.
///     <para>
///         It is a button now, and the licence read out of the verified archive has to be accepted before
///         anything is extracted. These cases drive that flow with an injected acquisition, so they need
///         no network, no 140 MB transfer and no Windows-x64 machine.
///     </para>
///     <para>
///         They run on the headless UI thread because the consent callback marshals there — production's
///         caller is <c>FfmpegAcquisition</c> on a pool thread — and off it the publish would be a
///         <c>Dispatcher.Post</c> with nothing pumping.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DExportFfmpegDownloadTests
{
    [Test]
    public async Task WithNothingPinnedForThisMachine_TheDownloadIsNotOffered()
    {
        Playback2DExportDialogViewModel vm = new(
            [new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings(),
            ffmpegLocator: () => FfmpegLocation.NotFound,
            fileExists: _ => false);

        await Assert.That(vm.ShowFfmpegStrip).IsTrue();
        await Assert.That(vm.CanOfferFfmpegDownload).IsFalse()
            .Because("a button that cannot do anything is the defect this replaced, in a new shape");
        await Assert.That(vm.DownloadFfmpegCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task TheLicence_IsShown_AndInstallingWaitsForTheUserToAcceptIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            bool installed = false;
            bool ffmpegPresent = false;

            Playback2DExportDialogViewModel vm = Dialog(() => ffmpegPresent,
                async (consent, progress, ct) =>
                {
                    progress?.Report(0.5);
                    if (!await consent(Offer(), "LGPL-2.1 …", ct).ConfigureAwait(true))
                    {
                        return FfmpegLocation.NotFound;
                    }

                    installed = true;
                    ffmpegPresent = true;
                    return new FfmpegLocation(true, "/managed", FfmpegOrigin.Managed);
                });

            Task download = vm.DownloadFfmpegCommand.ExecuteAsync(null);

            // The licence is on screen and nothing has been written: that ordering is the whole point of
            // FfmpegAcquisition's contract, which asks only after the bytes are downloaded and hashed.
            await Assert.That(vm.HasFfmpegLicense).IsTrue();
            await Assert.That(vm.FfmpegLicenseText).IsNotNull();
            await Assert.That(installed).IsFalse();

            vm.AcceptFfmpegLicenseCommand.Execute(null);
            await download.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(installed).IsTrue();
            await Assert.That(vm.HasFfmpegLicense).IsFalse();
            await Assert.That(vm.IsDownloadingFfmpeg).IsFalse();

            // Re-probed, so the pane stops refusing video without a second press of Re-check.
            await Assert.That(vm.IsFfmpegMissing).IsFalse();
            await Assert.That(vm.CanStart).IsTrue();
        });

    [Test]
    public async Task DecliningTheLicence_InstallsNothing_AndSaysSo() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            bool installed = false;
            Playback2DExportDialogViewModel vm = Dialog(() => false,
                async (consent, _, ct) =>
                {
                    if (!await consent(Offer(), "LGPL-2.1 …", ct).ConfigureAwait(true))
                    {
                        return FfmpegLocation.NotFound;
                    }

                    installed = true;
                    return new FfmpegLocation(true, "/managed", FfmpegOrigin.Managed);
                });

            Task download = vm.DownloadFfmpegCommand.ExecuteAsync(null);
            await Assert.That(vm.HasFfmpegLicense).IsTrue();

            vm.DeclineFfmpegLicenseCommand.Execute(null);
            await download.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(installed).IsFalse();
            await Assert.That(vm.FfmpegDownloadStatus!).Contains("Nothing was installed");
            await Assert.That(vm.CanStart).IsFalse().Because("no ffmpeg still means no video export");
        });

    [Test]
    public async Task AFailedDownload_DegradesToTheMessage_NeverToACrash() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DExportDialogViewModel vm = Dialog(() => false,
                (_, _, _) => throw new FfmpegAcquisitionException(
                    "The downloaded ffmpeg archive did not match its pinned checksum, so it was discarded."));

            await vm.DownloadFfmpegCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(vm.FfmpegDownloadStatus!).Contains("pinned checksum");
            await Assert.That(vm.IsDownloadingFfmpeg).IsFalse();
        });

    /// <summary>
    ///     Closing the pane mid-download must not leave the acquisition awaiting a licence answer that
    ///     can no longer be given — the pane it would have been given in is gone.
    /// </summary>
    [Test]
    public async Task DisposingMidConsent_ReleasesTheAcquisition() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DExportDialogViewModel vm = Dialog(() => false,
                async (consent, _, ct) => await consent(Offer(), "LGPL-2.1 …", ct).ConfigureAwait(true)
                    ? new FfmpegLocation(true, "/managed", FfmpegOrigin.Managed)
                    : FfmpegLocation.NotFound);

            Task download = vm.DownloadFfmpegCommand.ExecuteAsync(null);
            await Assert.That(vm.HasFfmpegLicense).IsTrue();

            vm.Dispose();
            await download.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(vm.IsDownloadingFfmpeg).IsFalse();
        });

    private static FfmpegDownloadOffer Offer() =>
        new("https://example.invalid/ffmpeg.zip", new string('a', 64), "autobuild-test",
            "https://example.invalid", "LGPL-2.1", 1024, "/managed");

    private static Playback2DExportDialogViewModel Dialog(Func<bool> ffmpegPresent,
        FfmpegAcquire acquire) =>
        new([new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings(),
            ffmpegLocator: () => ffmpegPresent()
                ? new FfmpegLocation(true, "/managed", FfmpegOrigin.Managed)
                : FfmpegLocation.NotFound,
            fileExists: _ => false,
            acquireFfmpeg: acquire);
}

/// <summary>
///     <b>Whether the Export button can ever appear.</b> <c>CanExport</c> is computed over the gate, the
///     export host and <c>HasDemo</c>, and nothing raised a change notification for it — so the button's
///     <c>IsVisible</c> binding latched its first read, which on a cold start is "no demo loaded", and the
///     export entry point stayed invisible however many demos were opened afterwards. Reported as "there
///     is no export button"; it was a missing <c>OnPropertyChanged</c>.
/// </summary>
public class Playback2DExportVisibilityTests
{
    [Test]
    public async Task WhenADemoArrivesUnderAnActiveTab_CanExportIsRaised()
    {
        Playback2DTabViewModel vm = new();
        Playback2DFakeContext ctx = new()
        {
            HasDemo = false, Gate = new FakeModuleFeatureGate()
        };
        vm.OnActivated(ctx);

        List<string> raised = [];
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.PropertyChanged += handler;

        try
        {
            ctx.HasDemo = true;
            ctx.RaiseDemoReset();
        }
        finally
        {
            vm.PropertyChanged -= handler;
        }

        await Assert.That(raised).Contains(nameof(Playback2DTabViewModel.CanExport))
            .Because("a demo loading is exactly when the affordance has to appear");
    }

    [Test]
    public async Task AGateFlip_RaisesItToo()
    {
        Playback2DTabViewModel vm = new();
        FakeModuleFeatureGate gate = new();
        Playback2DFakeContext ctx = new()
        {
            Gate = gate
        };
        vm.OnActivated(ctx);

        List<string> raised = [];
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.PropertyChanged += handler;

        try
        {
            // Live, like every other gate on this tab: turning the feature off in Settings has to take the
            // button away without rebuilding the tab.
            gate.SetEnabled("playback2d.export", false);
        }
        finally
        {
            vm.PropertyChanged -= handler;
        }

        await Assert.That(raised).Contains(nameof(Playback2DTabViewModel.CanExport));
    }
}

/// <summary>
///     <b>What ink an export is handed.</b> The runner evaluates the tab's setup on a pool thread and then
///     renders for minutes, while the user carries on drawing in the window it came from.
/// </summary>
public class Playback2DExportInkTests
{
    [Test]
    public async Task TheInkAnExportGets_IsASnapshot_NotTheLiveDocument()
    {
        Playback2DTabViewModel vm = new();
        AnnotationDocument live = vm.Annotations.Session.Document;
        live.Apply(new DocDelta.Add(Stroke(), 0));

        AnnotationSession? frozen = vm.SnapshotInkForExport();
        await Assert.That(frozen).IsNotNull();
        await Assert.That(frozen!.Document.Elements.Count).IsEqualTo(1);

        // Drawing during the render must not reach frames the export has already passed — and erasing
        // must not take ink out of frames that already showed it. AnnotationLayer re-records its cached
        // pictures on every Version bump, so the live document would do exactly both.
        live.Apply(new DocDelta.Add(Stroke(), 1));

        await Assert.That(live.Elements.Count).IsEqualTo(2);
        await Assert.That(frozen.Document.Elements.Count).IsEqualTo(1)
            .Because("the export renders from the document as it stood when Start was pressed");
    }

    private static AnnotationElement Stroke() => new(
        Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default,
        new SpaceRef.World(0), TimeEnvelope.Static,
        [new InkPoint(0, 0, 0.5f), new InkPoint(100, 100, 0.5f)], null);
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

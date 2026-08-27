#region

using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.Views.Playback2D;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>The export pane, through the surface rather than through the view-model.</b>
///     <para>
///         Every rule the export dialog applies was already covered by a suite that instantiated the
///         view-model directly, which never exercised view resolution — so the pane <em>had no view</em>
///         and nothing caught it. <c>Playback2DView</c> mounts it as a bare <c>ContentControl</c>,
///         resolution goes through the app <c>ViewLocator</c>, and the locator matches on
///         <c>ViewModelBase</c> while the view-model derived
///         from <c>ObservableObject</c> — so the whole pane rendered as one line of fully-qualified type
///         name next to a Close button, for the life of the feature.
///     </para>
///     <para>
///         The assertion is therefore the real view type in the real visual tree, plus a named control
///         inside it. "Is the ContentControl there" would have passed on the broken tree; it was there the
///         whole time.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DExportPaneMountTests
{
    [Test]
    public async Task TheExportPane_MountsItsRealView_NotItsTypeName()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            try
            {
                vm.ExportDialog = Dialog();
                Playback2DTimelineHarness.Pump();

                Playback2DExportDialogView[] mounted =
                    [.. view.GetVisualDescendants().OfType<Playback2DExportDialogView>()];

                await Assert.That(mounted.Length).IsEqualTo(1)
                    .Because("ViewLocator.Match is `data is ViewModelBase`, and the pane's VM has to be one");

                // A named control INSIDE the view: proof the template was applied, not that a
                // control of the right type was constructed.
                await Assert.That(mounted[0].FindControl<Button>("ExportStartButton")).IsNotNull();

                // The failure signature, asserted directly: a ToString() fallback renders the VM's
                // fully-qualified type name as the ContentControl's only text.
                bool rendersTypeName = view.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text?.Contains("Playback2DExportDialogViewModel", StringComparison.Ordinal)
                              == true);
                await Assert.That(rendersTypeName).IsFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     The eleven labels sit at the edge of the palette's contrast floor: app-chrome <c>TextDim</c> on
    ///     a <c>Pb2dPanelBg</c> host measures 1.71:1 in Dark. Asserted on the resolved brushes rather than on
    ///     the XAML text, so a token renamed out from under the pane fails here too.
    /// </summary>
    [Test]
    public async Task TheExportPanesDimLabels_ResolveTheViewportPalette_NotTheAppChromeOne()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            try
            {
                vm.ExportDialog = Dialog();
                Playback2DTimelineHarness.Pump();

                Playback2DExportDialogView pane =
                    view.GetVisualDescendants().OfType<Playback2DExportDialogView>().Single();

                object? chrome = Find(pane, "TextDim");
                object? viewport = Find(pane, "Pb2dTextDim");
                await Assert.That(viewport).IsNotNull().Because("the pane lives in the viewport column");
                await Assert.That(chrome).IsNotNull();
                await Assert.That(chrome!.Equals(viewport)).IsFalse()
                    .Because("if the two tokens resolved identically this test would be vacuous");

                // Every dim label in the pane — the section captions and the two hint lines.
                TextBlock[] dim =
                [
                    .. pane.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Foreground is not null && t.Foreground.Equals(viewport))
                ];

                await Assert.That(dim.Length).IsGreaterThanOrEqualTo(6)
                    .Because("the pane's captions are all dim labels");

                bool anyChrome = pane.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Foreground is not null && t.Foreground.Equals(chrome));
                await Assert.That(anyChrome).IsFalse()
                    .Because("TextDim on Pb2dPanelBg is 1.71:1 — it is not a contrast ratio");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static object? Find(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out object? value) ? value : null;

    private static Playback2DExportDialogViewModel Dialog() =>
        new([new ExportRangeOption("Current round", 100, 400)],
            new Playback2DSettings(),
            ffmpegLocator: () => new FfmpegLocation(true, "/usr/bin", FfmpegOrigin.SystemPath),
            fileExists: _ => false);
}

/// <summary>
///     <b>The export's status chip.</b> <c>ExportJobService</c> marshalled phase, frame counts, throughput,
///     elapsed and the error to the UI thread and raised <c>StatusChanged</c> on every one of them, and
///     <em>nothing anywhere subscribed</em>: <c>ExportStatus</c> appeared in two lines repo-wide, no
///     <c>.axaml</c> bound it, and <c>CancelAsync</c> had zero production call sites — so a started export
///     could not be stopped and a failed one reported nothing. Three doc comments described this chip as
///     though it already existed.
///     <para>
///         These go through the bound surface, not the mapper: asserting <c>service.Status.Phase</c> on the
///         service is precisely how the whole gap survived 1594 green tests.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DExportStatusSurfaceTests
{
    [Test]
    public async Task AStatusChange_ReachesTheBoundFlyout()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeExportJob job = new();
            Playback2DExportStatusViewModel status = new(job);
            (Window window, Playback2DExportStatusView view) = Show(status);

            try
            {
                TextBlock headline = view.FindControl<TextBlock>("ExportHeadline")!;
                await Assert.That(headline.Text ?? "").IsEmpty().Because("nothing has run yet");

                job.Push(new ExportJobStatus(ExportPhase.Rendering, 300, 1200, 118, TimeSpan.FromMinutes(1),
                    "clip.webm", null, TimeSpan.FromMinutes(3)));
                Playback2DTimelineHarness.Pump();

                await Assert.That(headline.Text!).Contains("Rendering");
                await Assert.That(status.Chip.Label).IsEqualTo("Export · 25%");
                await Assert.That(status.ProgressFraction).IsEqualTo(0.25);

                // The ETA the session computes but the App contract had nowhere to put.
                await Assert.That(status.Detail).Contains("left");
                await Assert.That(status.Detail).Contains("118 fps");

                // Cancel is REACHABLE — a visible, enabled button whose command reaches the job.
                Button cancel = view.FindControl<Button>("CancelExportButton")!;
                await Assert.That(cancel.IsVisible).IsTrue();
                await Assert.That(cancel.IsEffectivelyEnabled).IsTrue();

                await status.CancelCommand.ExecuteAsync(null);
                await Assert.That(job.Cancels).IsEqualTo(1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task AFailedExport_SurfacesItsErrorAndTheEncoderLog()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeExportJob job = new();
            Playback2DExportStatusViewModel status = new(job);
            (Window window, Playback2DExportStatusView view) = Show(status);

            try
            {
                // The runner's log sink — the other optional parameter production omitted, which is why a
                // dead ffmpeg used to report its exception message and nothing else.
                status.AppendLog("video encoder: av1_nvenc (hardware)");
                status.AppendLog("[libvpx] Error: could not open the output pipe");

                job.Push(new ExportJobStatus(ExportPhase.Failed, 40, 1200, 12, TimeSpan.FromSeconds(9),
                    "clip.webm", "ffmpeg exited with code 1", null));
                Playback2DTimelineHarness.Pump();

                SelectableTextBlock error = view.FindControl<SelectableTextBlock>("ExportErrorText")!;
                await Assert.That(error.IsVisible).IsTrue();
                await Assert.That(error.Text!).Contains("exited with code 1");

                await Assert.That(status.Chip.Label).IsEqualTo("Export · failed");
                await Assert.That(status.HasLog).IsTrue();

                // The copy block is a self-contained report: how far it got, what broke, and the tail
                // ffmpeg actually printed.
                await Assert.That(status.CopyDiagnosticsText).Contains("40 of 1200");
                await Assert.That(status.CopyDiagnosticsText).Contains("av1_nvenc");
                await Assert.That(status.CopyDiagnosticsText).Contains("could not open the output pipe");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     The shell half: the chip appears when a job runs and goes away when it is dismissed. Mirrors
    ///     <c>ReelChipShellReconcileTests</c>, because it is the same contract with a different job.
    /// </summary>
    [Test]
    public async Task TheChip_JoinsTheStrip_WhileRunning_AndLeavesOnDismiss()
    {
        FakeExportJob job = new();
        Playback2DExportStatusViewModel status = new(job);
        ViewModels.Shell.MainViewModel shell = new();

        shell.AttachPlayback2DExportStatus(status);
        await Assert.That(shell.Chips.Contains(status.Chip)).IsFalse()
            .Because("attaching happens when the pane first opens, long before any Start");

        job.Push(new ExportJobStatus(ExportPhase.Rendering, 1, 100, 0, TimeSpan.Zero, "clip.webm", null));
        await Assert.That(shell.Chips.Contains(status.Chip)).IsTrue();

        job.Push(new ExportJobStatus(ExportPhase.Completed, 100, 100, 60, TimeSpan.FromMinutes(1),
            "clip.webm", null));
        await Assert.That(shell.Chips.Contains(status.Chip)).IsTrue()
            .Because("a finished result stays until the user dismisses it");

        status.DismissCommand.Execute(null);
        await Assert.That(shell.Chips.Contains(status.Chip)).IsFalse();

        // A fresh export un-dismisses, so the chip comes back rather than staying gone for the session.
        job.Push(new ExportJobStatus(ExportPhase.Preparing, 0, 100, 0, TimeSpan.Zero, "clip2.webm", null));
        await Assert.That(shell.Chips.Contains(status.Chip)).IsTrue();
    }

    private static (Window Window, Playback2DExportStatusView View) Show(
        Playback2DExportStatusViewModel status)
    {
        Playback2DExportStatusView view = new()
        {
            DataContext = status
        };
        Window window = new()
        {
            Width = 420, Height = 420, Content = view
        };
        window.Show();
        Playback2DTimelineHarness.Pump();
        return (window, view);
    }

    /// <summary>A job that only publishes what a test tells it to. The seam the chip is built on.</summary>
    private sealed class FakeExportJob : IExportJobService
    {
        public int Cancels { get; private set; }

        public ExportJobStatus Status { get; private set; } = ExportJobStatus.Idle;

        public event EventHandler<ExportJobStatus>? StatusChanged;

        public void Start(Scene2DExportRequest request)
        {
        }

        public Task CancelAsync()
        {
            Cancels++;
            return Task.CompletedTask;
        }

        public void Push(ExportJobStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }
}

/// <summary>
///     What the tab hands the runner for one export. See
///     <see cref="Playback2DExportDialogTests.EachStart_CarriesItsOwnInk" /> for why this rides the
///     request rather than a tab-level field.
/// </summary>
public class Playback2DExportSetupTests
{
    // This used to run on the UI thread, with a comment explaining that BuildExportSetup resolves the
    // theme variant off Application.Current and that AvaloniaObject.GetValue verifies dispatcher affinity.
    // That was the right observation about the wrong subject: the affinity problem belonged to the
    // PRODUCTION code, which builds this setup on the export's pool thread by contract, not to the test.
    // Keeping the test on the UI thread hid a crash that killed every real export before frame zero.
    // It stays here because the ink assertion needs no particular thread; the thread itself is now
    // pinned by TheSetup_BuildsOffTheUiThread_LikeTheRunnerDoes below.
    [Test]
    public async Task TheSetupTakesItsInkFromTheRequest_NotFromTheTab() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            Playback2DExportHost host = new(
                () => null, null, null, null, () => new AppSettings(), _ => { });

            Scene2DExportRequest first = Request(vm.SnapshotInkForExport());
            vm.Annotations.Session.Document.Apply(new DocDelta.Add(Stroke(), 0));
            Scene2DExportRequest second = Request(vm.SnapshotInkForExport());

            // Two runs, two documents, and the setup is a pure function of the run it was handed.
            await Assert.That(vm.BuildExportSetup(host, first).Annotations!.Document.Elements.Count)
                .IsEqualTo(0);
            await Assert.That(vm.BuildExportSetup(host, second).Annotations!.Document.Elements.Count)
                .IsEqualTo(1);

            // And with no request at all — the design-preview path — there is simply no ink, rather than
            // whatever the last export left behind.
            await Assert.That(vm.BuildExportSetup(host).Annotations).IsNull();
        });

    /// <summary>
    ///     The setup must build on a POOL thread, because that is the only place production ever builds it:
    ///     <c>SceneExportRunner.RunAsync</c> calls the factory after the job has been handed to
    ///     <c>Task.Run</c> and has awaited the heavy-job gate.
    ///     <para>
    ///         It used to resolve the palette from <c>Application.Current.ActualThemeVariant</c> — a
    ///         styled property — and threw <i>"Call from invalid thread"</i> for every user who pressed
    ///         Export. The application must be BUILT for this test to mean anything: with no
    ///         <c>Application.Current</c> the affinity check has nothing to verify and the old code passed
    ///         off-thread too, which is why the previous test could not have caught it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheSetup_BuildsOffTheUiThread_LikeTheRunnerDoes() =>
        // The hop to the pool happens INSIDE the session: the headless harness builds an isolated
        // application per dispatch, so Application.Current exists only for the life of this delegate. Off
        // the pool thread outside it there is nothing for VerifyAccess to object to, and the unfixed code
        // passes — which is exactly the "worst kind of green" the old comment on the test above described
        // without recognising it applied here.
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            Playback2DExportHost host = new(
                () => null, null, null, null, () => new AppSettings(), _ => { });

            await Assert.That(Avalonia.Application.Current).IsNotNull()
                .Because("with no Application there is no affinity to violate and this proves nothing");

            ScenePalette captured = vm.CaptureExportPalette();   // what Start does, on the UI thread
            Scene2DExportRequest request = Request(null) with { Palette = captured };

            // Task.Run, not the dispatcher: this is the runner's thread, and the whole point.
            ExportSceneSetup setup = await Task.Run(() => vm.BuildExportSetup(host, request));

            await Assert.That(setup.Palette).IsEqualTo(captured)
                .Because("the palette is resolved at Start and travels on the request; the READ is what "
                         + "is thread-affine, never the value");
        });

    private static Scene2DExportRequest Request(AnnotationSession? ink) =>
        new(new ExportRequest(0, 9, 60, new SKSizeI(320, 240), 1.0, ExportFormats.WebM,
                new HashSet<string>(StringComparer.Ordinal),
                new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>())),
            "out.webm", "demo.dem", Ink: ink);

    private static AnnotationElement Stroke() => new(
        Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default,
        new SpaceRef.World(0), TimeEnvelope.Static,
        [new InkPoint(0, 0, 0.5f), new InkPoint(50, 50, 0.5f)], null);
}

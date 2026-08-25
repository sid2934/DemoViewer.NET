#region

using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     B4 deviation 20, closed. The export dialog's "mirror the live view" camera is a CAPTURE taken once
///     at Start (B4 D12) — but until the v2 host exposed its panes it captured an empty <c>Fixed</c>
///     script, so every exported pane silently kept the fit its own level was born with. A user who had
///     zoomed into A site got a whole-map export and no indication why.
///     <para>
///         Two halves: the host produces a keyed snapshot of what is actually on screen, and the View
///         hands that capture to the tab view-model — because the VM cannot reach the mounted surface and
///         the legacy escape hatch has no pane cameras to capture at all.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DMirrorLiveViewTests
{
    [Test]
    public async Task Host_CapturesEveryPaneCamera_KeyedByLevelId()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            try
            {
                Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
                host.FitToExtent();
                Playback2DTimelineHarness.Pump();

                CameraScript.MirrorLiveView captured = Mirror(host);

                await Assert.That(captured.Panes.Length).IsEqualTo(host.PaneCountForTest)
                    .Because("one snapshot per live pane");
                await Assert.That(captured.DisplayMode).IsEqualTo(host.DisplayMode);
                await Assert.That(captured.Panes[0].LevelId).IsEqualTo(host.PrimaryPaneLevelForTest);
                await Assert.That(captured.Panes[0].Transform).IsEqualTo(host.PrimaryCameraTransform);
                await Assert.That(captured.Panes[0].ManualOverride).IsFalse();

                // Level ids, not indices: a level set that gains a floor mid-export must not slide every
                // camera down one band (design risk 5).
                await Assert.That(captured.Panes.Select(p => p.LevelId).Distinct().Count())
                    .IsEqualTo(captured.Panes.Length);
                await Assert.That(captured.Panes.Any(p => p.LevelId == MapLevelId.None)).IsFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     A pan is what makes the capture worth taking: after it the snapshot carries the user's framing
    ///     AND the manual-override flag, so the export reproduces what they were looking at rather than
    ///     the level's birth fit.
    /// </summary>
    [Test]
    public async Task Capture_TakenAfterAPan_CarriesThePannedTransform_AndTheOverrideFlag()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            try
            {
                Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
                host.FitToExtent();
                Playback2DTimelineHarness.Pump();

                CameraScript.MirrorLiveView before = Mirror(host);

                Point start = Playback2DTimelineHarness.ToWindow(host, window, 200, 200);
                Point end = Playback2DTimelineHarness.ToWindow(host, window, 260, 230);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end);
                window.MouseUp(end, MouseButton.Left);
                Playback2DTimelineHarness.Pump();

                CameraScript.MirrorLiveView after = Mirror(host);

                Console.WriteLine($"[mirror] pan {before.Panes[0].Transform.PanX:F1} → " +
                                  $"{after.Panes[0].Transform.PanX:F1}, " +
                                  $"manual {before.Panes[0].ManualOverride} → {after.Panes[0].ManualOverride}");

                await Assert.That(after.Panes[0].Transform.PanX)
                    .IsEqualTo(before.Panes[0].Transform.PanX + 60).Within(0.5);
                await Assert.That(after.Panes[0].ManualOverride).IsTrue();

                // D12's immutability: the capture is a value, so panning again cannot reach back into it.
                ImmutableArray<PaneCameraSnapshot> frozen = after.Panes;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(Playback2DTimelineHarness.ToWindow(host, window, 400, 400));
                window.MouseUp(end, MouseButton.Left);
                Playback2DTimelineHarness.Pump();

                await Assert.That(after.Panes[0].Transform).IsEqualTo(frozen[0].Transform);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     The wiring: with the v2 host mounted the tab's capture is the host's, and it is cleared when
    ///     the View unbinds — the View is destroyed on every tab deactivation, and a stale delegate onto a
    ///     disposed control is the shape of bug that outlives its own tab.
    /// </summary>
    [Test]
    public async Task View_HandsTheHostsCapture_ToTheTab_AndClearsItOnUnbind()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            try
            {
                // `Assert.That(someFunc)` binds to TUnit's delegate overload, so the null checks go
                // through a bool.
                await Assert.That(vm.LiveCameraSource is null).IsFalse();

                CameraScript captured = vm.LiveCameraSource!.Invoke();
                await Assert.That(captured).IsTypeOf<CameraScript.MirrorLiveView>();

                view.DataContext = null;
                await Assert.That(vm.LiveCameraSource is null).IsTrue()
                    .Because("a capture delegate must not outlive the surface it closes over");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     Under the legacy escape hatch there are no pane cameras, so nothing is wired and the dialog
    ///     falls back to the empty <c>Fixed</c> script — the pre-B5 behaviour, kept deliberately rather
    ///     than stubbed with a lie.
    /// </summary>
    [Test]
    public async Task LegacySurface_WiresNoCapture()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();

            (Window window, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Legacy);
            try
            {
                await Assert.That(vm.LiveCameraSource is null).IsTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     The other half of the capture. B4 D12 says the snapshot carries the pane cameras <b>plus the
    ///     host's current <c>LevelDisplayMode</c></b>; <c>MirrorLiveView.DisplayMode</c> recorded it and the
    ///     App's export setup hard-coded <c>Stacked</c>, so a user watching a two-floor map in SINGLE mode
    ///     and exporting "mirror the live view" got a stacked video of a framing they had never seen. The
    ///     setup is a FACTORY on the runner, evaluated at Start, so reading the live mode there is the same
    ///     instant the cameras are frozen.
    /// </summary>
    [Test]
    public async Task ExportSetup_TakesTheLiveDisplayMode_NotAHardCodedStacked()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, -700f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            try
            {
                Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
                Playback2DTimelineHarness.Pump();

                Playback2DExportHost exportHost = new(
                    () => null, null, null, null, () => new AppSettings(), _ => { });

                await Assert.That(vm.BuildExportSetup(exportHost).DisplayMode)
                    .IsEqualTo(LevelDisplayMode.Stacked).Because("stacked is the shipping default");

                vm.LevelStrip.IsSingleMode = true;
                Playback2DTimelineHarness.Pump();

                await Assert.That(host.DisplayMode).IsEqualTo(LevelDisplayMode.Single)
                    .Because("the strip drives the live surface");
                await Assert.That(Mirror(host).DisplayMode).IsEqualTo(LevelDisplayMode.Single);
                await Assert.That(vm.BuildExportSetup(exportHost).DisplayMode)
                    .IsEqualTo(LevelDisplayMode.Single)
                    .Because("an export that mirrors the live view must mirror its LAYOUT too");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static CameraScript.MirrorLiveView Mirror(Scene2DHost host) =>
        host.CaptureCameraScript() as CameraScript.MirrorLiveView
        ?? throw new InvalidOperationException("the v2 host must capture a MirrorLiveView script");
}

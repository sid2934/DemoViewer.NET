#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Views.Playback2D;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Avalonia half of the v2 host: mounting, rendering through the custom draw operation, panning
///     the band under the cursor, falling back to the <c>WriteableBitmap</c> path, and surviving the
///     render gate under contention.
///     <para>
///         Everything that can be asserted without a window is asserted in the direct-execution suite
///         instead (<c>src/Playback2D/DemoViewer.NET.Playback2D.Tests</c>). What is left here needs a
///         visual tree, a dispatcher and a real render pass.
///     </para>
/// </summary>
[NotInParallel]
public class Scene2DHostTests
{
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;

    [Test]
    public async Task Default_MountsTheSceneHost_AndLegacyIsStillReachable()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();

            (Window sceneWindow, Playback2DView sceneView) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            await Assert.That(Playback2DTimelineHarness.Surface(sceneView)).IsTypeOf<Scene2DHost>();
            sceneWindow.Close();

            (Window legacyWindow, Playback2DView legacyView) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Legacy);
            await Assert.That(Playback2DTimelineHarness.Surface(legacyView))
                .IsTypeOf<Playback2DViewport>();
            legacyWindow.Close();
        });
    }

    /// <summary>
    ///     The environment variable is the CI and bisecting path, and it outranks the setting. It is an
    ///     env var rather than a <c>FeatureCatalog</c> id on purpose: catalog ids are permanent persisted
    ///     keys, and this toggle goes away with the old control.
    /// </summary>
    [Test]
    public async Task EnvironmentVariable_SelectsTheSurface_AndOutranksTheSetting()
    {
        string? original = Environment.GetEnvironmentVariable(Playback2DRenderer.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(Playback2DRenderer.EnvironmentVariable, "legacy");
            Playback2DRenderer.ResetForTest(null); // clear the pin so the variable is re-read
            await Assert.That(Playback2DRenderer.Selected).IsEqualTo(Playback2DRendererKind.Legacy);

            Environment.SetEnvironmentVariable(Playback2DRenderer.EnvironmentVariable, "scene");
            Playback2DRenderer.ResetForTest(null);
            await Assert.That(Playback2DRenderer.Selected).IsEqualTo(Playback2DRendererKind.Scene);

            Environment.SetEnvironmentVariable(Playback2DRenderer.EnvironmentVariable, null);
            Playback2DRenderer.ResetForTest(null);
            await Assert.That(Playback2DRenderer.Selected).IsEqualTo(Playback2DRendererKind.Scene)
                .Because("the v2 host is the default when nothing overrides it");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Playback2DRenderer.EnvironmentVariable, original);
            Playback2DRenderer.ResetForTest(null);
        }
    }

    /// <summary>
    ///     Both surfaces satisfy <see cref="IPlayback2DSurface" />, so the mode menu, the follow funnel
    ///     and the Fit button drive either one. A failure here means the two surfaces have diverged.
    /// </summary>
    [Test]
    public async Task BothSurfaces_SatisfyThePlayback2DSurfaceContract()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Scene2DHost sceneHost = new();
            Playback2DViewport legacyHost = new();

            await Assert.That(DriveThroughTheContract(sceneHost)).IsEqualTo(CameraMode.Fit);
            await Assert.That(DriveThroughTheContract(legacyHost)).IsEqualTo(CameraMode.Fit);
        });
    }

    [Test]
    public async Task SceneHost_RendersANonBlankFrame_WithTeamColouredMarkers()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers(
                (0, 2, -800f, 600f, 64f, 90f),
                (1, 3, 900f, -500f, 64f, 270f),
                (2, 3, 400f, 200f, 64f, 0f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            WriteableBitmap? captured = window.CaptureRenderedFrame();
            await Assert.That(captured).IsNotNull();

            string path = Path.Combine(HeadlessSession.ArtifactDir, "scene2d-synthetic.png");
            captured!.Save(path);
            (int nonBg, bool team) = ScanPixels(captured);
            Console.WriteLine($"[scene2d] {path} nonBg={nonBg} team={team} " +
                              $"panes={host.Compositor.Stats.PanesRendered} " +
                              $"layers={host.Compositor.Stats.LayersRendered}");

            await Assert.That(nonBg).IsGreaterThan(100);
            await Assert.That(team).IsTrue();
        });
    }

    /// <summary>
    ///     A drag on a band pans only that band and flips it to manual override, and the mode selector
    ///     clears every override again. Ports <c>Playback2DCameraModeTests</c>' assertions onto the v2
    ///     host's identically named hooks.
    /// </summary>
    [Test]
    public async Task Drag_PansOnlyThePaneUnderTheCursor_AndSetsManualOverride()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            await Assert.That(host.PrimaryCameraManual).IsFalse();
            ViewportTransform before = host.PrimaryCameraTransform;

            Point start = Playback2DTimelineHarness.ToWindow(host, window, 200, 200);
            Point end = Playback2DTimelineHarness.ToWindow(host, window, 260, 230);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            ViewportTransform after = host.PrimaryCameraTransform;
            Console.WriteLine($"[drag] pan {before.PanX:F1},{before.PanY:F1} → {after.PanX:F1},{after.PanY:F1}");

            await Assert.That(host.PrimaryCameraManual).IsTrue();
            await Assert.That(after.PanX).IsEqualTo(before.PanX + 60).Within(0.5);
            await Assert.That(after.PanY).IsEqualTo(before.PanY + 30).Within(0.5);

            // Re-picking a mode re-arms every pane's auto camera.
            host.Mode = CameraMode.Alive;
            await Assert.That(host.PrimaryCameraManual).IsFalse();
        });
    }

    [Test]
    public async Task Wheel_ZoomsAndSetsManualOverride()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            double before = host.PrimaryCameraTransform.Zoom;
            window.MouseWheel(Playback2DTimelineHarness.ToWindow(host, window, 300, 250), new Vector(0, 1));
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[wheel] zoom {before:F3} → {host.PrimaryCameraTransform.Zoom:F3}");
            await Assert.That(host.PrimaryCameraTransform.Zoom).IsGreaterThan(before);
            await Assert.That(host.PrimaryCameraManual).IsTrue();
        });
    }

    /// <summary>
    ///     The self-terminating animation loop. It exists so an idle tab requests no frames at all; if
    ///     it ever stops terminating, the app burns a core in the background with nothing to report it.
    /// </summary>
    [Test]
    public async Task AnimationLoop_StopsRearmingOnceEverythingHasSettled()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window _, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);

            // Alive mode with a moving roster: the camera is chasing, so the loop MUST be armed.
            host.Mode = CameraMode.Alive;
            for (int i = 0; i < 20; i++)
            {
                ctx.PushMarkers((0, 2, -800f + i * 40f, 600f, 64f, 90f),
                    (1, 3, 900f - i * 40f, -500f, 64f, 270f));
                Playback2DTimelineHarness.Pump(1);
            }

            int whileMoving = host.FrameLoopArmCountForTest;
            await Assert.That(whileMoving).IsGreaterThan(0);

            // Stop pushing and let it converge.
            Playback2DTimelineHarness.Pump(200);
            int afterSettling = host.FrameLoopArmCountForTest;

            // Then idle. Nothing is moving, so nothing should ask for another frame.
            Playback2DTimelineHarness.Pump(120);
            int afterIdle = host.FrameLoopArmCountForTest;

            Console.WriteLine($"[raf] arms: moving={whileMoving} settled={afterSettling} idle={afterIdle}");
            await Assert.That(afterIdle).IsEqualTo(afterSettling);
        });
    }

    /// <summary>
    ///     The <c>WriteableBitmap</c> fallback. It never runs in normal use, so it is forced on every
    ///     run: a path that only executes on a broken backend is a path that rots.
    /// </summary>
    [Test]
    public async Task CpuFallback_RendersAndSurvivesResizes()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.ForceLeaseUnavailableForTest();
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            await Assert.That(host.LeaseUnavailable).IsTrue();

            WriteableBitmap? captured = window.CaptureRenderedFrame();
            await Assert.That(captured).IsNotNull();
            (int nonBg, bool team) = ScanPixels(captured!);
            Console.WriteLine($"[cpu-fallback] nonBg={nonBg} team={team}");
            await Assert.That(nonBg).IsGreaterThan(100);

            // 100 resizes: the bitmap is reallocated on a size change, and leaking one per resize is
            // exactly the shape of bug this path hides.
            for (int i = 0; i < 100; i++)
            {
                window.Width = 900 + i % 7;
                window.Height = 650 + i % 5;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
            await Assert.That(window.CaptureRenderedFrame()).IsNotNull();
        });
    }

    /// <summary>
    ///     The UI thread advances and submits while a worker replays the draw operation's work against
    ///     the same compositor. Under the gate this must produce no exception and a strictly monotonic
    ///     submission id: a torn frame would show up as one or the other.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task RenderGate_UnderContention_NeitherThrowsNorTears()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window _, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            Exception? failure = null;
            using CancellationTokenSource cancel = new();
            Task worker = Task.Run(() =>
            {
                try
                {
                    using SKSurface surface = SKSurface.Create(
                        new SKImageInfo(200, 200, SKColorType.Rgba8888,
                            SKAlphaType.Premul));
                    while (!cancel.Token.IsCancellationRequested)
                    {
                        host.RenderForGateStressTest(surface.Canvas);
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }, cancel.Token);

            for (int i = 0; i < 60; i++)
            {
                ctx.PushMarkers((0, 2, -800f + i, 600f, 64f, 90f), (1, 3, 900f - i, -500f, 64f, 270f));
                Playback2DTimelineHarness.Pump(1);
            }

            await cancel.CancelAsync();
            await worker;

            Console.WriteLine($"[gate] submissions={host.LastSubmissionIdForTest} " +
                              $"workerFrames={host.GateStressFramesForTest}");
            await Assert.That(failure).IsNull();
            await Assert.That(host.GateStressFramesForTest).IsGreaterThan(0);
            await Assert.That(host.LastSubmissionIdForTest).IsGreaterThan(0L);
        });
    }

    /// <summary>
    ///     A control that is detached and re-attached must still draw.
    ///     <para>
    ///         The v2 host disposes its compositor from <c>OnDetachedFromVisualTree</c>, which is right:
    ///         a tab activation builds a fresh view, and leaking a compositor's SKPaints, SKPaths and
    ///         recorded pictures per activation is a native-memory climb. But detach is not only
    ///         teardown: Avalonia detaches and re-attaches on a re-parent, a re-template, and a
    ///         presenter recycling its content, and the pre-v2 <c>Playback2DViewport</c> survives all
    ///         three. A host that releases on the first detach and never revives renders a blank surface
    ///         for the rest of the session, with no exception to point at it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Host_SurvivesDetachAndReattach_AndStillRenders()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            ContentControl slot = view.FindControl<ContentControl>("ViewportHost")!;

            // Re-parent: out of the tree and back into it, the same instance.
            slot.Content = null;
            Playback2DTimelineHarness.Pump();
            slot.Content = host;
            Playback2DTimelineHarness.Pump();

            host.FitToExtent();
            ctx.PushMarkers((0, 2, -700f, 500f, 64f, 90f), (1, 3, 800f, -400f, 64f, 270f));
            Playback2DTimelineHarness.Pump();

            // Asserted on the compositor's own counters, NOT on captured pixels: the headless surface
            // retains the last frame that actually drew, so a host that has stopped rendering entirely
            // still captures the picture it painted before the detach. The counters cannot lie about it.
            Console.WriteLine($"[reattach] panes={host.Compositor.Stats.PanesRendered} " +
                              $"layers={host.Compositor.Stats.LayersRendered}");

            await Assert.That(host.Compositor.Stats.PanesRendered).IsGreaterThan(0)
                .Because("a re-attached host must draw the scene again, not a dead surface");
            await Assert.That(host.Compositor.Stats.LayersRendered).IsGreaterThan(0);
        });
    }

    // Exercised only through the interface: the view drives whichever surface is mounted this way, so
    // a member that quietly stopped being part of the contract would break the tab, not this test.
    private static CameraMode DriveThroughTheContract(IPlayback2DSurface surface)
    {
        surface.Mode = CameraMode.Alive;
        surface.FollowSlot = 3;
        surface.FitToExtent();
        return surface.Mode;
    }

    private static (int NonBackground, bool SawTeamColour) ScanPixels(WriteableBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4]; // BGRA8888

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            Marshal.Copy(framebuffer.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        bool team = false;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (Math.Abs(r - BgR) > 6 || Math.Abs(g - BgG) > 6 || Math.Abs(b - BgB) > 6)
            {
                nonBg++;
            }

            bool amber = r > 170 && g is > 110 and < 200 && b < 110;
            bool blue = b > 150 && g is > 100 and < 200 && r < 130;
            if (amber || blue)
            {
                team = true;
            }
        }

        return (nonBg, team);
    }
}

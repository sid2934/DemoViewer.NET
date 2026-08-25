#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>B2's exit criterion, one case per clause:</b> "draw / erase / undo survive seek, zoom, level
///     switch and tab deactivate". Everything provable without a window is proved in the direct-execution
///     suite instead; what is left here genuinely needs a visual tree, real pointer plumbing and a real
///     render pass.
/// </summary>
[NotInParallel]
public class Playback2DAnnotationHostTests
{
    [Test]
    public async Task AnnotationLayer_IsRegistered_WhenTheGateIsOn()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();

            await Assert.That(f.Host.Compositor.Find(SceneLayerIds.Annotations)).IsNotNull();
            await Assert.That(f.Vm.IsAnnotationsEnabled).IsTrue();
        });
    }

    /// <summary>Gated off, the toolbar is gone, the layer is skipped and the tools revert to pan.</summary>
    [Test]
    public async Task GateOff_HidesTheToolbar_AndDropsTheLayer()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);

            f.Ctx.Gate!.SetEnabled("playback2d.annotations", false);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Vm.IsAnnotationsEnabled).IsFalse();
            await Assert.That(f.Vm.AnnotationSession).IsNull();
            await Assert.That(f.Vm.Annotations.ActiveTool).IsEqualTo(ToolKind.PanZoom);
            await Assert.That(f.Host.Compositor.Find(SceneLayerIds.Annotations)).IsNull();
        });
    }

    [Test]
    public async Task Draw_ThenSeek_StrokeSurvives()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            IReadOnlyList<InkPoint> before = f.Document.Elements[0].Points;

            // Seek backward, then forward past where the stroke was made.
            f.Ctx.Push(40, 640);
            Playback2DTimelineHarness.Pump();
            f.Ctx.Push(400, 6400);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0].Points).IsEquivalentTo(before);
        });
    }

    [Test]
    public async Task Draw_ThenZoomAndPan_StrokeStaysWorldAnchored()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();

            AnnotationElement element = f.Document.Elements[0];
            InkPoint anchor = element.Points[0];
            Point screenBefore = f.WorldToWindow(anchor.X, anchor.Y);

            f.Vm.Annotations.SelectTool(ToolKind.PanZoom);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseWheel(f.HostPoint(300, 300), new Vector(0, 1));
            Playback2DTimelineHarness.Pump();
            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(360, 340));
            f.Window.MouseUp(f.HostPoint(360, 340), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            Point screenAfter = f.WorldToWindow(anchor.X, anchor.Y);

            await Assert.That(f.Document.Elements[0].Points).IsEquivalentTo(element.Points)
                .Because("ink is stored in WORLD units; a camera move must not touch a single sample");
            await Assert.That(Math.Abs(screenAfter.X - screenBefore.X)
                              + Math.Abs(screenAfter.Y - screenBefore.Y)).IsGreaterThan(1)
                .Because("...but it must land somewhere else on screen, or the camera did nothing");
        });
    }

    /// <summary>
    ///     Plan decision D6, and the reason the remap is history-transparent: the stroke follows its
    ///     PHYSICAL floor across a rebuild, and no undo slot is consumed doing it.
    /// </summary>
    [Test]
    public async Task Draw_ThenLevelRebuild_StrokeRemapsToSameLevel()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();

            double before = ((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ;
            int undoBefore = f.Document.UndoDepth;

            f.Vm.ApplyAnnotationLevelRebuild(new Dictionary<double, double>
            {
                [before] = before + 64
            });

            await Assert.That(((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ)
                .IsEqualTo(before + 64);
            await Assert.That(f.Document.UndoDepth).IsEqualTo(undoBefore)
                .Because("a level rebuild is a system event; Ctrl+Z must not restore a stale anchor");
        });
    }

    [Test]
    public async Task Draw_ThenDeactivateReactivateTab_StrokeSurvives()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            AnnotationElement drawn = f.Document.Elements[0];

            f.Vm.OnDeactivated();
            f.Vm.OnActivated(f.Ctx);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0]).IsEqualTo(drawn);
        });
    }

    [Test]
    public async Task Erase_ThenUndo_StrokeReturns()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            AnnotationElement drawn = f.Document.Elements[0];

            f.Vm.Annotations.SelectTool(ToolKind.Erase);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(400, 320));
            f.Window.MouseUp(f.HostPoint(400, 320), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements).IsEmpty();

            f.Document.Undo();
            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0]).IsEqualTo(drawn)
                .Because("a restored stroke must come back byte-identical, samples included");
        });
    }

    /// <summary>
    ///     The undo-scope contract (design risk 13). A seek between the stroke and the Ctrl+Z must not
    ///     end up on the history stack — the document has no reference to a playhead at all.
    /// </summary>
    [Test]
    public async Task Undo_AfterSeek_UndoesTheStroke_NotTheSeek()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            await Assert.That(f.Document.UndoDepth).IsEqualTo(1);

            f.Ctx.Push(500, 8000);
            Playback2DTimelineHarness.Pump();
            int frameAfterSeek = f.Ctx.CurrentFrameIndex;

            await Assert.That(f.Vm.Annotations.CanUndo).IsTrue()
                .Because("the toolbar's undo button must light up the moment the stroke is committed");
            await Assert.That(f.Vm.ExecuteAction(Playback2DAction.Undo)).IsTrue();

            await Assert.That(f.Document.Elements).IsEmpty();
            await Assert.That(f.Ctx.CurrentFrameIndex).IsEqualTo(frameAfterSeek)
                .Because("the annotation history holds annotations and nothing else");
        });
    }

    /// <summary>Plan decision D3: hold-Space diverts the next press to pan even under the draw tool.</summary>
    [Test]
    public async Task HoldSpace_DuringDrawTool_Pans()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            f.FocusForKeys();

            ViewportTransform before = f.Host.PrimaryCameraTransform;

            f.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(360, 340));
            f.Window.MouseUp(f.HostPoint(360, 340), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            f.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements).IsEmpty()
                .Because("hold-Space diverts the whole gesture to pan; no ink is committed");
            await Assert.That(f.Host.PrimaryCameraTransform.PanX).IsEqualTo(before.PanX + 60).Within(0.5);
        });
    }

    [Test]
    public async Task Escape_MidStroke_LeavesNoElement()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            f.FocusForKeys();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(340, 330));
            Playback2DTimelineHarness.Pump();
            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsTrue();

            f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsFalse();
            await Assert.That(f.Document.Elements).IsEmpty();
            await Assert.That(f.Document.UndoDepth).IsEqualTo(0);
        });
    }

    /// <summary>The ink actually reaches the surface — a captured frame, for eyeball review too.</summary>
    [Test]
    public async Task DrawnStroke_RendersOnTheSurface()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.InkColor = Avalonia.Media.Color.FromRgb(0xFF, 0x00, 0xFF);
            f.Vm.Annotations.InkWidth = 40;
            f.DrawStroke();
            Playback2DTimelineHarness.Pump(6);

            WriteableBitmap? captured = f.Window.CaptureRenderedFrame();
            await Assert.That(captured).IsNotNull();

            string path = Path.Combine(HeadlessSession.ArtifactDir, "annotations-stroke.png");
            Directory.CreateDirectory(HeadlessSession.ArtifactDir);
            captured!.Save(path);

            int magenta = CountMagenta(captured);
            Console.WriteLine($"[annotations] {path} magenta={magenta}");
            await Assert.That(magenta).IsGreaterThan(200);
        });
    }

    // BGRA8888, copied out through Marshal rather than pointer arithmetic so the suite needs no
    // unsafe blocks.
    private static int CountMagenta(WriteableBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            Marshal.Copy(framebuffer.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            byte b = buffer[i];
            byte g = buffer[i + 1];
            byte r = buffer[i + 2];
            if (r > 170 && b > 170 && g < 90)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>An activated tab, a shown window with the v2 host, and the pointer helpers.</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(Playback2DTabViewModel vm, Playback2DFakeContext ctx, Window window,
            Playback2DView view)
        {
            Vm = vm;
            Ctx = ctx;
            Window = window;
            View = view;
            Host = Playback2DTimelineHarness.SceneHost(view);
        }

        public Playback2DTabViewModel Vm { get; }

        public Playback2DFakeContext Ctx { get; }

        public Window Window { get; }

        public Playback2DView View { get; }

        public Scene2DHost Host { get; }

        public AnnotationDocument Document => Vm.Annotations.Document;

        public static Fixture Create()
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Fixture fixture = new(vm, ctx, window, view);
            fixture.Host.FitToExtent();
            Playback2DTimelineHarness.Pump();
            return fixture;
        }

        /// <summary>
        ///     Focuses the view so the tunnelling key handlers see the keyboard. Nothing focuses it on
        ///     its own until the user clicks the surface, and a key test that never clicks would
        ///     otherwise silently assert nothing.
        /// </summary>
        public void FocusForKeys()
        {
            View.Focus();
            Playback2DTimelineHarness.Pump();
        }

        /// <summary>Host-local point → window coordinates, for headless input.</summary>
        /// <param name="x">Host X.</param>
        /// <param name="y">Host Y.</param>
        public Point HostPoint(double x, double y) =>
            Playback2DTimelineHarness.ToWindow(Host, Window, x, y);

        /// <summary>A world point projected through the primary pane's camera into window coordinates.</summary>
        /// <param name="worldX">World X.</param>
        /// <param name="worldY">World Y.</param>
        public Point WorldToWindow(double worldX, double worldY)
        {
            (double sx, double sy) = Host.PrimaryCameraTransform.WorldToScreen(worldX, worldY);
            return HostPoint(sx, sy);
        }

        /// <summary>Selects the draw tool and drags a three-sample stroke well clear of the toolbar.</summary>
        public void DrawStroke()
        {
            Vm.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();

            Window.MouseDown(HostPoint(300, 300), MouseButton.Left);
            Window.MouseMove(HostPoint(350, 310));
            Window.MouseMove(HostPoint(400, 320));
            Window.MouseUp(HostPoint(400, 320), MouseButton.Left);
            Playback2DTimelineHarness.Pump();
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
        }
    }
}

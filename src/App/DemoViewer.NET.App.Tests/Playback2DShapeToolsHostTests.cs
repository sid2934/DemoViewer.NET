#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Shape Tools through the real surface (step-authoring.md §3.2): the host registers the shape and
///     text tools on its router, a drag commits one shape, Esc still cancels under a shape tool, and the
///     text tool's inline editor places a label on Enter and drops it on Esc. What needs no window is
///     proved in the Playback2D suite's <c>ShapeToolTests</c> and <c>TextToolTests</c>.
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DShapeToolsHostTests
{
    [Test]
    [Arguments(ToolKind.Line, AnnotationKind.Line)]
    [Arguments(ToolKind.Arrow, AnnotationKind.Arrow)]
    [Arguments(ToolKind.Rect, AnnotationKind.Rect)]
    [Arguments(ToolKind.Ellipse, AnnotationKind.Ellipse)]
    public async Task Drag_WithAShapeTool_CommitsOneTwoPointShape(ToolKind tool, AnnotationKind kind)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(tool);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(340, 320));
            f.Window.MouseMove(f.HostPoint(420, 360));
            f.Window.MouseUp(f.HostPoint(420, 360), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0].Kind).IsEqualTo(kind);
            await Assert.That(f.Document.Elements[0].Points.Count).IsEqualTo(2);
            await Assert.That(f.Document.UndoDepth).IsEqualTo(1);
        });
    }

    [Test]
    public async Task Escape_MidShape_LeavesNoElement()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Rect);
            f.FocusForKeys();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(380, 360));
            Playback2DTimelineHarness.Pump();
            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsTrue();

            f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsFalse();
            await Assert.That(f.Document.Elements).IsEmpty();
        });
    }

    [Test]
    public async Task TextTool_Click_OpensTheEditor_AndEnterPlacesTheLabel()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Text);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseUp(f.HostPoint(300, 300), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            TextBox editor = f.Editor;
            await Assert.That(editor.IsVisible).IsTrue();
            await Assert.That(editor.IsFocused).IsTrue()
                .Because("the editor takes the keyboard, so typing does not drive the keymap");
            await Assert.That(f.Document.IsGestureOpen).IsTrue();

            editor.Text = "B short";
            f.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(editor.IsVisible).IsFalse();
            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0].Kind).IsEqualTo(AnnotationKind.Text);
            await Assert.That(f.Document.Elements[0].Text).IsEqualTo("B short");
            await Assert.That(f.Document.IsGestureOpen).IsFalse();
            await Assert.That(f.Document.UndoDepth).IsEqualTo(1);
        });
    }

    [Test]
    public async Task TextTool_Escape_DropsTheLabel()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Text);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseUp(f.HostPoint(300, 300), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            f.Editor.Text = "never mind";
            f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Editor.IsVisible).IsFalse();
            await Assert.That(f.Document.Elements).IsEmpty();
            await Assert.That(f.Document.UndoDepth).IsEqualTo(0);
            await Assert.That(f.Vm.Annotations.ActiveTool).IsEqualTo(ToolKind.Text)
                .Because("Esc in the editor cancels the label, not the tool");
        });
    }

    [Test]
    public async Task TextTool_AClickElsewhere_KeepsWhatWasTyped()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Text);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseUp(f.HostPoint(300, 300), MouseButton.Left);
            Playback2DTimelineHarness.Pump();
            f.Editor.Text = "first";

            // The second click commits the first label, then places a second one.
            f.Window.MouseDown(f.HostPoint(420, 340), MouseButton.Left);
            f.Window.MouseUp(f.HostPoint(420, 340), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(2);
            await Assert.That(f.Document.Elements[0].Text).IsEqualTo("first");
            await Assert.That(f.Editor.IsVisible).IsTrue();
        });
    }

    /// <summary>An activated tab, a shown window with the v2 host, and the pointer helpers.</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(Playback2DTabViewModel vm, Window window, Playback2DView view)
        {
            Vm = vm;
            Window = window;
            View = view;
            Host = Playback2DTimelineHarness.SceneHost(view);
            Editor = view.FindControl<TextBox>("AnnotationTextEditor")!;
        }

        public Playback2DTabViewModel Vm { get; }

        public Window Window { get; }

        public Playback2DView View { get; }

        public Scene2DHost Host { get; }

        public TextBox Editor { get; }

        public AnnotationDocument Document => Vm.Annotations.Document;

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
        }

        public static Fixture Create()
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Fixture fixture = new(vm, window, view);
            fixture.Host.FitToExtent();
            Playback2DTimelineHarness.Pump();
            return fixture;
        }

        public void FocusForKeys()
        {
            View.Focus();
            Playback2DTimelineHarness.Pump();
        }

        public Point HostPoint(double x, double y) =>
            Playback2DTimelineHarness.ToWindow(Host, Window, x, y);
    }
}

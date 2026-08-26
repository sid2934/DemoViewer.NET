#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The tool router: one active tool, per-pane pan capture, router-level wheel, hold-Space diversion
///     and Esc bail. Every case here is a behaviour the pre-v2 viewport had (or that B2's decisions add
///     on top of it) and that a mis-wired router would silently lose.
/// </summary>
public class InputToolRouterTests
{
    [Test]
    public async Task DefaultTool_IsPanZoom()
    {
        (InputToolRouter router, FakeToolServices _, PaneSet _) = Build();

        await Assert.That(router.ActiveKind).IsEqualTo(ToolKind.PanZoom);
        await Assert.That(router.Active).IsTypeOf<PanZoomTool>();
        await Assert.That(router.IsDrawingToolActive).IsFalse();
    }

    /// <summary>
    ///     The invariant the pre-v2 viewport encodes by capturing <c>_dragSlice</c> at press: a drag that
    ///     wanders into another band keeps panning the band it began on, or a fast diagonal drag would
    ///     yank two floors at once.
    /// </summary>
    [Test]
    public async Task PanZoom_Drag_PansCapturedPaneOnly()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet panes) = Build();

        LevelPane upper = panes.Panes[1];
        LevelPane lower = panes.Panes[0];
        ViewportTransform lowerBefore = lower.Camera.Current;

        SKPoint start = new(200, 100);
        LevelPane captured = services.PaneAt(start)!;
        router.OnPressed(Sample(captured, start));
        router.OnMoved(Sample(captured, new SKPoint(260, 140)));
        router.OnReleased(Sample(captured, new SKPoint(260, 140)));

        await Assert.That(captured.Camera.Current.PanX).IsEqualTo(60);
        await Assert.That(captured.Camera.Current.PanY).IsEqualTo(40);
        await Assert.That(ReferenceEquals(captured, upper) || ReferenceEquals(captured, lower)).IsTrue();

        LevelPane other = ReferenceEquals(captured, upper) ? lower : upper;
        await Assert.That(other.Camera.Current.PanX).IsEqualTo(
            ReferenceEquals(other, lower) ? lowerBefore.PanX : other.Camera.Current.PanX);
        await Assert.That(other.Camera.Current.PanX).IsEqualTo(0);
        await Assert.That(other.Camera.Current.PanY).IsEqualTo(0);
    }

    [Test]
    public async Task PanZoom_Drag_SetsManualOverride()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        await Assert.That(pane.Camera.ManualOverride).IsFalse();

        router.OnPressed(Sample(pane, start));
        router.OnMoved(Sample(pane, new SKPoint(210, 110)));

        await Assert.That(pane.Camera.ManualOverride).IsTrue();
    }

    /// <summary>Plan decision D2: wheel is router-level, so zoom-to-cursor survives every tool.</summary>
    [Test]
    [Arguments(ToolKind.PanZoom)]
    [Arguments(ToolKind.Draw)]
    [Arguments(ToolKind.Erase)]
    public async Task Wheel_ZoomsAboutCursor_UnderEveryTool(ToolKind kind)
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(kind);

        SKPoint cursor = new(200, 100);
        LevelPane pane = services.PaneAt(cursor)!;
        double zoomBefore = pane.Camera.Current.Zoom;
        SKPoint worldUnder = services.ScreenToWorld(pane, cursor);

        router.OnWheel(new ToolWheelEvent(pane, cursor,
            new SKPoint(cursor.X - pane.ViewportRect.Left, cursor.Y - pane.ViewportRect.Top),
            1, ToolModifiers.None));

        await Assert.That(pane.Camera.Current.Zoom).IsGreaterThan(zoomBefore);
        await Assert.That(pane.Camera.ManualOverride).IsTrue();

        SKPoint worldAfter = services.ScreenToWorld(pane, cursor);
        await Assert.That(Math.Abs(worldAfter.X - worldUnder.X)).IsLessThan(0.01f);
        await Assert.That(Math.Abs(worldAfter.Y - worldUnder.Y)).IsLessThan(0.01f);
    }

    /// <summary>Plan decision D3: hold-Space diverts the NEXT press to pan.</summary>
    [Test]
    public async Task SpaceHeld_DivertsNextPressToPan()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);
        router.IsSpaceHeld = true;

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));
        router.OnMoved(Sample(pane, new SKPoint(240, 130)));

        await Assert.That(services.Session.Wet.IsActive).IsFalse();
        await Assert.That(services.Session.Document.IsGestureOpen).IsFalse();
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(40);
        await Assert.That(router.GestureTool).IsTypeOf<PanZoomTool>();
    }

    /// <summary>
    ///     D3's other half. Releasing (or pressing) Space mid-gesture must not re-route the gesture: a
    ///     half-committed stroke is worse than a missed pan.
    /// </summary>
    [Test]
    public async Task SpaceHeld_DoesNotHijackOpenGesture()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));
        await Assert.That(services.Session.Wet.IsActive).IsTrue();

        router.IsSpaceHeld = true;
        router.OnMoved(Sample(pane, new SKPoint(240, 130)));

        await Assert.That(services.Session.Wet.IsActive).IsTrue()
            .Because("the tool that took the press keeps the whole gesture");
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(0);
    }

    [Test]
    public async Task Escape_CancelsGesture_AndBailsDocument()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));
        router.OnMoved(Sample(pane, new SKPoint(240, 130)));

        router.CancelActive();

        await Assert.That(services.Session.Wet.IsActive).IsFalse();
        await Assert.That(services.Session.Document.Elements).IsEmpty();
        await Assert.That(services.Session.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(services.Session.Document.IsGestureOpen).IsFalse();
        await Assert.That(router.IsGestureOpen).IsFalse();
    }

    [Test]
    public async Task SetActive_MidGesture_CancelsFirst()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));
        await Assert.That(services.Session.Document.IsGestureOpen).IsTrue();

        router.SetActive(ToolKind.Erase);

        await Assert.That(services.Session.Wet.IsActive).IsFalse();
        await Assert.That(services.Session.Document.IsGestureOpen).IsFalse();
        await Assert.That(services.Session.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(router.ActiveKind).IsEqualTo(ToolKind.Erase);
    }

    [Test]
    public async Task SetActive_MirrorsOntoTheSession_AndRaisesOnce()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        List<ToolKind> raised = [];
        router.ActiveToolChanged += raised.Add;

        router.SetActive(ToolKind.Draw);
        router.SetActive(ToolKind.Draw);

        await Assert.That(raised.Count).IsEqualTo(1);
        await Assert.That(services.Session.ActiveTool).IsEqualTo(ToolKind.Draw);
        await Assert.That(router.IsDrawingToolActive).IsTrue();
    }

    /// <summary>
    ///     D2 §2.3: the middle button pans under EVERY tool. A pen that takes the wheel button hostage
    ///     leaves no way back to the view except putting it down.
    /// </summary>
    [Test]
    [Arguments(ToolKind.Draw)]
    [Arguments(ToolKind.Erase)]
    public async Task MiddleDrag_Pans_UnderEveryDrawingTool(ToolKind kind)
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(kind);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start, ToolPointerButton.Middle));
        router.OnMoved(Sample(pane, new SKPoint(240, 130), ToolPointerButton.Middle));

        await Assert.That(router.GestureTool).IsTypeOf<PanZoomTool>();
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(40);
        await Assert.That(services.Session.Wet.IsActive).IsFalse();
        await Assert.That(services.Session.Document.IsGestureOpen).IsFalse()
            .Because("neither the pen nor the eraser may open a document gesture on a middle-drag");
    }

    /// <summary>The other half of D2 §2.3, for the pointing device that has no middle button.</summary>
    [Test]
    [Arguments(ToolKind.Draw)]
    [Arguments(ToolKind.Erase)]
    public async Task ControlDrag_Pans_UnderEveryDrawingTool(ToolKind kind)
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(kind);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start, modifiers: ToolModifiers.Control));
        router.OnMoved(Sample(pane, new SKPoint(240, 130), modifiers: ToolModifiers.Control));

        await Assert.That(router.GestureTool).IsTypeOf<PanZoomTool>();
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(40);
        await Assert.That(services.Session.Wet.IsActive).IsFalse();
        await Assert.That(services.Session.Document.IsGestureOpen).IsFalse();
    }

    [Test]
    public async Task PanOnMiddleButton_Off_LeavesTheButtonToTheTool()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);
        router.PanOnMiddleButton = false;

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start, ToolPointerButton.Middle));

        await Assert.That(services.Session.Wet.IsActive).IsTrue();
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(0);
    }

    /// <summary>
    ///     The <see cref="SpaceHeld_DoesNotHijackOpenGesture" /> invariant, for the two new diversions:
    ///     an accidental middle-click halfway through a stroke must not trade the ink for a pan.
    /// </summary>
    [Test]
    public async Task MiddlePress_DuringAnOpenStroke_DoesNotHijackIt()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));
        await Assert.That(services.Session.Wet.IsActive).IsTrue();

        bool took = router.OnPressed(Sample(pane, new SKPoint(240, 130), ToolPointerButton.Middle));

        await Assert.That(took).IsFalse().Because("chording is not a gesture");
        await Assert.That(router.GestureTool).IsTypeOf<DrawTool>();
        await Assert.That(services.Session.Wet.IsActive).IsTrue();
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(0);
    }

    /// <summary>Ctrl coming down mid-stroke is a modifier change, and modifiers are read at press only.</summary>
    [Test]
    public async Task ControlHeld_DoesNotHijackOpenGesture()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));
        router.OnMoved(Sample(pane, new SKPoint(240, 130), modifiers: ToolModifiers.Control));

        await Assert.That(services.Session.Wet.IsActive).IsTrue()
            .Because("the tool that took the press keeps the whole gesture");
        await Assert.That(pane.Camera.Current.PanX).IsEqualTo(0);
    }

    /// <summary>D2 §2.2: the right button is a tool binding, not a second left button.</summary>
    [Test]
    public async Task RightPress_RoutesToTheSecondaryTool()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);
        router.SecondaryTool = ToolKind.Erase;

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start, ToolPointerButton.Right));

        await Assert.That(router.GestureTool).IsTypeOf<EraseTool>();
        await Assert.That(services.Session.Wet.IsActive).IsFalse();
    }

    [Test]
    public async Task LeftPress_IgnoresTheSecondaryTool()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);
        router.SecondaryTool = ToolKind.Erase;

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start));

        await Assert.That(router.GestureTool).IsTypeOf<DrawTool>();
        await Assert.That(services.Session.Wet.IsActive).IsTrue();
    }

    /// <summary>Null — the shipped default — means "the same tool", so a right-drag still draws.</summary>
    [Test]
    public async Task RightPress_WithNoSecondaryTool_StaysOnTheActiveTool()
    {
        (InputToolRouter router, FakeToolServices services, PaneSet _) = Build();
        router.SetActive(ToolKind.Draw);

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start, ToolPointerButton.Right));

        await Assert.That(router.SecondaryTool).IsNull();
        await Assert.That(router.GestureTool).IsTypeOf<DrawTool>();
        await Assert.That(services.Session.Wet.IsActive).IsTrue();
    }

    /// <summary>
    ///     The binding arrives from a persisted string, so an unregistered kind has to degrade to the
    ///     active tool rather than silently hand the right button to pan.
    /// </summary>
    [Test]
    public async Task UnregisteredSecondaryTool_FallsBackToTheActiveTool()
    {
        (MapSpace _, PaneSet panes) = AnnotationFakes.Panes(new SKSize(600, 400),
            new FloorSlice(-448, -384), new FloorSlice(-384, -128));

        AnnotationSession session = new(new AnnotationDocument());
        FakeToolServices services = new(session, panes);
        InputToolRouter router = new(services, new PanZoomTool());
        router.Register(new DrawTool());
        router.SetActive(ToolKind.Draw);
        router.SecondaryTool = ToolKind.Erase; // never registered

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Sample(pane, start, ToolPointerButton.Right));

        await Assert.That(router.GestureTool).IsTypeOf<DrawTool>();
    }

    private static ToolPointerEvent Sample(LevelPane pane, SKPoint screen,
        ToolPointerButton button = ToolPointerButton.Left,
        ToolModifiers modifiers = ToolModifiers.None)
    {
        (double wx, double wy) = pane.Camera.Current.ScreenToWorld(
            screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);

        return new ToolPointerEvent
        {
            Pane = pane,
            Screen = screen,
            PaneLocal = new SKPoint(screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top),
            World = new SKPoint((float)wx, (float)wy),
            Pressure = 0.5f,
            Button = button,
            Modifiers = modifiers
        };
    }

    private static (InputToolRouter Router, FakeToolServices Services, PaneSet Panes) Build()
    {
        (MapSpace _, PaneSet panes) = AnnotationFakes.Panes(new SKSize(600, 400),
            new FloorSlice(-448, -384), new FloorSlice(-384, -128));

        AnnotationSession session = new(new AnnotationDocument());
        FakeToolServices services = new(session, panes);
        InputToolRouter router = new(services, new PanZoomTool());
        router.Register(new DrawTool());
        router.Register(new EraseTool());
        return (router, services, panes);
    }
}

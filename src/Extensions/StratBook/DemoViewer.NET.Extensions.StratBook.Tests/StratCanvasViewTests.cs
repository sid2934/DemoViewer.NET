#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Views.StratBook;
using static DemoViewer.NET.AppTests.StratCanvasTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Step Authoring canvas view in a headless window: the scene host inside it binds the canvas as its
///     frame host through the view's DataContext, the keys reach the canvas through the shared keymap, and a
///     tool picked by key reaches the host's router, the four wirings the 2D view makes around its own host.
/// </summary>
[NotInParallel]
[Category("Render")]
public class StratCanvasViewTests
{
    [Test]
    public async Task TheView_BindsTheCanvasAsTheHostsFrameHost_AndRoutesItsKeys()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (StratStore _, StratSession session) = Opened(FiveSteps());
            using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());

            StratCanvasView view = new() { DataContext = canvas };
            Window window = new() { Width = 1100, Height = 800, Content = view };
            window.Show();
            view.Focus();
            Playback2DTimelineHarness.Pump();

            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();
            await Assert.That(host.FrameHost).IsSameReferenceAs(canvas);

            // ] then V: the first step is shown, and the token tool is on the host's router.
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            using (Assert.Multiple())
            {
                await Assert.That(canvas.Transport.Tick).IsEqualTo(320);
                await Assert.That(host.CurrentSceneFrame.Markers.Count).IsEqualTo(6);
                await Assert.That(canvas.IsTokenToolSelected).IsTrue();
                await Assert.That(host.Router.ActiveKind).IsEqualTo(ToolKind.Token);
            }

            // Ctrl+Z reaches the strat's history: a step added by key goes away by key.
            window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Shift);
            await Assert.That(session.Document!.Steps.Count).IsEqualTo(6);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            await Assert.That(session.Document!.Steps.Count).IsEqualTo(5);

            window.Close();
        });
    }

    /// <summary>
    ///     On the tab the canvas sits beside the step table, and opening a strat is what binds it: the host
    ///     under the tab finds the canvas, not the tab's own view-model, which is not a frame host.
    /// </summary>
    [Test]
    public async Task OnTheStratBookTab_OpeningAStrat_BindsTheCanvas()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StratDocument document = FiveSteps();
            document.Owner = StratOwner.Me();
            (StratStore store, StratSession parked) = Opened(document);
            parked.Dispose();

            using ViewModels.StratBook.StratBookTabViewModel tab = new(store, null, null, false, null, _ => null);
            tab.SelectedStrat = tab.Strats.Single();

            StratBookTabView view = new() { DataContext = tab };
            Window window = new() { Width = 1600, Height = 900, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();

            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();
            using (Assert.Multiple())
            {
                await Assert.That(tab.HasOpenStrat).IsTrue();
                await Assert.That(host.FrameHost).IsSameReferenceAs(tab.Canvas);
                await Assert.That(tab.Canvas.Projection!.Path.Count).IsEqualTo(5);
            }

            window.Close();
        });
    }

    /// <summary>
    ///     Set On Map through the real view: the toolbar button arms it, Esc from the keyboard ends it without
    ///     writing, and a plain left click on the map reaches the canvas ahead of the pointer tools and writes the
    ///     selected step's <c>to</c> as one entry.
    /// </summary>
    [Test]
    public async Task SetOnMap_EscCancels_AndAClickOnTheMapWritesTheSelectedStep()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (StratStore _, StratSession session) = Opened(FiveSteps());
            using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
                placesFor: _ => Task.FromResult<IZonePlaceResolver?>(new StratMapFirstTests.EverywhereIs("Hut")), post: a => a());

            StratCanvasView view = new() { DataContext = canvas };
            Window window = new() { Width = 1100, Height = 800, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();
            canvas.SelectStep(session.Document!.Steps[1].Id);
            Playback2DTimelineHarness.Pump();

            ToggleButton button = view.GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b.Content as string == "Set “to” on map");
            await Assert.That(button.IsVisible).IsTrue();
            button.Command!.Execute(null);
            Playback2DTimelineHarness.Pump();
            await Assert.That(canvas.IsSettingPlace).IsTrue();
            await Assert.That(button.IsChecked).IsTrue();

            view.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            using (Assert.Multiple())
            {
                await Assert.That(canvas.IsSettingPlace).IsFalse();
                await Assert.That(session.UndoDepth).IsEqualTo(0);
            }

            canvas.BeginSetPlace();
            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();
            Point centre = Playback2DTimelineHarness.ToWindow(host, window, host.Bounds.Width / 2, host.Bounds.Height / 2);
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Hut");
                await Assert.That(session.UndoDepth).IsEqualTo(1);
                await Assert.That(canvas.IsSettingPlace).IsFalse();
            }

            window.Close();
        });
    }

    /// <summary>
    ///     On the tab, a press in a step row selects that step on the canvas, and a step chosen on the canvas
    ///     highlights its row.
    /// </summary>
    [Test]
    public async Task OnTheStratBookTab_ARowAndTheCanvas_ShareOneSelectedStep()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StratDocument document = FiveSteps();
            document.Owner = StratOwner.Me();
            (StratStore store, StratSession parked) = Opened(document);
            parked.Dispose();

            using ViewModels.StratBook.StratBookTabViewModel tab = new(store, null, a => a(), false, null, _ => null);
            StratBookTabView view = new() { DataContext = tab };

            // Short enough that the first step row is below the fold, and the strat opens with the view up.
            Window window = new() { Width = 1600, Height = 600, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();
            tab.SelectedStrat = tab.Strats.Single();
            Playback2DTimelineHarness.Pump();

            // Opening a strat selects its first step without scrolling the editor down to it.
            ItemsControl rows = view.FindControl<ItemsControl>("StepRows")!;
            ScrollViewer editor = rows.GetVisualAncestors().OfType<ScrollViewer>().First();
            await Assert.That(tab.Editor.Steps[0].IsSelected).IsTrue();
            await Assert.That(editor.Offset.Y).IsEqualTo(0);

            // The third row's note box: focus inside a row selects its step.
            TextBox note = rows.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.PlaceholderText == "note").ElementAt(2);
            note.Focus();
            Playback2DTimelineHarness.Pump();

            using (Assert.Multiple())
            {
                await Assert.That(tab.Canvas.ActiveStepIndex).IsEqualTo(2);
                await Assert.That(tab.Canvas.Transport.Tick).IsEqualTo(1600);
                await Assert.That(tab.Editor.Steps[2].IsSelected).IsTrue();
                await Assert.That(rows.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("selected"))).IsEqualTo(1);
            }

            tab.Canvas.ExecuteAction(Playback2DAction.NextStep);
            Playback2DTimelineHarness.Pump();
            using (Assert.Multiple())
            {
                await Assert.That(tab.Editor.Steps[3].IsSelected).IsTrue();
                await Assert.That(tab.Editor.Steps[2].IsSelected).IsFalse();
            }

            window.Close();
        });
    }

    /// <summary>
    ///     Under the default pan tool a press on a token drags it and writes the selected step's to as one entry, and a
    ///     press on empty map pans; under the pen, a press on a token draws.
    /// </summary>
    [Test]
    public async Task UnderPan_ATokenPressDrags_AnEmptyPressPans_AndThePenStillDraws()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (StratStore _, StratSession session) = Opened(FiveSteps());
            using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
            StratCanvasView view = new() { DataContext = canvas };
            Window window = new() { Width = 1100, Height = 800, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();
            canvas.SelectStep(session.Document!.Steps[1].Id);
            Playback2DTimelineHarness.Pump();
            await Assert.That(canvas.Annotations.ActiveTool).IsEqualTo(ToolKind.PanZoom);

            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();
            Point Screen(double x, double y)
            {
                (double sx, double sy) = host.PrimaryCameraTransform.WorldToScreen(x, y);
                return Playback2DTimelineHarness.ToWindow(host, window, sx, sy);
            }

            // A is at (600, 0) at step 2.
            Point a = Screen(600, 0);
            window.MouseDown(a, MouseButton.Left);
            window.MouseMove(new Point(a.X + 20, a.Y));
            window.MouseMove(new Point(a.X + 40, a.Y));
            window.MouseUp(new Point(a.X + 40, a.Y), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            // The drop is step 2's to; A still leaves from its spot at the step's time.
            PlaceRef moved = session.Document!.Steps[1].To!;
            using (Assert.Multiple())
            {
                await Assert.That(session.UndoDepth).IsEqualTo(1);
                await Assert.That(moved.X!.Value).IsGreaterThan(600);
                await Assert.That(session.Document!.Steps[1].Positions.Single(p => p.Slot == "A").X).IsEqualTo(600);
                await Assert.That(canvas.Annotations.ActiveTool).IsEqualTo(ToolKind.PanZoom);
            }

            // Empty map: the camera moves and the strat does not.
            Point empty = Screen(-1500, 1500);
            Point before = Screen(0, 0);
            window.MouseDown(empty, MouseButton.Left);
            window.MouseMove(new Point(empty.X + 30, empty.Y + 30));
            window.MouseUp(new Point(empty.X + 30, empty.Y + 30), MouseButton.Left);
            Playback2DTimelineHarness.Pump();
            using (Assert.Multiple())
            {
                await Assert.That(session.UndoDepth).IsEqualTo(1);
                await Assert.That(Screen(0, 0)).IsNotEqualTo(before).Because("the press panned");
            }

            // The pen keeps its own press: a stroke from the token, which stays put.
            canvas.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();
            Point a2 = Screen(600, 0);
            window.MouseDown(a2, MouseButton.Left);
            window.MouseMove(new Point(a2.X + 20, a2.Y + 20));
            window.MouseMove(new Point(a2.X + 40, a2.Y + 40));
            window.MouseUp(new Point(a2.X + 40, a2.Y + 40), MouseButton.Left);
            Playback2DTimelineHarness.Pump();
            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Steps[1].To!.X).IsEqualTo(moved.X);
                await Assert.That(session.Document!.Steps[1].Strokes.Count).IsEqualTo(1);
            }

            window.Close();
        });
    }
}

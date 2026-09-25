#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Input;
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
}

#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The Strat Book tab in a window: a token dragged with the pointer changes the field its step row shows.</summary>
[NotInParallel]
[Category("Render")]
public class StratDragWindowTests
{
    [Test]
    public async Task DraggingATokenWithThePointer_ChangesTheRowsField() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = new(new StratStore(null), null, a => Dispatcher.UIThread.Post(a), false, layout: new StratBookLayout());
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_mirage";
            vm.NewStratCommand.Execute(null);

            // Everyone at one spot on a hold, then A's move with no place yet.
            StratStep seed = new()
            {
                Id = Guid.NewGuid(), AtSeconds = 115, Actor = StratVocabulary.ActorAll, Verb = "hold",
                Positions = [.. StratVocabulary.Slots.Select((s, i) => new StepPosition { Slot = s, X = i * 300, Y = 0, LevelMinZ = 0 })]
            };
            StratStep move = new() { Id = Guid.NewGuid(), AtSeconds = 110, Actor = "A", Verb = "move" };
            vm.Session.Apply(PatchOp.ReplaceOp("/steps", null, new JsonArray(
                JsonSerializer.SerializeToNode(seed, StratJsonContext.Default.StratStep),
                JsonSerializer.SerializeToNode(move, StratJsonContext.Default.StratStep))));
            int depth = vm.Session.UndoDepth;

            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();
            vm.StepSelection.Select(move.Id);
            Playback2DTimelineHarness.Pump();

            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().First(h => h.IsEffectivelyVisible);
            Point Screen(double x, double y)
            {
                (double sx, double sy) = host.PrimaryCameraTransform.WorldToScreen(x, y);
                return Playback2DTimelineHarness.ToWindow(host, window, sx, sy);
            }

            Point a = Screen(0, 0);
            window.MouseDown(a, MouseButton.Left);
            window.MouseMove(new Point(a.X + 30, a.Y + 10));
            Playback2DTimelineHarness.Pump();
            await Assert.That(vm.Canvas.DragLabel).StartsWith("A · to: (");
            window.MouseMove(new Point(a.X + 60, a.Y + 20));
            window.MouseUp(new Point(a.X + 60, a.Y + 20), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            PlaceRef to = vm.Session.Document!.Steps[1].To!;
            StratStepRow row = vm.Editor.Steps[1];
            PlaceField field = view.GetVisualDescendants().OfType<PlaceField>()
                .First(f => f.Name == "GroupPlaceField" && ReferenceEquals(f.DataContext, row));
            TextBox box = field.GetVisualDescendants().OfType<TextBox>().First();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(to.X!.Value).IsGreaterThan(0);
                await Assert.That(vm.Session.Document!.Steps[1].Positions).IsEmpty();
                await Assert.That(row.GroupPlaceText).IsEqualTo(StratLocations.PointText(to.X!.Value, to.Y!.Value));
                await Assert.That(box.Text).IsEqualTo(row.GroupPlaceText).Because("the row's to field shows the drop");
            }

            window.Close();
        });
}

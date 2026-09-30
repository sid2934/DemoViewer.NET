#region

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The Who button's flyout in the real view: its toggles are staged and written as one entry on close.</summary>
[NotInParallel]
[Category("Integration")]
public class StratWhoFlyoutTests
{
    [Test]
    public async Task TheWhoFlyout_WritesItsTogglesWhenItCloses() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            StratStep step = new() { Id = Guid.NewGuid(), AtSeconds = 100, Actor = "B", Verb = "hold", To = new PlaceRef { Place = "BombsiteA" } };
            StratStepEditingTests.Seed(vm, step);
            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            int index = vm.Session.Document!.Steps.FindIndex(s => s.Id == step.Id);
            int depth = vm.Session.UndoDepth;

            Button who = view.FindControl<ItemsControl>("StepRows")!.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Name == "WhoButton");
            Flyout flyout = (Flyout)who.Flyout!;
            flyout.ShowAt(who);
            Dispatcher.UIThread.RunJobs();
            List<ToggleButton> toggles = [.. ((Control)flyout.Content!).GetVisualDescendants().OfType<ToggleButton>()];
            await Assert.That(toggles.Select(t => (string)t.Content!)).IsEquivalentTo(StratVocabulary.Slots);
            toggles.Single(t => (string?)t.Content == "C").IsChecked = true;
            toggles.Single(t => (string?)t.Content == "D").IsChecked = true;
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth).Because("nothing is written while the flyout is open");

            // A reprojection while it is open (the zones landing does one) leaves the staged picks alone.
            vm.Editor.Project();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(toggles.Single(t => (string?)t.Content == "C").IsChecked).IsTrue();
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(vm.Session.Document!.Steps[index].Assignments!.Select(l => l.Slot)).IsEquivalentTo(["B", "C", "D"]);
                await Assert.That(who.GetVisualDescendants().OfType<TextBlock>().First().Text).IsEqualTo("B, C, D");
            }

            window.Close();
        });
}

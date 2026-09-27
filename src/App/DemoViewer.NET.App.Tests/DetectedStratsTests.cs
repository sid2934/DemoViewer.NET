#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;
using Library = DemoViewer.NET.AppTests.StratMiningServiceTests.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strats section's Detected inbox over the synthetic library: the first look mines, the side filter
///     narrows, Add to book saves the strat and returns to the book with it open, a dismissal hides the row, and
///     the view renders the list and the detail.
/// </summary>
[NotInParallel]
public class DetectedStratsTests
{
    private static StratBookTabViewModel Tab(Library library, StratMiningService mining) =>
        new(library.Strats, null, null, false, mining: mining);

    [Test]
    public async Task TheInbox_MinesOnFirstLook_Filters_AndAddsToTheBook()
    {
        using Library library = Library.Create();
        using StratMiningService mining = library.Service();
        using StratBookTabViewModel vm = Tab(library, mining);

        await Assert.That(mining.MinedUtc).IsNull();
        vm.IsDetectedView = true;
        await Assert.That(mining.MinedUtc).IsNotNull().Because("the first look at the inbox searches");
        int all = vm.Detected.Rows.Count;

        vm.SelectedSide = StratVocabulary.SideCt;
        await Assert.That(vm.Detected.Rows.Count).IsLessThan(all);
        await Assert.That(vm.Detected.Rows.All(r => r.Summary.Contains(" · CT · "))).IsTrue();
        vm.SelectedSide = StratVocabulary.SideT;

        vm.Detected.SelectedRow = vm.Detected.Rows.First(r => r.Title.StartsWith("A execute", StringComparison.Ordinal));
        string header = vm.DetectedHeader;
        vm.Detected.AddToBookCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsDetectedView).IsFalse().Because("the new strat opens in the book");
            await Assert.That(vm.SelectedStrat?.Name).StartsWith("A execute");
            await Assert.That(library.Strats.Index.Single().Type).IsEqualTo("execute");
            await Assert.That(vm.DetectedHeader).IsNotEqualTo(header).Because("a pattern in the book is no longer new");
        }

        vm.IsDetectedView = true;
        DetectedRowViewModel first = vm.Detected.Rows.First(r => r.CanAdd);
        vm.Detected.SelectedRow = first;
        vm.Detected.DismissCommand.Execute(null);
        await Assert.That(vm.Detected.Rows.Select(r => r.Key)).DoesNotContain(first.Key);
        vm.Detected.ShowDismissed = true;
        await Assert.That(vm.Detected.Rows.Single(r => r.Key == first.Key).IsDismissed).IsTrue();
    }

    [Test]
    [Category("Integration")]
    public async Task TheView_RendersTheInbox_AndADetail() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using Library library = Library.Create();
            using StratMiningService mining = library.Service();
            using StratBookTabViewModel vm = Tab(library, mining);
            vm.IsDetectedView = true;
            vm.Detected.SelectedRow = vm.Detected.Rows.First(r => r.Title.StartsWith("A execute", StringComparison.Ordinal));
            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1400, Height = 800, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();

            ListBox list = view.FindControl<ListBox>("DetectedList")!;
            Control detail = view.FindControl<Control>("DetectedDetail")!;
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "strat-detected.png"), new PngBitmapEncoderOptions());
            using (Assert.Multiple())
            {
                await Assert.That(list.IsEffectivelyVisible).IsTrue();
                await Assert.That(list.ItemCount).IsGreaterThan(0);
                await Assert.That(detail.IsEffectivelyVisible).IsTrue();
            }

            window.Close();
        });
}

#region

using Avalonia.Controls;
using Avalonia.Headless;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.StratBook;
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
        Guid added = library.Strats.Index.Single().Id;
        await Assert.That(vm.Detected.Rows.Any(r => r.StratId == added)).IsFalse().Because("an added pattern leaves the default view");
        await Assert.That(vm.Detected.SettledLabel).IsEqualTo("Show settled (1)");
        vm.Detected.ShowSettled = true;
        await Assert.That(vm.Detected.Rows.Single(r => r.StratId == added).IsInBook).IsTrue();
        vm.Detected.ShowSettled = false;

        vm.IsDetectedView = true;
        DetectedRowViewModel first = vm.Detected.Rows.First(r => r.CanAdd);
        vm.Detected.SelectedRow = first;
        vm.Detected.DismissCommand.Execute(null);
        await Assert.That(vm.Detected.Rows.Select(r => r.Key)).DoesNotContain(first.Key);
        vm.Detected.ShowSettled = true;
        await Assert.That(vm.Detected.Rows.Single(r => r.Key == first.Key).IsDismissed).IsTrue();
    }

    [Test]
    public async Task ARefusedDismissalsFile_IsNamed_AndAddToBookSaysSo()
    {
        using Library library = Library.Create();
        Directory.CreateDirectory(library.Root);
        await File.WriteAllTextAsync(Path.Combine(library.Root, "strat-mining.json"), "{ not json");
        using StratMiningService mining = library.Service();
        using StratBookTabViewModel vm = Tab(library, mining);
        vm.IsDetectedView = true;

        vm.Detected.SelectedRow = vm.Detected.Rows.First(r => r.CanAdd);
        vm.Detected.AddToBookCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Detected.HasStateProblem).IsTrue();
            await Assert.That(vm.Detected.StateProblem).Contains("strat-mining.json");
            await Assert.That(vm.Detected.StateProblem).Contains("Move it aside");
            await Assert.That(vm.Detected.StatusLine).StartsWith("Not added: ");
            await Assert.That(vm.Detected.StatusLine).DoesNotContain("cached files");
        }

        vm.Detected.DismissCommand.Execute(null);
        await Assert.That(vm.Detected.StatusLine).StartsWith("Dismissed for this session only");
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

    [Test]
    [Category("Integration")]
    public async Task TheView_RendersTheSettledToggle_WithAnAddedAndADismissedPattern() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using Library library = Library.Create();
            using StratMiningService mining = library.Service();
            using StratBookTabViewModel vm = Tab(library, mining);
            SelectAExecute(vm);
            vm.Detected.AddToBookCommand.Execute(null);
            vm.IsDetectedView = true;
            vm.Detected.SelectedRow = vm.Detected.Rows.First();
            vm.Detected.DismissCommand.Execute(null);
            vm.Detected.ShowSettled = true;
            vm.Detected.SelectedRow = vm.Detected.Rows.First(r => r.IsInBook);
            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1400, Height = 800, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "strat-detected-settled.png"), new PngBitmapEncoderOptions());
            await Assert.That(vm.Detected.SettledLabel).IsEqualTo("Show settled (2)");
            window.Close();
        });

    private static DetectedRowViewModel SelectAExecute(StratBookTabViewModel vm)
    {
        vm.IsDetectedView = true;
        DetectedRowViewModel row = vm.Detected.Rows.First(r => r.Title.StartsWith("A execute", StringComparison.Ordinal));
        vm.Detected.SelectedRow = row;
        return row;
    }

    // Ids and stamps differ by construction between two builds; everything else must not.
    private static string Normalized(StratDocument doc) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(StratStore.Serialize(doc), "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<id>"),
                "\"\\d{4}-\\d{2}-\\d{2}T[^\"]*\"", "<utc>"),
            "\"revision\": \\d+", "\"revision\": 0");

    [Test]
    public async Task ThePreview_WritesNothing_AndIsWhatAddToBookSaves()
    {
        using Library library = Library.Create();
        using StratMiningService mining = library.Service();
        ReviewQueue review = new(null);
        using StratBookTabViewModel vm = new(library.Strats, null, null, false, review: review, tags: library.Tags, mining: mining);
        DetectedRowViewModel row = SelectAExecute(vm);

        int writes = 0;
        library.Strats.Changed += _ => writes++;
        library.Tags.Changed += _ => writes++;
        review.Changed += () => writes++;

        await vm.Detected.PreviewStratCommand.ExecuteAsync(null);
        StratPreviewViewModel preview = vm.Detected.Preview!;
        StratCanvasViewModel canvas = preview.Canvas!;
        int steps = preview.Document!.Steps.Count;

        // What a user can reach on the preview's canvas: keys and step edits are swallowed, not applied.
        await Assert.That(canvas.ExecuteAction(Playback2DAction.AddStep)).IsTrue();
        canvas.ExecuteAction(Playback2DAction.DeleteStep);
        canvas.ExecuteAction(Playback2DAction.Undo);
        canvas.PlaceTokensCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(preview.IsReady).IsTrue();
            await Assert.That(canvas.IsReadOnly).IsTrue();
            await Assert.That(canvas.HasDocument).IsTrue();
            await Assert.That(canvas.TokenEditor).IsNull();
            await Assert.That(preview.Steps.Count).IsEqualTo(steps).And.IsGreaterThan(0);
            await Assert.That(preview.Steps.Count(s => s.IsActive)).IsEqualTo(1);
            await Assert.That(canvas.Projection!.Path.Count).IsEqualTo(steps).Because("the canvas's edits do not apply");
            await Assert.That(writes).IsEqualTo(0);
            await Assert.That(library.Strats.Index).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(library.Root, "strat-mining.json"))).IsFalse();
            await Assert.That(vm.Detected.Rows.Single(r => r.Key == row.Key).CanAdd).IsTrue();
            await Assert.That(vm.Session.Document).IsNull().Because("the book's session is not the preview's");
        }

        string previewed = Normalized(preview.Document);
        List<Guid> previewedSteps = [.. preview.Document.Steps.Select(s => s.Id)];
        await vm.Detected.AddToBookCommand.ExecuteAsync(null);
        StratDocument saved = library.Strats.TryLoad(library.Strats.Index.Single().Id)!;
        using (Assert.Multiple())
        {
            await Assert.That(saved.Steps.Select(s => s.Id)).IsEquivalentTo(previewedSteps)
                .Because("Add to book saves the previewed document, not a rebuild");
            await Assert.That(saved.Id).IsNotEqualTo(preview.Document.Id);
            await Assert.That(Normalized(saved)).IsEqualTo(previewed);
            await Assert.That(vm.IsDetectedView).IsFalse();
            await Assert.That(vm.Detected.Preview).IsNull();
            await Assert.That(canvas.HasDocument).IsFalse().Because("leaving Detected disposes the preview");
            await Assert.That(vm.SelectedStrat?.Id).IsEqualTo(saved.Id);
        }
    }

    [Test]
    public async Task AddingAPreview_WhosePatternChanged_RefusesAndRebuilds()
    {
        using Library library = Library.Create();
        using StratMiningService mining = library.Service();
        using StratBookTabViewModel vm = Tab(library, mining);
        DetectedRowViewModel row = SelectAExecute(vm);
        await vm.Detected.PreviewStratCommand.ExecuteAsync(null);
        StratPreviewViewModel before = vm.Detected.Preview!;

        // A fifth A take joins the library and a re-mine grows the pattern under the same key.
        library.Cache.Upsert(StratMiningServiceTests.Record(5, BombSite.A));
        library.Positions.WritePositions("/d/m5.dem", StratMiningServiceTests.Positions(5, BombSite.A, 200));
        await mining.MineAsync();
        MinedPattern now = mining.Patterns.Single(p => p.Pattern.Key == row.Key).Pattern;
        await Assert.That(now.Support).IsGreaterThan(before.Pattern.Support);
        await Assert.That(vm.Detected.Preview).IsSameReferenceAs(before);

        await vm.Detected.AddToBookCommand.ExecuteAsync(null);
        using (Assert.Multiple())
        {
            await Assert.That(library.Strats.Index).IsEmpty();
            await Assert.That(vm.Detected.StatusLine).IsEqualTo("This pattern changed; review again.");
            await Assert.That(vm.Detected.Preview).IsNotSameReferenceAs(before);
            await Assert.That(vm.Detected.Preview?.Notice).IsEqualTo("This pattern changed; review again.");
            await Assert.That(vm.Detected.Preview?.Pattern.Support).IsEqualTo(now.Support);
            await Assert.That(vm.IsDetectedView).IsTrue();
        }

        // Reviewed again: the rebuilt preview is what gets saved.
        List<Guid> steps = [.. vm.Detected.Preview!.Document!.Steps.Select(s => s.Id)];
        await vm.Detected.AddToBookCommand.ExecuteAsync(null);
        await Assert.That(library.Strats.TryLoad(library.Strats.Index.Single().Id)!.Steps.Select(s => s.Id)).IsEquivalentTo(steps);
    }

    [Test]
    public async Task ThePreview_Loads_ThenSaysTheFilesAreGone()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        using Library library = Library.Create();
        using StratMiningService mining = library.Service(a =>
        {
            if (hold)
            {
                return release.Task.ContinueWith(_ => a(), TaskScheduler.Default);
            }

            a();
            return Task.CompletedTask;
        });
        using StratBookTabViewModel vm = Tab(library, mining);
        DetectedRowViewModel row = SelectAExecute(vm);
        hold = true;

        Task building = vm.Detected.PreviewStratCommand.ExecuteAsync(null);
        StratPreviewViewModel preview = vm.Detected.Preview!;
        using (Assert.Multiple())
        {
            await Assert.That(preview.IsLoading).IsTrue();
            await Assert.That(preview.Canvas).IsNull();
            await Assert.That(vm.Detected.ShowPatternDetail).IsFalse();
        }

        library.Positions.Delete(mining.Patterns.Single(p => p.Pattern.Key == row.Key).Pattern.Medoid.DemoPath);
        release.SetResult();
        await building;

        using (Assert.Multiple())
        {
            await Assert.That(preview.IsMissing).IsTrue();
            await Assert.That(preview.Canvas).IsNull();
            await Assert.That(library.Strats.Index).IsEmpty();
        }
    }

    [Test]
    public async Task ThePreview_SurvivesARefresh_AndIsDiscardedBySwitchingOrLeaving()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        using Library library = Library.Create();
        using StratMiningService mining = library.Service(a =>
        {
            if (hold)
            {
                return release.Task.ContinueWith(_ => a(), TaskScheduler.Default);
            }

            a();
            return Task.CompletedTask;
        });
        using StratBookTabViewModel vm = Tab(library, mining);
        SelectAExecute(vm);

        await vm.Detected.PreviewStratCommand.ExecuteAsync(null);
        StratPreviewViewModel first = vm.Detected.Preview!;
        vm.Detected.ShowSettled = true;
        await Assert.That(vm.Detected.Preview).IsSameReferenceAs(first).Because("a refresh reselects the same pattern");

        vm.Detected.SelectedRow = vm.Detected.Rows.First(r => r.Key != first.Key);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Detected.Preview).IsNull();
            await Assert.That(first.Canvas).IsNull();
            await Assert.That(vm.Detected.ShowPatternDetail).IsTrue();
        }

        // A build still running when the user moves on is never shown.
        SelectAExecute(vm);
        hold = true;
        Task building = vm.Detected.PreviewStratCommand.ExecuteAsync(null);
        StratPreviewViewModel stale = vm.Detected.Preview!;
        vm.Detected.BackToDetectedCommand.Execute(null);
        release.SetResult();
        await building;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Detected.Preview).IsNull();
            await Assert.That(stale.IsLoading).IsTrue();
        }

        hold = false;
        await vm.Detected.PreviewStratCommand.ExecuteAsync(null);
        await Assert.That(vm.Detected.Preview?.IsReady).IsTrue();
        vm.IsDetectedView = false;
        await Assert.That(vm.Detected.Preview).IsNull();
    }

    [Test]
    [Category("Integration")]
    public async Task TheView_RendersAPreview_Playing() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using Library library = Library.Create();
            using StratMiningService mining = library.Service();
            using StratBookTabViewModel vm = Tab(library, mining);
            SelectAExecute(vm);
            await vm.Detected.PreviewStratCommand.ExecuteAsync(null);
            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1600, Height = 900, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();

            StratPreviewViewModel preview = vm.Detected.Preview!;
            preview.Canvas!.NextStepCommand.Execute(null);
            preview.Canvas.NextStepCommand.Execute(null);
            Playback2DTimelineHarness.Pump();

            Control detail = view.FindControl<Control>("DetectedPreview")!;
            Control canvas = view.FindControl<Control>("DetectedPreviewCanvas")!;
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "strat-detected-preview.png"), new PngBitmapEncoderOptions());
            using (Assert.Multiple())
            {
                await Assert.That(detail.IsEffectivelyVisible).IsTrue();
                await Assert.That(canvas.IsEffectivelyVisible).IsTrue();
                await Assert.That(canvas.GetVisualDescendants().OfType<StratCanvasView>().Single().IsEffectivelyVisible).IsTrue();
                await Assert.That(detail.GetVisualDescendants().Any(c => c is TextBox or ComboBox)).IsFalse()
                    .Because("the preview reads as text, not fields");
                await Assert.That(preview.Steps.Single(s => s.IsActive).NumberText).IsNotEqualTo("1").Because("the canvas moved on and the list follows");
            }

            window.Close();
        });
}

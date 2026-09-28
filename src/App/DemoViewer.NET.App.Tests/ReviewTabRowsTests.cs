#region

using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.Views.Review;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Review tab's rows under background sends: ranged changes that leave a row being edited alone,
///     and filter picks that fall back when their value leaves the queue.
/// </summary>
public class ReviewTabRowsTests
{
    private static ReviewEntry Clip(string path, int from, string note = "", string source = ReviewSources.Situation) =>
        ReviewEntry.Clip(path, from, from + 64, note, source, 64);

    [Test]
    public async Task SyncTo_RaisesRangedChanges_AndResetsOnlyOnAReorder()
    {
        object a = new(), b = new(), c = new(), d = new(), e = new();
        BulkObservableCollection<object> rows = [];
        rows.ReplaceAll([a, b, c]);
        List<NotifyCollectionChangedEventArgs> events = [];
        rows.CollectionChanged += (_, args) => events.Add(args);

        rows.SyncTo([d, a, c, e]);
        using (Assert.Multiple())
        {
            await Assert.That(rows.ToList()).IsEquivalentTo([d, a, c, e]);
            await Assert.That(events.Select(x => x.Action)).IsEquivalentTo(
                [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add]);
        }

        events.Clear();
        rows.SyncTo([c, a]);
        await Assert.That(events.Select(x => x.Action).Last()).IsEqualTo(NotifyCollectionChangedAction.Reset);
    }

    [Test]
    public async Task ABackgroundSend_LeavesTheRowBeingEditedInPlace()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 100, "one"), Clip("/d/b.dem", 100, "two")], "Situations");
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);
        ReviewRowViewModel edited = tab.Rows[2];
        List<NotifyCollectionChangedEventArgs> events = [];
        tab.Rows.CollectionChanged += (_, args) => events.Add(args);

        // Two sections touched in one deferred send, as a producer's batch does.
        using (queue.Defer())
        {
            queue.Merge([Clip("/d/c.dem", 100, "three")], "Situations", static (_, _) => false);
            queue.Add([Clip("/d/d.dem", 100, "four", ReviewSources.Dossier)], "Dossier");
        }

        using (Assert.Multiple())
        {
            await Assert.That(events.Any(x => x.Action == NotifyCollectionChangedAction.Reset)).IsFalse();
            await Assert.That(events.Any(x => x.OldItems?.Contains(edited) == true)).IsFalse();
            await Assert.That(tab.Rows).Contains(edited);
            await Assert.That(tab.Rows.Count).IsEqualTo(6);
        }
    }

    [Test]
    public async Task APickedSource_FallsBackToAll_WhenItsLastClipGoes()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 100), Clip("/d/b.dem", 100, source: ReviewSources.Dossier)]);
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);
        tab.SelectedSource = ReviewSources.Dossier;

        queue.Remove([queue.Clips[1].Id]);

        using (Assert.Multiple())
        {
            await Assert.That(tab.SelectedSource).IsEqualTo(ReviewQueueTabViewModel.AllSources);
            await Assert.That(tab.Rows.Count).IsEqualTo(1);
        }
    }

    [Test]
    [NotInParallel]
    [Category("Integration")]
    public async Task AnUncommittedEdit_SurvivesABackgroundSend()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ReviewQueue queue = new(null);
            queue.Add([Clip("/d/a.dem", 100, "one"), Clip("/d/b.dem", 100, "two")], "Situations");
            using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);
            ReviewQueueTabView view = new() { DataContext = tab };
            Window window = new() { Width = 1000, Height = 700, Content = view };
            window.Show();
            Settle();

            TextBox note = view.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.DataContext is ReviewRowViewModel { IsClip: true } r && r.Note == "two" && t.Text == "two" && t.IsEffectivelyVisible);
            window.Activate();
            note.Focus();
            note.Text = "two, half typed";
            Settle();
            await Assert.That(note.IsFocused).IsTrue().Because("the precondition: the box is being typed in");

            using (queue.Defer())
            {
                queue.Merge([Clip("/d/c.dem", 100, "three")], "Situations", static (_, _) => false);
                queue.Add([Clip("/d/d.dem", 100, "four", ReviewSources.Dossier)], "Dossier");
            }

            Settle();
            TextBox? after = view.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Text == "two, half typed");
            using (Assert.Multiple())
            {
                await Assert.That(after).IsSameReferenceAs(note);
                await Assert.That(note.IsFocused).IsTrue();
                await Assert.That(queue.Clips[1].Note).IsEqualTo("two").Because("nothing was committed for the user");
            }

            window.Close();
        });
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

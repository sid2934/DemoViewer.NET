#region

using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Review;
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

    // ── Reviewed ──────────────────────────────────────────────────────────────

    [Test]
    public async Task AReviewedMark_PersistsOnlyWhenSet_AndIsOneChange()
    {
        string root = Directory.CreateTempSubdirectory("dv-review-reviewed-").FullName;
        try
        {
            ReviewQueue queue = new(root);
            queue.Add([Clip("/d/a.dem", 100), Clip("/d/b.dem", 100), Clip("/d/c.dem", 100)], "S");
            int changes = 0;
            queue.Changed += () => changes++;

            int marked = queue.SetReviewed([queue.Clips[0].Id, queue.Clips[1].Id, queue.Entries[0].Id], true);
            queue.Flush();
            string json = await File.ReadAllTextAsync(Path.Combine(root, ReviewQueue.FileName));
            ReviewQueue reread = new(root);

            using (Assert.Multiple())
            {
                await Assert.That(marked).IsEqualTo(2).Because("a title card has no mark");
                await Assert.That(changes).IsEqualTo(1);
                await Assert.That(json.Split("\"reviewed\": true").Length - 1).IsEqualTo(2);
                await Assert.That(json).DoesNotContain("\"reviewed\": false").Because("older files and builds see no new field");
                await Assert.That(reread.Clips.Select(c => c.Reviewed)).IsEquivalentTo([true, true, false]);
                await Assert.That(reread.UnreviewedCount).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ReviewedClips_HideByDefault_TheSectionCountsUnreviewed_AndTheBadgeFollows()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 100, "one"), Clip("/d/b.dem", 100, "two"), Clip("/d/c.dem", 100, "three")], "S");
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);
        WorkspaceTabDescriptor badge = new ReviewQueueModule(() => tab, () => queue).CreateTabs(null!).Single();
        badge.ViewModelFactory!.Invoke(); // activation: the badge subscribes from here on

        tab.Rows[1].ToggleReviewedCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(tab.Rows.Where(r => r.IsClip).Select(r => r.Note)).IsEquivalentTo(["two", "three"]);
            await Assert.That(tab.Rows[0].ClipCountText).IsEqualTo("2 of 3 clips unreviewed");
            await Assert.That(tab.Rows[1].Position).IsEqualTo(2).Because("positions count every clip");
            await Assert.That(badge.Badge).IsEqualTo("2");
        }

        tab.ShowReviewed = true;
        await Assert.That(tab.Rows.Count).IsEqualTo(4);
        await Assert.That(tab.Rows[1].ReviewText).IsEqualTo("Reviewed");

        tab.Rows[0].ToggleReviewedCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(queue.UnreviewedCount).IsEqualTo(0).Because("mark section reviewed marks every clip under the card");
            await Assert.That(tab.Rows[0].ClipCountText).IsEqualTo("all 3 clips reviewed");
            await Assert.That(tab.Rows[0].ReviewText).IsEqualTo("Mark unreviewed");
            await Assert.That(badge.Badge).IsNull();
        }

        tab.Rows[0].ToggleReviewedCommand.Execute(null);
        await Assert.That(queue.UnreviewedCount).IsEqualTo(3);
    }

    [Test]
    public async Task MarkSectionReviewed_UnderAFilter_MarksOnlyTheClipsItShows()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 100, "one", ReviewSources.Dossier), Clip("/d/b.dem", 100, "two", ReviewSources.Dossier),
            Clip("/d/c.dem", 100, "three")], "S");
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);
        tab.SelectedSource = ReviewSources.Dossier;
        await Assert.That(tab.Rows[0].ReviewText).IsEqualTo("Mark 2 shown reviewed");

        tab.Rows[0].ToggleReviewedCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(queue.Clips.Select(c => c.Reviewed)).IsEquivalentTo([true, true, false]);
            await Assert.That(tab.Rows.Count(r => r.IsClip)).IsEqualTo(0);
        }

        tab.ShowReviewed = true;
        await Assert.That(tab.Rows[0].ReviewText).IsEqualTo("Mark 2 unreviewed");
    }

    // ── Map and team ──────────────────────────────────────────────────────────

    [Test]
    public async Task TheMapAndTeamFilters_ComeFromTheQueue_AndOlderClipsSitUnderNoTeam()
    {
        Guid spirit = Guid.NewGuid(), deleted = Guid.NewGuid();
        ReviewQueue queue = new(null);
        queue.Add(
        [
            Clip("/d/nuke.dem", 100, "spirit nuke") with { TeamId = spirit },
            Clip("/d/mirage.dem", 100, "spirit mirage") with { TeamId = spirit },
            Clip("/d/nuke2.dem", 100, "old nuke"),
            Clip("/d/gone.dem", 100, "deleted team") with { TeamId = deleted },
            Clip("/d/unknown.dem", 100, "no demo")
        ], "S");
        int lookups = 0;
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false,
            mapOf: c =>
            {
                lookups++;
                return c.DemoPath.Contains("nuke", StringComparison.Ordinal) ? "de_nuke"
                    : c.DemoPath.Contains("mirage", StringComparison.Ordinal) ? "de_mirage" : null;
            },
            teamName: id => id == spirit ? "Spirit" : null);

        using (Assert.Multiple())
        {
            await Assert.That(tab.MapFilters).IsEquivalentTo([ReviewQueueTabViewModel.AllMaps, "de_mirage", "de_nuke", ReviewQueueTabViewModel.NoMap]);
            await Assert.That(tab.TeamFilters).IsEquivalentTo(
                [ReviewQueueTabViewModel.AllTeams, "Spirit", ReviewQueueTabViewModel.NoTeam, ReviewQueueTabViewModel.UnknownTeam]);
        }

        tab.SelectedMap = "de_nuke";
        await Assert.That(tab.Rows.Where(r => r.IsClip).Select(r => r.Note)).IsEquivalentTo(["spirit nuke", "old nuke"]);
        tab.SelectedTeam = "Spirit";
        await Assert.That(tab.Rows.Where(r => r.IsClip).Select(r => r.Note)).IsEquivalentTo(["spirit nuke"]);
        tab.SelectedMap = ReviewQueueTabViewModel.AllMaps;
        tab.SelectedTeam = ReviewQueueTabViewModel.NoTeam;
        await Assert.That(tab.Rows.Where(r => r.IsClip).Select(r => r.Note)).IsEquivalentTo(["old nuke", "no demo"]);
        await Assert.That(lookups).IsEqualTo(5).Because("one lookup per demo, not per clip per reconcile");

        tab.SelectedMap = "de_mirage";
        queue.Remove([queue.Clips[1].Id]);
        await Assert.That(tab.SelectedMap).IsEqualTo(ReviewQueueTabViewModel.AllMaps).Because("no clip is on that map any more");
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

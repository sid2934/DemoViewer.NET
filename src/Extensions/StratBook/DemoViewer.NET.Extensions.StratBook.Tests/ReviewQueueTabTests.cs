#region

using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The pack's Review tab over the core Review Queue: the tab's rows, in-place edits, the playhead pick,
///     the module's contributed section under its persisted ids, and the pack-off badge. Split out of
///     <c>DemoViewer.NET.AppTests.ReviewQueueTests</c>, which keeps the queue's own core-mechanism cases.
/// </summary>
public class ReviewQueueTabTests
{
    private static ReviewEntry Clip(string path, int from, int to, string note = "", string source = ReviewSources.Manual) =>
        ReviewEntry.Clip(path, from, to, note, source, 64);

    [Test]
    public async Task TheTab_ListsSectionsAndClips_EditsInPlace_OpensAndPicks()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 640, 1920, "a"), Clip("/d/b.dem", 64, 128, "b")], "Aces");
        ResultCardTests.RecordingPlayback seam = new();
        using ReviewQueueTabViewModel tab = new(queue, () => seam, isBrowser: false);

        using (Assert.Multiple())
        {
            await Assert.That(tab.Rows.Count).IsEqualTo(3);
            await Assert.That(tab.HeaderLine).IsEqualTo("2 clips, 2 unreviewed · 1 section · 2 demos");
            await Assert.That(tab.Rows[0].IsSection).IsTrue();
            await Assert.That(tab.Rows[0].ClipCountText).IsEqualTo("2 of 2 clips unreviewed");
            await Assert.That(tab.Rows[1].Position).IsEqualTo(1);
            await Assert.That(tab.Rows[1].DemoLabel).IsEqualTo("a.dem");
            await Assert.That(tab.Rows[1].RangeText).IsEqualTo("0:10 to 0:30 · 20 s");
        }

        // An edit writes through and the row is kept, so the box being typed in keeps its focus.
        ReviewRowViewModel row = tab.Rows[2];
        row.Question = "who traded? ";
        using (Assert.Multiple())
        {
            await Assert.That(queue.Clips[1].Question).IsEqualTo("who traded?");
            await Assert.That(tab.Rows[2]).IsSameReferenceAs(row);
            await Assert.That(row.Question).IsEqualTo("who traded? ").Because("the trailing space being typed is not eaten");
        }

        row.MoveUpCommand.Execute(null);
        await Assert.That(tab.Rows[1].Entry.Note).IsEqualTo("b");
        await Assert.That(tab.Rows[1].Position).IsEqualTo(1);

        await tab.Rows[2].OpenCommand.ExecuteAsync(null);
        await Assert.That(seam.Seeks).IsEquivalentTo([("/d/a.dem", 640)]);

        // A pick needs an open demo, then takes five seconds either side of its playhead.
        await Assert.That(tab.AddAtPlayheadCommand.CanExecute(null)).IsFalse();
        tab.OnActivated(new Playback2DFakeContext { DemoPath = "/d/c.dem", CurrentTick = 3200, TickRate = 64 });
        tab.AddAtPlayheadCommand.Execute(null);
        ReviewEntry pick = queue.Clips[^1];
        using (Assert.Multiple())
        {
            await Assert.That(pick.DemoPath).IsEqualTo("/d/c.dem");
            await Assert.That(pick.FromTick).IsEqualTo(3200 - 5 * 64);
            await Assert.That(pick.ToTick).IsEqualTo(3200 + 5 * 64);
            await Assert.That(pick.Source).IsEqualTo(ReviewSources.Manual);
            await Assert.That(pick.Note).IsEqualTo("picked at 0:50");
        }

        tab.ClearCommand.Execute(null);
        await Assert.That(tab.ShowClearConfirm).IsTrue();
        tab.ConfirmClearCommand.Execute(null);
        await Assert.That(tab.HasRows).IsFalse();
        await Assert.That(queue.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TheModule_ContributesTheReviewSection_UnderThePersistedIds_WithTheClipCountAsBadge()
    {
        ReviewQueue queue = new(null);
        ReviewQueueModule module = new(() => throw new InvalidOperationException("never built here"), () => true, queue);
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();
        await Assert.That(tab.Badge).IsNull();
        queue.Add([Clip("/d/a.dem", 1, 2)], "A section");

        FeatureDescriptor? feature = FeatureCatalog.ById("tab.review");
        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.review");
            await Assert.That(tab.TabId).IsEqualTo("review.queue");
            await Assert.That(tab.Header).IsEqualTo("Review");
            await Assert.That(tab.HostId).IsEqualTo(HostIds.StratBookHub);
            await Assert.That(tab.Order).IsEqualTo(4).Because("after Utility on the rail");
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
            await Assert.That(tab.Badge).IsEqualTo("1").Because("clips only; the title card is not counted");
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain("tab.review")
                .Because("the tab renders on the browser and keeps the queue for the session");
        }
    }

    [Test]
    public async Task WithThePackOff_TheReviewBadge_IsNeverShown_AndTheServiceIsIgnored()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 1, 2)], "A section");
        ReviewQueueModule module = new(() => throw new InvalidOperationException("never built here"), () => false, queue);
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        await Assert.That(tab.Badge).IsNull().Because("the initial read is skipped while the section is off");

        queue.Add([Clip("/d/b.dem", 1, 2)], "Another section");
        await Assert.That(tab.Badge).IsNull().Because("queue.Changed must not recompute it while the section is off");
    }
}

#region

using System.Text.Json;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Export;
using DemoViewer.NET.Extensions.Sdk.Review;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export.Pack;
using DemoViewer.NET.Services.Review;
using TUnit.Assertions.Enums;
using PipelineReview = DemoViewer.NET.Playback2D.Pipeline.Review;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The published review queue over the app's own: the memoized projection the tab's row reuse depends
///     on, the one-way translation of a send, the guarded change event, and the UI-thread refusal that
///     keeps a published mutation out of a worker.
/// </summary>
public class ExtensionReviewQueueTests
{
    private static PipelineReview.ReviewEntry Entry(string path, int from, string note = "") =>
        PipelineReview.ReviewEntry.Clip(path, from, from + 640, note, PipelineReview.ReviewSources.Situation, 64, "sha-" + note);

    [Test]
    public async Task TwoReads_HandBackTheSameClipInstances_SoARealizedRowIsKept()
    {
        ReviewQueue queue = new(null);
        queue.Add([Entry("/d/a.dem", 100, "one"), Entry("/d/b.dem", 200, "two")], "Situations");
        ExtensionReviewQueue published = new(queue);

        IReadOnlyList<ReviewClip> first = published.Entries;
        IReadOnlyList<ReviewClip> second = published.Entries;

        using (Assert.Multiple())
        {
            await Assert.That(first).IsNotSameReferenceAs(second).Because("each read is its own snapshot list");
            await Assert.That(first.Count).IsEqualTo(3);
            for (int i = 0; i < first.Count; i++)
            {
                await Assert.That(second[i]).IsSameReferenceAs(first[i]);
            }
        }
    }

    [Test]
    public async Task AColdFirstRead_DoesTheStoredQueueItself_SoATabDoesNotRenderEmpty()
    {
        string root = Directory.CreateTempSubdirectory("dv-review-cold-").FullName;
        try
        {
            ReviewQueue seed = new(null);
            seed.Add([Entry("/d/a.dem", 100, "one"), Entry("/d/b.dem", 200, "two")], "Situations");
            await File.WriteAllTextAsync(Path.Combine(root, ReviewQueue.FileName),
                JsonSerializer.Serialize(new PipelineReview.ReviewQueueFile { Entries = [.. seed.Entries] },
                    PipelineReview.ReviewQueueFile.JsonOptions));

            // The scheduled read never runs, which is the first-launch case: the queue is built before the
            // processing queue drains anything, and the tab asks for its rows in between.
            ReviewQueue queue = new(root, scheduleLoad: static _ => Task.CompletedTask);
            ExtensionReviewQueue published = new(queue);

            using (Assert.Multiple())
            {
                await Assert.That(published.Entries.Count).IsEqualTo(3);
                await Assert.That(published.ClipCount).IsEqualTo(2);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AnEdit_ReplacesOnlyTheRowItTouched_AndEveryOtherInstanceSurvives()
    {
        ReviewQueue queue = new(null);
        queue.Add([Entry("/d/a.dem", 100, "one"), Entry("/d/b.dem", 200, "two"), Entry("/d/c.dem", 300, "three")]);
        ExtensionReviewQueue published = new(queue);
        IReadOnlyList<ReviewClip> before = published.Entries;

        published.SetNote(before[1].Id, "edited");
        IReadOnlyList<ReviewClip> after = published.Entries;

        using (Assert.Multiple())
        {
            await Assert.That(after[0]).IsSameReferenceAs(before[0]);
            await Assert.That(after[2]).IsSameReferenceAs(before[2]);
            await Assert.That(after[1]).IsNotSameReferenceAs(before[1]);
            await Assert.That(after[1].Note).IsEqualTo("edited");
            await Assert.That(after[1].Id).IsEqualTo(before[1].Id).Because("an edit keeps the row's id");
        }
    }

    [Test]
    public async Task ASend_CarriesTheClipsFields_AndTheCountIsHowManyWereNew()
    {
        ReviewQueue queue = new(null);
        ExtensionReviewQueue published = new(queue);
        Guid team = Guid.NewGuid();
        ReviewClip clip = ReviewClip.Clip("/d/a.dem", 1920, 640, "a note", ReviewSources.Dossier, 64, "sha-a", team);

        int added = published.Add([clip], "Dossier", "1 round");
        int again = published.Add([clip]);

        PipelineReview.ReviewEntry queued = queue.Clips[0];
        using (Assert.Multiple())
        {
            await Assert.That(added).IsEqualTo(1);
            await Assert.That(again).IsEqualTo(0).Because("the same clip twice is one row, and the caller is told so");
            await Assert.That(queue.Entries[0].Kind).IsEqualTo(PipelineReview.ReviewEntryKind.Section);
            await Assert.That(queue.Entries[0].Title).IsEqualTo("Dossier");
            await Assert.That(queued.DemoPath).IsEqualTo("/d/a.dem");
            await Assert.That(queued.FromTick).IsEqualTo(640).Because("a reversed range is put the right way round");
            await Assert.That(queued.ToTick).IsEqualTo(1920);
            await Assert.That(queued.TickRate).IsEqualTo(64);
            await Assert.That(queued.Sha256).IsEqualTo("sha-a");
            await Assert.That(queued.Source).IsEqualTo(ReviewSources.Dossier);
            await Assert.That(queued.TeamId).IsEqualTo(team);
            await Assert.That(queued.Id).IsEqualTo(clip.Id);
        }
    }

    [Test]
    public async Task ReSendingRowsItReadBack_GivesThemFreshIds_SoEveryRowStaysAddressable()
    {
        ReviewQueue queue = new(null);
        queue.Add(
        [
            PipelineReview.ReviewEntry.Clip("/d/a.dem", 100, 740, "staged", PipelineReview.ReviewSources.Highlight, 64,
                "sha-a", new PipelineReview.ReviewHighlightRef("rs", "hl", 100, 3)),
            Entry("/d/b.dem", 200, "two")
        ], "Situations");
        ExtensionReviewQueue published = new(queue);

        // Neither of these is caught by the queue's own dedup: a section card is not a clip, and the staged
        // clip's highlight ref is not on the projection, so the two do not compare equal.
        int added = published.Add([published.Entries[0], published.Entries[1]]);

        List<Guid> ids = [.. queue.Entries.Select(e => e.Id)];
        using (Assert.Multiple())
        {
            await Assert.That(added).IsEqualTo(1).Because("one of the two re-sent rows is a title card");
            await Assert.That(queue.Entries.Count).IsEqualTo(5);
            await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count)
                .Because("a duplicate id makes every mutation reach the first row and removal take both");
        }
    }

    [Test]
    public async Task MoveBy_IsASignedDelta_SoMinusOneIsOnePlaceUp()
    {
        ReviewQueue queue = new(null);
        queue.Add([Entry("/d/a.dem", 100, "one"), Entry("/d/b.dem", 200, "two"), Entry("/d/c.dem", 300, "three")]);
        ExtensionReviewQueue published = new(queue);

        published.MoveBy(published.Entries[2].Id, -1);

        await Assert.That(published.Entries.Select(c => c.Note))
            .IsEquivalentTo(["one", "three", "two"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AThrowingChangeHandler_DoesNotReachWhateverMadeTheChange()
    {
        ReviewQueue queue = new(null);
        ExtensionReviewQueue published = new(queue, ExtensionGuard.Standalone(new HostLibraryTests.LibraryTestExtension()));
        int seen = 0;
        published.Changed += () => throw new InvalidOperationException("the extension's own bug");
        published.Changed += () => seen++;

        queue.Add([Entry("/d/a.dem", 100, "one")]);

        using (Assert.Multiple())
        {
            await Assert.That(seen).IsEqualTo(1).Because("a throwing handler must not skip the ones after it");
            await Assert.That(published.ClipCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task OffTheUiThread_EveryMemberIsRefused_IncludingARead()
    {
        ReviewQueue queue = new(null);
        ExtensionReviewQueue published = new(queue, onUiThread: static () => false);

        using (Assert.Multiple())
        {
            await Assert.That(() => published.Entries).Throws<InvalidOperationException>();
            await Assert.That(() => published.ClipCount).Throws<InvalidOperationException>();
            await Assert.That(() => published.UnreviewedCount).Throws<InvalidOperationException>();
            await Assert.That(() => published.FileProblem).Throws<InvalidOperationException>();
            await Assert.That(() => published.Add([])).Throws<InvalidOperationException>();
            await Assert.That(() => published.AddSection("x")).Throws<InvalidOperationException>();
            await Assert.That(() => published.Remove([])).Throws<InvalidOperationException>();
            await Assert.That(() => published.MoveBy(Guid.Empty, -1)).Throws<InvalidOperationException>();
            await Assert.That(() => published.Clear()).Throws<InvalidOperationException>();
            await Assert.That(() => published.SetReviewed([], true)).Throws<InvalidOperationException>();
            await Assert.That(() => published.ClipsUnder(Guid.Empty)).Throws<InvalidOperationException>();
            await Assert.That(() => published.SetTitle(Guid.Empty, "x")).Throws<InvalidOperationException>();
            await Assert.That(() => published.SetNote(Guid.Empty, "x")).Throws<InvalidOperationException>();
            await Assert.That(() => published.SetQuestion(Guid.Empty, "x")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task ThePackExport_PlansTheQueue_AndTheSummaryNumbersAreWhatTheStripShows()
    {
        ReviewQueue queue = new(null);
        queue.Add([Entry("/d/a.dem", 100, "one"), Entry("/d/b.dem", 200, "two"), Entry("/d/gone.dem", 300, "three")],
            "Situations");
        PackPlan? ran = null;
        ExtensionReviewPackExport export = new(
            (plan, _, _) => { ran = plan; return Task.FromResult(new PackResult([], 2, [])); },
            queue, "/videos", path => !path.EndsWith("gone.dem", StringComparison.Ordinal));

        ReviewPackPlan planned = export.Plan("/videos/pack.mp4", ExportFormats.Mp4);
        ReviewPackResult result = await export.RunAsync(planned, new Progress<ReviewPackProgress>(), default);

        using (Assert.Multiple())
        {
            await Assert.That(export.Formats[0]).IsEqualTo(ExportFormats.Mp4).Because("the format a pack opens on");
            await Assert.That(export.DefaultOutputDirectory).IsEqualTo("/videos");
            await Assert.That(planned.ClipCount).IsEqualTo(2);
            await Assert.That(planned.DemoCount).IsEqualTo(2);
            await Assert.That(planned.TitleCardCount).IsEqualTo(1);
            await Assert.That(planned.SkippedCount).IsEqualTo(1);
            await Assert.That(planned.SkipReasons).IsEquivalentTo(["demo not found"], CollectionOrdering.Matching);
            await Assert.That(planned.OutputDescription).IsEqualTo("/videos/pack.mp4");
            await Assert.That(planned.Seconds).IsGreaterThan(0);
            await Assert.That(ran).IsSameReferenceAs(planned.Native).Because("the opaque token carried the host's own plan");
            await Assert.That(result.ClipsRendered).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ThePackExport_RefusesAPlanItDidNotMake_AndARunOffTheUiThread()
    {
        ReviewQueue queue = new(null);
        ExtensionReviewPackExport onWorker = new(static (_, _, _) => Task.FromResult(new PackResult([], 0, [])),
            queue, onUiThread: static () => false);
        ExtensionReviewPackExport onUi = new(static (_, _, _) => Task.FromResult(new PackResult([], 0, [])), queue);

        using (Assert.Multiple())
        {
            await Assert.That(() => onWorker.Plan("/videos/pack.mp4", ExportFormats.Mp4)).Throws<InvalidOperationException>();
            await Assert.That(async () => await onWorker.RunAsync(new ReviewPackPlan(), new Progress<ReviewPackProgress>(), default))
                .Throws<InvalidOperationException>();
            await Assert.That(async () => await onUi.RunAsync(new ReviewPackPlan(), new Progress<ReviewPackProgress>(), default))
                .Throws<ArgumentException>().Because("the plan carries no host plan to render");
            await Assert.That(onUi.Validate("", ExportFormats.Mp4)).IsNotNull();
            await Assert.That(onUi.Validate("/videos/pack.mp4", ExportFormats.Mp4)).IsNull();
        }
    }
}

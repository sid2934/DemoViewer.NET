#region

using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Parser;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Extensions.StratBook.Modules.Review;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Review;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Review Queue: ordering, title cards and the question per clip; the file with its clock block,
///     refused rather than overwritten; a tag ref as a clip; the Reels tray as a client of the queue
///     (its staging, removal, reorder and clear are queue mutations, and clips other surfaces queued
///     survive all four); and Result Cards sending a set. The pack's Review tab over this queue is
///     <c>ReviewQueueTabTests</c>, in the extension test project.
/// </summary>
public class ReviewQueueTests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-review-{Guid.NewGuid():N}");

    private static ReviewEntry Clip(string path, int from, int to, string note = "", string source = ReviewSources.Manual) =>
        ReviewEntry.Clip(path, from, to, note, source, 64);

    // ── The queue ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Add_OpensASection_SkipsWhatIsQueued_AndKeepsTheOrder()
    {
        ReviewQueue queue = new(null);
        int changes = 0;
        queue.Changed += () => changes++;

        int added = queue.Add([Clip("/d/a.dem", 100, 200, "one"), Clip("/d/b.dem", 300, 400, "two")], "Mirage A", "2 rounds");
        int again = queue.Add([Clip("/D/A.dem", 100, 200, "one, again")], "Mirage A");
        int partly = queue.Add([Clip("/d/a.dem", 100, 200), Clip("/d/c.dem", 50, 10)]);

        IReadOnlyList<ReviewEntry> entries = queue.Entries;
        using (Assert.Multiple())
        {
            await Assert.That(added).IsEqualTo(2);
            await Assert.That(again).IsEqualTo(0).Because("the same demo and range is the same clip, whatever the path casing");
            await Assert.That(partly).IsEqualTo(1);
            await Assert.That(changes).IsEqualTo(2).Because("a send that adds nothing changes nothing");
            await Assert.That(entries.Count).IsEqualTo(4).Because("no title card is left behind by a send that added no clip");
            await Assert.That(entries[0].Kind).IsEqualTo(ReviewEntryKind.Section);
            await Assert.That(entries[0].Title).IsEqualTo("Mirage A");
            await Assert.That(entries[0].Note).IsEqualTo("2 rounds");
            await Assert.That(entries[1].Note).IsEqualTo("one");
            await Assert.That(entries[3].FromTick).IsEqualTo(10).Because("a reversed range is put the right way round");
            await Assert.That(entries[3].ToTick).IsEqualTo(50);
            await Assert.That(queue.ClipCount).IsEqualTo(3);
            await Assert.That(queue.SectionOf(entries[3].Id)).IsEqualTo("Mirage A");
        }
    }

    [Test]
    public async Task Moves_Edits_AndSubsetReorders_TouchOnlyWhatTheyName()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 1, 2, "a"), Clip("/d/b.dem", 1, 2, "b"), Clip("/d/c.dem", 1, 2, "c"), Clip("/d/d.dem", 1, 2, "d")]);
        Guid[] ids = [.. queue.Entries.Select(e => e.Id)];

        queue.Move(ids[0], +1);
        await Assert.That(queue.Entries.Select(e => e.Note)).IsEquivalentTo(["b", "a", "c", "d"]);
        queue.MoveTo(ids[3], -5);
        await Assert.That(queue.Entries.Select(e => e.Note)).IsEquivalentTo(["d", "b", "a", "c"]).Because("clamped to the top");

        // The subset a and c swap between the two slots they hold; b and d stay put.
        queue.ReorderSubset([ids[2], ids[0]]);
        await Assert.That(queue.Entries.Select(e => e.Note)).IsEquivalentTo(["d", "b", "c", "a"]);

        ReviewEntry card = queue.AddSection("Retakes", 2);
        queue.SetQuestion(ids[1], "  why did\nB fall?  ");
        queue.SetTitle(card.Id, "B retakes");
        using (Assert.Multiple())
        {
            await Assert.That(queue.Find(ids[1])!.Question).IsEqualTo("why did B fall?").Because("one line, trimmed");
            await Assert.That(queue.Entries[2].Title).IsEqualTo("B retakes");
            await Assert.That(queue.SectionOf(ids[0])).IsEqualTo("B retakes");
            await Assert.That(queue.SectionOf(ids[3])).IsNull().Because("above every title card");
            await Assert.That(queue.Remove([ids[3], Guid.NewGuid()])).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheFile_RoundTrips_WithAFrameClockBlock_AndANewerSchemaIsRefusedNotOverwritten()
    {
        string root = TempRoot();
        try
        {
            ReviewQueue queue = new(root);
            queue.Add([ReviewEntry.Clip("/d/a.dem", 640, 1280, "a", ReviewSources.Highlight, 64, "abc",
                new ReviewHighlightRef("r", "ace", 1000, 3))], "Aces");
            queue.SetQuestion(queue.Clips[0].Id, "what did he see first?");
            queue.Flush();

            string path = Path.Combine(root, ReviewQueue.FileName);
            using (JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                await Assert.That(json.RootElement.GetProperty("clock").GetProperty("kind").GetString())
                    .IsEqualTo(ClockIdentity.DvFrameClock).Because("every tick in the file is on the frame clock");
                await Assert.That(json.RootElement.GetProperty("entries")[1].GetProperty("kind").GetString()).IsEqualTo("clip");
            }

            ReviewQueue reread = new(root);
            ReviewEntry clip = reread.Clips.Single();
            using (Assert.Multiple())
            {
                await Assert.That(reread.IsSessionOnly).IsFalse();
                await Assert.That(reread.Entries.Count).IsEqualTo(2);
                await Assert.That(reread.Entries[0].Title).IsEqualTo("Aces");
                await Assert.That(clip).IsEqualTo(queue.Clips[0] with { Extra = clip.Extra });
                await Assert.That(clip.Highlight).IsEqualTo(new ReviewHighlightRef("r", "ace", 1000, 3));
                await Assert.That(clip.Seconds).IsEqualTo(10d);
            }

            string newer = """{ "schemaVersion": 99, "entries": [] }""";
            await File.WriteAllTextAsync(path, newer);
            ReviewQueue refused = new(root);
            refused.Add([Clip("/d/b.dem", 1, 2)]);
            using (Assert.Multiple())
            {
                await Assert.That(refused.FileProblem).Contains("newer than this build reads");
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(newer).Because("a refused file is never overwritten");
                await Assert.That(refused.ClipCount).IsEqualTo(1).Because("the session still works");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task ATagRef_BecomesAClip_ThroughTheCacheIndex_OrNothingForAnUnknownHash()
    {
        TagInstanceRef instance = new("abc", Guid.NewGuid(), "exec-a", 2000, 2640, 7);
        ReviewEntry? clip = TagClips.FromTag(instance, sha => sha == "abc" ? "/d/a.dem" : null, 64);
        ReviewEntry? missing = TagClips.FromTag(instance with { Sha256 = "zzz" }, sha => sha == "abc" ? "/d/a.dem" : null);

        using (Assert.Multiple())
        {
            await Assert.That(clip).IsNotNull();
            await Assert.That(clip!.DemoPath).IsEqualTo("/d/a.dem");
            await Assert.That(clip.FromTick).IsEqualTo(2000);
            await Assert.That(clip.ToTick).IsEqualTo(2640);
            await Assert.That(clip.Sha256).IsEqualTo("abc");
            await Assert.That(clip.Note).IsEqualTo("exec-a · round 7");
            await Assert.That(clip.Source).IsEqualTo(ReviewSources.Tag);
            await Assert.That(missing).IsNull().Because("a demo not in the library is not queued as a dead link");
        }
    }

    // ── The Reels tray as a client ────────────────────────────────────────────

    private static DemoCacheRecord HighlightRow(string path, string steam, params (string Id, int Tick)[] highlights) => new()
    {
        Path = path,
        Map = "de_nuke",
        TickRate = 64,
        TickCount = 120_000,
        ModifiedTicks = 1,
        Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
        AnalysisState = DemoAnalysisState.Indexed,
        ConfigFingerprint = "fp@64",
        Sha256 = "sha-" + Path.GetFileName(path),
        Players = [new CachedPlayerInfo { Name = "p" + steam, SteamId64 = steam, Team = 2, Slot = 0 }],
        Rounds = [new CachedRound { Number = 1, StartTickFrameClock = 1000 }],
        Highlights =
        [
            .. highlights.Select(h => new CachedHighlightEvent
            {
                RulesetId = "r",
                HighlightId = h.Id,
                PlayerSlot = 0,
                RoundNumber = 1,
                Tick = h.Tick,
                RenderedTitle = $"{h.Id} at {h.Tick}"
            })
        ]
    };

    private static (DemoCacheStore Store, HighlightsTabViewModel Vm) Tray(ReviewQueue queue, params DemoCacheRecord[] rows)
    {
        DemoCacheStore store = new(null);
        foreach (DemoCacheRecord row in rows)
        {
            store.Upsert(row);
        }

        HighlightScanService scanner = new(store, new NoHarvester(), () => [.. rows.Select(r => r.Path)], () => false);
        HighlightsTabViewModel vm = new(store, scanner, isLiveSyncSessionActive: () => false, fileExists: _ => true,
            reviewQueue: queue);
        vm.ReelConfig.OutputFolder = "/out";
        return (store, vm);
    }

    [Test]
    public async Task Staging_IsAQueueClip_WithTheHighlightsIdentity_AndTheReelsWindow()
    {
        ReviewQueue queue = new(null);
        DemoCacheRecord a = HighlightRow("/d/a.dem", "1", ("ace", 5000));
        (_, HighlightsTabViewModel vm) = Tray(queue, a);

        vm.Stage(a, a.Highlights[0]);

        ReviewEntry clip = queue.Clips.Single();
        using (Assert.Multiple())
        {
            await Assert.That(vm.StagedCount).IsEqualTo(1);
            await Assert.That(clip.Source).IsEqualTo(ReviewSources.Highlight);
            await Assert.That(clip.Highlight).IsEqualTo(new ReviewHighlightRef("r", "ace", 5000, 0));
            await Assert.That(clip.DemoPath).IsEqualTo("/d/a.dem");
            await Assert.That(clip.Sha256).IsEqualTo("sha-a.dem");
            await Assert.That(clip.Note).IsEqualTo("ace at 5000");
            // The default padding: fifteen seconds in, floored at the round start, five out.
            await Assert.That(clip.FromTick).IsEqualTo(Math.Max(1000, 5000 - 15 * 64));
            await Assert.That(clip.ToTick).IsEqualTo(5000 + 5 * 64);
        }

        // Removing the clip in the Review tab un-stages it in the tray.
        queue.Remove([clip.Id]);
        await Assert.That(vm.StagedCount).IsEqualTo(0);
        await Assert.That(vm.IsStaged(new HighlightKey("/d/a.dem", "r", "ace", 5000, 0))).IsFalse();
    }

    [Test]
    public async Task TrayMutations_LeaveOtherSurfacesClipsWhereTheyAre()
    {
        ReviewQueue queue = new(null);
        DemoCacheRecord a = HighlightRow("/d/a.dem", "1", ("ace", 5000));
        DemoCacheRecord b = HighlightRow("/d/b.dem", "2", ("clutch", 9000));
        (_, HighlightsTabViewModel vm) = Tray(queue, a, b);

        vm.Stage(a, a.Highlights[0]);
        queue.Add([Clip("/d/x.dem", 1, 2, "from a search", ReviewSources.Situation)]);
        vm.Stage(b, b.Highlights[0]);
        await Assert.That(queue.Clips.Select(c => c.Note)).IsEquivalentTo(["ace at 5000", "from a search", "clutch at 9000"]);

        // The tray moves b's group to the top: the search clip keeps its slot between them.
        vm.MoveGroup(ClipTrayKeys.Group("/d/b.dem", "2"), -1);
        using (Assert.Multiple())
        {
            await Assert.That(vm.StagedSelections.Select(s => s.Record.Path)).IsEquivalentTo(["/d/b.dem", "/d/a.dem"]);
            await Assert.That(queue.Clips.Select(c => c.Note)).IsEquivalentTo(["clutch at 9000", "from a search", "ace at 5000"]);
        }

        // A move in the Review tab is a tray reorder too.
        queue.MoveTo(queue.Clips[2].Id, 0);
        await Assert.That(vm.StagedSelections.Select(s => s.Record.Path)).IsEquivalentTo(["/d/a.dem", "/d/b.dem"]);

        // Clear tray empties the tray and nothing else.
        vm.ClearTrayCommand.Execute(null);
        vm.ConfirmClearTrayCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.StagedCount).IsEqualTo(0);
            await Assert.That(queue.Clips.Select(c => c.Note)).IsEquivalentTo(["from a search"]);
        }
    }

    [Test]
    public async Task ATrayBuiltOverAQueueThatAlreadyHoldsItsClips_ShowsThem_AndIgnoresTheSessionsList()
    {
        string root = TempRoot();
        try
        {
            DemoCacheRecord a = HighlightRow("/d/a.dem", "1", ("ace", 5000));
            DemoCacheRecord b = HighlightRow("/d/b.dem", "2", ("clutch", 9000));
            ReviewQueue first = new(root);
            (_, HighlightsTabViewModel before) = Tray(first, a, b);
            before.Stage(b, b.Highlights[0]);
            before.Stage(a, a.Highlights[0]);
            object? session = before.SnapshotState();
            first.Flush();

            // The restart: a fresh queue over the same file, a fresh tray over it.
            ReviewQueue second = new(root);
            (_, HighlightsTabViewModel after) = Tray(second, a, b);
            await Assert.That(after.StagedSelections.Select(s => s.Record.Path)).IsEquivalentTo(["/d/b.dem", "/d/a.dem"]);

            // The session's list is older than the queue: with the tray's clips already queued it is not replayed.
            second.MoveTo(second.Clips[1].Id, 0);
            after.RestoreState(session);
            await Assert.That(after.StagedSelections.Select(s => s.Record.Path)).IsEquivalentTo(["/d/a.dem", "/d/b.dem"]);
            await Assert.That(second.ClipCount).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task AQueuedHighlightTheCacheLost_LeavesTheTrayAndTheQueue()
    {
        ReviewQueue queue = new(null);
        DemoCacheRecord a = HighlightRow("/d/a.dem", "1", ("ace", 5000));
        DemoCacheRecord b = HighlightRow("/d/b.dem", "2", ("clutch", 9000));
        (DemoCacheStore store, HighlightsTabViewModel vm) = Tray(queue, a, b);
        vm.Stage(a, a.Highlights[0]);
        vm.Stage(b, b.Highlights[0]);
        queue.Add([Clip("/d/b.dem", 1, 2, "a manual pick in the same demo")]);

        store.RemoveWhere(r => r.Path == "/d/b.dem");

        using (Assert.Multiple())
        {
            await Assert.That(vm.StagedCount).IsEqualTo(1);
            await Assert.That(vm.StatusMessage).Contains("no longer in the highlights cache");
            await Assert.That(queue.Clips.Select(c => c.Note)).IsEquivalentTo(["ace at 5000", "a manual pick in the same demo"])
                .Because("only the tray's own clip rests on the cache");
        }
    }

    // ── Result Cards ──────────────────────────────────────────────────────────

    [Test]
    public async Task ResultCards_SendTheSet_UnderOneTitleCard_OnceOnly()
    {
        DemoCacheStore cache = new(null);
        RoundIndexStore sidecars = new(cache.Data());
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        ReviewQueue queue = new(null);
        ResultCardsViewModel cards = new(cache.Library(), sidecars, sources, () => null, new SituationThumbnailCache(),
            () => new SituationThumbnailRenderer(_ => null), action => action(), _ => null, review: queue);
        cache.Upsert(ParsedRecord("/d/a.dem"));
        Guid opponent = Guid.NewGuid();
        cards.SearchTeam = () => opponent;

        cards.Load(
        [
            new SituationHit("/d/a.dem", DemoCacheStore.StableKey("/d/a.dem"), "sha-a", "de_nuke", 4, 1000, 4008, 4200, 3),
            new SituationHit("/d/b.dem", DemoCacheStore.StableKey("/d/b.dem"), null, "de_nuke", 9, 20000, 20640, 20640, 1)
        ]);
        await cards.BatchTask;

        await Assert.That(cards.CanSendToReview).IsTrue();
        await Assert.That(cards.SendToReviewLabel).IsEqualTo("Send 2 to Review");
        cards.SendToReviewCommand.Execute(null);

        IReadOnlyList<ReviewEntry> entries = queue.Entries;
        using (Assert.Multiple())
        {
            await Assert.That(entries.Count).IsEqualTo(3);
            await Assert.That(entries[0].Title).IsEqualTo("Situations · de_nuke");
            await Assert.That(entries[0].Note).IsEqualTo("2 rounds");
            await Assert.That(entries[1].DemoPath).IsEqualTo("/d/a.dem");
            await Assert.That(entries[1].FromTick).IsEqualTo(cards.Cards[0].SeekTick).Because("the queue opens where the card does");
            await Assert.That(entries[1].ToTick).IsEqualTo(4200 + ResultCardsViewModel.ReviewTailSeconds * 64);
            await Assert.That(entries[1].Sha256).IsEqualTo("sha-a");
            await Assert.That(entries[1].Note).IsEqualTo("a · Round 4");
            await Assert.That(entries[2].Source).IsEqualTo(ReviewSources.Situation);
            await Assert.That(entries.Skip(1).All(e => e.TeamId == opponent)).IsTrue().Because("the search's opponent at load");
            await Assert.That(cards.ReviewLine).IsEqualTo("2 rounds sent to Review");
        }

        cards.SendToReviewCommand.Execute(null);
        await Assert.That(queue.Entries.Count).IsEqualTo(3);
        await Assert.That(cards.ReviewLine).IsEqualTo("already in Review");

        ResultCardsViewModel without = new(cache.Library(), sidecars, sources, () => null);
        await Assert.That(without.HasReview).IsFalse().Because("no queue on the host hides the action");
    }

    // ── Thousands of rows ─────────────────────────────────────────────────────

    [Test]
    public async Task Defer_RaisesOneChange_AndTheSaveWaitsForFlush()
    {
        string root = TempRoot();
        try
        {
            ReviewQueue queue = new(root, TimeSpan.FromHours(1));
            int changes = 0;
            queue.Changed += () => changes++;
            using (queue.Defer())
            {
                queue.Add([Clip("/d/a.dem", 1, 2)], "A");
                queue.Merge([Clip("/d/b.dem", 1, 2)], "B", static (_, _) => false);
                await Assert.That(changes).IsEqualTo(0);
            }

            string path = Path.Combine(root, ReviewQueue.FileName);
            using (Assert.Multiple())
            {
                await Assert.That(changes).IsEqualTo(1).Because("a deferred batch is one change");
                await Assert.That(File.Exists(path)).IsFalse().Because("the write waits out its delay off the UI thread");
            }

            queue.Flush();
            ReviewQueue reread = new(root);
            await reread.Loaded;
            await Assert.That(reread.Clips.Select(c => c.DemoPath)).IsEquivalentTo(["/d/a.dem", "/d/b.dem"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task MergeByKey_ReplacesTheKeyedClipInPlace_AndAppendsTheRest()
    {
        ReviewQueue queue = new(null);
        Guid lineup = Guid.NewGuid();
        queue.Merge([Clip("/d/a.dem", 1, 2, source: ReviewSources.Lineup) with { LineupId = lineup }], "Lineups",
            static (_, _) => false);
        Guid id = queue.Clips[0].Id;
        queue.SetQuestion(id, "why here?");

        int count = queue.MergeByKey(
            [
                Clip("/d/a.dem", 5, 9, "moved", ReviewSources.Lineup) with { LineupId = lineup },
                Clip("/d/c.dem", 1, 2, source: ReviewSources.Lineup) with { LineupId = Guid.NewGuid() }
            ], "Lineups", static e => e.LineupId);

        using (Assert.Multiple())
        {
            await Assert.That(count).IsEqualTo(2);
            await Assert.That(queue.Clips[0].Id).IsEqualTo(id);
            await Assert.That(queue.Clips[0].FromTick).IsEqualTo(5);
            await Assert.That(queue.Clips[0].Question).IsEqualTo("why here?");
            await Assert.That(queue.Clips[1].DemoPath).IsEqualTo("/d/c.dem");
            await Assert.That(queue.Entries.Count(e => e.Kind == ReviewEntryKind.Section)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ABigSectionStartsCollapsed_AndTheFilterAndSearchNarrowTheRows()
    {
        ReviewQueue queue = new(null);
        queue.Add(Enumerable.Range(0, ReviewQueueTabViewModel.CollapseAbove + 1)
            .Select(i => Clip($"/d/{i}.dem", i, i + 1, $"smoke {i}", ReviewSources.Lineup)), "Lineup clips, de_mirage");
        queue.Add([Clip("/d/x.dem", 1, 2, "a plant", ReviewSources.Dossier)], "Dossier");
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);

        ReviewRowViewModel card = tab.Rows[0];
        using (Assert.Multiple())
        {
            await Assert.That(tab.Rows.Count).IsEqualTo(3).Because("the big section shows only its card");
            await Assert.That(card.IsCollapsed).IsTrue();
            await Assert.That(tab.Rows[2].Position).IsEqualTo(ReviewQueueTabViewModel.CollapseAbove + 2)
                .Because("positions count every clip, shown or not");
            await Assert.That(tab.SourceFilters).IsEquivalentTo([ReviewQueueTabViewModel.AllSources, "dossier", "lineup"]);
        }

        card.ToggleCommand.Execute(null);
        await Assert.That(tab.Rows.Count).IsEqualTo(ReviewQueueTabViewModel.CollapseAbove + 4);

        tab.SelectedSource = ReviewSources.Dossier;
        await Assert.That(tab.Rows.Select(r => r.IsSection ? r.Entry.Title : r.Entry.Note)).IsEquivalentTo(["Dossier", "a plant"]);

        tab.SelectedSource = ReviewQueueTabViewModel.AllSources;
        card.ToggleCommand.Execute(null);
        tab.SearchText = "smoke 7";
        using (Assert.Multiple())
        {
            await Assert.That(tab.Rows.Where(r => r.IsClip).Select(r => r.Entry.Note)).IsEquivalentTo(["smoke 7"])
                .Because("a search opens the collapsed section it finds a clip in");
            await Assert.That(tab.Rows[0].ClipCountText).IsEqualTo("1 of 51 clips match");
        }
    }

    [Test]
    public async Task AnEditInPlace_KeepsTheRowInstance()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 1, 2), Clip("/d/b.dem", 1, 2)], "A");
        using ReviewQueueTabViewModel tab = new(queue, isBrowser: false);
        ReviewRowViewModel row = tab.Rows[1];
        int resets = 0;
        tab.Rows.CollectionChanged += (_, _) => resets++;

        queue.SetNote(row.Entry.Id, "edited");

        using (Assert.Multiple())
        {
            await Assert.That(tab.Rows[1]).IsSameReferenceAs(row);
            await Assert.That(row.Note).IsEqualTo("edited");
            await Assert.That(resets).IsEqualTo(0).Because("the text box being typed in keeps its container");
        }
    }

    private sealed class NoHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate) =>
            ($"fp@{tickRate}", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) => throw new NotSupportedException();

        public void InvalidateRules()
        {
        }
    }
}

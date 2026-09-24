#region

using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Parser;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Review Queue: ordering, title cards and the question per clip; the file with its clock block,
///     refused rather than overwritten; a tag ref as a clip; the Reels tray as a client of the queue
///     (its staging, removal, reorder and clear are queue mutations, and clips other surfaces queued
///     survive all four); Result Cards sending a set; the Review tab over the queue; and the module's
///     persisted ids.
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

            string path = Path.Combine(root, ReviewQueue.FileName);
            using (JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                await Assert.That(json.RootElement.GetProperty("clock").GetProperty("kind").GetString())
                    .IsEqualTo(ClockIdentity.DvFrameClock).Because("every tick in the file is on the frame clock (F15)");
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
        ReviewEntry? clip = ReviewQueue.FromTag(instance, sha => sha == "abc" ? "/d/a.dem" : null, 64);
        ReviewEntry? missing = ReviewQueue.FromTag(instance with { Sha256 = "zzz" }, sha => sha == "abc" ? "/d/a.dem" : null);

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
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        ReviewQueue queue = new(null);
        ResultCardsViewModel cards = new(cache, sidecars, sources, () => null, new SituationThumbnailCache(),
            () => new SituationThumbnailRenderer(_ => null), action => action(), _ => null, review: queue);
        cache.Upsert(ParsedRecord("/d/a.dem"));

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
            await Assert.That(cards.ReviewLine).IsEqualTo("2 rounds sent to Review");
        }

        cards.SendToReviewCommand.Execute(null);
        await Assert.That(queue.Entries.Count).IsEqualTo(3);
        await Assert.That(cards.ReviewLine).IsEqualTo("already in Review");

        ResultCardsViewModel without = new(cache, sidecars, sources, () => null);
        await Assert.That(without.HasReview).IsFalse().Because("no queue on the host hides the action");
    }

    // ── The Review tab ────────────────────────────────────────────────────────

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
            await Assert.That(tab.HeaderLine).IsEqualTo("2 clips · 1 section · 2 demos");
            await Assert.That(tab.Rows[0].IsSection).IsTrue();
            await Assert.That(tab.Rows[0].ClipCountText).IsEqualTo("2 clips");
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
    public async Task TheModule_ContributesOneMainTab_UnderThePersistedIds_WithTheClipCountAsBadge()
    {
        ReviewQueue queue = new(null);
        ReviewQueueModule module = new(() => throw new InvalidOperationException("never built here"), queue);
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();
        await Assert.That(tab.Badge).IsNull();
        queue.Add([Clip("/d/a.dem", 1, 2)], "A section");

        FeatureDescriptor? feature = FeatureCatalog.ById("tab.review");
        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.review");
            await Assert.That(tab.TabId).IsEqualTo("review.queue");
            await Assert.That(tab.Header).IsEqualTo("Review");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
            await Assert.That(tab.Badge).IsEqualTo("1").Because("clips only; the title card is not counted");
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain("tab.review")
                .Because("the tab renders on the browser and keeps the queue for the session");
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

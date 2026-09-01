#region

using System.Text.Json;
using Avalonia.Controls;
using CS2DemoKit.Analysis;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Highlights;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Reels dashboard VM battery covers the clip tray: staging, ordering, removal, the
///     guarded clear. It also covers the tray→plan→output ordering contract, the
///     staging-time pre-flight, tray persistence, and the responsive collapse. All headless over a seeded
///     cache store (no demos, no parse, no UI thread).
///     <para>
///         The card-grid battery this replaces (master filtering, per-demo details, the Verify-in-CS2 gate) went
///         with the grid: filtering re-homes to the Add-clips picker and Verify to Match Overview, which has
///         its own suites. What is asserted here is what the dashboard newly owns.
///     </para>
/// </summary>
public class HighlightsTabViewModelTests
{
    // CA1861: hoisted out of the assertions so the analyser stops flagging repeated inline arrays.
    //
    // These are ORDER-SENSITIVE expectations, and TUnit's IsEquivalentTo honours sequence: verified by
    // deliberately flipping _pathsBA to {a,b} and _pathsAAB to the same multiset in a different order
    // {a,b,a}: both tests FAILED. Worth stating, because "equivalent" means order-INsensitive in several
    // assertion libraries, and if it did here the reorder contract would be unverified by a green suite.
    private static readonly string[] _pathsAB = ["/d/a.dem", "/d/b.dem"];
    private static readonly string[] _pathsBA = ["/d/b.dem", "/d/a.dem"];
    private static readonly string[] _pathsAAB = ["/d/a.dem", "/d/a.dem", "/d/b.dem"];
    private static readonly string[] _fileNamesAB = ["a.dem", "b.dem"];

    // camelCase writer: the naming policy a session/settings file plausibly grows tomorrow. Hoisted per
    // CA1869 (the analyser rejects a per-call JsonSerializerOptions).
    private static readonly JsonSerializerOptions _webCase = new(JsonSerializerDefaults.Web);

    // NOTE: name/steam are no longer stored ON the event. The unified record keeps each player once and
    // references them by SLOT, so they must match a roster entry passed to Row(). They stay in this
    // signature because they seed the rendered title, which IS captured at emission.
    private static CachedHighlightEvent Ev(string name, string steam, int slot, int round, int tick, string type)
    {
        int dot = type.IndexOf('.');
        return new CachedHighlightEvent
        {
            RulesetId = dot > 0 ? type[..dot] : "r",
            HighlightId = dot > 0 ? type[(dot + 1)..] : type,
            PlayerSlot = slot,
            RoundNumber = round,
            Tick = tick,
            RenderedTitle = $"{name} — {type} (round {round})"
        };
    }

    private static DemoCacheRecord Row(string path, string map, long modified,
        (string Name, string Steam, int Team, int Slot)[] players,
        CachedHighlightEvent[] events, DemoAnalysisState state = DemoAnalysisState.Indexed) => new()
    {
        Path = path,
        Map = map,
        TickRate = 64,
        TickCount = 120_000,
        ModifiedTicks = modified,
        Analysis = new TierStamp
        {
            Schema = DemoCacheRecord.AnalysisSchema,
            ComputedAtTicks = 1
        },
        AnalysisState = state,
        ConfigFingerprint = "fp@64",
        Sha256 = "sha-" + Path.GetFileName(path),
        Players =
        [
            .. players.Select(p => new CachedPlayerInfo
            {
                Name = p.Name,
                SteamId64 = p.Steam,
                Team = p.Team,
                Slot = p.Slot
            })
        ],
        Rounds =
        [
            new CachedRound
            {
                Number = 1,
                StartTickFrameClock = 1000
            }
        ],
        Highlights = [.. events]
    };

    // Seeds BOTH stores: the tray reads the unified record, while the scanner still owns highlights.json
    // until step 4's writer move. Keeping them in sync here is what the dual-write does in production.
    private static (DemoCacheStore Store, HighlightScanService Scanner) NewStore(
        params DemoCacheRecord[] rows)
    {
        DemoCacheStore store = new(null);
        foreach (DemoCacheRecord r in rows)
        {
            store.Upsert(r);
        }

        HighlightScanService scanner = new(store,
            new FakeHarvester(),
            () => [.. rows.Select(r => r.Path)],
            () => false);
        return (store, scanner);
    }

    private static HighlightsTabViewModel Vm(
        DemoCacheStore store, HighlightScanService scanner,
        IReelJobService? job = null, bool demosExist = true, Action? addClips = null)
    {
        HighlightsTabViewModel vm = new(store, scanner,
            reelJob: job,
            isLiveSyncSessionActive: () => false,
            fileExists: _ => demosExist);
        // The reel defaults are unseeded in a test host; without an output folder every plan is blocked by
        // the "choose an output folder" banner and the ordering assertions below can never reach Start.
        vm.ReelConfig.OutputFolder = "/out";
        return vm;
    }

    // Stage every highlight of every row, in row order: the shorthand most tests want.
    private static void StageAll(HighlightsTabViewModel vm, params DemoCacheRecord[] rows)
    {
        foreach (DemoCacheRecord row in rows)
        {
            foreach (CachedHighlightEvent h in row.Highlights)
            {
                vm.Stage(row, h);
            }
        }
    }

    // ── Staging ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Staging_IsIdempotent_SpansDemos_And_IsStagedIsExact()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 1500, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        HighlightsTabViewModel vm = Vm(store, scanner);

        vm.Stage(a, a.Highlights[0]);
        vm.Stage(a, a.Highlights[0]); // same key twice must not duplicate
        await Assert.That(vm.StagedCount).IsEqualTo(1);

        vm.Stage(b, b.Highlights[0]);
        await Assert.That(vm.StagedCount).IsEqualTo(2).Because("the tray spans demos by construction");
        await Assert.That(vm.IsStaged(new HighlightSelection(a, a.Highlights[0]).Key)).IsTrue();
        await Assert.That(vm.IsStaged(new HighlightKey("/d/nope.dem", "clutch", "ace", 1, 0))).IsFalse();
        await Assert.That(vm.StagedSelections.Select(s => s.Record.Path))
            .IsEquivalentTo(_pathsAB);
    }

    [Test]
    public async Task Toggle_And_Unstage_RoundTrip()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);

        await Assert.That(vm.ToggleStaged(a, a.Highlights[0])).IsTrue();
        await Assert.That(vm.HasStagedClips).IsTrue();
        await Assert.That(vm.ToggleStaged(a, a.Highlights[0])).IsFalse();
        await Assert.That(vm.StagedCount).IsEqualTo(0);

        vm.Stage(a, a.Highlights[0]);
        vm.Unstage(new HighlightSelection(a, a.Highlights[0]).Key);
        await Assert.That(vm.StagedCount).IsEqualTo(0);
    }

    [Test]
    public async Task StageFromCache_ResolvesTheRow_And_RefusesUnknownIdentities()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);

        // This is the seam Match Overview stages through: it holds an identity, not a cache row.
        await Assert.That(vm.StageFromCache("/d/a.dem", "clutch", "ace", 5000, 0)).IsTrue();
        await Assert.That(vm.StagedCount).IsEqualTo(1);
        await Assert.That(vm.StagedSelections[0].Record.TickRate).IsEqualTo(64)
            .Because("the row's window maths must come with it");

        await Assert.That(vm.StageFromCache("/d/gone.dem", "clutch", "ace", 5000, 0)).IsFalse();
        await Assert.That(vm.StageFromCache("/d/a.dem", "clutch", "ace", 999, 0)).IsFalse();
        await Assert.That(vm.StagedCount).IsEqualTo(1);
    }

    // ── The tray IS the plan ──────────────────────────────────────────────────

    [Test]
    public async Task Tray_Carries_Provenance_And_LiveCoalescingFeedback()
    {
        // Two highlights, same player + round, windows overlapping at the default 15s lead-in → one clip.
        DemoCacheRecord a = Row("/d/faceit_dust2.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace"), Ev("s1mple", "1", 0, 7, 5600, "clutch.plant")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);
        StageAll(vm, a);

        await Assert.That(vm.ReelConfig.ClipsHeader).IsEqualTo("CLIPS (2 staged · 1 after merge)");
        ReelClipGroupViewModel group = vm.ReelConfig.ClipGroups.Single();

        // Provenance is mandatory: map accent key, map, demo file, player.
        await Assert.That(group.MapName).IsEqualTo("de_dust2");
        await Assert.That(group.MapDisplay).IsEqualTo("Dust2");
        await Assert.That(group.FileName).IsEqualTo("faceit_dust2.dem");
        await Assert.That(group.Header).Contains("s1mple");

        ReelClipRowViewModel clip = group.Rows.Single();
        await Assert.That(clip.IsMerged).IsTrue().Because("coalescing is visible WHILE building, not after");
        await Assert.That(clip.Contributors.Count).IsEqualTo(2);
        await Assert.That(clip.Contributors[0].RoundDisplay).IsEqualTo("r7");
        await Assert.That(clip.Contributors[0].TickDisplay).IsEqualTo("tick 5,000");
    }

    [Test]
    public async Task EditingLeadIn_ReflowsTheTray_Live()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace"), Ev("s1mple", "1", 0, 7, 6000, "clutch.plant")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);
        StageAll(vm, a);

        await Assert.That(vm.ReelConfig.ClipGroups[0].Rows.Count).IsEqualTo(1);
        vm.ReelConfig.LeadInSeconds = 5; // windows no longer overlap
        await Assert.That(vm.ReelConfig.ClipGroups[0].Rows.Count).IsEqualTo(2);
        await Assert.That(vm.ReelConfig.ClipsHeader).IsEqualTo("CLIPS (2 staged · 2 after merge)");
    }

    // ── Ordering: tray order reaches the rendered output ──────────────────────

    [Test]
    public async Task Reorder_Changes_TheEmittedClipSequence_ButNotTheMerge()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace"), Ev("s1mple", "1", 0, 7, 5600, "clutch.plant")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        FakeReelJob job = new();
        HighlightsTabViewModel vm = Vm(store, scanner, job);
        StageAll(vm, a, b);

        await Assert.That(vm.ReelConfig.ClipGroups.Select(g => g.FileName))
            .IsEquivalentTo(_fileNamesAB);
        int mergedBefore = vm.ReelConfig.ClipGroups.Sum(g => g.Rows.Count);

        // Move the nuke group to the front.
        string nukeKey = vm.ReelConfig.ClipGroups[1].GroupKey;
        vm.MoveGroup(nukeKey, -1);

        await Assert.That(vm.ReelConfig.ClipGroups[0].FileName).IsEqualTo("b.dem");
        await Assert.That(vm.ReelConfig.ClipGroups[0].CanMoveUp).IsFalse().Because("it is the head now");

        // The load-bearing half: reorder must reach the RENDERED sequence. ReelJobService walks
        // request.Clips by index and maps them 1:1 into Cs2Compilation.Clips, which the shipped CSVG docs
        // define as "Ordered list of clips to capture. Processed sequentially", and ConcatenateClips
        // "uses FFmpeg to combine clips in order". So this list order IS the order of the finished video.
        vm.ReelConfig.GenerateCommand.Execute(null);
        await Assert.That(job.LastRequest!.Clips.Select(c => c.DemoPath))
            .IsEquivalentTo(_pathsBA);

        // …and the OTHER half: reorder must NOT change merge behaviour. ClipWindows.Coalesce groups by
        // (demo, player, round) and is order-independent; wiring order into it would make two identical
        // trays render differently depending on how they were assembled.
        await Assert.That(vm.ReelConfig.ClipGroups.Sum(g => g.Rows.Count)).IsEqualTo(mergedBefore);
        await Assert.That(vm.ReelConfig.ClipsHeader).IsEqualTo("CLIPS (3 staged · 2 after merge)");
    }

    [Test]
    public async Task Reorder_KeepsEachDemosClipsContiguous()
    {
        // Interleaving demos is the expensive case: ReelJobService issues a LoadDemoAsync whenever
        // clip.DemoPath changes, so the tray normalises to group-contiguous order however it was built.
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 3, 3000, "clutch.ace"), Ev("s1mple", "1", 0, 9, 40000, "clutch.plant")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        FakeReelJob job = new();
        HighlightsTabViewModel vm = Vm(store, scanner, job);

        // Stage a, then b, then a again: an interleaved build order.
        vm.Stage(a, a.Highlights[0]);
        vm.Stage(b, b.Highlights[0]);
        vm.Stage(a, a.Highlights[1]);

        vm.ReelConfig.GenerateCommand.Execute(null);
        await Assert.That(job.LastRequest!.Clips.Select(c => c.DemoPath))
            .IsEquivalentTo(_pathsAAB);
    }

    // ── Removal + the guarded clear ───────────────────────────────────────────

    [Test]
    public async Task Remove_Works_PerClip_And_PerGroup()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace"), Ev("s1mple", "1", 0, 7, 5600, "clutch.plant")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        HighlightsTabViewModel vm = Vm(store, scanner);
        StageAll(vm, a, b);

        // Per clip: the ✕ on one contributing highlight.
        vm.ReelConfig.ClipGroups[0].Rows[0].Contributors[0].RemoveCommand.Execute(null);
        await Assert.That(vm.StagedCount).IsEqualTo(2);

        // Per (player · demo) group.
        vm.ReelConfig.ClipGroups[0].RemoveCommand.Execute(null);
        await Assert.That(vm.StagedCount).IsEqualTo(1);
        await Assert.That(vm.ReelConfig.ClipGroups.Single().FileName).IsEqualTo("b.dem");
    }

    [Test]
    public async Task ClearTray_Requires_Confirmation()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);

        await Assert.That(vm.ClearTrayCommand.CanExecute(null)).IsFalse().Because("nothing to clear");
        StageAll(vm, a);
        await Assert.That(vm.ClearTrayCommand.CanExecute(null)).IsTrue();

        // Arming must NOT clear. A tray is minutes of curation with no undo.
        vm.ClearTrayCommand.Execute(null);
        await Assert.That(vm.ShowClearConfirm).IsTrue();
        await Assert.That(vm.StagedCount).IsEqualTo(1);

        vm.CancelClearTrayCommand.Execute(null);
        await Assert.That(vm.ShowClearConfirm).IsFalse();
        await Assert.That(vm.StagedCount).IsEqualTo(1);

        vm.ClearTrayCommand.Execute(null);
        vm.ConfirmClearTrayCommand.Execute(null);
        await Assert.That(vm.StagedCount).IsEqualTo(0);
        await Assert.That(vm.ShowClearConfirm).IsFalse();
    }

    // ── Pre-flight at staging time ────────────────────────────────────────────

    [Test]
    public async Task MovedDemo_Surfaces_Inline_At_StagingTime_And_Blocks_Generate()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner, demosExist: false);
        StageAll(vm, a);

        // The point: the user learns the demo moved when they stage it, not when they press Generate.
        await Assert.That(vm.ReelConfig.ClipGroups[0].HasProblem).IsTrue();
        await Assert.That(vm.ReelConfig.ClipGroups[0].Rows[0].HasError).IsTrue();
        await Assert.That(vm.ReelConfig.HasError).IsTrue();
        await Assert.That(vm.ReelConfig.ErrorBanner).Contains("demo moved");
        await Assert.That(vm.ReelConfig.GenerateCommand.CanExecute(null)).IsFalse();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Test]
    public async Task Snapshot_Restores_TheTray_InOrder_And_Drops_VanishedKeys()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        HighlightsTabViewModel vm = Vm(store, scanner);
        StageAll(vm, b, a); // deliberately NOT store order: the snapshot must carry the user's sequence
        vm.TrayColumnWidth = new GridLength(2, GridUnitType.Star);

        object? blob = vm.SnapshotState();

        HighlightsTabViewModel restored = Vm(store, scanner);
        restored.RestoreState(blob);
        await Assert.That(restored.StagedSelections.Select(s => s.Record.Path))
            .IsEquivalentTo(_pathsBA);
        await Assert.That(restored.TrayColumnWidth.Value).IsEqualTo(2d);
        await Assert.That(restored.HasStatusMessage).IsFalse();

        // A demo that vanished between sessions is DROPPED with a note, never resurrected against maths
        // (tickRate / tickCount / rounds) that no longer exists.
        (DemoCacheStore thinner, HighlightScanService scanner2) = NewStore(a);
        HighlightsTabViewModel partial = Vm(thinner, scanner2);
        partial.RestoreState(blob);
        await Assert.That(partial.StagedCount).IsEqualTo(1);
        await Assert.That(partial.StagedSelections[0].Record.Path).IsEqualTo("/d/a.dem");
        await Assert.That(partial.HasStatusMessage).IsTrue();
        await Assert.That(partial.StatusMessage).Contains("could not be restored");
    }

    [Test]
    public async Task Snapshot_Survives_A_Real_JsonRoundTrip()
    {
        // THE ONLY PATH THAT HAPPENS IN PRODUCTION. The shell persists module tab state through the session
        // FILE, so RestoreState is handed a JsonElement, not the DTO SnapshotState returned. The DTO-typed
        // test above would stay green while the tray silently evaporated on every restart, which is exactly
        // the loss tray persistence exists to prevent.
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        HighlightsTabViewModel vm = Vm(store, scanner);
        StageAll(vm, b, a);
        vm.TrayColumnWidth = new GridLength(2, GridUnitType.Star);

        JsonElement blob = JsonSerializer.SerializeToElement(vm.SnapshotState());

        HighlightsTabViewModel restored = Vm(store, scanner);
        restored.RestoreState(blob);
        await Assert.That(restored.StagedSelections.Select(s => s.Record.Path)).IsEquivalentTo(_pathsBA);
        await Assert.That(restored.TrayColumnWidth.Value).IsEqualTo(2d);

        // AND THE SAME PAYLOAD IN camelCase. Today the write path sets no naming policy, so both halves
        // happen to say "StagedClips", an agreement that is incidental, not contracted. One
        // JsonSerializerDefaults.Web added anywhere upstream renames every property, a case-SENSITIVE read
        // binds nothing, and the tray restores empty with no error raised anywhere. This is the assertion
        // that pins it; without it the test above only proves our two halves agree with each other.
        HighlightsTabViewModel webCase = Vm(store, scanner);
        webCase.RestoreState(JsonSerializer.SerializeToElement(vm.SnapshotState(), _webCase));
        await Assert.That(webCase.StagedSelections.Select(s => s.Record.Path)).IsEquivalentTo(_pathsBA);

        // A session file outlives app versions and is user-writable: a shape that no longer matches must
        // degrade to "restore nothing", never throw away the whole tab restore.
        HighlightsTabViewModel garbage = Vm(store, scanner);
        garbage.RestoreState(JsonSerializer.SerializeToElement(new
        {
            StagedClips = "not-an-array"
        }));
        await Assert.That(garbage.StagedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ScanStatus_Projects_TheQueue_And_Is_A_Shell_Seam()
    {
        DemoCacheRecord indexed = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        // Pending WITH events = the stale case: the row still shows its previous harvest everywhere.
        DemoCacheRecord stale = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")], DemoAnalysisState.Pending);
        DemoCacheRecord failed = Row("/d/c.dem", "de_mirage", 0, [("b1t", "3", 2, 0)], [],
            DemoAnalysisState.Failed);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(indexed, stale, failed);

        using HighlightScanStatusViewModel scan = new(scanner, store);
        await Assert.That(scan.QueueDepth).IsEqualTo(1);
        await Assert.That(scan.StaleCount).IsEqualTo(1);
        await Assert.That(scan.FailedCount).IsEqualTo(1);
        await Assert.That(scan.IsRelevant).IsTrue();
        // State is carried by the WORD in the label; the dot is a redundant colour cue (WCAG 1.4.1).
        await Assert.That(scan.Chip.Label).Contains("queued");
        await Assert.That(scan.RetryAllFailedCommand.CanExecute(null)).IsTrue();

        // Retry re-queues the FAILED row only. A user retrying one broken demo has not asked to re-harvest
        // the ones that worked.
        scan.RetryAllFailedCommand.Execute(null);
        await Assert.That(store.TryLoadRecord("/d/c.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Pending)
            .Because("retry lifts Failed — it is excluded from the derived backlog until something does");
        await Assert.That(store.TryLoadRecord("/d/a.dem")!.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
        await Assert.That(scan.FailedCount).IsEqualTo(0)
            .Because("the projection tracks the store, not a snapshot taken at construction");

        // The tab exposes it the way it exposes JobStatus: a settable seam the shell fills.
        HighlightsTabViewModel vm = Vm(store, scanner);
        await Assert.That(vm.ScanStatus).IsNull();
        vm.ScanStatus = scan;
        await Assert.That(vm.ScanStatus).IsSameReferenceAs(scan)
            .Because("the shell hands the tab the SAME mapper the status-strip chip uses");
    }

    [Test]
    public async Task EncodingSection_Follows_TheFeatureGate_Live()
    {
        (DemoCacheStore store, HighlightScanService scanner) = NewStore();
        FakeGate gate = new();

        HighlightsTabViewModel ungated = Vm(store, scanner);
        await Assert.That(ungated.ReelConfig.IsEncodingVisible).IsTrue()
            .Because("with no gate injected (tests, capture) the pane must not lose a section");

        gate.Enabled = false;
        HighlightsTabViewModel vm = new(store, scanner, featureGate: gate);
        await Assert.That(vm.ReelConfig.IsEncodingVisible).IsFalse();

        // A gate is a USER-INITIATED axis and reconciles live. A one-shot read at construction would leave
        // the section wrong until the tab was rebuilt, after the user toggled it in Settings.
        gate.Enabled = true;
        gate.Raise();
        await Assert.That(vm.ReelConfig.IsEncodingVisible).IsTrue();
    }

    // ── Empty states + stubs + layout ─────────────────────────────────────────

    [Test]
    public async Task EmptyTray_And_EmptyLibrary_AreDifferentEmptinesses()
    {
        // Library HAS highlights, tray is empty → the tray's own empty state, and NO "scan my library" line.
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel withLibrary = Vm(store, scanner);
        await Assert.That(withLibrary.ShowEmptyTray).IsTrue();
        await Assert.That(withLibrary.AnyHighlightsIndexed).IsTrue();
        await Assert.That(withLibrary.ShowLibraryNotIndexedLine).IsFalse()
            .Because("sending a user who HAS highlights to a full library re-scan is the exact trap this flow exists to avoid");

        StageAll(withLibrary, a);
        await Assert.That(withLibrary.ShowEmptyTray).IsFalse();

        // Nothing indexed anywhere → the secondary line appears.
        (DemoCacheStore empty, HighlightScanService scanner2) = NewStore();
        HighlightsTabViewModel bare = Vm(empty, scanner2);
        await Assert.That(bare.ShowEmptyTray).IsTrue();
        await Assert.That(bare.ShowLibraryNotIndexedLine).IsTrue();
    }

    [Test]
    public async Task AddClips_Opens_TheTabOwnedPicker_And_Closes_Back()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);

        // The picker is an OVERLAY owned by this tab, not an injected shell action: a second window would
        // need IWindowService (the surface the modal's retirement is stripping) and is unreachable on WASM.
        // So the command is always executable, even with the (now discarded) requestAddClips arg null.
        await Assert.That(vm.AddClipsCommand.CanExecute(null)).IsTrue();
        await Assert.That(vm.IsPickerOpen).IsFalse();

        await vm.AddClipsCommand.ExecuteAsync(null);
        await Assert.That(vm.IsPickerOpen).IsTrue();
        await Assert.That(vm.Picker!.TotalHighlights).IsEqualTo(1);

        vm.ClosePickerCommand.Execute(null);
        await Assert.That(vm.IsPickerOpen).IsFalse();
        await Assert.That(vm.Picker).IsNull();
    }

    [Test]
    public async Task Picker_Staging_RoundTrips_With_TheTray()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
        [
            Ev("s1mple", "1", 0, 7, 5000, "clutch.ace"),
            Ev("s1mple", "1", 0, 9, 9000, "clutch.retake")
        ]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a);
        HighlightsTabViewModel vm = Vm(store, scanner);
        await vm.AddClipsCommand.ExecuteAsync(null);
        AddClipsPickerViewModel picker = vm.Picker!;

        // [ + ] stages into the tray, and the row learns it is staged FROM the tray (it never self-sets, so
        // a refused stage cannot leave the row claiming to be in a reel it is not in).
        picker.Rows[0].ToggleStageCommand.Execute(null);
        await Assert.That(vm.StagedCount).IsEqualTo(1);
        await Assert.That(picker.Rows[0].IsStaged).IsTrue();
        await Assert.That(picker.Rows[0].StageGlyph).IsEqualTo("✓");

        // …and the round trip the plan requires: removing it in the TRAY flips the open picker back to [ + ].
        vm.Unstage(picker.Rows[0].Key);
        await Assert.That(picker.Rows[0].IsStaged).IsFalse();
        await Assert.That(picker.Rows[0].StageGlyph).IsEqualTo("+");

        // Bulk add: multi-select then "Add N selected", in ONE tray push.
        picker.Rows[0].IsPicked = true;
        picker.Rows[1].IsPicked = true;
        await Assert.That(picker.PickedCount).IsEqualTo(2);
        await Assert.That(picker.AddSelectedLabel).IsEqualTo("Add 2 selected");

        int pushes = 0;
        vm.PropertyChanged += (_, e) => pushes += e.PropertyName == nameof(vm.StagedCount) ? 1 : 0;
        picker.AddSelectedCommand.Execute(null);
        await Assert.That(vm.StagedCount).IsEqualTo(2);
        await Assert.That(pushes).IsEqualTo(1)
            .Because("every push re-runs the plan and rebuilds ClipGroups — twenty adds must not be twenty rebuilds");
        await Assert.That(picker.PickedCount).IsEqualTo(0);
        await Assert.That(picker.Rows.All(r => r.IsStaged)).IsTrue();
        // A staged row cannot also be pending-add, or the footer would offer to add what is already staged.
        await Assert.That(picker.Rows[0].CanPick).IsFalse();
    }

    [Test]
    public async Task JobStrip_Tracks_TheSharedStatusViewModel()
    {
        (DemoCacheStore store, HighlightScanService scanner) = NewStore();
        HighlightsTabViewModel vm = Vm(store, scanner);
        await Assert.That(vm.ShowJobStrip).IsFalse().Because("no job VM attached");

        FakeReelJob job = new();
        ReelJobStatusViewModel status = new(job);
        vm.JobStatus = status;
        await Assert.That(vm.ShowJobStrip).IsFalse().Because("attached but idle");

        // A status change must re-raise ShowJobStrip; without the subscription the strip is evaluated once
        // at bind time and then never appears.
        status.Apply(new ReelJobStatus(ReelJobPhase.Capturing, 1, 3, "s1mple — ace", null, null, []));
        await Assert.That(vm.ShowJobStrip).IsTrue();

        status.Apply(ReelJobStatus.Idle);
        await Assert.That(vm.ShowJobStrip).IsFalse();
    }

    [Test]
    public async Task Narrow_Collapse_Toggles_Pane_Visibility()
    {
        (DemoCacheStore store, HighlightScanService scanner) = NewStore();
        HighlightsTabViewModel vm = Vm(store, scanner);

        vm.SetViewportWidth(1200);
        await Assert.That(vm.IsNarrow).IsFalse();
        await Assert.That(vm.TrayVisible).IsTrue();
        await Assert.That(vm.ConfigVisible).IsTrue();
        await Assert.That(vm.SplitterVisible).IsTrue();

        vm.SetViewportWidth(560);
        await Assert.That(vm.IsNarrow).IsTrue();
        // The TRAY is the landing pane here: the inverse of the browser layout this pattern came from.
        await Assert.That(vm.TrayVisible).IsTrue();
        await Assert.That(vm.ConfigVisible).IsFalse();
        await Assert.That(vm.ShowConfigButton).IsTrue();

        vm.ShowConfigCommand.Execute(null);
        await Assert.That(vm.ConfigVisible).IsTrue();
        await Assert.That(vm.TrayVisible).IsFalse();
        await Assert.That(vm.ShowBackButton).IsTrue();

        vm.BackToTrayCommand.Execute(null);
        await Assert.That(vm.TrayVisible).IsTrue();
    }

    [Test]
    public async Task EnrichmentSlot_IsEmpty_ByDefault_And_SelfNotifies()
    {
        (DemoCacheStore store, HighlightScanService scanner) = NewStore();
        HighlightsTabViewModel vm = Vm(store, scanner);
        await Assert.That(vm.HasEnrichments).IsFalse()
            .Because("the slot is in the tree from frame one and costs zero height when empty");

        // No explicit raise: an enrichment that registers and renders zero height is invisible by
        // construction, so the collection must not depend on the registrant remembering to notify.
        bool raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(vm.HasEnrichments);
        vm.EnrichmentSections.Add(new object());
        await Assert.That(raised).IsTrue();
        await Assert.That(vm.HasEnrichments).IsTrue();
    }

    // ── The tray holds cache-row OBJECTS, so a store change can invalidate it ──

    [Test]
    public async Task StoreChange_Reresolves_TheTray_And_Drops_WhatIsGone()
    {
        DemoCacheRecord a = Row("/d/a.dem", "de_dust2", 2, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        DemoCacheRecord b = Row("/d/b.dem", "de_nuke", 1, [("ZywOo", "2", 3, 0)],
            [Ev("ZywOo", "2", 0, 2, 20000, "clutch.retake")]);
        (DemoCacheStore store, HighlightScanService scanner) = NewStore(a, b);
        HighlightsTabViewModel vm = Vm(store, scanner);
        StageAll(vm, a, b);

        // A rescan replaces the row with a DIFFERENT tick rate. Every window in the plan is computed from
        // that row, so a tray still pointing at the detached snapshot computes silently wrong clips.
        DemoCacheRecord rescanned = Row("/d/a.dem", "de_dust2", 9, [("s1mple", "1", 2, 0)],
            [Ev("s1mple", "1", 0, 7, 5000, "clutch.ace")]);
        rescanned.TickRate = 128;
        store.Upsert(rescanned);

        await Assert.That(vm.StagedCount).IsEqualTo(2);
        await Assert.That(vm.StagedSelections.First(x => x.Record.Path == "/d/a.dem").Record.TickRate)
            .IsEqualTo(128).Because("the tray must follow the store, not a snapshot of it");

        // The demo goes away entirely (deleted on disk, pruned from the cache) while the tab is open.
        store.RemoveWhere(r => r.Path == "/d/b.dem");
        await Assert.That(vm.StagedCount).IsEqualTo(1);
        await Assert.That(vm.StagedSelections[0].Record.Path).IsEqualTo("/d/a.dem");
        await Assert.That(vm.StatusMessage).Contains("no longer in the highlights cache");
    }

    // Minimal IFeatureGate stand-in: only highlights.encoding is interesting here.
    private sealed class FakeGate : IFeatureGate
    {
        public bool Enabled { get; set; } = true;
        public UserCategory Category => UserCategory.PowerUser;
        public int HiddenCount => 0;

        public event EventHandler? Changed;

        public bool IsEnabled(string featureId) =>
            featureId != "highlights.encoding" || Enabled;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate) =>
            ($"fp@{tickRate}", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) => throw new NotSupportedException();

        public void InvalidateRules()
        {
        }
    }

    // Records the hand-off (Start payload) without launching anything.
    private sealed class FakeReelJob : IReelJobService
    {
        public ReelRequest? LastRequest { get; private set; }
        public ReelJobStatus Status { get; private set; } = ReelJobStatus.Idle;

        public event EventHandler<ReelJobStatus>? StatusChanged;

        public void Start(ReelRequest request)
        {
            LastRequest = request;
            Status = new ReelJobStatus(ReelJobPhase.Capturing, 0, request.Clips.Count, null, null, null, []);
            StatusChanged?.Invoke(this, Status);
        }

        public Task CancelAsync() => Task.CompletedTask;

        public void RetryRemaining()
        {
        }
    }
}

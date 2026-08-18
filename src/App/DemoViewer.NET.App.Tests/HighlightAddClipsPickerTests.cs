#region

using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Highlights;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The cross-demo <b>Add clips</b> picker — the
///     surface that keeps multi-demo reels possible with the card grid gone. Covers what the picker newly
///     owns: the four re-homed filters over highlight ROWS, the no-filter-match empty state,
///     the honest coverage footer, and the snapshot semantics.
///     <para>
///         The tray round-trip (<c>[ + ]</c> / <c>[ ✓ ]</c> ↔ the staged tray) is asserted where the tray
///         lives, in <c>HighlightsTabViewModelTests</c> — it is a contract BETWEEN the two, and testing it
///         against a mock tray here would prove only that the mock works.
///     </para>
/// </summary>
public class HighlightAddClipsPickerTests
{
    private static EvSpec Ev(string name, string steam, int round, int tick, string type) =>
        new(name, steam, round, tick, type);

    // The unified record stores each player ONCE and events reference them by SLOT, so a fixture cannot hand
    // a name/steamId to an event any more — it has to put them in the roster and link by slot. Row() below
    // assigns slots by first appearance, which is what makes these specs enough.
    private sealed record EvSpec(string Name, string Steam, int Round, int Tick, string Type);

    private static DemoCacheRecord Row(string path, string map, long modified, EvSpec[] events,
        DemoAnalysisState state = DemoAnalysisState.Indexed)
    {
        Dictionary<string, int> slotOf = [];
        List<CachedPlayerInfo> roster = [];
        List<CachedHighlightEvent> harvested = [];
        foreach (EvSpec e in events)
        {
            if (!slotOf.TryGetValue(e.Steam, out int slot))
            {
                slot = slotOf.Count;
                slotOf[e.Steam] = slot;
                roster.Add(new CachedPlayerInfo { Slot = slot, Name = e.Name, SteamId64 = e.Steam });
            }

            int dot = e.Type.IndexOf('.', StringComparison.Ordinal);
            harvested.Add(new CachedHighlightEvent
            {
                RulesetId = dot > 0 ? e.Type[..dot] : "r",
                HighlightId = dot > 0 ? e.Type[(dot + 1)..] : e.Type,
                PlayerSlot = slot,
                RoundNumber = e.Round,
                Tick = e.Tick,
                RenderedTitle = $"{e.Name} — {e.Type} (round {e.Round})"
            });
        }

        return new DemoCacheRecord
        {
            Path = path,
            Map = map,
            TickRate = 64,
            TickCount = 200_000,
            ModifiedTicks = modified,
            Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
            AnalysisState = state,
            Rounds = [new Services.DemoCache.CachedRound { Number = 1, StartTickFrameClock = 1000 }],
            Players = roster,
            Highlights = harvested
        };
    }

    // A picker over a plain row list with a no-op tray — every assertion here is about projection, not staging.
    private static AddClipsPickerViewModel Picker(params DemoCacheRecord[] rows) =>
        new(rows, rows.Length, _ => false, _ => { }, _ => { }, () => { });

    [Test]
    public async Task Lists_HighlightRows_NotDemos_NewestDemoFirst()
    {
        AddClipsPickerViewModel picker = Picker(
            Row("/d/old.dem", "de_mirage", 1, [Ev("b1t", "3", 19, 88_000, "clutch.clutch_1v3")]),
            Row("/d/new.dem", "de_dust2", 9,
            [
                Ev("s1mple", "1", 7, 54_000, "clutch.plant_kills"),
                Ev("s1mple", "1", 7, 54_600, "clutch.ace")
            ]));

        // The unit of work is a CLIP: three demos-worth of cards would be two rows, three highlights are three.
        await Assert.That(picker.Rows.Count).IsEqualTo(3);
        await Assert.That(picker.TotalHighlights).IsEqualTo(3);
        await Assert.That(picker.DemosWithHighlights).IsEqualTo(2);
        await Assert.That(picker.Rows[0].FileName).IsEqualTo("new.dem")
            .Because("newest demo first — the same ordering the card grid used");
        await Assert.That(picker.Rows[0].MapDisplay).IsEqualTo("Dust2");
        await Assert.That(picker.Rows[0].RoundDisplay).IsEqualTo("r7");
        // The estimate uses the SAME window maths the config pane does, so "~20s" is the duration you get.
        await Assert.That(picker.Rows[0].DurationText).IsEqualTo("~20s");
    }

    [Test]
    public async Task Coverage_Counts_DemosWithEVENTS_NotIndexedRows()
    {
        // THE SUBTLETY, resolved deliberately. The measured library has 346/348 rows Pending yet 267
        // events present: a re-queued row keeps its previous harvest. Counting ScanState == Indexed would
        // print "0 analysed demos" above a list of visible highlights — a page contradicting itself.
        AddClipsPickerViewModel picker = Picker(
            Row("/d/pending.dem", "de_nuke", 3, [Ev("ZywOo", "2", 4, 30_000, "clutch.retake_3k")],
                DemoAnalysisState.Pending),
            Row("/d/indexed.dem", "de_dust2", 2, [Ev("s1mple", "1", 7, 54_000, "clutch.ace")]),
            // Never scanned, no events → contributes to the LIBRARY total but not to the coverage numerator.
            Row("/d/unscanned.dem", "de_train", 1, [], DemoAnalysisState.Pending));

        await Assert.That(picker.Rows.Count).IsEqualTo(2);
        await Assert.That(picker.DemosWithHighlights).IsEqualTo(2);
        await Assert.That(picker.LibraryRowCount).IsEqualTo(3);
        await Assert.That(picker.CoverageLine).IsEqualTo("2 highlights across 2 demos");
        // The wireframe's "Only demos with full stats appear here" is FALSE under this definition — a Pending
        // row with a harvest appears — so the copy follows the definition, not the wireframe.
        await Assert.That(picker.CoverageNote).Contains("analysed for highlights");
        await Assert.That(picker.CoverageNote).Contains("2 of 3");
    }

    [Test]
    public async Task Coverage_Omits_TheLibraryClause_WhenItWouldReadAsABug()
    {
        // "12 of 12 cached demos have been" is noise at best and a defect report at worst. Before the first
        // RefreshStaleness pass (and in every test/capture host) the cache holds nothing BUT analysed rows.
        AddClipsPickerViewModel picker = Picker(
            Row("/d/a.dem", "de_dust2", 2, [Ev("s1mple", "1", 7, 54_000, "clutch.ace")]));
        await Assert.That(picker.CoverageNote).DoesNotContain(" of ");
    }

    [Test]
    public async Task Filters_Narrow_TheRows_And_Clear_RestoresThem()
    {
        AddClipsPickerViewModel picker = Picker(
            Row("/d/faceit_dust2.dem", "de_dust2", 3,
            [
                Ev("s1mple", "1", 7, 54_000, "clutch.ace"),
                Ev("ZywOo", "2", 4, 30_000, "clutch.retake_3k")
            ]),
            Row("/d/pug_mirage.dem", "de_mirage", 2, [Ev("s1mple", "1", 19, 88_000, "clutch.clutch_1v3")]));

        await Assert.That(picker.Rows.Count).IsEqualTo(3);
        await Assert.That(picker.HasActiveFilters).IsFalse();

        // Player multi-select carries LIBRARY-WIDE counts — the affordance's whole point is telling you how
        // much is behind each choice before you pick it.
        PlayerFilterItem s1mple = picker.PlayerFilters.Single(p => p.Display == "s1mple");
        await Assert.That(s1mple.Count).IsEqualTo(2);
        await Assert.That(picker.PlayerFilterSummary).IsEqualTo("Players");

        s1mple.IsSelected = true;
        await Assert.That(picker.Rows.Count).IsEqualTo(2);
        await Assert.That(picker.PlayerFilterSummary).IsEqualTo("Players (1)");
        await Assert.That(picker.CoverageLine).IsEqualTo("Showing 2 of 3 highlights across 2 demos");

        // Filters intersect (AND), never union: two selected facets must narrow, not widen.
        picker.MapFilters.Single(m => m.Display == "Mirage").IsSelected = true;
        await Assert.That(picker.Rows.Count).IsEqualTo(1);
        await Assert.That(picker.Rows[0].FileName).IsEqualTo("pug_mirage.dem");

        picker.ClearFiltersCommand.Execute(null);
        await Assert.That(picker.Rows.Count).IsEqualTo(3);
        await Assert.That(picker.HasActiveFilters).IsFalse();
    }

    [Test]
    public async Task TypeFilter_And_Search_Match_WhatTheRowDisplays()
    {
        AddClipsPickerViewModel picker = Picker(
            Row("/d/faceit_dust2.dem", "de_dust2", 3,
            [
                Ev("s1mple", "1", 7, 54_000, "clutch.ace"),
                Ev("ZywOo", "2", 4, 30_000, "clutch.retake_3k")
            ]));

        // Type chips are keyed by the QUALIFIED {rulesetId}.{highlightId} so historical ids in old cache rows
        // stay selectable; the label is the friendly tail.
        HighlightTypeFilterItem ace = picker.TypeFilters.Single(t => t.TypeKey == "clutch.ace");
        await Assert.That(ace.Display).IsEqualTo("ace");
        ace.IsSelected = true;
        await Assert.That(picker.Rows.Count).IsEqualTo(1);
        ace.IsSelected = false;

        // Search spans everything the row shows — map, file, player and title — because a user searching
        // "retake" is describing what they can see, not which field it came from.
        picker.SearchText = "retake";
        await Assert.That(picker.Rows.Count).IsEqualTo(1);
        picker.SearchText = "dust";
        await Assert.That(picker.Rows.Count).IsEqualTo(2);
        picker.SearchText = "faceit";
        await Assert.That(picker.Rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task NoFilterMatch_And_NothingIndexed_AreDifferentEmptinesses()
    {
        AddClipsPickerViewModel picker = Picker(
            Row("/d/a.dem", "de_dust2", 2, [Ev("s1mple", "1", 7, 54_000, "clutch.ace")]));
        picker.SearchText = "nothing matches this";
        await Assert.That(picker.HasRows).IsFalse();
        await Assert.That(picker.ShowNoFilterMatch).IsTrue()
            .Because("rows exist, the filters exclude them — Clear filters is the way out");
        await Assert.That(picker.ShowNothingIndexed).IsFalse();

        // Nothing harvested anywhere is a different problem with a different answer (scan, not clear).
        AddClipsPickerViewModel bare = Picker(
            Row("/d/a.dem", "de_dust2", 2, [], DemoAnalysisState.Pending));
        await Assert.That(bare.ShowNothingIndexed).IsTrue();
        await Assert.That(bare.ShowNoFilterMatch).IsFalse();
    }

    [Test]
    public async Task Picks_Survive_A_FilterChange()
    {
        // Rows are re-projected into a fresh visible collection on every filter change, but multi-select
        // lives on the ROW view-model — the same reason virtualization cannot be allowed to own it. A user
        // who ticks, then filters, then presses Add meant all of their ticks.
        AddClipsPickerViewModel picker = Picker(
            Row("/d/a.dem", "de_dust2", 3, [Ev("s1mple", "1", 7, 54_000, "clutch.ace")]),
            Row("/d/b.dem", "de_nuke", 2, [Ev("ZywOo", "2", 4, 30_000, "clutch.retake_3k")]));

        picker.Rows.Single(r => r.PlayerName == "ZywOo").IsPicked = true;
        await Assert.That(picker.PickedCount).IsEqualTo(1);

        picker.PlayerFilters.Single(p => p.Display == "s1mple").IsSelected = true;
        await Assert.That(picker.Rows.Count).IsEqualTo(1);
        await Assert.That(picker.PickedCount).IsEqualTo(1)
            .Because("a filtered-out pick is still a pick — silently dropping it loses deliberate work");
    }

    [Test]
    public async Task Rescan_IsMirrored_And_SaysSoWhenTheSnapshotGoesStale()
    {
        int rescans = 0;
        AddClipsPickerViewModel picker = new(
            [Row("/d/a.dem", "de_dust2", 2, [Ev("s1mple", "1", 7, 54_000, "clutch.ace")])], 1,
            _ => false, _ => { }, _ => { }, () => { },
            rescanAll: () => rescans++, scanQueueDepth: 4);

        await Assert.That(picker.CanRescan).IsTrue();
        await Assert.That(picker.ShowScanPendingNote).IsTrue();
        await Assert.That(picker.ScanPendingNote).Contains("4 demos still queued");

        picker.RescanAllCommand.Execute(null);
        await Assert.That(rescans).IsEqualTo(1);
        // The row set is a SNAPSHOT (a backfill mid-assembly would reset scroll and wipe the multi-select),
        // so a rescan provably leaves it behind and the picker must say so rather than look inert.
        await Assert.That(picker.HasStatusNote).IsTrue();
        await Assert.That(picker.StatusNote).Contains("Re-open this picker");

        // No opener injected (browser host / tests) → the mirror is absent, not dead.
        AddClipsPickerViewModel noScan = Picker(
            Row("/d/a.dem", "de_dust2", 2, [Ev("s1mple", "1", 7, 54_000, "clutch.ace")]));
        await Assert.That(noScan.CanRescan).IsFalse();
        await Assert.That(noScan.ShowScanPendingNote).IsFalse();
    }
}

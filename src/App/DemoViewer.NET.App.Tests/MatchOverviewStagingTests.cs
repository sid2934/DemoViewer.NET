#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Match Overview's <c>[ + ]</c> stages into the Reels clip tray. This is the
///     per-game half of "explore the highlights per game, assemble across matches": you look at a match, take
///     the good bits, move on.
///     <para>
///         The tray is the authority throughout. Match Overview holds no clip state of its own and never
///         assumes a stage succeeded — the tray resolves the owning cache row itself, because it needs that
///         row's tick rate and round boundaries to compute a clip window at all.
///     </para>
/// </summary>
public class MatchOverviewStagingTests
{
    private const string Demo = "/demos/staging.dem";

    private static (HighlightsTabViewModel Tray, DemoCacheStore Store) Tray()
    {
        DemoCacheStore demoCache = new(null);
        demoCache.Upsert(Record());
        HighlightScanService scanner = new(demoCache, new NoopHarvester(), () => [], () => false);
        return (new HighlightsTabViewModel(demoCache, scanner), demoCache);
    }

    // The one demo both surfaces read: Match Overview renders it, the tray windows clips out of it.
    private static DemoCacheRecord Record() => new()
    {
        Path = Demo,
        Size = 10,
        ModifiedTicks = 20,
        Map = "de_dust2",
        Parse = new TierStamp
        {
            Schema = DemoCacheRecord.ParseSchema,
            ComputedAtTicks = 1
        },
        Analysis = new TierStamp
        {
            Schema = DemoCacheRecord.AnalysisSchema,
            ComputedAtTicks = 1
        },
        AnalysisState = DemoAnalysisState.Indexed,
        Players =
        [
            new CachedPlayerInfo
            {
                Slot = 1,
                Name = "s1mple",
                SteamId64 = "765",
                Team = 3
            }
        ],
        Highlights =
        [
            new CachedHighlightEvent
            {
                RulesetId = "clutch",
                HighlightId = "ace",
                Tick = 54_000,
                PlayerSlot = 1,
                RoundNumber = 7,
                RenderedTitle = "s1mple — ace"
            }
        ]
    };

    private static MatchOverviewTabViewModel Overview(HighlightsTabViewModel tray) => new(
        stageClip: (d, r, h, t, s) => tray.StageFromCache(d, r, h, t, s),
        unstageClip: (d, r, h, t, s) => tray.Unstage(new HighlightKey(d, r, h, t, s)),
        isClipStaged: (d, r, h, t, s) => tray.IsStaged(new HighlightKey(d, r, h, t, s)));

    private static OverviewHighlightRow FirstRow(MatchOverviewTabViewModel vm) =>
        vm.HighlightGroups[0].Highlights[0];

    [Test]
    public async Task Staging_AddsToTheTray_AndUnstaging_RemovesIt()
    {
        (HighlightsTabViewModel tray, _) = Tray();
        MatchOverviewTabViewModel vm = Overview(tray);
        vm.SetCachedRecord(Record());

        OverviewHighlightRow row = FirstRow(vm);

        using (Assert.Multiple())
        {
            await Assert.That(row.IsStaged).IsFalse();
            await Assert.That(row.DemoPath).IsEqualTo(Demo)
                .Because("a reel is cross-demo — the clip identity must carry which demo it came from");
            await Assert.That(row.PlayerSlot).IsEqualTo(1);
        }

        row.StageCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(row.IsStaged).IsTrue();
            await Assert.That(tray.StagedCount).IsEqualTo(1);
        }

        row.StageCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(row.IsStaged).IsFalse().Because("the second press is unmistakably undo");
            await Assert.That(tray.StagedCount).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     Re-rendering the page must show what is already in the tray. Otherwise a staged clip shows a
    ///     <c>[ + ]</c>, and pressing it would toggle the clip OUT — the button doing the opposite of what it
    ///     says.
    /// </summary>
    [Test]
    public async Task ReRendering_ShowsClipsAlreadyInTheTrayAsStaged()
    {
        (HighlightsTabViewModel tray, _) = Tray();
        MatchOverviewTabViewModel vm = Overview(tray);

        vm.SetCachedRecord(Record());
        FirstRow(vm).StageCommand.Execute(null);
        await Assert.That(tray.StagedCount).IsEqualTo(1);

        // Navigate away and back — the page rebuilds its rows from the cache record.
        vm.Clear();
        vm.SetCachedRecord(Record());

        await Assert.That(FirstRow(vm).IsStaged).IsTrue();
    }

    /// <summary>
    ///     The tray resolves the owning cache row itself and refuses a clip it cannot window. Match Overview
    ///     must reflect the REPORTED outcome — an optimistic ✓ would claim a clip is staged when the tray
    ///     holds nothing, and the tray is what actually renders.
    /// </summary>
    [Test]
    public async Task AClipTheTrayCannotResolve_DoesNotShowAsStaged()
    {
        (HighlightsTabViewModel tray, DemoCacheStore store) = Tray();
        MatchOverviewTabViewModel vm = Overview(tray);
        vm.SetCachedRecord(Record());

        // A rescan drops the highlight the page is still showing.
        store.UpdateExisting(Demo, r => r.Highlights.Clear());

        OverviewHighlightRow row = FirstRow(vm);
        row.StageCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(row.IsStaged).IsFalse();
            await Assert.That(tray.StagedCount).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     Un-wired (no shell, browser host, tests), the button must be inert — not throwing, and not
    ///     pretending it staged something.
    /// </summary>
    [Test]
    public async Task WithoutAShell_TheStageButtonIsInert()
    {
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(Record());

        OverviewHighlightRow row = FirstRow(vm);
        row.StageCommand.Execute(null);

        await Assert.That(row.IsStaged).IsFalse();
    }

    // A three-highlight record for one player — the Select-all header button's subject.
    private static DemoCacheRecord MultiRecord() => new()
    {
        Path = Demo,
        Size = 10,
        ModifiedTicks = 20,
        Map = "de_dust2",
        Parse = new TierStamp
        {
            Schema = DemoCacheRecord.ParseSchema,
            ComputedAtTicks = 1
        },
        Analysis = new TierStamp
        {
            Schema = DemoCacheRecord.AnalysisSchema,
            ComputedAtTicks = 1
        },
        AnalysisState = DemoAnalysisState.Indexed,
        Players =
        [
            new CachedPlayerInfo
            {
                Slot = 1,
                Name = "s1mple",
                SteamId64 = "765",
                Team = 3
            }
        ],
        Highlights =
        [
            new CachedHighlightEvent
            {
                RulesetId = "clutch",
                HighlightId = "ace",
                Tick = 54_000,
                PlayerSlot = 1,
                RoundNumber = 7,
                RenderedTitle = "s1mple — ace"
            },
            new CachedHighlightEvent
            {
                RulesetId = "multikill",
                HighlightId = "quad_kill",
                Tick = 61_000,
                PlayerSlot = 1,
                RoundNumber = 9,
                RenderedTitle = "s1mple — 4K"
            },
            new CachedHighlightEvent
            {
                RulesetId = "objective",
                HighlightId = "ninja_defuse",
                Tick = 72_000,
                PlayerSlot = 1,
                RoundNumber = 12,
                RenderedTitle = "s1mple — ninja defuse"
            }
        ]
    };

    // Per-player sections start COLLAPSED — a demo can produce a dozen of them, and pre-expanding buries the
    // "who had moments" overview.
    [Test]
    public async Task PerPlayerSections_StartCollapsed()
    {
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(MultiRecord());

        await Assert.That(vm.HighlightGroups[0].IsExpanded).IsFalse();
    }

    // The header "Select all" stages every un-staged highlight in the section; it is ADD-ONLY, so pressing it
    // again after one is already staged leaves the whole section staged (never toggles a clip back out).
    [Test]
    public async Task SelectAll_StagesEveryHighlightInTheSection_AndIsAddOnly()
    {
        (HighlightsTabViewModel tray, _) = Tray();
        DemoCacheStore multi = new(null);
        multi.Upsert(MultiRecord());
        HighlightScanService scanner = new(multi, new NoopHarvester(), () => [], () => false);
        HighlightsTabViewModel multiTray = new(multi, scanner);
        MatchOverviewTabViewModel vm = new(
            stageClip: (d, r, h, t, s) => multiTray.StageFromCache(d, r, h, t, s),
            unstageClip: (d, r, h, t, s) => multiTray.Unstage(new HighlightKey(d, r, h, t, s)),
            isClipStaged: (d, r, h, t, s) => multiTray.IsStaged(new HighlightKey(d, r, h, t, s)));
        vm.SetCachedRecord(MultiRecord());

        OverviewHighlightGroup group = vm.HighlightGroups[0];
        await Assert.That(group.Highlights.Count).IsEqualTo(3);

        group.SelectAllCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(group.Highlights.All(h => h.IsStaged)).IsTrue();
            await Assert.That(multiTray.StagedCount).IsEqualTo(3);
        }

        // Add-only: a second press does not toggle the already-staged rows back out.
        group.SelectAllCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(group.Highlights.All(h => h.IsStaged)).IsTrue();
            await Assert.That(multiTray.StagedCount).IsEqualTo(3);
        }
    }

    private sealed class NoopHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
            => ("fp", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) =>
            throw new NotSupportedException("staging tests never scan");

        public void InvalidateRules()
        {
        }
    }
}

#region

using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RuleWorkbench;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Commands;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.LiveSync;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.Tutorial;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Views;
using DemoViewer.NET.Views.Analysis;
using DemoViewer.NET.Views.MatchOverview;
using DemoViewer.NET.Views.Tutorial;
using DemoViewer.NET.Views.DemoProcessing;
using DemoViewer.NET.Views.Highlights;
using DemoViewer.NET.Views.Library;
using DemoViewer.NET.Views.LiveSync;
using DemoViewer.NET.Views.RuleWorkbench;
using DemoViewer.NET.Views.Settings;
using DemoViewer.NET.Views.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Path = System.IO.Path;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The named design variants the CLI can render. Each is a factory for a self-contained control
///     (no ParsedDemo, no heavy VM) so captures are fast and deterministic. Add variants here as the
///     UI/UX work needs them (a panel, a proposed layout, a component in a given state).
/// </summary>
public static class Variants
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //  NavStrip visual redesign — dependency-free inline vector icons.
    //
    //  These variants replace the letter-labels ('ev'/'rnd'/'tk') and ASCII glyphs (▶▶/▶|/▶||) that
    //  were flagged as "looks and feels bad" with proper iconography. Icons are Avalonia
    //  PathIcon + Geometry path-data — NO new NuGet dependency (dep policy: a handful of glyphs does
    //  not justify a package). The path-data lives as constants so the eventual production NavStrip
    //  can lift them verbatim into a Styles/Icons.axaml Geometry dictionary once an option is picked.
    //
    //  All three options SHARE the clock + breakpoint iconography (so the comparison isolates the
    //  named complaint — the semantic-JUMP treatment) and keep the responsive DockPanel shape
    //  (CLOCK docked left, TO-BREAKPOINT docked right so it never clips, JUMP fills a ScrollViewer).
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    // Filled 24×24 icon geometries. PathIcon fills the geometry with its Foreground and scales it
    // Uniform to the control's Width/Height. Media-standard filled shapes (crisper than strokes at
    // ~15px). The `F0` prefix on GeoRing selects the EvenOdd fill rule so the two circles cut a hole
    // (a ring), not a solid disc.
    private const string GeoTriLeft = "M 16 5 L 8 12 L 16 19 Z";
    private const string GeoTriRight = "M 8 5 L 16 12 L 8 19 Z";
    private const string GeoStepBack = "M 8 5 L 6 5 L 6 19 L 8 19 Z M 18 5 L 9 12 L 18 19 Z";
    private const string GeoStepFwd = "M 6 5 L 15 12 L 6 19 Z M 16 5 L 18 5 L 18 19 L 16 19 Z";
    private const string GeoPlay = "M 8 5 L 19 12 L 8 19 Z";
    private const string GeoPause = "M 7 5 L 10 5 L 10 19 L 7 19 Z M 14 5 L 17 5 L 17 19 L 14 19 Z";
    private const string GeoFastFwd = "M 3 5 L 11 12 L 3 19 Z M 12 5 L 20 12 L 12 19 Z";
    private const string GeoChevronLeft = "M 15 4 L 7 12 L 15 20 L 12.4 20 L 10.2 12 L 12.4 4 Z";
    private const string GeoChevronRight = "M 9 4 L 17 12 L 9 20 L 11.6 20 L 13.8 12 L 11.6 4 Z";
    private const string GeoFlag = "M 6 4 L 7.4 4 L 7.4 20 L 6 20 Z M 7.4 4 L 16 4 L 12.8 8 L 16 12 L 7.4 12 Z";

    private const string GeoRing =
        "F0 M 12 3 A 9 9 0 1 1 12 21 A 9 9 0 1 1 12 3 Z M 12 7.5 A 4.5 4.5 0 1 1 12 16.5 A 4.5 4.5 0 1 1 12 7.5 Z";

    // A mini timeline ruler: a top baseline with an emphasized centre tick flanked by two short ticks
    // — reads as "a fine time step" (an inverted-T notch read as ⊥, not "tick").
    private const string GeoTick =
        "M 4 5 L 20 5 L 20 7.5 L 4 7.5 Z M 11 7.5 L 13 7.5 L 13 17.5 L 11 17.5 Z "
        + "M 6.2 7.5 L 8 7.5 L 8 13 L 6.2 13 Z M 16 7.5 L 17.8 7.5 L 17.8 13 L 16 13 Z";

    public static readonly IReadOnlyDictionary<string, Func<Control>> All =
        new Dictionary<string, Func<Control>>(StringComparer.OrdinalIgnoreCase)
        {
            ["primitives"] = Primitives,
            ["chrome"] = Chrome,
            ["tables"] = TablesAndCards,
            ["swatches"] = Swatches,
            ["navstrip-real"] = NavStripReal,
            ["navstrip-real-target"] = NavStripRealTarget,
            ["navstrip-proposed"] = () => NavStripProposed(),
            ["navstrip-icon-probe"] = NavIconProbe,
            ["navstrip-redesign-a"] = () => NavStripRedesign(JumpVariant.IconOnly),
            ["navstrip-redesign-b"] = () => NavStripRedesign(JumpVariant.IconCaption),
            ["navstrip-redesign-c"] = () => NavStripRedesign(JumpVariant.SegmentedPill),
            ["navstrip-redesign-jump-a"] = () => JumpDetail(JumpVariant.IconOnly),
            ["navstrip-redesign-jump-b"] = () => JumpDetail(JumpVariant.IconCaption),
            ["navstrip-redesign-jump-c"] = () => JumpDetail(JumpVariant.SegmentedPill),
            ["navstrip-redesign-compare"] = NavStripRedesignCompare,
            ["toolbar-current"] = () => Toolbar(false),
            ["toolbar-proposed"] = () => Toolbar(true),
            ["welcome-current"] = () => Welcome(false),
            ["welcome-proposed"] = () => Welcome(true),
            ["breakpoints-map"] = () => BreakpointsMap(),
            ["settings"] = () => Settings(),
            // Concurrency seeded to 2 so the BACKGROUND PROCESSING section shows the RAM-risk warning (the
            // safety-critical element per demo-processing-queue.md) — the default state (1, no warning) is
            // captured by "settings".
            ["settings-queue-warn"] = () => Settings(2),
            ["wizard"] = Wizard,
            ["library-landing"] = () => Library(LibraryState.Landing),
            ["library-populated"] = () => Library(LibraryState.Populated),
            ["library-dropover"] = () => Library(LibraryState.DragOver),
            ["workbench"] = Workbench,
            ["framelist"] = FrameList,
            // v0.6.0 fit-and-finish surfaces — the code-color-promotion consumers (severity ramps,
            // Classifier* accents, hex swatch tiers) plus the two render-only gaps
            // (analysis eval progress, 2D vision overlay). Render each under BOTH themes.
            ["output-panel"] = OutputPanelDrawer,
            ["diagnostics-log"] = DiagnosticsLog,
            ["command-palette"] = CommandPaletteResults,
            ["binarypane-hex"] = BinaryPaneHex,
            ["msgcard-accents"] = MsgCardAccents,
            ["analysis-progress"] = AnalysisProgress,
            ["pb2d-vision"] = Pb2DVision,
            ["playback2d-canvas"] = () => new Playback2DViewport(),
            ["pb2d-hud-accents"] = Pb2DHudAccents,
            ["livesync-chips"] = LiveSyncChips,
            ["livesync-flyouts"] = LiveSyncFlyouts,
            ["playback2d-livesync-hud"] = Playback2DLiveSyncHud,
            ["highlights-populated"] = () => Highlights(true, false),
            ["highlights-empty"] = () => Highlights(false, false),
            ["highlights-narrow"] = () => Highlights(true, true),
            ["highlights-moved"] = () => Highlights(true, false, demosExist: false),
            ["highlights-job"] = () => Highlights(true, false, withJob: true),
            ["highlights-empty-library"] = () => Highlights(false, false, libraryIndexed: false),
            // The Add-clips picker, IN SITU (overlay over the dashboard) — the state that matters most,
            // because the scrim, the card bounds and the tray behind it only exist together.
            // populated:false on purpose — a picker opened over a FULL tray shows every row already
            // staged, which hides the [ + ] resting state the whole surface is built around.
            ["addclips-populated"] = () => Highlights(false, false, picker: PickerMock.Open),
            ["addclips-staged"] = () => Highlights(false, false, picker: PickerMock.SomeStaged),
            ["addclips-nofiltermatch"] = () => Highlights(false, false, picker: PickerMock.NoFilterMatch),
            ["addclips-nothing-indexed"] = () => Highlights(false, false, libraryIndexed: false,
                picker: PickerMock.Open),
            ["addclips-narrow"] = () => Highlights(false, true, picker: PickerMock.Open),
            // The density case. Four mock rows prove nothing about the one claim the design entry makes —
            // that a flat row list virtualizes trivially — so this seeds ~240 rows over 8 demos: the
            // VirtualizingStackPanel, the scrollbar (which must not shift the columns) and the pinned footer.
            ["addclips-dense"] = () => Highlights(false, false, picker: PickerMock.Open, denseLibrary: true),
            // The highlight-scan StatusChip (the fourth consumer): the chip itself in the strip, and its flyout body.
            ["scanchip-flyout"] = ScanChipFlyout,
            ["scanchip-chips"] = ScanChips,
            ["reel-dialog"] = () => ReelDialog(false, false),
            ["reel-dialog-invalid"] = () => ReelDialog(false, true),
            ["reel-dialog-macos"] = () => ReelDialog(true, false),
            ["reel-chips"] = ReelChips,
            ["queue-flyout"] = () => QueueFlyout(true),
            ["queue-flyout-empty"] = () => QueueFlyout(false),
            ["queue-chips"] = QueueChips,
            // First-run Visual Walkthrough overlay. Render at --size 1100x680 so the
            // seeded SpotlightRects line up with the coarse backdrop regions.
            ["tutorial-welcome"] = () => Tutorial(TutorialMock.Welcome),
            ["tutorial-tabnav"] = () => Tutorial(TutorialMock.TabNav),
            ["tutorial-library"] = () => Tutorial(TutorialMock.Library),
            ["tutorial-waiting"] = () => Tutorial(TutorialMock.Waiting),
            ["tutorial-transport"] = () => Tutorial(TutorialMock.Transport),
            // Forced-phase variants for reviewing the breathing pulse at both ends (the live animation
            // settles near its bright Cue 0% under the headless render pump, so a static dim isn't otherwise
            // observable). AnimatePulse is disabled and Pulse pinned.
            ["tutorial-tabnav-bright"] = () => Tutorial(TutorialMock.TabNav, 1.0),
            ["tutorial-tabnav-dim"] = () => Tutorial(TutorialMock.TabNav, 0.0),
            // Match Overview landing page — the demo-opening landing.
            ["match-overview-opening"] = () => MatchOverview(MatchOverviewMock.Opening),
            ["match-overview-parsed"] = () => MatchOverview(MatchOverviewMock.Parsed),
            ["match-overview-ready"] = () => MatchOverview(MatchOverviewMock.Ready),
            ["match-overview-failed"] = () => MatchOverview(MatchOverviewMock.Failed),
            ["match-overview-spectators"] = () => MatchOverview(MatchOverviewMock.Ready, spectators: 3),
            // Cached render (the page's second job) at each tier the cache can be in. Header = nothing has
            // parsed it; Parse = the 80%-coverage case, real rosters + score but no scoreboard; Analysis =
            // the full record. Drive the width from the CLI to see the two-column body collapse
            // (e.g. --size 1400x1000 vs --size 820x1200).
            ["match-overview-cached-header"] = () => MatchOverviewCached(DemoCacheTier.Header),
            ["match-overview-cached-indexed"] = () => MatchOverviewCached(DemoCacheTier.Parse),
            ["match-overview-cached-full"] = () =>
                MatchOverviewCached(DemoCacheTier.Analysis, DemoAnalysisState.Indexed),
            // A MIGRATED legacy row: player names with no team split, so the rosters must say why rather
            // than draw two empty teams.
            ["match-overview-cached-nosplit"] = () => MatchOverviewCached(DemoCacheTier.Parse, teamSplit: false),
            ["match-overview-cached-failed"] = () =>
                MatchOverviewCached(DemoCacheTier.Analysis, analysisState: DemoAnalysisState.Failed)
        };

    private static readonly (string Event, string Tick, string Hits)[] _dataRows =
    [
        ("player_death", "12480", "×4"),
        ("round_start", "10992", "×1"),
        ("bomb_planted", "13104", "×2"),
        ("weapon_fire", "12511", "×37")
    ];

    private static readonly string[] _speedOptions = ["1x", "2x", "4x", "0.5x"];
    private static readonly string[] _navSpeeds = ["1x", "2x", "4x"];
    private static readonly string[] _jumpButtons = ["◀ ev", "◀ rnd", "◀ tk", "tk ▶", "rnd ▶", "ev ▶", "⚙ ▾"];

    private static readonly (string Name, string Meta)[] _recentDemos =
    [
        ("mirage_vs_faze.dem", "de_mirage · 2 days ago"),
        ("nuke_ecoround.dem", "de_nuke · last week"),
        ("anubis_clutch.dem", "de_anubis · last week")
    ];

    // Marker + label metadata for the three jump targets (event / round / tick).
    private static readonly (string Geo, string Word, string PrevTip, string NextTip)[] _jumpTargets =
    [
        (GeoFlag, "Event", "Previous event", "Next event"),
        (GeoRing, "Round", "Previous round", "Next round"),
        (GeoTick, "Tick", "Previous tick", "Next tick")
    ];

    private static readonly string[] _navSpeedsX = ["0.25", "0.5", "1", "2", "4", "8"];

    /// <summary>
    ///     The real <see cref="DemoViewer.NET.Views.Highlights.HighlightsTabView" /> over a real
    ///     <see cref="DemoViewer.NET.ViewModels.Highlights.HighlightsTabViewModel" /> seeded with fake cache
    ///     rows (no demos, no parse). Populated selects the first demo so the details pane + a reel selection
    ///     render; narrow demonstrates the responsive single-column collapse (set the tab width small on the
    ///     CLI, e.g. --size 560x680). Empty shows the hero.
    /// </summary>
    /// <summary>
    ///     The Reels dashboard (docs/ui/highlights-matchoverview-redesign.md): the ordered clip tray plus
    ///     the promoted reel config pane. Variants exercise the three states that actually differ — a populated
    ///     cross-demo tray, the empty tray, and the narrow (single-column) collapse — plus a "moved demo"
    ///     variant, because the staging-time pre-flight is the one thing that reads only from disk state.
    /// </summary>
    /// <summary>Which Add-clips picker state a Highlights capture opens with (null = closed).</summary>
    private enum PickerMock
    {
        /// <summary>Open, unfiltered, nothing staged from it yet.</summary>
        Open,

        /// <summary>Open with part of the list already in the tray — the [ + ] vs [ ✓ ] contrast.</summary>
        SomeStaged,

        /// <summary>Open with a search needle that excludes every row.</summary>
        NoFilterMatch
    }

    private static HighlightsTabView Highlights(bool populated, bool narrow, bool demosExist = true,
        bool withJob = false, bool libraryIndexed = true, PickerMock? picker = null,
        bool denseLibrary = false)
    {
        DemoCacheStore store = new(null);
        if (denseLibrary)
        {
            SeedDenseHighlightLibrary(store);
            libraryIndexed = false; // the dense seed replaces the two hand-written demos
        }

        // A bare cache is a real first-run state: the empty tray then also carries the SECONDARY
        // "your library isn't indexed" line, which must NOT appear when highlights already exist.
        if (libraryIndexed)
        {
            store.Upsert(HlRow("/demos/faceit_2025-06-14_dust2.dem", "de_dust2", 300,
                [
                    ("s1mple", "76561198000000001", 2, 0),
                    ("ZywOo", "76561198000000002", 3, 1),
                    ("b1t", "76561198000000003", 2, 2)
                ],
                [(4, 28000), (7, 50000), (12, 58000)],
                [
                    ("s1mple", "76561198000000001", 0, 2, 7, 54321, "s1mple — 2 kills after the plant (round 7)", "clutch.plant_kills"),
                    ("s1mple", "76561198000000001", 0, 2, 7, 54600, "s1mple — ace (round 7)", "clutch.ace"),
                    ("ZywOo", "76561198000000002", 1, 3, 4, 30110, "ZywOo — 3k retake (round 4)", "clutch.retake_3k")
                ]));
            store.Upsert(HlRow("/demos/pug_2025-05-30_mirage.dem", "de_mirage", 200,
                [("b1t", "76561198000000003", 3, 0), ("stavn", "76561198000000011", 2, 1)],
                [(2, 12000), (19, 88000)],
                [
                    ("b1t", "76561198000000003", 0, 5, 19, 88400, "b1t — clutch 1v3 (round 19)", "clutch.clutch_1v3")
                ]));
        }

        HighlightScanService scanner = new HighlightScanService(
            store,
            new CaptureHarvester(),
            libraryDemoPaths: () => [],
            backgroundScanEnabled: () => false);

        HighlightsTabViewModel vm = new(
            store, scanner,
            reelJob: null,
            isLiveSyncSessionActive: () => false,
            // macOS is the capture host, but the dry-run caption is a platform fact, not a capture one — pin
            // it FALSE so the footer renders the real "Generate reel" primary the majority of users see.
            dryRunOnly: false,
            // Injected so the "demo moved" pre-flight is reachable without touching the filesystem.
            fileExists: _ => demosExist);
        // The reel defaults are unseeded in a headless host, so pin an output folder — otherwise every
        // capture shows the "choose an output folder" banner instead of the state under test.
        vm.ReelConfig.OutputFolder = "/Users/you/Movies/Reels";

        if (populated)
        {
            // A cross-demo tray: two dust2 highlights for one player in one round (they coalesce, so the
            // merged line renders) plus a second demo, which is what makes provenance load-bearing.
            foreach (DemoCacheRecord row in store.LoadRecords())
            {
                foreach (CachedHighlightEvent h in row.Highlights)
                {
                    vm.Stage(row, h);
                }
            }
        }

        if (withJob)
        {
            vm.JobStatus = new ReelJobStatusViewModel(new CaptureReelJob());
            vm.JobStatus.Apply(new ReelJobStatus(ReelJobPhase.Capturing, 1, 3, "s1mple — ace", null, null, []));
        }

        if (narrow)
        {
            vm.SetViewportWidth(560);
        }

        if (picker is { } pickerState)
        {
            vm.AddClipsCommand.Execute(null);
            AddClipsPickerViewModel open = vm.Picker!;
            switch (pickerState)
            {
                case PickerMock.SomeStaged:
                    // Stage from the picker itself, so the capture also proves the [ + ] → [ ✓ ] round trip
                    // renders (the flag is pushed back by the tray, never self-set by the row).
                    open.Rows[0].ToggleStageCommand.Execute(null);
                    open.Rows[1].IsPicked = true;
                    break;
                case PickerMock.NoFilterMatch:
                    open.SearchText = "zzzz-no-such-clip";
                    break;
                case PickerMock.Open:
                default:
                    break;
            }
        }

        return new HighlightsTabView
        {
            DataContext = vm
        };
    }

    // A realistic corpus for the density capture: 8 demos × 30 highlights over 6 maps and 8 players, which is
    // the shape a scanned library actually has. Deterministic (no Random) so two captures are diffable.
    private static void SeedDenseHighlightLibrary(DemoCacheStore store)
    {
        string[] maps = ["de_dust2", "de_mirage", "de_nuke", "de_inferno", "de_ancient", "de_overpass"];
        string[] players = ["s1mple", "ZywOo", "b1t", "stavn", "NiKo", "device", "ropz", "m0NESY"];
        string[] types = ["clutch.ace", "clutch.clutch_1v3", "clutch.retake_3k", "clutch.plant_kills",
            "clutch.deagle_hs", "opening.entry_trade"];

        for (int d = 0; d < 8; d++)
        {
            (string Name, string SteamId, int Team, int Slot)[] roster =
            [
                .. Enumerable.Range(0, 5).Select(i =>
                {
                    int p = (d * 3 + i) % players.Length;
                    return (players[p], "7656119800000" + p.ToString("D4",
                            System.Globalization.CultureInfo.InvariantCulture),
                        i < 3 ? 2 : 3, i);
                })
            ];

            (string Name, string SteamId, int Slot, int FrameIndex, int Round, int Tick, string Title,
                string TypeKey)[] events =
                [
                    .. Enumerable.Range(0, 30).Select(e =>
                    {
                        (string name, string steam, int _, int slot) = roster[e % roster.Length];
                        string type = types[(d + e) % types.Length];
                        int round = e + 1;
                        return (name, steam, slot, e, round, 6000 + e * 3700,
                            $"{name} — {type[(type.IndexOf('.') + 1)..].Replace('_', ' ')} (round {round})",
                            type);
                    })
                ];

            store.Upsert(HlRow(
                $"/demos/faceit_2025-0{d % 9 + 1}-1{d}_{maps[d % maps.Length][3..]}.dem",
                maps[d % maps.Length], 900 - d, roster,
                [.. Enumerable.Range(1, 30).Select(r => (r, 4000 + r * 3700))], events));
        }
    }

    /// <summary>
    ///     The highlight-scan chip's flyout body — the fourth <c>StatusChip</c> consumer, rendered
    ///     standalone in a <c>card-flyout</c> exactly as the strip hosts it (the <c>queue-flyout</c> pattern).
    /// </summary>
    private static Border ScanChipFlyout()
    {
        DemoCacheStore store = new(null);
        store.Upsert(HlRow("/demos/faceit_2025-06-14_dust2.dem", "de_dust2", 300,
            [("s1mple", "76561198000000001", 2, 0)], [(7, 50000)],
            [("s1mple", "76561198000000001", 0, 2, 7, 54321, "s1mple — ace (round 7)", "clutch.ace")]));
        // A demo carrying a harvest whose fingerprint has since moved = the "outdated" case: still queued,
        // still showing its previous results. Its record's fingerprint is deliberately not the current one.
        DemoCacheRecord outdated = HlRow("/demos/esl_2025-06-02_nuke.dem", "de_nuke", 200,
            [("ZywOo", "76561198000000002", 3, 0)], [(4, 28000)],
            [("ZywOo", "76561198000000002", 1, 3, 4, 30110, "ZywOo — 3k retake (round 4)", "clutch.retake_3k")]);
        outdated.ConfigFingerprint = "stale@64";
        store.Upsert(outdated);
        store.Upsert(HlRow("/demos/corrupt_half_download.dem", "de_mirage", 100, [], [], [],
            DemoAnalysisState.Failed));

        HighlightScanService scanner = new(store, new CaptureHarvester(), () => [], () => false);
        HighlightScanStatusViewModel vm = new(scanner, store);

        return new Border
        {
            Background = Tok("ShellBg"),
            Padding = new Thickness(20),
            Child = new Border
            {
                Classes =
                {
                    "card-flyout"
                },
                MaxWidth = 340,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new HighlightScanStatusView
                {
                    DataContext = vm
                }
            }
        };
    }

    /// <summary>The scan chip in each state it reaches in the status strip (label carries the state, not the dot).</summary>
    private static Border ScanChips()
    {
        (string State, StatusChipDotState Dot, bool Pulse, string Label)[] rows =
        [
            ("Scanning — pulsing", StatusChipDotState.Working, true, "Highlights · scanning (11 left)"),
            ("Queued, drain not started", StatusChipDotState.Working, false, "Highlights · 12 queued"),
            ("Failures only", StatusChipDotState.Error, false, "Highlights · 3 failed"),
            ("Idle", StatusChipDotState.Off, false, "Highlights · idle")
        ];

        StackPanel col = new()
        {
            Spacing = 5,
            Margin = new Thickness(0, 6, 0, 6)
        };
        foreach ((string state, StatusChipDotState dot, bool pulse, string label) in rows)
        {
            col.Children.Add(new StatusStrip
            {
                StatusText = state,
                StatusBrush = Tok("TextMid"),
                Chips = new[]
                {
                    new StatusChipViewModel
                    {
                        DotState = dot,
                        IsPulsing = pulse,
                        Label = label
                    }
                }
            });
        }

        return new Border
        {
            Width = 520,
            Background = Tok("ShellBg"),
            Child = col
        };
    }

    private static DemoCacheRecord HlRow(
        string path, string map, long modified,
        (string Name, string SteamId, int Team, int Slot)[] players,
        (int Number, int StartTick)[] rounds,
        (string Name, string SteamId, int Slot, int FrameIndex, int Round, int Tick, string Title, string TypeKey)[] events,
        DemoAnalysisState scanState =
            DemoAnalysisState.Indexed)
    {
        return new DemoCacheRecord
        {
            Path = path,
            Map = map,
            TickRate = 64,
            TickCount = 120000,
            ModifiedTicks = modified,
            ProfileName = "Cs2GotvProfile",
            Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
            AnalysisState = scanState,
            ConfigFingerprint = "capture@64",
            Players =
            [
                .. players.Select(p => new CachedPlayerInfo
                {
                    Name = p.Name,
                    SteamId64 = p.SteamId,
                    Team = p.Team,
                    Slot = p.Slot
                })
            ],
            Rounds =
            [
                .. rounds.Select(r => new Services.DemoCache.CachedRound
                {
                    Number = r.Number,
                    StartTickFrameClock = r.StartTick
                })
            ],
            Highlights =
            [
                .. events.Select(e =>
                {
                    int dot = e.TypeKey.IndexOf('.');
                    return new CachedHighlightEvent
                    {
                        RulesetId = dot > 0 ? e.TypeKey[..dot] : "rules",
                        HighlightId = dot > 0 ? e.TypeKey[(dot + 1)..] : e.TypeKey,
                        PlayerSlot = e.Slot,
                        FrameIndex = e.FrameIndex,
                        RoundNumber = e.Round,
                        Tick = e.Tick,
                        RenderedTitle = e.Title
                    };
                })
            ]
        };
    }

    /// <summary>
    ///     The real <see cref="LibraryTabView" /> bound to a real <see cref="LibraryTabViewModel" /> over
    ///     throwaway temp-dir stores. Seeds two recents (one on a real temp file → shown; one on a missing
    ///     path → dimmed grey-out), with back-dated open times so the relative-date labels ("2d ago" / "6d
    ///     ago") render. <see cref="LibraryState.Populated" /> also seeds folders + a few demo entries so the
    ///     browser + the persistent header actions render; <see cref="LibraryState.DragOver" /> forces the
    ///     drop overlay visible (a real file drag can't be synthesized off-display).
    /// </summary>
    private static LibraryTabView Library(LibraryState state)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "demoviewer-uicapture-library", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // A real temp file so one recent stats as "exists"; the other path never existed → dimmed.
        string realDemo = Path.Combine(dir, "mirage_vs_faze.dem");
        File.WriteAllText(realDemo, "x");

        // Recents live in the Recents section of the single config file. Seed them through a
        // throwaway temp-dir SettingsService, then load via the store (mirrors the real DI wiring).
        List<RecentFile> seeded = new()
        {
            new RecentFile("/demos/nuke_ecoround.dem", "de_nuke", DateTime.UtcNow.AddDays(-2)), // missing → dimmed
            new RecentFile(realDemo, "de_mirage", DateTime.UtcNow.AddDays(-6)) // exists
        };
        SettingsService recentsSettings = new(dir);
        recentsSettings.SaveRecents(seeded);
        RecentFilesStore recents = new(recentsSettings);

        string libJson = Path.Combine(dir, "library.json");
        DemoLibraryService lib = new(a => a(), libJson);

        if (state == LibraryState.Populated)
        {
            lib.Folders.Add("/demos/pro-matches");
            lib.Entries.Add(SampleDemo("de_mirage", "ZywOo, apEX, flameZ, Spinx, mezii", "mirage_vs_faze.dem"));
            lib.Entries.Add(SampleDemo("de_nuke", "device, stavn, jabbi, Staehr, cadiaN", "nuke_astralis.dem"));
            lib.Entries.Add(SampleDemo("de_dust2", "s1mple, b1t, Aleksib, iM, jL", "dust2_navi.dem"));
        }

        LibraryTabViewModel vm = new(
            lib,
            _ => Task.CompletedTask,
            () => Task.FromResult<IReadOnlyList<string>>([]),
            () => Task.CompletedTask,
            recents)
        {
            IsDragOver = state == LibraryState.DragOver
        };

        return new LibraryTabView
        {
            DataContext = vm
        };
    }

    /// <summary>
    ///     The real <see cref="RuleWorkbenchView" /> bound to a real <see cref="RuleWorkbenchTabViewModel" />
    ///     (no demo). Selects a shipped ruleset so the editor + the read-only "🔒 shipped" caution show, and
    ///     seeds a diagnostic so the Problems panel exercises the soft-error location colour. Renders the
    ///     workbench's translucent pane borders (P3.3 de-inline target).
    /// </summary>
    private static RuleWorkbenchView Workbench()
    {
        RuleWorkbenchTabViewModel vm = new();

        // A shipped ruleset → editor gets content and IsReadOnlyFile flips the AccentCaution indicator on.
        RulesetFileRef? shipped = vm.OpenableFiles.FirstOrDefault(f => f.IsShipped);
        if (shipped is not null)
        {
            vm.SelectedFile = shipped;
        }

        // Seed a diagnostic AFTER selection so the Problems list shows the AccentErrorSoft location text.
        vm.Diagnostics.Add(new WorkbenchDiagnostic(
            "example.rules.yaml(12,7)",
            "unknown name 'kils' — did you mean 'kills'?",
            "resolve.unknown-name", "example.rules.yaml", 12, 7));

        return new RuleWorkbenchView
        {
            DataContext = vm
        };
    }

    /// <summary>
    ///     The real <see cref="HarvestFrameListControl" /> (Parser tab's frame list) bound to mock
    ///     <see cref="HarvestFrameRowViewModel" /> rows over a <see cref="MainViewModel" /> DataContext
    ///     (for the breakpoint-toggle command binding). Varied frame types exercise the per-type accent
    ///     pills; one row has a breakpoint set (red-dim gutter dot) and one is selected (exercises the
    ///     selected-row fill token). P3.4 de-inline target — a plain ListBox, headless-capturable.
    /// </summary>
    private static HarvestFrameListControl FrameList()
    {
        List<HarvestFrameRowViewModel> rows = new()
        {
            new HarvestFrameRowViewModel
            {
                FrameNumber = 1,
                FrameType = "DEM_FileHeader",
                MessageCount = 0,
                ByteSize = 320
            },
            new HarvestFrameRowViewModel
            {
                FrameNumber = 2,
                FrameType = "DEM_SignonPacket",
                MessageCount = 8,
                ByteSize = 4096,
                IsBreakpointSet = true
            },
            new HarvestFrameRowViewModel
            {
                FrameNumber = 3,
                FrameType = "DEM_Packet",
                MessageCount = 24,
                ByteSize = 12800
            },
            new HarvestFrameRowViewModel
            {
                FrameNumber = 4,
                FrameType = "DEM_FullPacket",
                MessageCount = 41,
                ByteSize = 65536
            },
            new HarvestFrameRowViewModel
            {
                FrameNumber = 5,
                FrameType = "DEM_StringTables",
                MessageCount = 3,
                ByteSize = 2048
            },
            new HarvestFrameRowViewModel
            {
                FrameNumber = 6,
                FrameType = "DEM_ConsoleCmd",
                MessageCount = 0,
                ByteSize = 96
            },
            new HarvestFrameRowViewModel
            {
                FrameNumber = 7,
                FrameType = "DEM_Packet",
                MessageCount = 19,
                ByteSize = 9600
            }
        };

        MainViewModel vm = new()
        {
            HasFile = true
        };
        HarvestFrameListControl ctl = new()
        {
            DataContext = vm,
            Frames = rows
        };
        ctl.SelectedItem = rows[2]; // exercise the selected-row fill token
        return ctl;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //  v0.6.0 fit-and-finish — code-color-promotion consumers + render-only surfaces.
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The real <see cref="OutputPanel" /> drawer over an <see cref="OutputPanelViewModel" /> seeded
    ///     with rows in every fixed channel plus the lazily-created "Live Sync" channel
    ///     (<see cref="OutputSeverity.Live" /> → the AccentInfo teal). A NON-first channel is made active
    ///     via its SelectCommand so the active×severity underline + title tint (sev-err here) render —
    ///     the v0.6.0 severity-kind → theme-token mapping this panel replaced its code-held brushes with.
    /// </summary>
    private static Border OutputPanelDrawer()
    {
        OutputPanelViewModel vm = new(new FrameNavigationViewModel());

        vm.UnknownMessages.Append(new OutputRow(2, "t 11296", "WARN",
            "unknown net-message id 402 (28 B) — raw bytes kept for the RE workbench"));
        vm.UnknownMessages.Append(new OutputRow(5, "t 12480", "WARN", "unknown net-message id 547 (6 B)"));
        vm.DecodeErrors.Append(new OutputRow(3, "t 11360", "ERR",
            "svc_PacketEntities: field-path op 39 out of range at bit 18244"));
        vm.DecodeErrors.Append(new OutputRow(7, "t 12511", "ERR", "CCSUsrMsg_XpUpdate: truncated varint at byte 14"));
        vm.TrackerErrors.Append(new OutputRow(9, "t 13104", "ERR",
            "entity #612 delta on unseen baseline — resynced from full packet"));
        vm.BuildTest.Append(new OutputRow(-1, "—", "info", "analysis suite: 128 rules compiled, 0 diagnostics"));

        OutputChannelViewModel live = vm.GetOrAddChannel("Live Sync", OutputSeverity.Live);
        live.Append(new OutputRow(-1, "12:01:07", "info", "[CSVG] LoadDemoAsync complete — demo paused at tick 0"));
        live.Append(new OutputRow(-1, "12:01:11", "info", "[CSVG] tick-stream jump → 54321 (user seek)"));

        // Activate a non-first channel so the capture shows BOTH an inactive tab (Unknown messages,
        // count badge only) and the active sev-err underline/tint on Decode errors.
        vm.DecodeErrors.SelectCommand.Execute(null);

        return WrapInShell(new OutputPanel
        {
            DataContext = vm
        }, 900, 280);
    }

    /// <summary>
    ///     The Diagnostics tab's telemetry log rows, one per <see cref="LogLevel" /> tier, over a real
    ///     <see cref="DiagnosticsTelemetryHub" /> (synchronous uiPost, so Enqueue/Append land inline).
    ///     The full <c>DiagnosticsTabView</c>/<c>DiagnosticsTabViewModel</c> is NOT constructed — its ctor
    ///     needs a live <c>AnalysisTabViewModel</c> + demo accessors — so this rebuilds the SAME row
    ///     template shape (Time · level chip · Source · Category · Message) with the SAME class-based
    ///     styles the tab declares: <c>tlvl</c> / <c>tlvl-err</c> / <c>tlvl-warn</c> / <c>tlvl-debug</c>
    ///     → AccentInfo / AccentError / AccentAmber / TextMid (v0.6.0 — was the third code-held copy of
    ///     the severity ramp).
    /// </summary>
    private static Border DiagnosticsLog()
    {
        DiagnosticsTelemetryHub hub = new(() => 100, a => a());
        (string Source, LogLevel Level, string Label, string Category, string Message, string Time)[] rows =
        [
            ("App", LogLevel.Critical, "CRIT", "DemoViewer.NET.App",
                "Reel job crashed — OBS pipe closed mid-capture", "12:01:02"),
            ("Analysis", LogLevel.Error, "ERR", "Analysis.Evaluator",
                "Rule 'clutch.ace' threw: sequence contains no elements", "12:01:04"),
            ("CSVG", LogLevel.Warning, "WARN", "Csvg.WebHost",
                "Kestrel bound to 50051 after 2 retries — another client had the port", "12:01:05"),
            ("App", LogLevel.Information, "info", "Services.HeavyJobGate",
                "tier-2 parse queued behind interactive load", "12:01:06"),
            ("Analysis", LogLevel.Debug, "dbg", "Analysis.StateGraph",
                "chain 'opening_duel' compiled — 14 nodes, 22 edges", "12:01:06"),
            ("CSVG", LogLevel.Trace, "trace", "Csvg.TickStream",
                "tick batch 640..704 (64) in 3.1 ms", "12:01:07")
        ];
        foreach ((string src, LogLevel lvl, string label, string cat, string msg, string time) in rows)
        {
            hub.AppendOnUiThread(new TelemetryLogRow(src, lvl, label, cat, msg, time));
        }

        ListBox list = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 11
        };
        foreach (TelemetryLogRow row in hub.Logs)
        {
            Grid g = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,42,54,Auto,*"),
                ColumnSpacing = 8
            };

            void Cell(int col, string text, string? tok = null, Action<TextBlock>? mutate = null)
            {
                TextBlock tb = new()
                {
                    Text = text
                };
                if (tok is not null)
                {
                    tb.Foreground = Tok(tok);
                }

                mutate?.Invoke(tb);
                Grid.SetColumn(tb, col);
                g.Children.Add(tb);
            }

            Cell(0, row.Time, "TextDim");
            Cell(1, row.LevelLabel, null, tb =>
            {
                tb.FontWeight = FontWeight.SemiBold;
                tb.Classes.Add("tlvl");
                if (row.IsSevError)
                {
                    tb.Classes.Add("tlvl-err");
                }
                else if (row.IsSevWarn)
                {
                    tb.Classes.Add("tlvl-warn");
                }
                else if (row.IsSevDebug)
                {
                    tb.Classes.Add("tlvl-debug");
                }
            });
            Cell(2, row.Source, "TextDim");
            Cell(3, row.Category, "TextDim", tb =>
            {
                tb.MaxWidth = 200;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
            });
            Cell(4, row.Message, "TextValue", tb => tb.TextWrapping = TextWrapping.Wrap);
            list.Items.Add(g);
        }

        Border panel = new()
        {
            Background = Tok("PanelBg"),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Tok("BorderSubtle"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Child = list
        };

        // The tab's severity-chip class styles, verbatim (DiagnosticsTabView.axaml): default = AccentInfo,
        // err/warn/debug override. Attached here so the render proves the class path, not just the tokens.
        static Style Lvl(string? extra, string tok)
        {
            Selector Sel(Selector? s)
            {
                Selector baseSel = s.OfType<TextBlock>().Class("tlvl");
                return extra is null ? baseSel : baseSel.Class(extra);
            }

            Style style = new(Sel);
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Tok(tok)));
            return style;
        }

        Border root = WrapInShell(panel, 880, 240);
        root.Styles.Add(Lvl(null, "AccentInfo"));
        root.Styles.Add(Lvl("tlvl-err", "AccentError"));
        root.Styles.Add(Lvl("tlvl-warn", "AccentAmber"));
        root.Styles.Add(Lvl("tlvl-debug", "TextMid"));
        return root;
    }

    /// <summary>
    ///     The command-palette results list — one row per result kind, each glyph tinted through the
    ///     v0.6.0 <c>ck-*</c> → Classifier* token styles. The real <see cref="CommandPalette" /> control
    ///     hosts a <c>Popup</c> (headless-hostile: popups render in an overlay layer the frame capture
    ///     misses), so this renders the INNER card content with real <see cref="CommandPaletteItem" />
    ///     records (a <c>CommandPaletteViewModel</c> needs the nav/proto-index seams) and the same
    ///     ItemTemplate shape + ck-class styles CommandPalette.axaml declares.
    /// </summary>
    private static Border CommandPaletteResults()
    {
        RelayCommand noop = new(() => { });
        CommandPaletteItem[] items =
        [
            new("›", "Go to frame 42", "frame", noop),
            new("⏱", "Go to tick 9000", "tick", noop),
            new("◇", "CCSPlayerPawn", "class", noop),
            new("⤴", "CCSUsrMsg_ServerRankUpdate", "cstrike15_usermessages.proto", noop)
        ];

        StackPanel rows = new()
        {
            Spacing = 0
        };
        foreach (CommandPaletteItem it in items)
        {
            Grid g = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(4, 4)
            };
            TextBlock icon = new()
            {
                Text = it.Icon,
                Margin = new Thickness(0, 0, 8, 0)
            };
            icon.Classes.Add(it.IsKindFrame ? "ck-frame"
                : it.IsKindTick ? "ck-tick"
                : it.IsKindClass ? "ck-class"
                : "ck-proto");
            TextBlock label = new()
            {
                Text = it.Label,
                Foreground = Tok("TextBright")
            };
            TextBlock detail = new()
            {
                Text = it.Detail,
                Foreground = Tok("TextDim"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(detail, 2);
            g.Children.Add(icon);
            g.Children.Add(label);
            g.Children.Add(detail);
            rows.Children.Add(new Button
            {
                Command = it.PickCommand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = g
            });
        }

        Border card = new()
        {
            Background = Tok("CardBg"),
            BorderBrush = Tok("BorderAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Width = 560,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBox
                    {
                        Watermark = "Go to frame, tick, class, .proto…",
                        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                        Background = Tok("PanelBg")
                    },
                    rows
                }
            }
        };

        // The ck-* result-kind styles, verbatim (CommandPalette.axaml → Classifier* tokens, v0.6.0).
        static Style Ck(string cls, string tok)
        {
            Style style = new(s => s.OfType<TextBlock>().Class(cls));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Tok(tok)));
            return style;
        }

        Border root = WrapInShell(card, 640, 320);
        root.Styles.Add(Ck("ck-frame", "ClassifierBlue"));
        root.Styles.Add(Ck("ck-tick", "ClassifierGreen"));
        root.Styles.Add(Ck("ck-class", "ClassifierPurple"));
        root.Styles.Add(Ck("ck-proto", "ClassifierOrange"));
        return root;
    }

    /// <summary>
    ///     The real <see cref="BinaryPane" /> over a <see cref="HarvestHexViewModel" /> loaded with 2 KB
    ///     of deterministic bytes and four nested <see cref="HexSpan" />s at Levels 0–3, so all four
    ///     highlight tiers show at once (L0 selected → L3 deep ancestor; lower Level wins on overlap).
    ///     Exercises the v0.6.0 HexSwatchSelected/Parent/Ancestor/AncestorDeep token palette the pane
    ///     resolves on attach (SetPalette), plus the status bar + legend swatches.
    /// </summary>
    private static Border BinaryPaneHex()
    {
        byte[] bytes = new byte[2048];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31);
        }

        HarvestHexViewModel vm = new();
        vm.Load(bytes);
        vm.SetSpans(
        [
            new HexSpan(32, 320, 3, "frame payload"),
            new HexSpan(64, 224, 2, "svc_PacketEntities"),
            new HexSpan(96, 128, 1, "entity_data"),
            new HexSpan(128, 40, 0, "m_vecOrigin cell")
        ]);

        return new Border
        {
            Width = 760,
            Height = 460,
            Background = Tok("PanelBg"),
            Child = new BinaryPane
            {
                DataContext = vm
            }
        };
    }

    /// <summary>
    ///     One <see cref="InspectorCard" /> per <see cref="HarvestCardViewModel" /> category — the
    ///     net/svc/DEM/cs/CLC/GameEvent/default families plus an unknown card — exercising the v0.6.0
    ///     accent strip + cat badge + header wash, all driven by the IsKind* class flags through
    ///     Classifier*/MsgHeader* theme tokens (the old code-held AccentBrush trio is gone).
    /// </summary>
    private static Border MsgCardAccents()
    {
        (string Name, int Size, bool Unknown)[] kinds =
        [
            ("net_Tick", 12, false),
            ("svc_PacketEntities", 800, false),
            ("DEM_Packet", 4000, false),
            ("cs_TMClientHello", 60, false),
            ("CLC_Move", 20, false),
            ("GameEventMessage", 90, false),
            ("msg_Foo", 10, false),
            ("unknown(123)", 44, true)
        ];

        StackPanel col = new()
        {
            Spacing = 6,
            Margin = new Thickness(14)
        };
        foreach ((string name, int size, bool unknown) in kinds)
        {
            col.Children.Add(new InspectorCard
            {
                DataContext = new HarvestCardViewModel(name, size, unknown)
            });
        }

        return WrapInShell(col, 560, 620);
    }

    /// <summary>
    ///     The Analysis tab's determinate evaluation progress: the real
    ///     <see cref="AnalysisTabView" /> over a real <see cref="MainViewModel" /> (its parameterless
    ///     ctor builds the whole Analysis sub-VM) with <c>IsRunning</c> pinned on and
    ///     <c>EvaluationProgress</c> at 42%, so the status-bar ProgressBar + "Evaluating… 42 %" caption
    ///     render. The embedded MSAGL <c>GraphView</c> stays empty (no run) — per CaptureHost's scope
    ///     note it never settles a real graph headlessly, which is fine here: the surface under review
    ///     is the progress strip.
    /// </summary>
    private static AnalysisTabView AnalysisProgress()
    {
        MainViewModel vm = new()
        {
            HasFile = true
        };
        vm.Analysis.IsRunning = true;
        vm.Analysis.EvaluationProgress = 0.42;
        return new AnalysisTabView
        {
            DataContext = vm
        };
    }

    /// <summary>
    ///     The 2D viewport with the Vision overlay ON over the real committed de_dust2 baked bundle
    ///     (<c>assets/de_dust2</c> — radar + collision.tris). A fake module
    ///     context seeds a 2v2 roster around mid; the BVH is built synchronously through the
    ///     <c>LoadVisionEngineSyncForTest</c> seam (InternalsVisibleTo — the production off-thread load
    ///     can't be pumped headlessly, same as ZVisionOverlayRenderTests), each player is then
    ///     ground-snapped via <see cref="VisibilityEngine.RayDownDistance" /> and one Advanced push
    ///     rebuilds the markers — so the FOV cones + could-see sightlines come from REAL map geometry.
    /// </summary>
    private static Border Pb2DVision()
    {
        // T pair (team 2) west of mid facing east; CT pair (team 3) east facing back west. Positions are
        // radar-space mid (bundle transform: posX -2476, posY 3239, scale 4.4 → mid ≈ (-224, 986)); the
        // Z is a placeholder until the ground snap below.
        (string Name, float X, float Y, float Yaw, int Team)[] seed =
        [
            ("b1t", -560, 990, 0, 2),
            ("iM", -440, 1150, -20, 2),
            ("ropz", 60, 980, 180, 3),
            ("Twistzz", -30, 830, 160, 3)
        ];

        VisionModuleContext ctx = new("de_dust2",
            [.. seed.Select((s, i) => new VisionPlayer(i, s.Team, s.Name, s.X, s.Y, 40, s.Yaw))]);

        Playback2DTabViewModel vm = new()
        {
            ShowVision = true
        };
        vm.OnActivated(ctx);

        if (vm.MapAsset?.CollisionTrisPath is null)
        {
            throw new InvalidOperationException(
                "pb2d-vision needs the committed assets/de_dust2 bundle (bundle.json + collision.tris).");
        }

        vm.LoadVisionEngineSyncForTest();
        if (vm.VisionEngine is not { } engine)
        {
            throw new InvalidOperationException("assets/de_dust2/collision.tris failed to load — no vision engine.");
        }

        // Ground-snap each seeded player so eyes/cones sit on the real mid floor instead of a guessed Z.
        foreach (VisionPlayer p in ctx.VisionPlayers)
        {
            if (engine.RayDownDistance(new Vector3(p.X, p.Y, 400f), 800f, out float drop))
            {
                p.Z = 400f - drop + 1f;
            }
        }

        ctx.RaiseAdvanced(); // one push → BuildFrame at the snapped positions

        return new Border
        {
            Width = 720,
            Height = 720,
            Background = Tok("ShellBg"),
            Padding = new Thickness(10),
            Child = new Playback2DViewport
            {
                DataContext = vm
            }
        };
    }

    private static DemoEntry SampleDemo(string map, string players, string file) => new()
    {
        FilePath = "/demos/pro-matches/" + file,
        FileName = file,
        Directory = "/demos/pro-matches",
        FileSizeBytes = 480_000_000,
        Modified = DateTime.Now.AddDays(-3),
        MapName = map,
        ServerName = "BLAST.tv Premier CS2 Server",
        Players = players.Split(',').Select(p => p.Trim()).ToList(),
        DurationSeconds = 3375,
        CtScore = 13,
        TScore = 11,
        State = DemoIndexState.Indexed
    };

    /// <summary>
    ///     The real <see cref="FirstRunWizardView" /> bound to a real <see cref="FirstRunWizardViewModel" />
    ///     over a throwaway temp-dir <see cref="SettingsService" /> — parked on the Category step (index 1),
    ///     the richest content, which also shows the default PowerUser selection. Rendered inside the
    ///     headless UI thread by <c>CaptureHost</c>.
    /// </summary>
    private static FirstRunWizardView Wizard()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "demoviewer-uicapture-wizard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);

        SettingsService svc = new(dir);
        FirstRunWizardViewModel vm = new(svc)
        {
            CurrentStep = 1
        };
        return new FirstRunWizardView
        {
            DataContext = vm
        };
    }

    /// <summary>
    ///     The real <see cref="SettingsView" /> bound to a real <see cref="SettingsViewModel" /> — over a
    ///     throwaway temp-dir <see cref="SettingsService" /> and a real <see cref="FeatureGate" /> — so the
    ///     P2a-ii per-feature toggle list renders exactly as shipped. Two seeded overrides exercise both the
    ///     "overridden" indicator and the clear affordance (one default-off dev sub-feature turned ON, one
    ///     core tab turned OFF). Rendered inside the headless UI thread by <c>CaptureHost</c>.
    /// </summary>
    private static SettingsView Settings(int maxConcurrency = 1)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "demoviewer-uicapture-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);

        SettingsService svc = new(dir);
        svc.Write(s =>
        {
            s.UserCategory = UserCategory.PowerUser;
            s.Library.Folders = ["/demos/aim_botz", "/demos/retakes"];
            s.Features.Overrides["parser.hex"] = true; // dev sub-feature forced ON  → overridden + enabled
            s.Features.Overrides["tab.stats"] = false; // core tab forced OFF        → overridden + disabled
            s.ProcessingQueue.MaxConcurrency = maxConcurrency; // > 1 reveals the RAM-risk warning
        });

        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        ServiceProvider sp = services.BuildServiceProvider();
        IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
        FeatureGate gate = new(monitor);
        SettingsViewModel vm = new(svc, monitor, gate, new ThemeRegistry());
        return new SettingsView
        {
            DataContext = vm
        };
    }

    // Renders the Playback2D HUD DOMAIN accents (health/armor/headshot/…) as text + glyphs on the real
    // Pb2dInfoBg / Pb2dCardBg surfaces — the coherence + text-on-card contrast check for the Light domain-accent
    // values (each designed to clear WCAG AA on the #FBFBFD card). A static mock of the info strip + a player
    // card, not a live VM. Render under both --theme light and dark.
    private static Border Pb2DHudAccents()
    {
        static TextBlock Val(string text, string tok, bool bold = false)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Tok(tok),
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = bold ? 11 : 12,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static TextBlock Glyph(string text, string tok)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Tok(tok),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static TextBlock Lbl(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Tok("Pb2dTextMid"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
        }

        static StackPanel Row(params Control[] kids)
        {
            StackPanel sp = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 3)
            };
            foreach (Control k in kids)
            {
                sp.Children.Add(k);
            }

            return sp;
        }

        // Exercise the REAL production mechanism: no direct Background — the chip gets the `teamChip` class plus
        // `teamT`/`teamCt` (or neither = neutral), and the class STYLES (attached to the root below) resolve the
        // token. If the class selector silently fails, T/CT render neutral gray like the else-case — that's the
        // regression this catches (mirrors Playback2DView's Border.teamChip{,.teamT,.teamCt}).
        static Border Chip(string label, string teamClass)
        {
            Border b = new()
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1),
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Tok("Pb2dTextOnTeam")
                }
            };
            b.Classes.Add("teamChip");
            if (teamClass.Length > 0)
            {
                b.Classes.Add(teamClass);
            }

            return b;
        }

        StackPanel info = new()
        {
            Spacing = 2
        };
        info.Children.Add(Row(Lbl("Teams"), Chip("T", "teamT"), Chip("CT", "teamCt"), Chip("—", "")));
        info.Children.Add(Row(Lbl("Phase"), Val("LIVE", "Pb2dPositive")));
        info.Children.Add(Row(Lbl("Bomb"), Val("PLANTED A", "Pb2dBomb")));
        info.Children.Add(Row(Lbl("Defuse"), Val("6.7s", "Pb2dDefuseTime")));
        Border infoBorder = new()
        {
            Background = Tok("Pb2dInfoBg"),
            Padding = new Thickness(10, 8),
            Child = info
        };

        StackPanel card = new()
        {
            Spacing = 2
        };
        card.Children.Add(Row(Lbl("HP"), Val("100", "Pb2dHealth"),
            Glyph("◈", "Pb2dArmor"), Val("100", "Pb2dArmor"), Glyph("⛨", "Pb2dDefuser")));
        card.Children.Add(Row(Lbl("Cash"), Val("$4200", "Pb2dPositive"), Lbl("ADR"), Val("94.3", "Pb2dAdr")));
        card.Children.Add(Row(Lbl("Weapon"), Val("ak47", "Pb2dPositive"),
            Val("HS", "Pb2dHeadshot", true), Val("WB", "Pb2dWallbang", true),
            Val("NS", "Pb2dNoScope", true), Glyph("⚡", "Pb2dFlashAssist"), Glyph("✱", "Pb2dGlyphBlind"),
            Val("+assist", "Pb2dAssist")));
        card.Children.Add(Row(Lbl("Approx"), Val("A site", "Pb2dMapApprox")));
        Border cardBorder = new()
        {
            Background = Tok("Pb2dCardBg"),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(4),
            Child = card
        };

        StackPanel col = new()
        {
            Spacing = 0,
            Margin = new Thickness(10)
        };
        col.Children.Add(infoBorder);
        col.Children.Add(cardBorder);
        Border root = new()
        {
            Background = Tok("Pb2dPanelBg"),
            Child = col
        };

        // The team-chip class styles — same selectors as Playback2DView, so this render proves the class path.
        static Style ChipStyle(string extraClass, string tok)
        {
            Selector Sel(Selector? s)
            {
                return extraClass.Length > 0
                    ? s.OfType<Border>().Class("teamChip").Class(extraClass)
                    : s.OfType<Border>().Class("teamChip");
            }

            Style style = new(Sel);
            style.Setters.Add(new Setter(Border.BackgroundProperty, Tok(tok)));
            return style;
        }

        root.Styles.Add(ChipStyle("", "Pb2dCanvasNeutral"));
        root.Styles.Add(ChipStyle("teamT", "Pb2dTeamT"));
        root.Styles.Add(ChipStyle("teamCt", "Pb2dTeamCt"));
        return root;
    }

    // ── token / builder helpers ─────────────────────────────────────────────────────────────────
    private static IBrush Tok(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object? r) && r is IBrush b
            ? b
            : Brushes.Magenta;

    /// <summary>A NavStrip-style ghost button (matches the real <c>Button.nav-btn</c> look via tokens).</summary>
    private static Button NavBtn(string content, bool amber = false) => new()
    {
        Content = content,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 2),
        Height = 28,
        MinHeight = 0,
        CornerRadius = new CornerRadius(4),
        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        FontSize = 12,
        Foreground = amber ? Tok("AccentAmber") : Tok("TextMid"),
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static TextBlock GroupLabel(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        FontSize = 9,
        Foreground = Tok("TextDim"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 4, 0)
    };

    private static Rectangle VDiv(bool strong = false) => new()
    {
        Width = 1,
        Height = 18,
        Fill = Tok(strong ? "BorderStrong" : "BorderSubtle"),
        Margin = new Thickness(8, 0)
    };

    private static Border FrameReadoutPill()
    {
        StackPanel inner = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        void Mono(string t, string tok, double fs = 12)
        {
            inner.Children.Add(new TextBlock
            {
                Text = t,
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = fs,
                Foreground = Tok(tok),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        Mono("frame ", "TextDim", 11);
        Mono("0", "TextBright");
        Mono(" / 0", "TextDim");
        inner.Children.Add(new Rectangle
        {
            Width = 1,
            Height = 14,
            Fill = Tok("BorderSubtle"),
            Margin = new Thickness(8, 0)
        });
        Mono("tick 0", "TextMid");
        return new Border
        {
            Background = Tok("CardBg"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 0),
            Height = 26,
            Margin = new Thickness(3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner
        };
    }

    private static StackPanel ClockGroup()
    {
        StackPanel g = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        g.Children.Add(NavBtn("◀"));
        g.Children.Add(FrameReadoutPill());
        g.Children.Add(NavBtn("▶"));
        g.Children.Add(VDiv());
        g.Children.Add(new ToggleButton
        {
            Content = "⏯",
            FontSize = 13,
            Padding = new Thickness(9, 2),
            Height = 28,
            MinHeight = 0
        });
        g.Children.Add(new ComboBox
        {
            MinWidth = 58,
            FontSize = 11,
            Height = 28,
            MinHeight = 0,
            SelectedIndex = 0,
            ItemsSource = _navSpeeds
        });
        return g;
    }

    private static StackPanel JumpGroup()
    {
        StackPanel g = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        g.Children.Add(GroupLabel("JUMP"));
        foreach (string s in _jumpButtons)
        {
            g.Children.Add(NavBtn(s));
        }

        return g;
    }

    /// <summary>
    ///     PROPOSED responsive NavStrip: CLOCK + JUMP left, the dev-only BREAKPOINT cluster collapses
    ///     into a right-docked overflow <c>▾</c> (amber, gated). Nothing clips at narrow width because
    ///     the overflow holds the trailing group instead of a fixed horizontal StackPanel running off
    ///     the right edge.
    /// </summary>
    private static Border NavStripProposed()
    {
        // Right-docked overflow button standing in for the collapsed dev breakpoint cluster.
        Button overflow = NavBtn("▶▶ ▾", true);
        ToolTip.SetTip(overflow, "Breakpoint step controls (dev) — collapsed into overflow at narrow width");
        DockPanel.SetDock(overflow, Dock.Right);

        DockPanel dock = new()
        {
            LastChildFill = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        dock.Children.Add(overflow); // right-docked first
        StackPanel left = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        left.Children.Add(ClockGroup());
        left.Children.Add(VDiv(true));
        left.Children.Add(JumpGroup());
        dock.Children.Add(left);

        return new Border
        {
            Background = Tok("PanelHeaderBg"),
            BorderBrush = Tok("BorderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 3),
            Child = dock
        };
    }

    /// <summary>
    ///     The global toolbar row. CURRENT crams Open Demo + Parse Chain + Debugger + Output + Bookmark
    ///     + Bookmarks (dev chrome on every tab). PROPOSED keeps a compact Open + Bookmark(s) and folds
    ///     the dev toggles (Debugger / Output / Parse Chain) into a right-aligned <c>View ▾</c> overflow
    ///     (in-window flyout — WASM-safe, not a native menu), with an "N features hidden" hint.
    /// </summary>
    private static Border Toolbar(bool proposed)
    {
        static Button Chip(string content)
        {
            return new Button
            {
                Content = content,
                FontSize = 11,
                Padding = new Thickness(8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!proposed)
        {
            row.Children.Add(new Button
            {
                Content = "Open Demo…",
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(Chip("Parse Chain"));
            row.Children.Add(new ToggleButton
            {
                Content = "Debugger",
                FontSize = 11,
                Padding = new Thickness(8, 3)
            });
            row.Children.Add(new ToggleButton
            {
                Content = "Output",
                FontSize = 11,
                Padding = new Thickness(8, 3),
                IsChecked = true
            });
            row.Children.Add(Chip("★ Bookmark"));
            row.Children.Add(Chip("Bookmarks ▾"));
            row.VerticalAlignment = VerticalAlignment.Top;
            return new Border
            {
                Background = Tok("ShellBg"),
                Padding = new Thickness(6, 8),
                Child = row
            };
        }

        // proposed: compact primary + bookmarks, dev chrome behind a View overflow
        row.Children.Add(new Button
        {
            Content = "📂 Open Demo",
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(Chip("★ Bookmark"));
        row.Children.Add(Chip("Bookmarks ▾"));

        Button viewBtn = Chip("View ▾");
        StackPanel overflow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(overflow, Dock.Right);
        overflow.Children.Add(new TextBlock
        {
            Text = "2 features hidden",
            FontSize = 10,
            Foreground = Tok("TextDim"),
            VerticalAlignment = VerticalAlignment.Center
        });
        overflow.Children.Add(viewBtn);

        DockPanel dock = new()
        {
            LastChildFill = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        dock.Children.Add(overflow);
        dock.Children.Add(row);

        // annotate what the View ▾ flyout holds, so the render shows the moved items
        Border flyoutHint = new()
        {
            Background = Tok("CardBg"),
            BorderBrush = Tok("BorderAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = "View ▾  (gated: power/dev)",
                        FontSize = 10,
                        Foreground = Tok("TextDim")
                    },
                    new TextBlock
                    {
                        Text = "☑ Debugger    ☑ Output    ☐ Parse Chain",
                        FontSize = 11,
                        Foreground = Tok("TextMid"),
                        FontFamily = new FontFamily("Consolas,Menlo,monospace")
                    }
                }
            }
        };

        StackPanel col = new()
        {
            Spacing = 0
        };
        col.Children.Add(new Border
        {
            Child = dock,
            Height = 44,
            Padding = new Thickness(6, 4)
        });
        col.Children.Add(flyoutHint);
        return new Border
        {
            Background = Tok("ShellBg"),
            Child = col
        };
    }

    /// <summary>
    ///     The no-demo landing state. CURRENT is the bare Parser prompt ("Open a .dem file to begin").
    ///     PROPOSED is a consumer-first welcome card: a large primary Open Demo, recent files, and a
    ///     drop hint — the single primary entry (the compact toolbar entry is the persistent secondary).
    /// </summary>
    private static Border Welcome(bool proposed)
    {
        if (!proposed)
        {
            TextBlock prompt = new()
            {
                Text = "Open a .dem file to begin",
                FontSize = 13,
                Foreground = Tok("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return WrapInShell(prompt, 460, 460);
        }

        StackPanel card = new()
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 360
        };
        card.Children.Add(new TextBlock
        {
            Text = "DemoViewer.NET",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tok("TextValue"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        card.Children.Add(new TextBlock
        {
            Text = "Open a CS2 demo to explore it.",
            FontSize = 12,
            Foreground = Tok("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        card.Children.Add(new Button
        {
            Content = "📂  Open Demo…",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(22, 10),
            FontSize = 15
        });

        StackPanel recent = new()
        {
            Spacing = 4
        };
        recent.Children.Add(new TextBlock
        {
            Text = "RECENT",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tok("TextLabel"),
            Margin = new Thickness(2, 6, 0, 2)
        });
        foreach ((string name, string meta) in _recentDemos)
        {
            Border rowChild = new()
            {
                Background = Tok("CardBg"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = name,
                            FontSize = 12,
                            Foreground = Tok("TextBright"),
                            FontFamily = new FontFamily("Consolas,Menlo,monospace")
                        },
                        new TextBlock
                        {
                            Text = meta,
                            FontSize = 10,
                            Foreground = Tok("TextDim")
                        }
                    }
                }
            };
            recent.Children.Add(rowChild);
        }

        card.Children.Add(recent);
        card.Children.Add(new TextBlock
        {
            Text = "…or drop a .dem file here",
            FontSize = 11,
            Foreground = Tok("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        });
        return WrapInShell(card, 460, 460);
    }

    /// <summary>
    ///     Annotated static map of the three breakpoint surfaces (not an A/B; the Analysis graph surface
    ///     is not headlessly renderable). Communicates the coherence model: act / manage / rule-graph.
    /// </summary>
    private static Border BreakpointsMap()
    {
        static Border Panel(string title, string audience, string job, string tok)
        {
            return new Border
            {
                Background = Tok("CardBg"),
                BorderBrush = Tok(tok),
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Tok("TextCardHeader")
                        },
                        new TextBlock
                        {
                            Text = job,
                            FontSize = 12,
                            Foreground = Tok("TextMid"),
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = audience,
                            FontSize = 10,
                            Foreground = Tok("TextDim"),
                            FontFamily = new FontFamily("Consolas,Menlo,monospace")
                        }
                    }
                }
            };
        }

        StackPanel col = new()
        {
            Spacing = 0,
            Margin = new Thickness(18)
        };
        col.Children.Add(new TextBlock
        {
            Text = "Three breakpoint surfaces — one coherent model",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tok("TextValue"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        col.Children.Add(Panel("① NavStrip  ‘TO BREAKPOINT’  (amber)", "chrome · dev / power", "ACTION — continue / step-tick / step-round while navigating.", "AccentAmber"));
        col.Children.Add(Panel("② Debugger panel  (SplitView rail)", "panel · dev / power", "MANAGEMENT — add / list / enable / delete frame·tick·event breakpoints; ‘stopped at’ + Continue.", "BorderAccent"));
        col.Children.Add(Panel("③ Analysis graph breakpoints", "in-tab · dev only  (NOT headless-renderable — MSAGL)", "DISTINCT DOMAIN — breakpoints on rule-graph nodes/edges; own list + conditional editor.", "AccentHighlight"));
        return new Border
        {
            Background = Tok("ShellBg"),
            Width = 560,
            Height = 420,
            Child = col
        };
    }

    /// <summary>
    ///     The REAL <see cref="NavStrip" /> control bound to a mock <see cref="MainViewModel" />
    ///     (HasFile=true so the strip renders; all three ctor params are optional). Proves real
    ///     App controls capture headlessly. Render narrow (~880px) to expose the crowding/overflow
    ///     that motivates the responsive redesign — at 1600px the strip has room and the problem hides.
    /// </summary>
    private static NavStrip NavStripReal()
    {
        MainViewModel vm = new()
        {
            HasFile = true
        };
        return new NavStrip
        {
            DataContext = vm
        };
    }

    /// <summary>
    ///     The REAL production <see cref="NavStrip" /> (post SEEK consolidation) with a concrete
    ///     event target picked, so the target chip renders a real value + its MaxWidth truncation rather
    ///     than the default "Any event". Seeds the demo-derived <c>GameEventFilters</c> with one enabled
    ///     type; a long name is included (disabled) to keep the checklist realistic.
    /// </summary>
    private static NavStrip NavStripRealTarget()
    {
        MainViewModel vm = new()
        {
            HasFile = true
        };
        // Clear the ctor's default seed so exactly one target reads on the chip (not "N events").
        vm.GameEventFilters.Clear();
        vm.GameEventFilters.Add(new GameEventFilterItem("player_death"));
        vm.GameEventFilters.Add(new GameEventFilterItem("bomb_planted")
        {
            IsEnabled = false
        });
        vm.GameEventFilters.Add(new GameEventFilterItem("round_officially_ended")
        {
            IsEnabled = false
        });
        return new NavStrip
        {
            DataContext = vm
        };
    }

    /// <summary>A button carrying design-system style classes (picks up Styles/Primitives.axaml).</summary>
    private static Button Classed(string content, params string[] classes)
    {
        Button b = new()
        {
            Content = content,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (string c in classes)
        {
            b.Classes.Add(c);
        }

        return b;
    }

    /// <summary>
    ///     App-themed primitive controls under the P1.3 design-system style classes. Exercises every
    ///     Primitives.axaml Button/ToggleButton/TabItem/TextBox/ComboBox class so the render verifies the
    ///     actual look (spacing, contrast, hover-neutral state) against the shell background.
    /// </summary>
    private static Border Primitives()
    {
        StackPanel col = new()
        {
            Spacing = 10,
            Margin = new Thickness(20)
        };

        col.Children.Add(Section("Buttons — .primary / .ghost / .chip"));
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttons.Children.Add(Classed("Add", "primary"));
        buttons.Children.Add(Classed("Cancel", "ghost"));
        buttons.Children.Add(Classed("Parse Chain", "chip"));
        buttons.Children.Add(new ToggleButton
        {
            Content = "Debugger",
            Classes =
            {
                "chip"
            }
        });
        buttons.Children.Add(new ToggleButton
        {
            Content = "Output",
            IsChecked = true,
            Classes =
            {
                "chip"
            }
        });
        buttons.Children.Add(new Button
        {
            Content = "Fluent (unclassed)",
            VerticalAlignment = VerticalAlignment.Center
        });
        col.Children.Add(buttons);

        col.Children.Add(Section("Nav buttons — .nav-btn / .bp-btn / .icon-btn"));
        StackPanel navs = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        navs.Children.Add(Classed("◀", "nav-btn"));
        navs.Children.Add(Classed("▶", "nav-btn"));
        navs.Children.Add(Classed("▶▶", "nav-btn", "bp-btn"));
        navs.Children.Add(Classed("▶|", "nav-btn", "bp-btn"));
        navs.Children.Add(new Rectangle
        {
            Width = 1,
            Height = 18,
            Fill = Tok("BorderStrong"),
            Margin = new Thickness(6, 0),
            Classes =
            {
                "divider",
                "strong"
            }
        });
        navs.Children.Add(Classed("●", "icon-btn"));
        navs.Children.Add(Classed("✕", "icon-btn"));
        col.Children.Add(navs);

        col.Children.Add(Section("Inputs — .field / .mono (Fluent chrome kept)"));
        StackPanel inputs = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        inputs.Children.Add(new TextBox
        {
            Watermark = "value (e.g. 37)",
            Width = 160,
            Classes =
            {
                "field"
            }
        });
        inputs.Children.Add(new ComboBox
        {
            Width = 120,
            SelectedIndex = 0,
            ItemsSource = _speedOptions,
            Classes =
            {
                "field"
            }
        });
        inputs.Children.Add(new TextBlock
        {
            Text = "de_mirage · tick 12480",
            Classes =
            {
                "mono"
            },
            Foreground = Tok("TextValue"),
            VerticalAlignment = VerticalAlignment.Center
        });
        col.Children.Add(inputs);

        col.Children.Add(Section("Tabs — TabItem.shell-tab"));
        TabControl tabs = new()
        {
            Height = 40
        };
        foreach (string h in new[]
                 {
                     "Library", "Stats", "2D Playback", "Parser"
                 })
        {
            tabs.Items.Add(new TabItem
            {
                Header = h,
                Classes =
                {
                    "shell-tab"
                }
            });
        }

        tabs.SelectedIndex = 0;
        col.Children.Add(tabs);

        return WrapInShell(col, 680, 360);
    }

    /// <summary>
    ///     Shell/section chrome + card surfaces: Styles/Chrome.axaml (section header, group label, badge,
    ///     divider) and Styles/Cards.axaml (.card, .card-flyout with .ctx-action rows).
    /// </summary>
    private static Border Chrome()
    {
        StackPanel col = new()
        {
            Spacing = 0
        };

        // Section header band (Border.sectionHeader + TextBlock.sectionLabel) with a count badge.
        Border header = new()
        {
            Classes =
            {
                "sectionHeader"
            }
        };
        Grid hgrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        TextBlock hlabel = new()
        {
            Text = "DEBUGGER",
            Classes =
            {
                "sectionLabel"
            },
            VerticalAlignment = VerticalAlignment.Center
        };
        Border badge = new()
        {
            Classes =
            {
                "badge"
            },
            HorizontalAlignment = HorizontalAlignment.Right
        };
        badge.Child = new TextBlock
        {
            Text = "3 bp",
            Classes =
            {
                "mono"
            },
            FontSize = 10,
            Foreground = Tok("TextChainBadge")
        };
        Grid.SetColumn(hlabel, 0);
        Grid.SetColumn(badge, 2);
        hgrid.Children.Add(hlabel);
        hgrid.Children.Add(badge);
        header.Child = hgrid;
        col.Children.Add(header);

        StackPanel body = new()
        {
            Spacing = 14,
            Margin = new Thickness(16)
        };

        // Group label + divider (as used in the NavStrip groups).
        StackPanel groupRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        groupRow.Children.Add(new TextBlock
        {
            Text = "JUMP",
            Classes =
            {
                "group-label"
            }
        });
        groupRow.Children.Add(Classed("◀ ev", "nav-btn"));
        groupRow.Children.Add(Classed("ev ▶", "nav-btn"));
        groupRow.Children.Add(new Rectangle
        {
            Width = 1,
            Height = 18,
            Classes =
            {
                "divider"
            }
        });
        groupRow.Children.Add(new TextBlock
        {
            Text = "TO BREAKPOINT",
            Classes =
            {
                "group-label"
            }
        });
        groupRow.Children.Add(Classed("▶▶", "nav-btn", "bp-btn"));
        body.Children.Add(groupRow);

        // Border.card — a raised content tile, with a Border.badge count pill in its header row.
        Border card = new()
        {
            Classes =
            {
                "card"
            },
            Padding = new Thickness(14, 12)
        };
        Border cardBadge = new()
        {
            Classes =
            {
                "badge"
            }
        };
        cardBadge.Child = new TextBlock
        {
            Text = "12 events",
            Classes =
            {
                "mono"
            },
            FontSize = 10,
            Foreground = Tok("TextChainBadge")
        };
        StackPanel cardHead = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        cardHead.Children.Add(new TextBlock
        {
            Text = "Border.card",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tok("TextCardHeader"),
            VerticalAlignment = VerticalAlignment.Center
        });
        cardHead.Children.Add(cardBadge);
        card.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                cardHead,
                new TextBlock
                {
                    Text = "CardBg + BorderAccent, radius 8 — the general raised surface. The pill is Border.badge.",
                    FontSize = 11,
                    Foreground = Tok("TextMid"),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        body.Children.Add(card);

        // Border.card-flyout — a popup surface holding .ctx-action rows.
        Border flyout = new()
        {
            Classes =
            {
                "card-flyout"
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200
        };
        flyout.Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Classed("Select all", "ctx-action"),
                Classed("Deselect all", "ctx-action"),
                new Rectangle
                {
                    Height = 1,
                    Classes =
                    {
                        "divider"
                    },
                    Margin = new Thickness(0, 2)
                },
                Classed("Jump to round start", "ctx-action")
            }
        };
        body.Children.Add(new TextBlock
        {
            Text = "Border.card-flyout + Button.ctx-action",
            Classes =
            {
                "group-label"
            }
        });
        body.Children.Add(flyout);

        col.Children.Add(body);
        // Render on PanelBg (not ShellBg) so the sectionHeader band reads with the same context it has
        // in-app — its PanelHeaderBg fill + bottom BorderStrong rule sit over panel content, not the void.
        return new Border
        {
            Width = 520,
            Height = 430,
            Background = Tok("PanelBg"),
            Child = col
        };
    }

    /// <summary>
    ///     List/table primitives (Styles/Tables.axaml — .data-list + .col-label) plus a regression check
    ///     that the new global styles don't disturb the real <see cref="InspectorCard" /> shell and
    ///     <see cref="KeyValueTable" />.
    /// </summary>
    private static Border TablesAndCards()
    {
        StackPanel col = new()
        {
            Spacing = 10,
            Margin = new Thickness(16)
        };

        col.Children.Add(Section("ListBox.data-list + TextBlock.col-label"));

        // Column-header row using .col-label, above a transparent borderless .data-list.
        Grid headerRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("2*,*,Auto"),
            Margin = new Thickness(10, 0, 10, 2)
        };

        void Col(string t, int c, HorizontalAlignment ha = HorizontalAlignment.Left)
        {
            TextBlock tb = new()
            {
                Text = t,
                Classes =
                {
                    "col-label"
                },
                HorizontalAlignment = ha
            };
            Grid.SetColumn(tb, c);
            headerRow.Children.Add(tb);
        }

        Col("EVENT", 0);
        Col("TICK", 1);
        Col("×", 2, HorizontalAlignment.Right);
        col.Children.Add(headerRow);

        ListBox list = new()
        {
            Classes =
            {
                "data-list"
            },
            Height = 120,
            SelectedIndex = 1
        };
        foreach ((string ev, string tick, string hits) in _dataRows)
        {
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("2*,*,Auto")
            };
            TextBlock a = new()
            {
                Text = ev,
                Classes =
                {
                    "mono"
                },
                FontSize = 11,
                Foreground = Tok("TextBright")
            };
            TextBlock b = new()
            {
                Text = tick,
                Classes =
                {
                    "mono"
                },
                FontSize = 11,
                Foreground = Tok("TextFrameInfo")
            };
            TextBlock c2 = new()
            {
                Text = hits,
                Classes =
                {
                    "mono"
                },
                FontSize = 11,
                Foreground = Tok("TextHeaderField"),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(a, 0);
            Grid.SetColumn(b, 1);
            Grid.SetColumn(c2, 2);
            row.Children.Add(a);
            row.Children.Add(b);
            row.Children.Add(c2);
            list.Items.Add(row);
        }

        col.Children.Add(list);

        col.Children.Add(Section("Regression — real KeyValueTable"));
        col.Children.Add(new KeyValueTable
        {
            Height = 74,
            Rows = new List<KvpRow>
            {
                new("m_iHealth", "90", true, "100"),
                new("m_iAccount", "3250", false, null),
                new("m_ArmorValue", "100", false, null)
            }
        });

        col.Children.Add(Section("Regression — real InspectorCard header"));
        col.Children.Add(new InspectorCard
        {
            DataContext = new HarvestCardViewModel("CNETMsg_Tick", 24)
        });

        return WrapInShell(col, 540, 560);
    }

    /// <summary>The DarkPalette semantic tokens as labeled swatches — reference for the design system.</summary>
    private static Border Swatches()
    {
        string[] keys =
        [
            "ShellBg", "PanelBg", "CardBg", "PanelHeaderBg",
            "BorderSubtle", "BorderStrong", "BorderAccent",
            "AccentInteractive", "AccentHighlight", "AccentAmber", "AccentError", "StatPositive",
            "TextValue", "TextCardHeader", "TextDim"
        ];

        WrapPanel wrap = new()
        {
            Margin = new Thickness(16)
        };
        Application app = Application.Current!;
        foreach (string key in keys)
        {
            IBrush? brush = app.TryGetResource(key, app.ActualThemeVariant, out object? res) && res is IBrush b ? b : null;
            StackPanel cell = new()
            {
                Width = 150,
                Margin = new Thickness(6),
                Spacing = 4
            };
            cell.Children.Add(new Border
            {
                Height = 44,
                Background = brush ?? Brushes.Magenta,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x60))
            });
            cell.Children.Add(new TextBlock
            {
                Text = key,
                FontSize = 11,
                Opacity = brush is null ? 0.4 : 0.9
            });
            wrap.Children.Add(cell);
        }

        return WrapInShell(wrap, 520, 300);
    }

    private static TextBlock Section(string text) =>
        new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Classes =
            {
                "sectionLabel"
            }
        };

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //  First-run Visual Walkthrough (coach-mark tour) — the real TutorialView overlaid on a COARSE
    //  app-like backdrop (recognizable regions, not a full app mock) so the spotlight hole frames
    //  something. Render at 1280x800; the seeded SpotlightRects are authored for that size (the bottom
    //  transport strip is Dock=Bottom, so its rect tracks window height — keep size + rects in sync).
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    private enum TutorialMock
    {
        Welcome, // Default[0] — centred welcome card (no spotlight)
        TabNav, // Default[1] — the workspace tab strip
        Library, // Default[2] — the Library landing tab
        Waiting, // Default[3] — the open-demo gateway, parked in its IsWaiting state
        Transport // Default[6] — the NavStrip transport cluster
    }

    // Region rects (overlay == backdrop coords, both fill the window from 0,0 at 1280x800).
    private static readonly Rect _tabNavRect = new(6, 4, 384, 36); // union of the tab headers
    private static readonly Rect _libraryTabRect = new(8, 5, 96, 34); // the "Library" tab header
    private static readonly Rect _openDemoRect = new(1136, 6, 120, 32); // top-right "Open Demo" button
    private static readonly Rect _firstCardRect = new(24, 68, 231, 147); // the first Library demo card
    private static readonly Rect _transportRect = new(10, 758, 330, 34); // bottom transport strip

    // pulsePhase != null → pin the spotlight to a static breathing phase (0 dim … 1 bright) for review;
    // null → the live breathing animation (settles near its bright end under the headless render pump).
    private static Panel Tutorial(TutorialMock mock, double? pulsePhase = null)
    {
        (TutorialStep step, int index, Rect rect) = mock switch
        {
            TutorialMock.TabNav => (TutorialSteps.Default[1], 2, _tabNavRect),
            TutorialMock.Library => (TutorialSteps.Default[2], 3, _libraryTabRect),
            TutorialMock.Waiting => (TutorialSteps.Default[3], 4, _firstCardRect),
            TutorialMock.Transport => (TutorialSteps.Default[6], 7, _transportRect),
            _ => (TutorialSteps.Default[0], 1, default(Rect))
        };

        bool waiting = mock == TutorialMock.Waiting;
        TutorialViewModel vm = new()
        {
            IsActive = true,
            StepCount = TutorialSteps.Default.Count,
            StepNumber = index,
            CurrentStep = step,
            NextLabel = step.NextLabelOverride ?? "Next",
            CanGoBack = index > 1,
            CanGoNext = true,
            SpotlightRect = rect,
            IsWaiting = waiting,
            ActiveTarget = waiting ? TutorialTarget.FirstLibraryCard : step.Target,
            // Runtime the controller swaps in the card hint when the gateway points at a library card; mirror
            // that here so the capture matches what the user sees.
            WaitingHint = waiting
                ? "Double-click a demo below to open it — the tour will pick back up automatically."
                : string.Empty
        };

        // Set AnimatePulse BEFORE the DataContext so the code-behind's DataContextChanged handler applies
        // the correct (animated vs pinned) pulse state on first pass; then pin the static phase if asked.
        TutorialView view = new()
        {
            AnimatePulse = pulsePhase is null
        };
        view.DataContext = vm;
        if (pulsePhase is double p)
        {
            view.SetStaticPulse(p);
        }

        Panel root = new();
        root.Children.Add(TutorialBackdrop(mock));
        root.Children.Add(view);
        return root;
    }

    // Coarse recognizable app chrome behind the overlay: tab strip (top), content, transport (bottom).
    private static Border TutorialBackdrop(TutorialMock mock)
    {
        DockPanel dock = new();

        // ── Tab strip (top) ──
        StackPanel tabs = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        string[] tabNames = ["Library", "Stats", "2D Playback", "Parser"];
        int active = mock == TutorialMock.Transport ? 2 : 0;
        DockPanel.SetDock(tabs, Dock.Left);
        for (int i = 0; i < tabNames.Length; i++)
        {
            tabs.Children.Add(new Border
            {
                Padding = new Thickness(16, 6),
                CornerRadius = new CornerRadius(4),
                Background = i == active ? Tok("CardBg") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = tabNames[i],
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    FontSize = 13,
                    Foreground = Tok(i == active ? "TextValue" : "TextDim")
                }
            });
        }

        // Right-docked "Open Demo" toolbar affordance — the target the open-demo gateway (Waiting) step
        // spotlights (_openDemoRect). Present in every mock for a realistic toolbar.
        Button openDemo = new()
        {
            Classes = { "primary" },
            Content = "Open Demo",
            FontSize = 12,
            Padding = new Thickness(14, 5),
            Height = 30,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        DockPanel.SetDock(openDemo, Dock.Right);

        DockPanel tabBar = new();
        tabBar.Children.Add(openDemo);
        tabBar.Children.Add(tabs);

        Border tabStrip = new()
        {
            Background = Tok("PanelHeaderBg"),
            BorderBrush = Tok("BorderStrong"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 44,
            Child = tabBar
        };
        DockPanel.SetDock(tabStrip, Dock.Top);
        dock.Children.Add(tabStrip);

        // ── Transport strip (bottom) ──
        StackPanel transport = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        transport.Children.Add(NavBtn("◀"));
        transport.Children.Add(new ToggleButton
        {
            Content = "⏯",
            FontSize = 14,
            Padding = new Thickness(9, 2),
            Height = 28,
            MinHeight = 0
        });
        transport.Children.Add(NavBtn("▶"));
        transport.Children.Add(new ComboBox
        {
            MinWidth = 58,
            FontSize = 11,
            Height = 28,
            MinHeight = 0,
            SelectedIndex = 0,
            ItemsSource = _navSpeeds
        });
        transport.Children.Add(new Border
        {
            Width = 150,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Background = Tok("BorderStrong")
        });

        Border transportStrip = new()
        {
            Background = Tok("PanelHeaderBg"),
            BorderBrush = Tok("BorderStrong"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 48,
            Child = transport
        };
        DockPanel.SetDock(transportStrip, Dock.Bottom);
        dock.Children.Add(transportStrip);

        // ── Content (fills) ──
        dock.Children.Add(TutorialContent(mock));

        return new Border
        {
            Background = Tok("ShellBg"),
            Child = dock
        };
    }

    private static Control TutorialContent(TutorialMock mock)
    {
        // Library / TabNav / Waiting all sit on the Library landing (cards); Transport shows the map
        // viewport; Welcome is a plain shell.
        if (mock is TutorialMock.Library or TutorialMock.TabNav or TutorialMock.Waiting)
        {
            WrapPanel cards = new()
            {
                Margin = new Thickness(24, 22),
                Orientation = Orientation.Horizontal
            };
            (string Map, string Players)[] demos =
            [
                ("de_mirage", "ZywOo · apEX · flameZ"),
                ("de_nuke", "device · stavn · jabbi"),
                ("de_dust2", "s1mple · b1t · Aleksib"),
                ("de_anubis", "Twistzz · NAF · ropz")
            ];
            foreach ((string map, string players) in demos)
            {
                cards.Children.Add(new Border
                {
                    Width = 232,
                    Height = 150,
                    Margin = new Thickness(0, 0, 18, 18),
                    CornerRadius = new CornerRadius(8),
                    Background = Tok("CardBg"),
                    BorderBrush = Tok("BorderAccent"),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Margin = new Thickness(14, 12),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = map,
                                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                                FontSize = 15,
                                Foreground = Tok("TextValue")
                            },
                            new TextBlock
                            {
                                Text = players,
                                FontSize = 11,
                                Foreground = Tok("TextDim")
                            }
                        }
                    }
                });
            }

            return cards;
        }

        // Transport / playback mock: a large "map viewport" placeholder.
        Border viewport = new()
        {
            Margin = new Thickness(24, 22),
            CornerRadius = new CornerRadius(8),
            Background = Tok("PanelBg"),
            BorderBrush = Tok("BorderSubtle"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "de_mirage — 2D playback",
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = 14,
                Foreground = Tok("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        return mock == TutorialMock.Welcome
            ? new Border
            {
                Background = Tok("ShellBg")
            }
            : viewport;
    }

    // Give a variant the app's shell background so captures read against the real surface color.
    private static Border WrapInShell(Control body, double w, double h)
    {
        IBrush shell = Application.Current!.TryGetResource(
            "ShellBg", Application.Current.ActualThemeVariant, out object? res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x24));
        return new Border
        {
            Width = w,
            Height = h,
            Background = shell,
            Child = body
        };
    }

    /// <summary>A filled vector glyph, scaled uniform to <paramref name="size" />, tinted <paramref name="brush" />.</summary>
    private static PathIcon Icon(string data, double size, IBrush brush) => new()
    {
        Data = Geometry.Parse(data),
        Width = size,
        Height = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    /// <summary>
    ///     Glyph legibility probe (a de-risk step): renders every redesign icon large on the
    ///     CardBg tile so the even-odd ring, the hand-built chevrons, and the flag/tick markers can be
    ///     read before they are shrunk into the 28px nav buttons.
    /// </summary>
    private static Border NavIconProbe()
    {
        (string Name, string Data)[] glyphs =
        [
            ("tri-left", GeoTriLeft), ("tri-right", GeoTriRight),
            ("step-back", GeoStepBack), ("step-fwd", GeoStepFwd),
            ("play", GeoPlay), ("pause", GeoPause), ("fast-fwd", GeoFastFwd),
            ("chevron-L", GeoChevronLeft), ("chevron-R", GeoChevronRight),
            ("flag · event", GeoFlag), ("ring · round", GeoRing), ("tick", GeoTick)
        ];

        WrapPanel wrap = new()
        {
            Margin = new Thickness(18)
        };
        foreach ((string name, string data) in glyphs)
        {
            StackPanel cell = new()
            {
                Width = 108,
                Spacing = 6,
                Margin = new Thickness(6)
            };
            cell.Children.Add(new Border
            {
                Width = 64,
                Height = 64,
                Background = Tok("CardBg"),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = Icon(data, 40, Tok("TextValue"))
            });
            cell.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = Tok("TextMid"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            wrap.Children.Add(cell);
        }

        return WrapInShell(wrap, 720, 320);
    }

    // ── small builders shared by the redesign strips ────────────────────────────────────────────
    private static StackPanel HRow(double spacing, params Control[] kids)
    {
        StackPanel s = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (Control k in kids)
        {
            s.Children.Add(k);
        }

        return s;
    }

    /// <summary>A ghost (transparent, borderless) nav button wrapping arbitrary icon content + a tooltip.</summary>
    private static Button GhostBtn(Control content, string tip, double minWidth = 30)
    {
        Button b = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 2),
            Height = 30,
            MinHeight = 0,
            MinWidth = minWidth,
            CornerRadius = new CornerRadius(5),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>The shared CLOCK group (media transport): step-back · frame pill · step-fwd · play · speed.</summary>
    private static StackPanel ClockGroupRedesign()
    {
        StackPanel g = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        g.Children.Add(GhostBtn(Icon(GeoStepBack, 15, Tok("TextMid")), "Previous frame"));
        g.Children.Add(FrameReadoutPill());
        g.Children.Add(GhostBtn(Icon(GeoStepFwd, 15, Tok("TextMid")), "Next frame"));
        g.Children.Add(VDiv());
        g.Children.Add(new ToggleButton
        {
            Content = Icon(GeoPlay, 14, Tok("TextMid")),
            Padding = new Thickness(10, 2),
            Height = 30,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        });
        g.Children.Add(new ComboBox
        {
            MinWidth = 62,
            FontSize = 11,
            Height = 30,
            MinHeight = 0,
            SelectedIndex = 2,
            ItemsSource = _navSpeedsX,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return g;
    }

    /// <summary>The shared dev-only TO-BREAKPOINT cluster (amber), matching the feature-gated right-dock.</summary>
    private static StackPanel BreakpointGroupRedesign()
    {
        IBrush amber = Tok("AccentAmber");
        StackPanel g = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        g.Children.Add(new Rectangle
        {
            Width = 1,
            Height = 18,
            Fill = Tok("BorderStrong"),
            Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        g.Children.Add(GroupLabel("TO BREAKPOINT"));
        g.Children.Add(GhostBtn(Icon(GeoFastFwd, 15, amber), "Continue — run forward until a breakpoint trips (or end of demo)"));
        g.Children.Add(GhostBtn(HRow(1, Icon(GeoTriRight, 11, amber), Icon(GeoTick, 14, amber)),
            "Step Tick — advance to the next tick or breakpoint", 34));
        g.Children.Add(GhostBtn(HRow(1, Icon(GeoTriRight, 11, amber), Icon(GeoRing, 14, amber)),
            "Step Round — advance to the next round boundary or breakpoint", 34));
        return g;
    }

    /// <summary>Builds the JUMP group for the given treatment (the one variable the three options differ by).</summary>
    private static StackPanel BuildJumpGroup(JumpVariant v)
    {
        IBrush chevron = Tok("TextFrameInfo"); // brighter than TextDim so direction reads (esp. in A)
        IBrush marker = Tok("TextFieldName");
        IBrush word = Tok("TextMid");

        StackPanel g = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        g.Children.Add(GroupLabel("JUMP"));

        if (v == JumpVariant.IconOnly)
        {
            // A — palindrome order (◀event ◀round ◀tick | tick▶ round▶ event▶), icon-only. A centre
            // divider separates the "rewind" cluster from the "forward" cluster so the middle two
            // tick buttons don't read as icon-soup.
            foreach ((string geo, string _, string prevTip, string _) in _jumpTargets)
            {
                g.Children.Add(GhostBtn(HRow(1, Icon(GeoChevronLeft, 12, chevron), Icon(geo, 16, marker)), prevTip, 34));
            }

            g.Children.Add(new Rectangle
            {
                Width = 1,
                Height = 15,
                Fill = Tok("BorderSubtle"),
                Margin = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            for (int i = _jumpTargets.Length - 1; i >= 0; i--)
            {
                (string geo, string _, string _, string nextTip) = _jumpTargets[i];
                g.Children.Add(GhostBtn(HRow(1, Icon(geo, 16, marker), Icon(GeoChevronRight, 12, chevron)), nextTip, 34));
            }

            return g;
        }

        // B / C — grouped by target (Event · Round · Tick), each a prev/next cluster around a labeled marker.
        bool pill = v == JumpVariant.SegmentedPill;
        for (int i = 0; i < _jumpTargets.Length; i++)
        {
            (string geo, string wordText, string prevTip, string nextTip) = _jumpTargets[i];
            g.Children.Add(TargetCluster(geo, wordText, prevTip, nextTip, chevron, marker, word, pill));
            if (!pill && i < _jumpTargets.Length - 1)
            {
                g.Children.Add(new Rectangle
                {
                    Width = 1,
                    Height = 16,
                    Fill = Tok("BorderSubtle"),
                    Margin = new Thickness(6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        return g;
    }

    /// <summary>
    ///     One target cluster: <c>◀ | ⚑ Event | ▶</c>. Borderless (B) or a bordered segmented pill (C).
    ///     The marker + word is a static caption; the flanking chevrons are the prev/next buttons.
    /// </summary>
    private static Control TargetCluster(
        string geo, string wordText, string prevTip, string nextTip,
        IBrush chevron, IBrush marker, IBrush word, bool pill)
    {
        Button prev = GhostBtn(Icon(GeoChevronLeft, 12, chevron), prevTip, 26);
        Button next = GhostBtn(Icon(GeoChevronRight, 12, chevron), nextTip, 26);
        Control caption = HRow(5, Icon(geo, 16, marker), new TextBlock
        {
            Text = wordText,
            FontSize = 11.5,
            Foreground = word,
            VerticalAlignment = VerticalAlignment.Center
        });
        caption.Margin = new Thickness(pill ? 4 : 3, 0);

        StackPanel inner = HRow(pill ? 0 : 1, prev, caption, next);

        if (!pill)
        {
            return inner;
        }

        return new Border
        {
            Background = Tok("CardBg"),
            BorderBrush = Tok("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2, 0),
            Margin = new Thickness(3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner
        };
    }

    /// <summary>The full redesigned strip for a treatment — shares the responsive DockPanel shape.</summary>
    private static Border NavStripRedesign(JumpVariant v)
    {
        StackPanel bp = BreakpointGroupRedesign();
        DockPanel.SetDock(bp, Dock.Right);

        StackPanel clock = ClockGroupRedesign();
        DockPanel.SetDock(clock, Dock.Left);

        Rectangle div = new()
        {
            Width = 1,
            Height = 18,
            Fill = Tok("BorderStrong"),
            Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(div, Dock.Left);

        ScrollViewer jump = new()
        {
            Content = BuildJumpGroup(v),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center
        };

        DockPanel dock = new()
        {
            LastChildFill = true
        };
        dock.Children.Add(bp);
        dock.Children.Add(clock);
        dock.Children.Add(div);
        dock.Children.Add(jump);

        return new Border
        {
            Background = Tok("PanelHeaderBg"),
            BorderBrush = Tok("BorderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = dock
        };
    }

    /// <summary>Large detail render of just the JUMP group — for judging glyph crispness at iteration time.</summary>
    private static Border JumpDetail(JumpVariant v)
    {
        StackPanel col = new()
        {
            Spacing = 12,
            Margin = new Thickness(20)
        };
        col.Children.Add(new TextBlock
        {
            Text = "JUMP group — " + v,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tok("TextValue")
        });
        col.Children.Add(new Border
        {
            Background = Tok("PanelHeaderBg"),
            BorderBrush = Tok("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = BuildJumpGroup(v)
        });
        return new Border
        {
            Width = 620,
            Height = 130,
            Background = Tok("ShellBg"),
            Child = col
        };
    }

    /// <summary>
    ///     The money shot: the CURRENT real NavStrip stacked above the three redesign
    ///     options, each full-width and labeled, so all four read at a glance. Render ~1040 wide.
    /// </summary>
    private static Border NavStripRedesignCompare()
    {
        StackPanel col = new()
        {
            Spacing = 0,
            Margin = new Thickness(0, 6, 0, 6)
        };

        void Row(string caption, Control strip)
        {
            col.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Tok("TextMid"),
                Margin = new Thickness(14, 10, 0, 3)
            });
            col.Children.Add(strip);
        }

        Row("CURRENT  —  letter labels (ev / rnd / tk) + ASCII glyphs (▶▶ ▶| ▶||)",
            new NavStrip
            {
                DataContext = new MainViewModel
                {
                    HasFile = true
                }
            });
        Row("OPTION A  —  compact icons, tooltip-driven (most media-bar)", NavStripRedesign(JumpVariant.IconOnly));
        Row("OPTION B  —  icon + caption word (most self-documenting)", NavStripRedesign(JumpVariant.IconCaption));
        Row("OPTION C  —  segmented pills (most modern grouping)", NavStripRedesign(JumpVariant.SegmentedPill));

        return new Border
        {
            Background = Tok("ShellBg"),
            Child = col
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //  Live Sync (CS2) — StatusChip states + flyout bodies (docs/csvg-integration/ux-design.md).
    //  `livesync-chips`  renders the dot vocabulary as real StatusStrips (correct PanelHeaderBg
    //                    surface) — one per engine state, incl. the SYNTHETIC hollow "Paused (inferred)"
    //                    so the ring path is verified even though nothing sets IsInferred yet.
    //  `livesync-flyouts` renders the real LiveSyncStatusView per state over a fake engine + demo context.
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every chip state as a real StatusStrip row (dot on the real PanelHeaderBg surface).</summary>
    private static Border LiveSyncChips()
    {
        (string State, StatusChipDotState Dot, bool Pulse, bool Hollow, string Label)[] rows =
        [
            ("Disconnected / Off", StatusChipDotState.Off, false, false, "CS2 · Off"),
            ("Connecting…", StatusChipDotState.Working, true, false, "CS2 · Connecting…"),
            ("ConnectedIdle (no demo)", StatusChipDotState.Working, false, false, "CS2 · Connected (no demo)"),
            ("SyncedHolding", StatusChipDotState.Good, false, false, "CS2 · Synced (paused)"),
            ("SyncedFollowing", StatusChipDotState.Good, true, false, "CS2 · Following"),
            ("SyncedSeekPending", StatusChipDotState.Good, true, false, "CS2 · Seeking…"),
            ("Paused (inferred) — HOLLOW RING", StatusChipDotState.Good, false, true, "CS2 · Paused (inferred)"),
            ("Degraded", StatusChipDotState.Degraded, false, false, "CS2 · Seek unconfirmed"),
            ("Faulted", StatusChipDotState.Error, false, false, "CS2 · Disconnected"),
            ("SuspendedForReel", StatusChipDotState.Off, false, false, "CS2 · Paused for reel render")
        ];

        StackPanel col = new()
        {
            Spacing = 5,
            Margin = new Thickness(0, 6, 0, 6)
        };
        foreach ((string state, StatusChipDotState dot, bool pulse, bool hollow, string label) in rows)
        {
            StatusChipViewModel chip = new()
            {
                DotState = dot,
                IsPulsing = pulse,
                IsHollow = hollow,
                Label = label
            };
            col.Children.Add(new StatusStrip
            {
                StatusText = state,
                StatusBrush = Tok("TextMid"),
                Chips = new[]
                {
                    chip
                }
            });
        }

        return new Border
        {
            Width = 500,
            Background = Tok("ShellBg"),
            Child = col
        };
    }

    /// <summary>The real flyout body per engine state, over a fake engine + demo context.</summary>
    private static Border LiveSyncFlyouts()
    {
        // A rooted, on-disk temp demo so the OFF flyout's Enable is enabled + the demo binding renders.
        string dir = Path.Combine(Path.GetTempPath(), "demoviewer-uicapture-livesync");
        Directory.CreateDirectory(dir);
        string demo = Path.Combine(dir, "faceit_2025.dem");
        if (!File.Exists(demo))
        {
            File.WriteAllText(demo, "stub");
        }

        FakeModuleContext withDemo = new(demo, "de_dust2");
        FakeModuleContext bareName = new("match.dem", "de_dust2"); // bare filename → the no-local-path guard

        WrapPanel wrap = new()
        {
            Margin = new Thickness(16)
        };

        void Add(string caption, LiveSyncState state, FakeModuleContext ctx, long? tick = null,
            LiveSyncVersionInfo? versions = null, LiveSyncCapabilities? caps = null)
        {
            FakeLiveSync svc = new()
            {
                State = state,
                LastCs2DemoTick = tick,
                Versions = versions,
                Capabilities = caps
            };
            LiveSyncStatusViewModel vm = new(svc, ctx, new PlaybackController(), () => { });
            StackPanel cell = new()
            {
                Width = 340,
                Margin = new Thickness(8),
                Spacing = 6
            };
            cell.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Tok("TextMid")
            });
            cell.Children.Add(new Border
            {
                Classes =
                {
                    "card-flyout"
                },
                Child = new LiveSyncStatusView
                {
                    DataContext = vm
                }
            });
            wrap.Children.Add(cell);
        }

        Add("OFF (demo bound)", LiveSyncState.Disconnected, withDemo);
        Add("OFF (no local path)", LiveSyncState.Disconnected, bareName);
        Add("WORKING (Connecting)", new LiveSyncState(LiveSyncStateKind.Connecting, "Waiting for plugin"), withDemo);
        Add("SYNCED (Following)", new LiveSyncState(LiveSyncStateKind.SyncedFollowing), withDemo,
            54321, new LiveSyncVersionInfo("1.0.0-rc.42", "14021"));
        Add("SYNCED (v1.0 plugin — baseline note)", new LiveSyncState(LiveSyncStateKind.SyncedFollowing), withDemo,
            54321, new LiveSyncVersionInfo("1.0.0", "14021"), LiveSyncCapabilities.None);
        Add("DEGRADED", new LiveSyncState(LiveSyncStateKind.Degraded), withDemo);
        Add("DEGRADED (v1.0 plugin — baseline note)", new LiveSyncState(LiveSyncStateKind.Degraded), withDemo,
            caps: LiveSyncCapabilities.None);
        Add("FAULTED", new LiveSyncState(LiveSyncStateKind.Faulted, "Disconnected — CS2 quit."), withDemo);

        return new Border
        {
            Width = 1120,
            Background = Tok("ShellBg"),
            Child = wrap
        };
    }

    /// <summary>
    ///     The 2D-tab in-context CS2 indicator in the Pb2d HUD palette, rendered over a real
    ///     Playback2DViewport (the authentic dark HUD island) in each state — so the Pb2dPositive-on-dark
    ///     legibility + the hollow-vs-solid shape can be read back (render-only design-system items).
    ///     The dot fill/stroke are set to the SAME Pb2d tokens the view's <c>Ellipse.pb2dDot.*</c> styles
    ///     resolve (good=Pb2dPositive, working=Pb2dTextBright, degraded=Pb2dTeamT); the class path itself is
    ///     exercised by the real view build + the Playback2D render tests.
    /// </summary>
    private static Border Playback2DLiveSyncHud()
    {
        // One indicator chip: a Pb2dOverlayBg pill [dot][label], the dot filled/stroked with its Pb2d token.
        static Border Chip(string dotTok, bool hollow, string label)
        {
            Ellipse dot = new()
            {
                Width = 9,
                Height = 9,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (hollow)
            {
                dot.Stroke = Tok(dotTok);
                dot.StrokeThickness = 1.5;
                dot.Fill = Brushes.Transparent;
            }
            else
            {
                dot.Fill = Tok(dotTok);
            }

            StackPanel row = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Tok("Pb2dTextBright"),
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            return new Border
            {
                Background = Tok("Pb2dOverlayBg"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                Child = row,
                HorizontalAlignment = HorizontalAlignment.Right
            };
        }

        StackPanel col = new()
        {
            Spacing = 12,
            Margin = new Thickness(10)
        };

        void Add(string caption, Border chip)
        {
            StackPanel cell = new()
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            cell.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = Tok("Pb2dTextDim"),
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right
            });
            cell.Children.Add(chip);
            col.Children.Add(cell);
        }

        Add("Following — Good (Pb2dPositive)", Chip("Pb2dPositive", false, "CS2 · Following"));
        Add("Paused (inferred) — HOLLOW RING, green", Chip("Pb2dPositive", true, "CS2 · Paused (inferred)"));
        Add("Seek unconfirmed — Degraded (Pb2dTeamT)", Chip("Pb2dTeamT", false, "CS2 · Seek unconfirmed"));
        Add("Connecting… — Working, neutral", Chip("Pb2dTextBright", false, "CS2 · Connecting…"));

        // Backdrop = the real dark viewport island; the chips overlay top-right exactly as in the view.
        Grid root = new()
        {
            Width = 640,
            Height = 440
        };
        root.Children.Add(new Playback2DViewport());
        root.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                col
            }
        });

        return new Border
        {
            Background = Tok("ShellBg"),
            Padding = new Thickness(16),
            Child = root
        };
    }

    // ── Create-Reel dialog + Reel chip (docs/csvg-integration/ux-design.md) ────

    /// <summary>
    ///     The real <see cref="HighlightReelDialogView" /> over a real <see cref="HighlightReelDialogViewModel" />
    ///     with three synthetic selections (two overlapping dust2 highlights that coalesce + one nuke clip).
    ///     <paramref name="moved" /> makes the nuke demo "missing" (fileExists=false) so the per-row error
    ///     + banner render + Generate disables; <paramref name="dryRunOnly" /> flips the macOS primary
    ///     action ("Dry run (mock)" + the developer caption).
    /// </summary>
    private static HighlightReelDialogView ReelDialog(bool dryRunOnly, bool moved)
    {
        HighlightSelection[] selections = new[]
        {
            ReelSel("/demos/dust2_faze.dem", "de_dust2", "s1mple", "76561198000000010", 7, 54000,
                "s1mple — 2 kills after the plant (round 7)"),
            ReelSel("/demos/dust2_faze.dem", "de_dust2", "s1mple", "76561198000000010", 7, 54400,
                "s1mple — ace (round 7)"),
            ReelSel("/demos/nuke_moved.dem", "de_nuke", "ZywOo", "76561198000000020", 4, 30000,
                "ZywOo — 3k retake (round 4)")
        };
        HighlightsSettings defaults = new()
        {
            ReelOutputDirectory = "/Users/pro/Movies/Reels",
            ClipLeadInSeconds = 15,
            ClipLeadOutSeconds = 5
        };
        Func<string, bool> exists = moved ? p => !p.Contains("nuke_moved", StringComparison.Ordinal) : _ => true;

        HighlightReelDialogViewModel vm = new(
            selections, defaults, null, null,
            null, dryRunOnly, exists);

        return new HighlightReelDialogView
        {
            DataContext = vm,
            Width = 720,
            Height = 680
        };
    }

    private static HighlightSelection ReelSel(
        string path, string map, string name, string steam, int round, int tick, string title)
    {
        DemoCacheRecord row = new()
        {
            Path = path,
            Map = map,
            TickRate = 64,
            TickCount = 200_000,
            Sha256 = "sha-" + name,
            // Slot is the join now — the record holds the roster once and the event points at it.
            Players = [new CachedPlayerInfo { Slot = round, Name = name, SteamId64 = steam }],
            Rounds =
            [
                new Services.DemoCache.CachedRound
                {
                    Number = round,
                    StartTickFrameClock = tick - 3000
                }
            ]
        };
        CachedHighlightEvent h = new()
        {
            RulesetId = "rules",
            HighlightId = "clutch",
            RoundNumber = round,
            Tick = tick,
            RenderedTitle = title,
            PlayerSlot = round
        };
        return new HighlightSelection(row, h);
    }

    /// <summary>
    ///     The real <see cref="ReelJobStatusView" /> flyout body per phase (working / completed / failed),
    ///     each in a real <c>card-flyout</c> over a real <see cref="ReelJobStatusViewModel" /> — the same
    ///     status→dot+label+per-clip mapping the shell chip drives.
    /// </summary>
    private static Border ReelChips()
    {
        StackPanel col = new()
        {
            Spacing = 16,
            Margin = new Thickness(16),
            Width = 400
        };

        void Add(string caption, ReelJobStatus status)
        {
            ReelJobStatusViewModel vm = new(new CaptureReelJob
            {
                Status = status
            }, _ => { });
            col.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = Tok("TextDim"),
                FontSize = 11,
                Margin = new Thickness(2, 0)
            });
            col.Children.Add(new Border
            {
                Classes =
                {
                    "card-flyout"
                },
                MaxWidth = 380,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new ReelJobStatusView
                {
                    DataContext = vm
                }
            });
        }

        Add("WORKING (clip 3 of 8, pulsing)",
            new ReelJobStatus(ReelJobPhase.Capturing, 2, 8, "ZywOo · 3k retake (round 4)", null, null, []));
        Add("COMPLETED (positive · Open folder)",
            new ReelJobStatus(ReelJobPhase.Completed, 8, 8, null, null,
                "/Users/pro/Movies/Reels/dust2_s1mple.mp4", []));
        Add("FAILED (error · Retry remaining)",
            new ReelJobStatus(ReelJobPhase.Failed, 2, 8, null,
                "OBS dropped the capture — check that OBS is running.", null, [2]));

        return new Border
        {
            Background = Tok("ShellBg"),
            Padding = new Thickness(16),
            Child = col
        };
    }

    // ── Demo-processing queue (demo-processing-queue.md) ───────────────────────

    /// <summary>
    ///     The queue flyout body over a fake queue — the live queue-management surface (item list with
    ///     state dot + owner/priority chips + per-item ✕, status line, Pause/Resume).
    /// </summary>
    private static Border QueueFlyout(bool populated)
    {
        FakeProcessingQueue queue = new();
        if (populated)
        {
            queue.Seed(
                ("faceit_liquid_vs_navi.dem", "library, highlights", DemoJobPriority.Background, DemoQueueItemState.Running, null),
                ("mirage_scrim_2025.dem", "highlights", DemoJobPriority.UserRequested, DemoQueueItemState.Queued, null),
                ("de_nuke_pug_night.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued, null),
                ("ancient_ranked_2650.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Completed, null),
                ("corrupt_half_download.dem", "highlights", DemoJobPriority.Background, DemoQueueItemState.Failed, "Unexpected end of stream"),
                ("overpass_faceit.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Rejected, null));
            queue.RunningCount = 1;
            queue.QueuedCount = 2;
        }

        ProcessingQueueStatusViewModel vm = new(queue, () => { });
        return new Border
        {
            Background = Tok("ShellBg"),
            Padding = new Thickness(20),
            Child = new Border
            {
                Classes =
                {
                    "card-flyout"
                },
                MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new ProcessingQueueStatusView
                {
                    DataContext = vm
                }
            }
        };
    }

    /// <summary>The queue status chip in each state — the strip-level indicator that opens the flyout.</summary>
    private static Border QueueChips()
    {
        (string State, StatusChipDotState Dot, bool Pulse, string Label)[] rows =
        [
            ("Running (1) — pulsing", StatusChipDotState.Working, true, "Processing 1"),
            ("Queued (5), none running", StatusChipDotState.Working, false, "5 queued"),
            ("Paused", StatusChipDotState.Off, false, "Queue paused")
        ];

        StackPanel col = new()
        {
            Spacing = 5,
            Margin = new Thickness(0, 6, 0, 6)
        };
        foreach ((string state, StatusChipDotState dot, bool pulse, string label) in rows)
        {
            StatusChipViewModel chip = new()
            {
                DotState = dot,
                IsPulsing = pulse,
                Label = label
            };
            col.Children.Add(new StatusStrip
            {
                StatusText = state,
                StatusBrush = Tok("TextMid"),
                Chips = new[]
                {
                    chip
                }
            });
        }

        return new Border
        {
            Width = 460,
            Background = Tok("ShellBg"),
            Child = col
        };
    }

    // ── Highlights tab ─────────────────────────────────────────────────────────

    private sealed class CaptureHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate) =>
            ($"capture@{tickRate}", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) =>
            throw new NotSupportedException("capture never scans");

        public void InvalidateRules()
        {
        }
    }

    private enum LibraryState
    {
        Landing, // no folders → the landing hero (Open Demo + recents + drop hint)
        Populated, // folders + demos → the folder browser + persistent Open Demo / Recent ▾ actions bar
        DragOver // the landing with the drag-over overlay forced on (can't synthesize a real drag headlessly)
    }

    /// <summary>The three semantic-JUMP treatments the redesign options differ by.</summary>
    private enum JumpVariant
    {
        IconOnly, // A — compact icon-only buttons (marker + direction chevron), tooltip-driven
        IconCaption, // B — icon + caption word per target (self-documenting, text-forward)
        SegmentedPill // C — a bordered segmented pill per target (chevron | icon+word | chevron)
    }

    // Minimal IDemoProcessingQueue double for the queue-flyout capture. Only the members the flyout reads are
    // meaningful (Items + counts + pause/background + RemoveByUser); the pump/foreground paths throw.
    private sealed class FakeProcessingQueue : IDemoProcessingQueue
    {
        private readonly ObservableCollection<DemoQueueItem> _items = [];

        public FakeProcessingQueue() => Items = new ReadOnlyObservableCollection<DemoQueueItem>(_items);

        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; }
        public event Action? Changed;
        public event Action? CapacityAvailable;
        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused { get; private set; }
        public int QueuedCount { get; set; }
        public int RunningCount { get; set; }

        public void Pause()
        {
            IsPaused = true;
            Changed?.Invoke();
        }

        public void Resume()
        {
            IsPaused = false;
            Changed?.Invoke();
        }

        public void RemoveByUser(Guid itemId)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Id == itemId)
                {
                    _items.RemoveAt(i);
                }
            }

            CapacityAvailable?.Invoke();
            Changed?.Invoke();
        }

        public Task<ParsedDemo> RequestForegroundAsync(
            string? path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request) =>
            throw new NotSupportedException();

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void Seed(params (string Name, string Owners, DemoJobPriority Priority,
            DemoQueueItemState State, string? Error)[] rows)
        {
            foreach ((string name, string owners, DemoJobPriority priority, DemoQueueItemState state, string? error)
                     in rows)
            {
                _items.Add(new DemoQueueItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/demos/" + name,
                    DisplayName = name,
                    Owners = owners,
                    Priority = priority,
                    State = state,
                    Error = error
                });
            }
        }
    }

    // Minimal IReelJobService double — a fixed status set before the VM is constructed (the VM's ctor maps
    // from Status). Empty-accessor event so it needs no raise (avoids CS0067); the VM's subscribe is a no-op.
    private sealed class CaptureReelJob : IReelJobService
    {
        public ReelJobStatus Status { get; init; } = ReelJobStatus.Idle;

        public event EventHandler<ReelJobStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Start(ReelRequest request)
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        public void RetryRemaining()
        {
        }
    }

    // Minimal ILiveSyncService double — a fixed state set before the VM is constructed (the VM's ctor maps
    // from State). Empty-accessor event so it needs no raise (avoids CS0067); the VM's subscribe is a no-op.
    private sealed class FakeLiveSync : ILiveSyncService
    {
        public LiveSyncState State { get; init; } = LiveSyncState.Disconnected;
        public long? LastCs2DemoTick { get; init; }
        public LiveSyncVersionInfo? Versions { get; init; }
        public LiveSyncCapabilities? Capabilities { get; init; }

        public event EventHandler<LiveSyncStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task EnableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> VerifyMomentAsync(int frameClockTick, int preRollTicks = 192, int postRollTicks = 64,
            string? spectateName = null, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> HasLeftoverInstallModificationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task RestoreInstallAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── pb2d-vision fakes ──────────────────────────────────────────────────────
    // Minimal Playback2D module-context double: a fixed 2v2 roster with world positions + eye yaw,
    // an empty entity view (no rules proxy / grenades — the vision overlay needs only markers), and a
    // RaiseAdvanced() knob so the capture can rebuild markers after the ground snap. IModuleContext's
    // default members (DemoReset, event nav, GetEventTimeline) intentionally stay defaulted.

    private sealed class VisionEmptyEntities : IReadOnlyEntityView
    {
        public static readonly VisionEmptyEntities Instance = new();

        public IEnumerable<IReadOnlyEntity> All() => [];
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => [];
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }

    // A live pawn exposing exactly the fields BuildFrame reads: full health, alive, and the eye angles
    // (pitch = .X, yaw = .Y) that aim the vision cone. Everything else reads as absent.
    private sealed class VisionPawn(VisionPlayer owner) : IReadOnlyEntity
    {
        public string ClassName => "CCSPlayerPawn";
        public int Serial => owner.Slot + 1;
        public bool IsInPvs => true;
        public object? this[string fieldPath] => null;

        public bool TryGet<T>(string fieldPath, out T value)
        {
            object? boxed = fieldPath switch
            {
                "m_iHealth" => 100,
                "m_lifeState" => 0,
                "m_angEyeAngles" => new Vector3(0f, owner.Yaw, 0f),
                _ => null
            };
            if (boxed is T t)
            {
                value = t;
                return true;
            }

            value = default!;
            return false;
        }
    }

    private sealed class VisionPlayer : IPlayerState
    {
        public VisionPlayer(int slot, int team, string name, float x, float y, float z, float yaw)
        {
            Slot = slot;
            Team = team;
            Name = name;
            X = x;
            Y = y;
            Z = z;
            Yaw = yaw;
            Pawn = new VisionPawn(this);
        }

        public string Name { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; set; } // ground-snapped after the BVH loads
        public float Yaw { get; }

        public int Slot { get; }
        public int Team { get; }
        public bool HasLivePawn => true;
        public IReadOnlyEntity? Pawn { get; }
        public IReadOnlyEntity? Controller => null;
        public (float X, float Y, float Z)? WorldPosition => (X, Y, Z);
    }

    private sealed class VisionModuleContext(string mapName, IReadOnlyList<VisionPlayer> players)
        : IModuleContext, IPlaybackSnapshot
    {
        public IReadOnlyList<VisionPlayer> VisionPlayers => players;

        // ── IModuleContext ──
        public bool HasDemo => true;
        public string? DemoPath => "/demos/capture_dust2.dem";
        public string? MapName => mapName;
        public int TickRate => 64;
        public double CurtimeSeconds(int tick) => tick / 64.0;
        public int CurrentFrameIndex => FrameIndex;
        public int CurrentTick => Tick;
        public bool IsPlaying => false;
        public double Speed => 1.0;

        public void RequestSeekToFrame(int frameIndex)
        {
        }

        public void RequestSeekToTick(int tick)
        {
        }

        public void RequestPlay()
        {
        }

        public void RequestPause()
        {
        }

        public event Action<IPlaybackSnapshot>? Advanced;

        public IReadOnlyEntityView Entities => VisionEmptyEntities.Instance;

        public IReadOnlyList<PlayerRosterEntry> Players { get; } =
        [
            .. players.Select(p => new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = 76561198000000100UL + (ulong)p.Slot,
                Name = p.Name
            })
        ];

        public IReadOnlyList<IPlayerState> CurrentPlayers => [.. players];

        // ── IPlaybackSnapshot ──
        public int FrameIndex { get; private set; }
        public int Tick { get; private set; }
        IReadOnlyList<IPlayerState> IPlaybackSnapshot.Players => [.. players];

        /// <summary>One coalesced Advanced push (the capture's post-ground-snap rebuild).</summary>
        public void RaiseAdvanced()
        {
            FrameIndex++;
            Tick += 64;
            Advanced?.Invoke(this);
        }
    }

    // Minimal IModuleContext double — the LiveSyncStatusViewModel only reads DemoPath + MapName and (no-op)
    // subscribes to the default DemoReset. The rest are stubbed; none are exercised by the flyout capture.
    private sealed class FakeModuleContext(string? demoPath, string? mapName) : IModuleContext
    {
        public bool HasDemo => demoPath is not null;
        public string? DemoPath => demoPath;
        public string? MapName => mapName;
        public int TickRate => 64;
        public double CurtimeSeconds(int tick) => 0;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1.0;

        public void RequestSeekToFrame(int frameIndex)
        {
        }

        public void RequestSeekToTick(int tick)
        {
        }

        public void RequestPlay()
        {
        }

        public void RequestPause()
        {
        }

        public event Action<IPlaybackSnapshot>? Advanced
        {
            add { }
            remove { }
        }

        public IReadOnlyEntityView Entities => throw new NotSupportedException();
        public IReadOnlyList<PlayerRosterEntry> Players => [];
        public IReadOnlyList<IPlayerState> CurrentPlayers => [];
    }

    // Match Overview landing page — the demo-opening landing. The three load points exist as SEPARATE
    // variants on purpose: the page's contract is that every section is present from the first frame and only
    // its values change, so the way to check it is to render Opening / Parsed / Ready and compare — the
    // sections and the total height must match across all three. A section that appears between two of these
    // captures is the layout jump this page was built to remove.
    internal enum MatchOverviewMock
    {
        Opening,
        Parsed,
        Ready,
        Failed
    }

    // The two rosters, with one bot each so the BOT tag renders (real demos frequently have a filler bot).
    private static readonly (string Name, bool Bot)[] _overviewCt =
    [
        ("s1mple", false), ("b1t", false), ("electroNic", false), ("Perfecto", false), ("BOT Rock", true)
    ];

    private static readonly (string Name, bool Bot)[] _overviewT =
    [
        ("ZywOo", false), ("apEX", false), ("flameZ", false), ("mezii", false), ("BOT Wolf", true)
    ];

    private static Panel MatchOverview(MatchOverviewMock mock, int spectators = 0)
    {
        // Real (no-op) CTA actions so the captures show the buttons in their true enabled/disabled state —
        // both gates also require an action to be wired, so a bare `new()` would render them permanently
        // greyed and hide a regression in the gating.
        // Real (no-op) actions so the captures show every affordance in its true state — each gate also
        // requires a wired delegate, so a bare `new()` renders them permanently absent and hides a gating
        // regression. computeFullStats in particular drives the highlight card's "index them" action.
        MatchOverviewTabViewModel vm = new(() => { }, () => { }, _ => { }, _ => { });
        vm.BeginOpening(
            mock == MatchOverviewMock.Failed ? "corrupt_round12_de_ancient.dem" : "match730_pug_de_mirage_2024.dem",
            mock == MatchOverviewMock.Failed ? "Ancient" : "Mirage",
            mock == MatchOverviewMock.Failed ? "FACEIT.com register to play here" : "Valve Counter-Strike 2 Server",
            // The capture drives one demo at a time, so the file name is a sufficient subject key.
            mock == MatchOverviewMock.Failed ? "corrupt_round12_de_ancient.dem" : "match730_pug_de_mirage_2024.dem");

        if (mock == MatchOverviewMock.Failed)
        {
            vm.Fail(vm.SubjectKey, "The demo header is truncated — the file may have stopped recording early or been "
                    + "copied incompletely. Try re-downloading it from the source.");
            return OverviewPanel(vm);
        }

        if (mock == MatchOverviewMock.Opening)
        {
            vm.SetStage(vm.SubjectKey, "Parsing demo…", 0.15);
            return OverviewPanel(vm);
        }

        // Post-parse: what SetSummary(ParsedDemo) produces, without needing a real ParsedDemo.
        vm.DurationDisplay = "42:18";
        vm.TickRateDisplay = "64";
        vm.PlayerCountDisplay = "10";
        // Tournament demos routinely carry observers/coaches/admins; the tile is hidden at 0.
        vm.SpectatorCountDisplay = spectators.ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.HasSpectators = spectators > 0;
        foreach ((string n, bool bot) in _overviewCt)
        {
            vm.CounterTerrorists.Add(new OverviewPlayer(n, bot));
        }

        foreach ((string n, bool bot) in _overviewT)
        {
            vm.Terrorists.Add(new OverviewPlayer(n, bot));
        }

        vm.HasSummary = true;
        // A live parse always yields the team split, so the roster gate lands with the summary — without it
        // the header badges hold the placeholder, which is correct for a migrated cache row and wrong here.
        vm.HasRoster = true;
        vm.ParseStage.IsActive = false;
        vm.ParseStage.IsDone = true;
        vm.EnrichStage.IsActive = true;
        vm.SetStage(vm.SubjectKey, "Preparing playback and navigation…", 0.45);

        if (mock == MatchOverviewMock.Parsed)
        {
            return OverviewPanel(vm);
        }

        // Post-analysis: drive the REAL SetAnalysis with a synthesized game table, so the capture exercises
        // the same projection (column keys, "—" fallbacks, CT-then-T ordering) the shell feeds it.
        vm.BeginAnalysis(vm.SubjectKey);
        vm.SetAnalysis(vm.SubjectKey, OverviewGameTable(), new Dictionary<int, int?> { [0] = 13, [1] = 9 }, 22);
        // The score is authoritative (CCSTeam.m_iScore), fed separately from the analysis run — mirror that
        // here, with clan names so the capture shows the pro-demo labelling rather than "ENDED CT".
        vm.SetTeamScores(vm.SubjectKey, 13, 9);
        vm.SetTeamNames(vm.SubjectKey, "Team Vitality", "FaZe Clan");
        return OverviewPanel(vm);
    }

    /// <summary>
    ///     The CACHED render — the real <see cref="MatchOverviewTabView" /> over a real view-model fed a
    ///     synthesized <see cref="DemoCacheRecord" />. No store, no filesystem, no parse: exactly what the
    ///     production path does, which is the point of the mode.
    /// </summary>
    private static Panel MatchOverviewCached(
        DemoCacheTier tier,
        DemoAnalysisState analysisState = DemoAnalysisState.Pending,
        bool teamSplit = true)
    {
        // Real (no-op) actions so the captures show the buttons in their true enabled state — the gates all
        // require a wired action, so a bare `new()` would render them permanently absent and hide a
        // regression in the gating.
        MatchOverviewTabViewModel vm = new(
            () => { }, () => { }, _ => { }, _ => { }, () => { });

        DemoCacheRecord r = new()
        {
            Path = "/demos/faceit_2025-06-14_dust2.dem",
            Size = 148_000_000,
            ModifiedTicks = 638_000_000_000_000_000
        };

        if (tier >= DemoCacheTier.Header)
        {
            r.Header = new TierStamp { Schema = DemoCacheRecord.HeaderSchema, ComputedAtTicks = 1 };
            r.Map = "de_dust2";
            r.Server = "FACEIT Server EU #4021";
        }

        if (tier >= DemoCacheTier.Parse)
        {
            r.Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 };
            r.DurationSeconds = 2292;
            r.TickRate = 64;
            r.TickCount = 146_688;
            r.CtScore = 13;
            r.TScore = 9;
            r.CtClan = "Natus Vincere";
            r.TClan = "FaZe Clan";
            for (int i = 0; i < 22; i++)
            {
                r.Rounds.Add(new DemoViewer.NET.Services.DemoCache.CachedRound
                {
                    Number = i + 1, StartTickFrameClock = 1000 + (i * 5000)
                });
            }

            int slot = 0;
            foreach ((string name, bool bot) in _overviewCt)
            {
                r.Players.Add(new CachedPlayerInfo
                {
                    Slot = slot++, Name = name, Team = teamSplit ? 3 : 0, IsBot = bot
                });
            }

            foreach ((string name, bool bot) in _overviewT)
            {
                r.Players.Add(new CachedPlayerInfo
                {
                    Slot = slot++, Name = name, Team = teamSplit ? 2 : 0, IsBot = bot
                });
            }
        }

        r.AnalysisState = analysisState;
        if (tier >= DemoCacheTier.Analysis && analysisState != DemoAnalysisState.Failed)
        {
            r.Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 };
            r.AnalysisRoundCount = 22;
            r.CtSideWins = 12;
            r.TSideWins = 10;

            (string Name, int Team, int K, int D, int A, double Adr, double Rating)[] board =
            [
                ("s1mple", 3, 24, 14, 4, 92.4, 1.34), ("b1t", 3, 19, 15, 6, 78.1, 1.12),
                ("electroNic", 3, 17, 16, 8, 74.9, 1.08), ("Perfecto", 3, 12, 17, 9, 61.3, 0.92),
                ("BOT Rock", 3, 6, 19, 2, 34.0, 0.51), ("ZywOo", 2, 22, 16, 5, 88.7, 1.27),
                ("apEX", 2, 15, 18, 7, 69.2, 0.97), ("flameZ", 2, 14, 17, 6, 66.8, 0.95),
                ("mezii", 2, 11, 18, 10, 58.4, 0.88), ("BOT Wolf", 2, 5, 20, 3, 31.2, 0.47)
            ];
            for (int i = 0; i < board.Length; i++)
            {
                r.Scoreboard.Add(new CachedStatRow
                {
                    Slot = i, Team = board[i].Team, Kills = board[i].K, Deaths = board[i].D,
                    Assists = board[i].A, Adr = board[i].Adr, Rating = board[i].Rating
                });
            }

            (int Slot, int Round, int Tick, string Id, string Title)[] highlights =
            [
                (0, 7, 54_321, "plant_kills", "s1mple — 2 kills after the plant (round 7)"),
                (0, 12, 61_200, "ace", "s1mple — ace (round 12)"),
                (0, 18, 88_010, "clutch_1v3", "s1mple — 1v3 clutch (round 18)"),
                (5, 4, 30_110, "retake_3k", "ZywOo — 3k retake (round 4)"),
                (5, 15, 70_400, "double", "ZywOo — double kill through smoke (round 15)"),
                (1, 9, 42_010, "opening_duel", "b1t — opening duel won (round 9)")
            ];
            foreach ((int s, int round, int tick, string id, string title) in highlights)
            {
                r.Highlights.Add(new CachedHighlightEvent
                {
                    RulesetId = "clutch", HighlightId = id, PlayerSlot = s,
                    RoundNumber = round, Tick = tick, RenderedTitle = title
                });
            }
        }

        vm.SetCachedRecord(r);
        // A different demo is open behind the preview — shows the "◀ Back" affordance in its true state.
        vm.LiveDemoName = "esl_2025-06-02_nuke.dem";
        return OverviewPanel(vm);
    }

    // A stand-in for the analysis engine's per-player match table, using the same column keys the real
    // PlayerGameStatsProjector emits (see ColumnCatalogue).
    private static MetricTable OverviewGameTable()
    {
        (string Name, int Team, int K, int D, int A, double Adr, double Rating)[] rows =
        [
            ("s1mple", 3, 24, 14, 4, 92.4, 1.34),
            ("b1t", 3, 19, 15, 6, 78.1, 1.12),
            ("electroNic", 3, 17, 16, 8, 74.9, 1.08),
            ("Perfecto", 3, 12, 17, 9, 61.3, 0.92),
            ("BOT Rock", 3, 6, 19, 2, 34.0, 0.51),
            ("ZywOo", 2, 22, 16, 5, 88.7, 1.27),
            ("apEX", 2, 15, 18, 7, 69.2, 0.97),
            ("flameZ", 2, 14, 17, 6, 66.8, 0.95),
            ("mezii", 2, 11, 18, 10, 58.4, 0.88),
            ("BOT Wolf", 2, 5, 20, 3, 31.2, 0.47)
        ];

        return new MetricTable(
            "player_game",
            ["player_name", "team"],
            ["TotalK", "TotalD", "TotalA", "ADR", "HLTV", "CTW", "TW"],
            rows.Select(r => new MetricRow(
                    new Dictionary<string, object?> { ["player_name"] = r.Name, ["team"] = r.Team },
                    new Dictionary<string, object?>
                    {
                        ["TotalK"] = r.K,
                        ["TotalD"] = r.D,
                        ["TotalA"] = r.A,
                        ["ADR"] = r.Adr,
                        ["HLTV"] = r.Rating,
                        // Per-team round wins by side. The CT-ending team took 6 as CT + 7 as T = 13; the
                        // T-ending team 6 + 3 = 9. So the SIDE split (CT 12 / T 10) differs from the TEAM
                        // totals (13 / 9) — exactly the half-swap case the page must not conflate.
                        ["CTW"] = r.Team == 3 ? 6 : 6,
                        ["TW"] = r.Team == 3 ? 7 : 3
                    }))
                .ToList());
    }

    private static Panel OverviewPanel(MatchOverviewTabViewModel vm) =>
        new()
        {
            Background = Tok("ShellBg"),
            Children = { new MatchOverviewTabView { DataContext = vm } }
        };
}

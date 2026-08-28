#region

using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.Views.Highlights;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the <see cref="HighlightReelDialogViewModel" /> — the reel CONFIG PANE
///     (promoted out of the modal by the Reels-dashboard redesign): the
///     coalesced clip-plan build (via <see cref="ClipWindows" />), CRF⊕bitrate exclusivity,
///     inline pre-flight gating, the platform primary-action, the defaults-persistence
///     payload, and the live-sync interlock confirm. Pure VM — a fake <see cref="IReelJobService" /> captures the
///     hand-off; the demo-existence predicate is injected so the tests stay filesystem-free.
/// </summary>
public class HighlightReelDialogViewModelTests
{
    private const int Rate = 64;

    private static HighlightSelection Sel(
        string demo, string steam, string name, int round, int tick, string title,
        string? sha = "sha", int tickCount = 120_000)
    {
        // Slot is the join now: the unified record holds the roster once and the event references it, so
        // the player this clip is attributed to has to EXIST in Players or the name/steamId resolve empty.
        DemoCacheRecord record = new()
        {
            Path = demo,
            Map = "de_dust2",
            TickRate = Rate,
            TickCount = tickCount,
            Sha256 = sha,
            Players =
            [
                new CachedPlayerInfo
                {
                    Slot = round,
                    Name = name,
                    SteamId64 = steam
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
            PlayerSlot = round // unique slot per row
        };
        return new HighlightSelection(record, h);
    }

    private static HighlightReelDialogViewModel New(
        IReadOnlyList<HighlightSelection> selections,
        out FakeReelJob job,
        HighlightsSettings? defaults = null,
        bool dryRunOnly = false,
        bool demosExist = true,
        bool liveSyncActive = false,
        Action<Action<AppSettings>>? persist = null)
    {
        job = new FakeReelJob();
        HighlightsSettings d = defaults ?? new HighlightsSettings
        {
            ReelOutputDirectory = "/out/reels"
        };
        return new HighlightReelDialogViewModel(
            selections, d, job, persist,
            () => liveSyncActive, dryRunOnly,
            _ => demosExist);
    }

    // (a) two overlapping highlights for the same (demo, player, round) coalesce into ONE clip whose
    // row shows both contributors; the header reports "2 selected · 1 after merge"; Generate hands off 1 clip.
    [Test]
    public async Task Coalesces_OverlappingSamePlayerRound_IntoOneVisibleMergedClip()
    {
        HighlightSelection[] sels = new[]
        {
            Sel("/d/a.dem", "10", "s1mple", 7, 5000, "kill A"), Sel("/d/a.dem", "10", "s1mple", 7, 6000, "kill B")
        };
        HighlightReelDialogViewModel vm = New(sels, out FakeReelJob job); // default lead-in 15 → windows overlap

        await Assert.That(vm.ClipGroups.Count).IsEqualTo(1);
        await Assert.That(vm.ClipGroups[0].Rows.Count).IsEqualTo(1);
        await Assert.That(vm.ClipGroups[0].Rows[0].IsMerged).IsTrue();
        await Assert.That(vm.ClipGroups[0].Rows[0].Contributors.Count).IsEqualTo(2);
        await Assert.That(vm.ClipsHeader).IsEqualTo("CLIPS (2 staged · 1 after merge)");

        vm.GenerateCommand.Execute(null);
        await Assert.That(job.StartCount).IsEqualTo(1);
        await Assert.That(job.LastRequest!.Clips.Count).IsEqualTo(1);
        await Assert.That(job.LastRequest.Clips[0].Label).IsEqualTo("kill A + kill B");
    }

    // (b) Distinct rounds NEVER coalesce even with overlapping windows (Coalesce groups by round) → 2 clips.
    [Test]
    public async Task DistinctRounds_DoNotCoalesce()
    {
        HighlightSelection[] sels = new[]
        {
            Sel("/d/a.dem", "10", "s1mple", 7, 5000, "r7"), Sel("/d/a.dem", "10", "s1mple", 8, 5100, "r8")
        };
        HighlightReelDialogViewModel vm = New(sels, out _);

        await Assert.That(vm.ClipsHeader).IsEqualTo("CLIPS (2 staged · 2 after merge)");
        await Assert.That(vm.ClipGroups[0].Rows.Count).IsEqualTo(2);
        await Assert.That(vm.ClipGroups[0].Rows.All(r => !r.IsMerged)).IsTrue();
    }

    // (c) Editing the padding recomputes the plan live: a wider lead-in merges two clips that were
    // separate at a narrow lead-in.
    [Test]
    public async Task EditingLeadIn_RecomputesPlanLive()
    {
        HighlightSelection[] sels = new[]
        {
            Sel("/d/a.dem", "10", "s1mple", 7, 5000, "A"), Sel("/d/a.dem", "10", "s1mple", 7, 6000, "B")
        };
        HighlightReelDialogViewModel vm = New(sels, out _);

        vm.LeadInSeconds = 5; // windows [4680,5320] & [5680,6320] — no overlap → 2 clips
        await Assert.That(vm.ClipGroups[0].Rows.Count).IsEqualTo(2);

        vm.LeadInSeconds = 15; // windows overlap again → back to 1 merged clip
        await Assert.That(vm.ClipGroups[0].Rows.Count).IsEqualTo(1);
        await Assert.That(vm.ClipGroups[0].Rows[0].IsMerged).IsTrue();
    }

    // Mid-match demos (ServerStartTick ≠ 0): highlight ticks are FRAME CLOCK and
    // flow into the handed-off ReelClip window unmodified — an accidental −ServerStartTick
    // anywhere in the dialog's clip build would shift the window by 40k ticks. Asserted at the
    // hand-off layer (the one that could regress), not just in ClipWindows' pure math.
    [Test]
    public async Task MidMatchServerStartTick_NeverShiftsTheHandedOffClipWindow()
    {
        HighlightSelection midMatch = Sel("/d/a.dem", "10", "s1mple", 7, 5000, "A");
        midMatch.Record.ServerStartTick = 40_000;
        HighlightReelDialogViewModel vm = New([midMatch], out FakeReelJob job);

        vm.GenerateCommand.Execute(null);
        ReelClip clip = job.LastRequest!.Clips[0];
        await Assert.That(clip.StartTick).IsEqualTo(5000 - 15 * Rate);
        await Assert.That(clip.EndTick).IsEqualTo(5000 + 5 * Rate);
    }

    // The TickOffset shim is applied exactly ONCE, and NOT here: the handed-off
    // plan is FRAME CLOCK, and the reel job converts into CS2 demo-tick space at emission
    // (ReelJobService.Cs2Range, covered in the LiveSync suite). A dialog that pre-applied the offset
    // would double-count it against that conversion.
    [Test]
    public async Task HandedOffClips_AreFrameClock_NotCs2DemoTicks()
    {
        HighlightSelection[] sels = new[]
        {
            Sel("/d/a.dem", "10", "s1mple", 7, 5000, "A")
        };
        HighlightReelDialogViewModel vm = New(sels, out FakeReelJob job);

        vm.GenerateCommand.Execute(null);
        ReelClip clip = job.LastRequest!.Clips[0];
        await Assert.That(clip.StartTick).IsEqualTo(5000 - 15 * Rate);
        await Assert.That(clip.EndTick).IsEqualTo(5000 + 5 * Rate);
    }

    // (d) SteamId64 → PlayerSteamId64 via long.TryParse (0 on empty/non-numeric — never throws).
    [Test]
    public async Task SteamId_ParsedOrZero()
    {
        HighlightSelection[] ok = new[]
        {
            Sel("/d/a.dem", "76561198000000010", "s1mple", 7, 5000, "A")
        };
        HighlightReelDialogViewModel vmOk = New(ok, out FakeReelJob jobOk);
        vmOk.GenerateCommand.Execute(null);
        await Assert.That(jobOk.LastRequest!.Clips[0].PlayerSteamId64).IsEqualTo(76561198000000010L);

        HighlightSelection[] empty = new[]
        {
            Sel("/d/a.dem", "", "s1mple", 7, 5000, "A")
        };
        HighlightReelDialogViewModel vmEmpty = New(empty, out FakeReelJob jobEmpty);
        vmEmpty.GenerateCommand.Execute(null);
        await Assert.That(jobEmpty.LastRequest!.Clips[0].PlayerSteamId64).IsEqualTo(0L);
    }

    // (e) CRF ⊕ Bitrate is UI-enforced: exactly one field is enabled, and the request carries exactly
    // one of Crf / VideoBitrate.
    [Test]
    public async Task Encoding_CrfXorBitrate_IsExclusive()
    {
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job);

        // CRF mode (default).
        await Assert.That(vm.CrfEnabled).IsTrue();
        await Assert.That(vm.BitrateEnabled).IsFalse();
        vm.Crf = 22;
        vm.GenerateCommand.Execute(null);
        await Assert.That(job.LastRequest!.Crf).IsEqualTo(22);
        await Assert.That(job.LastRequest.VideoBitrate).IsNull();

        // Bitrate mode.
        HighlightReelDialogViewModel vm2 = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job2);
        vm2.UseCrf = false;
        await Assert.That(vm2.CrfEnabled).IsFalse();
        await Assert.That(vm2.BitrateEnabled).IsTrue();
        vm2.BitrateKbps = 6000;
        vm2.GenerateCommand.Execute(null);
        await Assert.That(job2.LastRequest!.Crf).IsNull();
        await Assert.That(job2.LastRequest.VideoBitrate).IsEqualTo("6000");
    }

    // (f) validation gates Generate: no selection, a moved demo, and an empty output folder each
    // disable it with an inline banner; a clean plan enables it.
    [Test]
    public async Task Validation_GatesGenerate()
    {
        // No selection.
        HighlightReelDialogViewModel none = New([], out _);
        await Assert.That(none.HasError).IsTrue();
        await Assert.That(none.GenerateCommand.CanExecute(null)).IsFalse();

        // Moved demo (fileExists → false): per-row error + banner, Generate disabled.
        HighlightReelDialogViewModel moved =
            New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out _, demosExist: false);
        await Assert.That(moved.ClipGroups[0].Rows[0].HasError).IsTrue();
        await Assert.That(moved.HasError).IsTrue();
        await Assert.That(moved.GenerateCommand.CanExecute(null)).IsFalse();

        // Empty output folder.
        HighlightReelDialogViewModel noOut = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out _,
            new HighlightsSettings
            {
                ReelOutputDirectory = null
            });
        await Assert.That(noOut.GenerateCommand.CanExecute(null)).IsFalse();

        // Clean plan enables Generate.
        HighlightReelDialogViewModel ok = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out _);
        await Assert.That(ok.HasError).IsFalse();
        await Assert.That(ok.GenerateCommand.CanExecute(null)).IsTrue();
    }

    // (f2) A placeholder roster (legacy names-only cache record: every player at Slot = -1) can't resolve the
    // highlight's PlayerSlot, so the spectate name is empty. CSVG rejects such a plan ("PlayerNameToSpectate
    // must not be empty"); DV catches it first — the clip is kept out of the plan, its row is flagged, and
    // Generate is blocked with an actionable "re-scan" banner instead of the raw CSVG dump.
    [Test]
    public async Task UnresolvedPlayer_KeptOutOfPlan_BlocksGenerate_WithActionableBanner()
    {
        DemoCacheRecord record = new()
        {
            Path = "/d/legacy.dem",
            Map = "de_mirage",
            TickRate = Rate,
            TickCount = 120_000,
            Sha256 = "sha",
            // Placeholder slot (-1) — the exact shape LegacyCacheMigration writes for a names-only record.
            Players =
            [
                new CachedPlayerInfo
                {
                    Slot = -1,
                    Name = "Vernon",
                    SteamId64 = ""
                }
            ]
        };
        CachedHighlightEvent h = new()
        {
            RulesetId = "rules",
            HighlightId = "clutch",
            RoundNumber = 5,
            Tick = 5000,
            RenderedTitle = "ace",
            PlayerSlot = 5 // a real slot with no roster match → RawPlayerName resolves empty
        };
        HighlightSelection sel = new(record, h);
        await Assert.That(sel.RawPlayerName).IsEqualTo(""); // precondition: the slot join fails

        HighlightReelDialogViewModel vm = New([sel], out FakeReelJob job);

        await Assert.That(vm.ClipGroups[0].Rows[0].HasError).IsTrue();
        await Assert.That(vm.HasError).IsTrue();
        await Assert.That(vm.ErrorBanner!).Contains("resolve the player");
        await Assert.That(vm.GenerateCommand.CanExecute(null)).IsFalse();

        // And even if invoked, nothing is handed to the job (the plan is empty).
        vm.GenerateCommand.Execute(null);
        await Assert.That(job.LastRequest).IsNull();
    }

    // (g) platform primary action: macOS (dryRunOnly) is a labelled "Dry run (mock)" with DryRun=true;
    // Windows/Linux is a real "Generate reel" with DryRun=false.
    [Test]
    public async Task PlatformPrimaryAction_ReflectsDryRunMode()
    {
        HighlightReelDialogViewModel real = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob rjob);
        await Assert.That(real.IsDryRunOnly).IsFalse();
        await Assert.That(real.PrimaryActionLabel).IsEqualTo("Generate reel");
        real.GenerateCommand.Execute(null);
        await Assert.That(rjob.LastRequest!.DryRun).IsFalse();

        HighlightReelDialogViewModel dry = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob djob,
            dryRunOnly: true);
        await Assert.That(dry.IsDryRunOnly).IsTrue();
        await Assert.That(dry.PrimaryActionLabel).IsEqualTo("Dry run (mock)");
        dry.GenerateCommand.Execute(null);
        await Assert.That(djob.LastRequest!.DryRun).IsTrue();
    }

    // (h) Generate persists the edited reel defaults ("set once"): the captured mutate applied to a
    // fresh AppSettings reflects every edited field.
    [Test]
    public async Task Generate_PersistsEditedDefaults()
    {
        Action<AppSettings>? captured = null;
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out _,
            persist: apply => captured = apply);

        vm.LeadInSeconds = 20;
        vm.LeadOutSeconds = 3;
        vm.OutputFolder = "/reels/out";
        vm.Fps = 120;
        vm.Concatenate = false;
        vm.UseCrf = false;
        vm.BitrateKbps = 8500;
        vm.GenerateCommand.Execute(null);

        await Assert.That(captured).IsNotNull();
        AppSettings settings = new();
        captured!(settings);
        await Assert.That(settings.Highlights.ClipLeadInSeconds).IsEqualTo(20d);
        await Assert.That(settings.Highlights.ClipLeadOutSeconds).IsEqualTo(3d);
        await Assert.That(settings.Highlights.ReelOutputDirectory).IsEqualTo("/reels/out");
        await Assert.That(settings.Highlights.ReelFps).IsEqualTo(120);
        await Assert.That(settings.Highlights.ReelConcatenate).IsFalse();
        await Assert.That(settings.Highlights.ReelBitrateKbps).IsEqualTo(8500);
    }

    // (h2) resolution — the picker seeds from the persisted size, a preset flows to the request Width/
    // Height AND persists, and the Custom sentinel unlocks the width/height fields.
    [Test]
    public async Task Resolution_SeedsSelectsAndPersists()
    {
        // Seed: a persisted 1280×720 matches the 720p preset (not Custom).
        HighlightReelDialogViewModel seeded = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out _,
            new HighlightsSettings
            {
                ReelOutputDirectory = "/out",
                ReelWidth = 1280,
                ReelHeight = 720
            });
        await Assert.That(seeded.SelectedResolution!.IsCustom).IsFalse();
        await Assert.That(seeded.SelectedResolution.Width).IsEqualTo(1280);
        await Assert.That(seeded.IsCustomResolution).IsFalse();

        // Select a preset → request carries its dims, and they persist.
        Action<AppSettings>? captured = null;
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job,
            persist: apply => captured = apply);
        vm.SelectedResolution = vm.ResolutionOptions.First(o => o is { Width: 2560, Height: 1440 });
        vm.GenerateCommand.Execute(null);
        await Assert.That(job.LastRequest!.Width).IsEqualTo(2560);
        await Assert.That(job.LastRequest.Height).IsEqualTo(1440);
        AppSettings settings = new();
        captured!(settings);
        await Assert.That(settings.Highlights.ReelWidth).IsEqualTo(2560);
        await Assert.That(settings.Highlights.ReelHeight).IsEqualTo(1440);
    }

    // (h3) resolution — a Custom size flows through; an ODD custom dimension (invalid for yuv420p) blocks
    // Generate with a banner, and correcting it to an even size clears the block.
    [Test]
    public async Task Resolution_CustomOddValue_BlocksGenerate_ThenClears()
    {
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job);
        vm.SelectedResolution = ReelResolutionOption.Custom;
        await Assert.That(vm.IsCustomResolution).IsTrue();

        vm.CustomWidth = 1921; // odd → invalid
        vm.CustomHeight = 1080;
        await Assert.That(vm.HasError).IsTrue();
        await Assert.That(vm.ErrorBanner!).Contains("even");
        await Assert.That(vm.GenerateCommand.CanExecute(null)).IsFalse();

        vm.CustomWidth = 1922; // even → valid
        await Assert.That(vm.GenerateCommand.CanExecute(null)).IsTrue();
        vm.GenerateCommand.Execute(null);
        await Assert.That(job.LastRequest!.Width).IsEqualTo(1922);
        await Assert.That(job.LastRequest.Height).IsEqualTo(1080);
    }

    // (i) with a live-sync session active, Generate surfaces the interlock confirm instead of starting;
    // Continue then starts + closes. With no session, Generate starts immediately.
    [Test]
    public async Task Interlock_ConfirmsBeforeStartingWhenSessionActive()
    {
        bool closed = false;
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job,
            liveSyncActive: true);
        vm.Closed += (_, _) => closed = true;

        vm.GenerateCommand.Execute(null);
        await Assert.That(vm.ShowInterlockConfirm).IsTrue().Because("a live session must be confirmed first");
        await Assert.That(job.StartCount).IsEqualTo(0);
        await Assert.That(closed).IsFalse();

        vm.ConfirmInterlockCommand.Execute(null);
        await Assert.That(job.StartCount).IsEqualTo(1);
        await Assert.That(closed).IsTrue();

        // No session ⇒ Generate starts immediately (no strip).
        HighlightReelDialogViewModel direct = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job2);
        direct.GenerateCommand.Execute(null);
        await Assert.That(direct.ShowInterlockConfirm).IsFalse();
        await Assert.That(job2.StartCount).IsEqualTo(1);
    }

    // (j) Cancel closes the dialog without starting a job.
    [Test]
    public async Task Cancel_ClosesWithoutStarting()
    {
        bool closed = false;
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job);
        vm.Closed += (_, _) => closed = true;

        vm.CancelCommand.Execute(null);
        await Assert.That(closed).IsTrue();
        await Assert.That(job.StartCount).IsEqualTo(0);
    }

    // (k) The dialog body resolves via the ViewLocator name mapping — a rename would ship a "Not Found"
    // TextBlock in the modal, invisible to build + capture (mirrors the LiveSync flyout guard).
    [Test]
    public async Task DialogView_TypeName_MatchesViewLocatorMapping()
    {
        string mapped = typeof(HighlightReelDialogViewModel).FullName!
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        await Assert.That(mapped).IsEqualTo(typeof(HighlightReelDialogView).FullName);
    }

    // (l) The pane now lives for the whole app run, where the modal was built per invocation. The
    // interlock latch must therefore RE-ARM after every Generate — otherwise the user consents to restarting
    // CS2 once and every later reel starts silently.
    [Test]
    public async Task Interlock_ReArms_After_Every_Generate()
    {
        HighlightReelDialogViewModel vm = New([Sel("/d/a.dem", "10", "p", 7, 5000, "A")], out FakeReelJob job,
            liveSyncActive: true);

        vm.GenerateCommand.Execute(null);
        await Assert.That(vm.ShowInterlockConfirm).IsTrue();
        vm.ConfirmInterlockCommand.Execute(null);
        await Assert.That(job.StartCount).IsEqualTo(1);

        // Second reel, same live session still active → the strip must appear AGAIN.
        vm.GenerateCommand.Execute(null);
        await Assert.That(vm.ShowInterlockConfirm).IsTrue()
            .Because("a one-shot latch in a long-lived pane permanently disarms the guard rail");
        await Assert.That(job.StartCount).IsEqualTo(1);
    }

    // (m) SetSelections replaces the tray live. The output name re-seeds while it is still OUR suggestion —
    // the pane is constructed against an EMPTY tray, so without this it reads "reel" forever — but never
    // overwrites a name the user typed.
    [Test]
    public async Task SetSelections_ReseedsBaseName_UntilTheUserEditsIt()
    {
        HighlightReelDialogViewModel vm = New([], out _);
        await Assert.That(vm.BaseFileName).IsEqualTo("reel");

        vm.SetSelections([Sel("/d/a.dem", "10", "s1mple", 7, 5000, "A")]);
        await Assert.That(vm.BaseFileName).IsEqualTo("dust2_s1mple");
        await Assert.That(vm.HasClips).IsTrue();

        vm.BaseFileName = "my_montage";
        vm.SetSelections([Sel("/d/b.dem", "20", "ZywOo", 3, 9000, "B")]);
        await Assert.That(vm.BaseFileName).IsEqualTo("my_montage");
    }

    // (n) Tray ORDER drives the emitted clip sequence — and nothing else. ClipWindows.Coalesce stays
    // order-independent (it groups by demo/player/round), so the same two selections in either order produce
    // the same MERGE and the opposite EMISSION order.
    [Test]
    public async Task SelectionOrder_DrivesEmittedOrder_WithoutChangingTheMerge()
    {
        HighlightSelection a = Sel("/d/a.dem", "10", "s1mple", 7, 5000, "A");
        HighlightSelection b = Sel("/d/b.dem", "20", "ZywOo", 3, 9000, "B");

        HighlightReelDialogViewModel forward = New([a, b], out FakeReelJob jf);
        forward.GenerateCommand.Execute(null);
        await Assert.That(jf.LastRequest!.Clips.Select(c => c.DemoPath).ToList())
            .IsEquivalentTo(new List<string>
            {
                "/d/a.dem",
                "/d/b.dem"
            });

        HighlightReelDialogViewModel reversed = New([b, a], out FakeReelJob jr);
        reversed.GenerateCommand.Execute(null);
        await Assert.That(jr.LastRequest!.Clips.Select(c => c.DemoPath).ToList())
            .IsEquivalentTo(new List<string>
            {
                "/d/b.dem",
                "/d/a.dem"
            });

        await Assert.That(reversed.ClipsHeader).IsEqualTo(forward.ClipsHeader)
            .Because("order must not reach ClipWindows.Coalesce");
    }

    // (o) Casing-variant paths: Coalesce upper-cases its group key, so the CONTRIBUTOR lookup has to as
    // well. It did not, and a clip built from a differently-cased path merged correctly while rendering with
    // ZERO contributors — an empty tray block under a real clip.
    [Test]
    public async Task CasingVariantPaths_StillMapContributorsOntoTheirClip()
    {
        HighlightSelection lower = Sel("/d/a.dem", "10", "s1mple", 7, 5000, "A");
        HighlightSelection upper = Sel("/D/A.DEM", "10", "s1mple", 7, 5600, "B");
        HighlightReelDialogViewModel vm = New([lower, upper], out _);

        await Assert.That(vm.ClipGroups.Sum(g => g.Rows.Count)).IsEqualTo(1);
        await Assert.That(vm.ClipGroups[0].Rows[0].Contributors.Count).IsEqualTo(2);
    }

    // A fake reel job that records the hand-off (Start payload) without launching anything.
    private sealed class FakeReelJob : IReelJobService
    {
        public ReelRequest? LastRequest { get; private set; }
        public int StartCount { get; private set; }
        public ReelJobStatus Status { get; private set; } = ReelJobStatus.Idle;

        public event EventHandler<ReelJobStatus>? StatusChanged;

        public void Start(ReelRequest request)
        {
            LastRequest = request;
            StartCount++;
            Status = new ReelJobStatus(ReelJobPhase.Capturing, 0, request.Clips.Count, null, null, null, []);
            StatusChanged?.Invoke(this, Status);
        }

        public Task CancelAsync() => Task.CompletedTask;

        public void RetryRemaining()
        {
        }
    }
}

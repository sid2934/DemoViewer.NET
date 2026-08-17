#region

using System.Globalization;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.LiveSync;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.Views.LiveSync;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the <see cref="LiveSyncStatusViewModel" /> state→chip + state→flyout mapping
///     (docs/csvg-integration/ux-design.md) over a fake <see cref="ILiveSyncService" />. Pure VM —
///     no headless UI session — so it runs in parallel. Asserts the dot vocabulary (token state + solid
///     vs hollow + pulse), the neutral "CS2 · …" labels, the mutually-exclusive flyout sections, and the
///     speed-lock (entering Synced forces DV playback to 1×).
/// </summary>
public class LiveSyncStatusViewModelTests
{
    private static LiveSyncStatusViewModel New(out FakeLiveSync svc, out PlaybackController playback)
    {
        svc = new FakeLiveSync();
        playback = new PlaybackController();
        return new LiveSyncStatusViewModel(svc, null, playback, () => { });
    }

    // (a) The initial state is seeded from the CURRENT service state in the ctor (not a first transition) —
    // Disconnected ⇒ a SOLID dim "Off" chip and the Off flyout section.
    [Test]
    public async Task InitialState_IsOff_SolidDimDot()
    {
        LiveSyncStatusViewModel vm = New(out _, out _);

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Off);
        await Assert.That(vm.Chip.Label).IsEqualTo("CS2 · Off");
        await Assert.That(vm.Chip.IsHollow).IsFalse().Because("Off is a SOLID dim dot — hollow means only 'inferred'");
        await Assert.That(vm.Chip.IsPulsing).IsFalse();
        await Assert.That(vm.IsOff).IsTrue();
        await Assert.That(vm.IsSynced).IsFalse();
    }

    // (b) The working states pulse an AccentInteractive dot with a neutral "Connecting…"/… label.
    [Test]
    public async Task WorkingStates_PulseAndShowStepText()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Connecting, "Waiting for plugin"));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Working);
        await Assert.That(vm.Chip.IsPulsing).IsTrue();
        await Assert.That(vm.Chip.Label).IsEqualTo("CS2 · Connecting…");
        await Assert.That(vm.IsWorking).IsTrue();
        await Assert.That(vm.StepText).IsEqualTo("Step: Waiting for plugin");
    }

    // (c) Following ⇒ a good (StatPositive) dot; the flyout Synced section; and DV speed is FORCED to 1×
    // on entering Synced (the lock affordance).
    [Test]
    public async Task EnteringSynced_ForcesSpeedTo1x_AndShowsGoodDot()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out PlaybackController playback);
        playback.Speed = 4.0;

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Good);
        await Assert.That(vm.Chip.Label).IsEqualTo("CS2 · Following");
        await Assert.That(vm.IsSynced).IsTrue();
        await Assert.That(playback.Speed).IsEqualTo(1.0).Because("entering Synced locks DV playback to 1×");
    }

    // (d) The inferred pause is the ONE hollow-ring state (green + hollow + "(inferred)" label), distinct
    // from the solid caution Degraded dot. Nothing sets IsInferred yet, but the path must render.
    [Test]
    public async Task InferredPause_IsHollowGreen_NotSolidCaution()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedHolding, IsInferred: true));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Good);
        await Assert.That(vm.Chip.IsHollow).IsTrue().Because("inferred is the only hollow-ring state");
        await Assert.That(vm.Chip.Label).IsEqualTo("CS2 · Paused (inferred)");
        await Assert.That(vm.IsSynced).IsTrue();
    }

    // (e) Degraded ⇒ a solid caution dot (NOT hollow), a neutral chip label, and the Degraded flyout section.
    [Test]
    public async Task Degraded_IsSolidCautionDot()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Degraded));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Degraded);
        await Assert.That(vm.Chip.IsHollow).IsFalse();
        await Assert.That(vm.Chip.Label).IsEqualTo("CS2 · Seek unconfirmed");
        await Assert.That(vm.IsDegraded).IsTrue();
    }

    // (f) Faulted ⇒ an error dot; the flyout Faulted section carries the "Reconnect (relaunch CS2)" action set
    // uniformly (the reason drives the headline, not the action set).
    [Test]
    public async Task Faulted_IsErrorDot_AndHeadlineReflectsReason()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Faulted, "Disconnected — CS2 quit."));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Error);
        await Assert.That(vm.IsFaulted).IsTrue();
        await Assert.That(vm.StateHeadline).IsEqualTo("Disconnected — CS2 quit.");
    }

    // (g) With no demo loaded (null module context) the informed-launch action is disabled with the honest
    // "Open a demo first." reason, and no path warning is shown (no demo to warn about).
    [Test]
    public async Task NoDemo_DisablesEnable_WithReason()
    {
        LiveSyncStatusViewModel vm = New(out _, out _);

        await Assert.That(vm.CanEnable).IsFalse();
        await Assert.That(vm.EnableDisabledReason).IsEqualTo("Open a demo first.");
        await Assert.That(vm.ShowNoPathWarning).IsFalse();
    }

    // (h) The flyout sections are mutually exclusive — exactly one is visible per state.
    [Test]
    public async Task FlyoutSections_AreMutuallyExclusive()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);

        foreach (LiveSyncStateKind kind in Enum.GetValues<LiveSyncStateKind>())
        {
            svc.Raise(new LiveSyncState(kind));
            int visible = new[]
            {
                vm.IsOff, vm.IsWorking, vm.IsConnectedIdle, vm.IsSynced, vm.IsDegraded, vm.IsFaulted, vm.IsSuspended
            }.Count(b => b);
            await Assert.That(visible).IsEqualTo(1).Because($"exactly one flyout section for {kind}");
        }
    }

    // (i) The chip flyout renders via the app ViewLocator, which maps the VM's full type name to the view's
    // by replacing "ViewModel"→"View". Assert that mapping lands EXACTLY on the real view type — a rename that
    // broke it would otherwise ship a "Not Found" TextBlock in the flyout, invisible to build + capture.
    [Test]
    public async Task FlyoutView_TypeName_MatchesViewLocatorMapping()
    {
        string mapped = typeof(LiveSyncStatusViewModel).FullName!
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        await Assert.That(mapped).IsEqualTo(typeof(LiveSyncStatusView).FullName)
            .Because("the ViewLocator resolves the flyout body from the status VM by this name mapping");
    }

    // (j) Offered restore: a crashed prior session's leftovers surface the Off-flyout offer once the
    // construction-time probe lands; the Restore command clears the install (fake: flips the flag) and the
    // offer disappears.
    [Test]
    public async Task LeftoverInstallModifications_SurfaceOfferedRestore_AndRestoreClearsIt()
    {
        FakeLiveSync svc = new()
        {
            LeftoverModifications = true
        };
        PlaybackController playback = new();
        LiveSyncStatusViewModel vm = new(svc, null, playback, () => { });

        // The probe is fire-and-forget from the ctor — wait for it to land.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.ShowLeftoverRestoreOffer && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Assert.That(vm.ShowLeftoverRestoreOffer).IsTrue();

        await vm.RestoreInstallCommand.ExecuteAsync(null);

        await Assert.That(vm.ShowLeftoverRestoreOffer).IsFalse();
        await Assert.That(svc.LeftoverModifications).IsFalse();
        await Assert.That(vm.RestoreFailureText).IsNull();
    }

    // (k) v1.0-baseline note: a plugin that advertised NOTHING (Capabilities.None ⇒
    // IsV10Baseline) shows the "update CSVG for exact pause sync" note in the Synced flyout section.
    [Test]
    public async Task V10BaselinePlugin_ShowsCautionNote_InSynced()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);
        svc.Capabilities = LiveSyncCapabilities.None;

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));

        await Assert.That(vm.IsSynced).IsTrue();
        await Assert.That(vm.ShowV10BaselineNote).IsTrue()
            .Because("a v1.0 plugin advertised no capabilities ⇒ the update-CSVG note");
    }

    // (l) The same note surfaces in the Degraded section (both carry the versions/capability row).
    [Test]
    public async Task V10BaselinePlugin_ShowsCautionNote_InDegraded()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);
        svc.Capabilities = LiveSyncCapabilities.None;

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Degraded));

        await Assert.That(vm.IsDegraded).IsTrue();
        await Assert.That(vm.ShowV10BaselineNote).IsTrue();
    }

    // (m) A PARTIAL capability set (something advertised) is NOT the v1.0 baseline — no note (the flyout stays
    // lean; v1 deliberately doesn't enumerate a capability matrix).
    [Test]
    public async Task PartialCapabilities_ShowNoBaselineNote()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);
        svc.Capabilities = new LiveSyncCapabilities(
            true, false, false, false, false,
            false, false, false, false);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));

        await Assert.That(vm.ShowV10BaselineNote).IsFalse()
            .Because("partial capabilities are not the v1.0 baseline");
    }

    // (n) No session / not reported (null Capabilities) ⇒ no baseline note.
    [Test]
    public async Task NullCapabilities_ShowNoBaselineNote()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));

        await Assert.That(vm.ShowV10BaselineNote).IsFalse();
    }

    // (o) The ~2 Hz position refresh runs ONLY while a Synced sub-state is current: it starts on entering
    // any Synced kind and stops on leaving. Asserts the start/stop DECISION (IsPositionTimerRunning), never a
    // real tick — the pure-VM test has no dispatcher pump (that is why the decision is separated out).
    [Test]
    public async Task PositionRefreshTimer_RunsOnlyWhileSynced()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);
        await Assert.That(vm.IsPositionTimerRunning).IsFalse().Because("Off ⇒ no position refresh");

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));
        await Assert.That(vm.IsPositionTimerRunning).IsTrue().Because("Synced ⇒ the refresh runs");

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedHolding));
        await Assert.That(vm.IsPositionTimerRunning).IsTrue().Because("still Synced ⇒ still running");

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Degraded));
        await Assert.That(vm.IsPositionTimerRunning).IsFalse().Because("leaving Synced ⇒ stopped");

        svc.Raise(LiveSyncState.Disconnected);
        await Assert.That(vm.IsPositionTimerRunning).IsFalse();
    }

    // (p) Entering Synced reads LastCs2DemoTick into the flyout Position line immediately (the timer keeps it
    // fresh thereafter). Formatted with the VM's own culture to stay culture-agnostic.
    [Test]
    public async Task Synced_ReadsPositionFromLastCs2DemoTick()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);
        svc.LastCs2DemoTick = 54321;

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));

        string expected = "tick " + 54321L.ToString("N0", CultureInfo.CurrentCulture);
        await Assert.That(vm.PositionText).IsEqualTo(expected);
    }

    // (q) The 2D HUD projection (ILiveSyncHudState): Disconnected ⇒ inactive/None; Following ⇒ active,
    // Good, pulsing, "CS2 · Following"; inferred ⇒ Good + hollow; Degraded ⇒ Degraded; Faulted ⇒ Error.
    [Test]
    public async Task HudProjection_MapsStateToActiveDotAndLabel()
    {
        LiveSyncStatusViewModel vm = New(out FakeLiveSync svc, out _);
        ILiveSyncHudState hud = vm;

        await Assert.That(hud.IsActive).IsFalse().Because("Disconnected ⇒ the 2D indicator is hidden");
        await Assert.That(hud.Dot).IsEqualTo(LiveSyncHudDot.None);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));
        await Assert.That(hud.IsActive).IsTrue();
        await Assert.That(hud.Dot).IsEqualTo(LiveSyncHudDot.Good);
        await Assert.That(hud.IsPulsing).IsTrue();
        await Assert.That(hud.Label).IsEqualTo("CS2 · Following");

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedHolding, IsInferred: true));
        await Assert.That(hud.Dot).IsEqualTo(LiveSyncHudDot.Good);
        await Assert.That(hud.IsHollow).IsTrue().Because("inferred pause is the hollow-ring HUD dot");

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Degraded));
        await Assert.That(hud.Dot).IsEqualTo(LiveSyncHudDot.Degraded);

        svc.Raise(new LiveSyncState(LiveSyncStateKind.Faulted));
        await Assert.That(hud.Dot).IsEqualTo(LiveSyncHudDot.Error);
    }

    // (r) The chrome.livesync gate folds into the HUD IsActive: with the gate returning false the
    // indicator is inactive even in a live Synced state; NotifyHudGateChanged re-projects on a gate flip.
    [Test]
    public async Task HudProjection_GateOff_HidesIndicator_UntilGateFlips()
    {
        FakeLiveSync svc = new();
        PlaybackController playback = new();
        bool gate = false;
        LiveSyncStatusViewModel vm = new(svc, null, playback, () => { },
            isHudGateEnabled: () => gate);
        ILiveSyncHudState hud = vm;

        svc.Raise(new LiveSyncState(LiveSyncStateKind.SyncedFollowing));
        await Assert.That(hud.IsActive).IsFalse().Because("gate off ⇒ no indicator even when synced");

        gate = true;
        vm.NotifyHudGateChanged();
        await Assert.That(hud.IsActive).IsTrue().Because("a gate flip re-projects IsActive");
    }

    // Fake App-side engine: a mutable current state + an inline StateChanged raise (real event → the VM's
    // handler runs, and there is no CS0067). Matches the FIXED ILiveSyncService contract surface.
    private sealed class FakeLiveSync : ILiveSyncService
    {
        /// <summary>Set true to exercise the Off-flyout offered-restore affordance.</summary>
        public bool LeftoverModifications { get; set; }

        public LiveSyncState State { get; private set; } = LiveSyncState.Disconnected;
        public long? LastCs2DemoTick { get; set; }
        public LiveSyncVersionInfo? Versions { get; set; }
        public LiveSyncCapabilities? Capabilities { get; set; }

        public event EventHandler<LiveSyncStateChangedEventArgs>? StateChanged;

        public Task EnableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> VerifyMomentAsync(int frameClockTick, int preRollTicks = 192, int postRollTicks = 64,
            string? spectateName = null, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> HasLeftoverInstallModificationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LeftoverModifications);

        public Task RestoreInstallAsync(CancellationToken cancellationToken = default)
        {
            LeftoverModifications = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(LiveSyncState next)
        {
            LiveSyncState prev = State;
            State = next;
            StateChanged?.Invoke(this, new LiveSyncStateChangedEventArgs(prev, next));
        }
    }
}

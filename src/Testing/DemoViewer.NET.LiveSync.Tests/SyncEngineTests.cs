#region

using DemoViewer.NET.Services.LiveSync;
using static Cs2VideoGenerator.Core.Proto.DemoPlaybackStatusChange.Types;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="SyncEngine" /> unit battery against a fake
///     <see cref="ISyncClient" /> — no Avalonia, no CSVG process. Covers the core invariants:
///     minimal command set (close-before-load on demo change), single-slot latest-wins seeks,
///     no position pushes while playing, the v1.0 ledger (pre-echo provisional confirm + grace
///     revocation, expiry → Degraded with adopted CS2 truth), play/pause echo confirmation, and
///     the pathless-demo degradation.
/// </summary>
[Category("Unit")]
public class SyncEngineTests
{
    // SeekTimeout/PlayPauseTimeout stay well clear of the ledger tests' sleep+grace sequences
    // (~170 ms of nominal work): the Unit classes are NOT serialized against the port-50051
    // integration key, so these timers race a loaded machine — 500 ms margins flaked.
    private static readonly SyncTimings _fast = new(
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2));

    // ── v1.1 capability modes ────────────────────────────────────────────────

    private static readonly LiveSyncCapabilities _v11 = LiveSyncCapabilities.None with
    {
        CommandAck = true,
        SeekAck = true,
        EnginePauseDetection = true,
        UserDemoUi = true
    };

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Drives a fresh engine to Synced·Holding on <paramref name="path" /> at <paramref name="tick" />.</summary>
    private static async Task<(SyncEngine Engine, FakeSyncClient Client, StatusProbe Probe)> SyncedHoldingAsync(
        string path = "/demos/a.dem", long tick = 500)
    {
        FakeSyncClient client = new();
        SyncEngine engine = new(client, _fast, LiveSyncCapabilities.None);
        StatusProbe probe = new();
        probe.Attach(engine);

        engine.SetDesiredDemo(path, tick, false);
        await WaitForAsync(() => client.Count("load:") == 1, "load command");
        // The load contract leaves the demo PAUSED at tick 0 — only the position fixup follows.
        await WaitForAsync(() => client.Count("seek:") >= 1, "position fixup seek");

        engine.NotifyTick(tick);
        await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "Synced·Holding");
        return (engine, client, probe);
    }

    [Test]
    public async Task DemoIntent_LoadsThenSeeks_NoRedundantPause()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            IReadOnlyList<string> log = client.Snapshot();
            int load = log.ToList().FindIndex(e => e.StartsWith("load:", StringComparison.Ordinal));
            int seek = log.ToList().FindIndex(e => e.StartsWith("seek:", StringComparison.Ordinal));

            await Assert.That(load).IsGreaterThanOrEqualTo(0);
            await Assert.That(seek).IsGreaterThan(load);
            await Assert.That(log[seek]).IsEqualTo("seek:500");
            // The load completes paused at tick 0 (client contract) — a pause push would be
            // redundant command noise.
            await Assert.That(client.Count("pause")).IsEqualTo(0);
            await Assert.That(probe.Current.Kind).IsEqualTo(LiveSyncStateKind.SyncedHolding);
        }
    }

    [Test]
    public async Task SeekStorm_CoalescesToLatestTarget()
    {
        (SyncEngine engine, FakeSyncClient client, _) = await SyncedHoldingAsync();
        await using (engine)
        {
            int before = client.Count("seek:");
            for (int t = 1000; t <= 1050; t++)
            {
                engine.SetDesiredTick(t);
            }

            await WaitForAsync(
                () => client.Snapshot().Any(e => e == "seek:1050"),
                "final seek target sent");

            int sent = client.Count("seek:") - before;
            // 51 rapid targets through the settle window + single-slot pipeline → a handful of
            // sends at most, and the LAST one is the final target.
            await Assert.That(sent).IsLessThan(6);
            await Assert.That(client.Snapshot().Last(e => e.StartsWith("seek:", StringComparison.Ordinal)))
                .IsEqualTo("seek:1050");
        }
    }

    [Test]
    public async Task SeekPreEcho_ContradictedWithinGrace_IsRevoked_ThenReconfirmed()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredTick(5000);
            await WaitForAsync(() => client.Snapshot().Contains("seek:5000"), "seek sent");
            await Assert.That(probe.Current.Kind).IsEqualTo(LiveSyncStateKind.SyncedSeekPending);

            // v1.0 pre-echo: target tick arrives instantly, then the stream contradicts it.
            engine.NotifyTick(5000);
            engine.NotifyTick(600); // > contradiction distance → provisional revoked
            await Task.Delay(_fast.SeekConfirmGrace + TimeSpan.FromMilliseconds(40));
            await Assert.That(probe.Current.Kind).IsEqualTo(LiveSyncStateKind.SyncedSeekPending);

            // The real seek lands within tolerance and holds through the grace window.
            engine.NotifyTick(4990);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "confirmed after grace");
        }
    }

    [Test]
    public async Task SeekUnconfirmed_TimesOutToDegraded()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredTick(9000);
            await WaitForAsync(() => client.Snapshot().Contains("seek:9000"), "seek sent");

            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.Degraded, "degraded on expiry");
            await Assert.That(probe.Current.Reason).Contains("Seek unconfirmed");
        }
    }

    [Test]
    public async Task PlayPause_ConfirmedByStatusEcho()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredPlaying(true);
            await WaitForAsync(() => client.Count("play") >= 1, "play sent");

            engine.NotifyPlaybackStatus(DemoPlaybackStatus.Playing);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedFollowing, "following");

            engine.SetDesiredPlaying(false);
            await WaitForAsync(() => client.Count("pause") >= 1, "pause sent");
            engine.NotifyPlaybackStatus(DemoPlaybackStatus.Paused);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "holding");
        }
    }

    [Test]
    public async Task PlayWithoutEcho_TimesOutToDegraded()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredPlaying(true);
            await WaitForAsync(() => client.Count("play") >= 1, "play sent");

            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.Degraded, "degraded on missing echo");
            await Assert.That(probe.Current.Reason).Contains("did not confirm");
        }
    }

    [Test]
    public async Task DemoChange_ClosesOldDemoBeforeLoadingNew()
    {
        (SyncEngine engine, FakeSyncClient client, _) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredDemo("/demos/b.dem", 0, false);
            await WaitForAsync(() => client.Count("load:/demos/b.dem") == 1, "second load");

            List<string> log = [.. client.Snapshot()];
            int close = log.FindIndex(e => e == "close");
            int loadB = log.FindIndex(e => e == "load:/demos/b.dem");
            await Assert.That(close).IsGreaterThanOrEqualTo(0);
            await Assert.That(loadB).IsGreaterThan(close);
        }
    }

    [Test]
    public async Task DemoChange_AbandonsInFlightSeek()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredTick(7000);
            await WaitForAsync(() => client.Snapshot().Contains("seek:7000"), "in-flight seek");

            engine.SetDesiredDemo("/demos/b.dem", 0, false);
            await WaitForAsync(() => client.Count("load:/demos/b.dem") == 1, "load of B");

            // Confirm demo B's own post-load fixups (they are legitimate commands with their own
            // ledger entries) so the only possible Degraded source left is the ABANDONED /a.dem
            // seek's expiry timer — which the demo change must have disarmed.
            engine.NotifyPlaybackStatus(DemoPlaybackStatus.Paused);
            engine.NotifyTick(0);
            await Task.Delay(_fast.SeekTimeout + TimeSpan.FromMilliseconds(150));
            await Assert.That(probe.Current.Kind).IsNotEqualTo(LiveSyncStateKind.Degraded);
            await Assert.That(probe.Current.Kind).IsEqualTo(LiveSyncStateKind.SyncedHolding);
        }
    }

    [Test]
    public async Task WhilePlaying_NoPositionPushes()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredPlaying(true);
            await WaitForAsync(() => client.Count("play") >= 1, "play sent");
            engine.NotifyPlaybackStatus(DemoPlaybackStatus.Playing);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedFollowing, "following");

            int seeksBefore = client.Count("seek:");
            // Stale discrete intent while both sides play must never become a seek storm —
            // drift is the servo's job.
            engine.SetDesiredTick(123_456);
            await Task.Delay(_fast.Settle * 4);
            await Assert.That(client.Count("seek:")).IsEqualTo(seeksBefore);
        }
    }

    [Test]
    public async Task PathlessDemo_DegradesWithHonestCopy()
    {
        FakeSyncClient client = new();
        SyncEngine engine = new(client, _fast, LiveSyncCapabilities.None);
        StatusProbe probe = new();
        probe.Attach(engine);
        await using (engine)
        {
            engine.NoteDemoPathUnavailable();
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.Degraded, "degraded");
            await Assert.That(probe.Current.Reason).Contains("local file path");

            // No commands were issued for a demo CSVG cannot open.
            await Assert.That(client.Snapshot().Count).IsEqualTo(0);
        }
    }

    private static async Task<(SyncEngine Engine, FakeSyncClient Client, StatusProbe Probe)> SyncedHoldingV11Async()
    {
        FakeSyncClient client = new();
        SyncEngine engine = new(client, _fast, _v11);
        StatusProbe probe = new();
        probe.Attach(engine);

        engine.SetDesiredDemo("/demos/a.dem", 500, false);
        // The load completes paused at tick 0 (transient Holding), then the settle-window
        // reconcile issues the acked fixup seek, which confirms itself (fake acks true) — wait
        // for the seek, not the first Holding.
        await WaitForAsync(() => client.Count("seekack:") >= 1, "acked position fixup");
        await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "Holding via acked seek");
        return (engine, client, probe);
    }

    [Test]
    public async Task V11_AckedSeek_ConfirmsWithoutAnyTicks_AndFoldsPause()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingV11Async();
        await using (engine)
        {
            // The fixup seek used the acked path with the pause folded in, and the v1.0 unacked
            // path was never touched.
            await Assert.That(client.Count("seekack:")).IsGreaterThanOrEqualTo(1);
            await Assert.That(client.Snapshot().First(e => e.StartsWith("seekack:", StringComparison.Ordinal)))
                .IsEqualTo("seekack:500:pause");
            await Assert.That(client.Count("seek:")).IsEqualTo(0);

            // A later discrete seek also self-confirms.
            engine.SetDesiredTick(9000);
            await WaitForAsync(() => client.Snapshot().Contains("seekack:9000:pause"), "acked far seek");
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "self-confirmed");
        }
    }

    [Test]
    public async Task V11_AckedSeekFailure_Degrades()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingV11Async();
        await using (engine)
        {
            client.AckedSeekResult = false;
            engine.SetDesiredTick(9000);
            await WaitForAsync(() => client.Snapshot().Contains("seekack:9000:pause"), "acked seek sent");
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.Degraded, "degraded on nack");
            await Assert.That(probe.Current.Reason).Contains("Seek unconfirmed");
        }
    }

    [Test]
    public async Task V11_EnginePauseTruth_RidesTickStream_AndConfirmsToggles()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingV11Async();
        await using (engine)
        {
            engine.SetDesiredPlaying(true);
            await WaitForAsync(() => client.Count("play") >= 1, "play sent");

            // No status echo at all — the per-tick engine-truth pause flag confirms the toggle.
            engine.NotifyTick(600, false);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedFollowing, "following via tick truth");

            engine.SetDesiredPlaying(false);
            await WaitForAsync(() => client.Count("pause") >= 1, "pause sent");
            engine.NotifyTick(660, true);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "holding via tick truth");
        }
    }

    [Test]
    public async Task V11_Load_RequestsInteractiveDemoUi_OnlyWhenAdvertised()
    {
        (SyncEngine v11Engine, FakeSyncClient v11Client, _) = await SyncedHoldingV11Async();
        await using (v11Engine)
        {
            await Assert.That(v11Client.LastLoadInteractiveUi).IsTrue();
        }

        (SyncEngine v10Engine, FakeSyncClient v10Client, _) = await SyncedHoldingAsync();
        await using (v10Engine)
        {
            await Assert.That(v10Client.LastLoadInteractiveUi).IsFalse();
        }
    }

    // ── Speed mirroring ──────────────────────────────────────────────────────

    [Test]
    public async Task Timescale_MirroredOnlyUnderCapability_AndDeduped()
    {
        // v1.0: ignored entirely.
        (SyncEngine v10, FakeSyncClient v10Client, _) = await SyncedHoldingAsync();
        await using (v10)
        {
            v10.SetDesiredTimescale(2.0);
            await Task.Delay(_fast.Settle * 4);
            await Assert.That(v10Client.Count("timescale:")).IsEqualTo(0);
        }

        // v1.1 timescale-set: mirrored, clamped, and duplicate values are not resent.
        FakeSyncClient client = new();
        SyncEngine engine = new(client, _fast, _v11 with
        {
            TimescaleSet = true
        });
        StatusProbe probe = new();
        probe.Attach(engine);
        await using (engine)
        {
            engine.SetDesiredDemo("/demos/a.dem", 500, false);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "holding");

            engine.SetDesiredTimescale(2.0);
            await WaitForAsync(() => client.Snapshot().Contains("timescale:2"), "timescale sent");

            engine.SetDesiredTimescale(2.0); // duplicate — deduped
            await Task.Delay(_fast.Settle * 4);
            await Assert.That(client.Count("timescale:")).IsEqualTo(1);

            engine.SetDesiredTimescale(100.0); // clamped to the DV speed ceiling
            await WaitForAsync(() => client.Snapshot().Contains("timescale:8"), "clamped timescale sent");
        }
    }

    // ── Spectate mirroring ───────────────────────────────────────────────────

    [Test]
    public async Task Spectate_SentOnChange_DedupedOnRepeat()
    {
        (SyncEngine engine, FakeSyncClient client, _) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredSpectator("s1mple");
            await WaitForAsync(() => client.Snapshot().Contains("spec:s1mple"), "spectate sent");

            engine.SetDesiredSpectator("s1mple"); // repeat — deduped
            await Task.Delay(_fast.Settle * 4);
            await Assert.That(client.Count("spec:")).IsEqualTo(1);

            engine.SetDesiredSpectator("ZywOo"); // change — sent
            await WaitForAsync(() => client.Snapshot().Contains("spec:ZywOo"), "second spectate sent");
            await Assert.That(client.Count("spec:")).IsEqualTo(2);

            engine.SetDesiredSpectator("   "); // blank — ignored
            await Task.Delay(_fast.Settle * 4);
            await Assert.That(client.Count("spec:")).IsEqualTo(2);
        }
    }

    // ── Inbound signals ──────────────────────────────────────────────────────

    [Test]
    public async Task V10_InferredPause_IsHollowHolding_AndTickEvidenceClearsIt()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.SetDesiredPlaying(true);
            await WaitForAsync(() => client.Count("play") >= 1, "play sent");
            engine.NotifyPlaybackStatus(DemoPlaybackStatus.Playing);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedFollowing, "following");

            engine.NoteInferredPause();
            await WaitForAsync(
                () => probe.Current is { Kind: LiveSyncStateKind.SyncedHolding, IsInferred: true },
                "inferred (hollow) holding");

            // A tick is evidence CS2 ticks again — the v1.0 path exits inference to Following.
            engine.NotifyTick(700);
            await WaitForAsync(
                () => probe.Current is { Kind: LiveSyncStateKind.SyncedFollowing, IsInferred: false },
                "confirmed following after tick evidence");
        }
    }

    [Test]
    public async Task RemoteDemoChanged_Degrades_PausesReconciliation_UntilFreshIntent()
    {
        (SyncEngine engine, FakeSyncClient client, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            int loadsBefore = client.Count("load:");
            engine.NoteRemoteDemoChanged("/cs2/replays/user_pick.dem");

            await WaitForAsync(
                () => probe.Current is { Kind: LiveSyncStateKind.Degraded, RemoteDemoPath: "/cs2/replays/user_pick.dem" },
                "degraded with the CS2-side demo path");
            await Assert.That(probe.Current.Reason).Contains("user_pick.dem");

            // D7: the reconciler must NOT fight the user by re-pushing DV's demo uninvited.
            await Task.Delay(_fast.Settle * 6);
            await Assert.That(client.Count("load:")).IsEqualTo(loadsBefore);

            // Open-in-DV = a fresh full-intent push for the demo CS2 ALREADY plays: the offer
            // clears, no CS2 reload is issued (believed == desired — adoption, not a push), and
            // only the position fixup flows.
            engine.SetDesiredDemo("/cs2/replays/user_pick.dem", 0, false);
            await WaitForAsync(() => probe.Current.RemoteDemoPath is null, "offer cleared");
            await WaitForAsync(() => client.Snapshot().Contains("seek:0"), "position fixup seek");
            await Assert.That(client.Count("load:")).IsEqualTo(loadsBefore);

            engine.NotifyTick(0);
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.SyncedHolding, "re-synced on adopted demo");
        }
    }

    [Test]
    public async Task RemoteDemoStateUnknown_DegradesWithResyncCopy()
    {
        (SyncEngine engine, _, StatusProbe probe) = await SyncedHoldingAsync();
        await using (engine)
        {
            engine.NoteRemoteDemoStateUnknown();
            await WaitForAsync(() => probe.Current.Kind == LiveSyncStateKind.Degraded, "degraded");
            await Assert.That(probe.Current.Reason).Contains("Re-sync");
        }
    }

    [Test]
    public async Task NoDemoIntent_StaysConnectedIdle_WithNoCommands()
    {
        FakeSyncClient client = new();
        SyncEngine engine = new(client, _fast, LiveSyncCapabilities.None);
        StatusProbe probe = new();
        probe.Attach(engine);
        await using (engine)
        {
            engine.SetDesiredDemo(null, null, false);
            await Task.Delay(_fast.Settle * 4);

            await Assert.That(probe.Current.Kind).IsEqualTo(LiveSyncStateKind.ConnectedIdle);
            await Assert.That(client.Snapshot().Count).IsEqualTo(0);
        }
    }

    private sealed class FakeSyncClient : ISyncClient
    {
        private readonly Lock _gate = new();
        private readonly List<string> _log = [];

        /// <summary>Result the acked-seek path returns (v1.1 mode tests).</summary>
        public volatile bool AckedSeekResult = true;

        /// <summary>The interactive-demo-UI flag of the most recent load (v1.1 assertion seam).</summary>
        public volatile bool LastLoadInteractiveUi;

        public Task LoadDemoAsync(string demoPath, bool interactiveDemoUi, CancellationToken cancellationToken)
        {
            LastLoadInteractiveUi = interactiveDemoUi;
            return Record($"load:{demoPath}");
        }

        public Task CloseDemoAsync(CancellationToken cancellationToken) => Record("close");

        public Task ResumeDemoAsync(CancellationToken cancellationToken) => Record("play");

        public Task PauseDemoAsync(CancellationToken cancellationToken) => Record("pause");

        public Task SetDemoTickAsync(int tick, CancellationToken cancellationToken) => Record($"seek:{tick}");

        public async Task<bool> SetDemoTickAckedAsync(int tick, bool? pauseAfterSeek,
            CancellationToken cancellationToken)
        {
            await Record($"seekack:{tick}:{(pauseAfterSeek == true ? "pause" : "keep")}");
            return AckedSeekResult;
        }

        public Task SetTimescaleAsync(float timescale, CancellationToken cancellationToken) =>
            Record($"timescale:{timescale:0.##}");

        public Task SetSpectatorAsync(string playerName, CancellationToken cancellationToken) =>
            Record($"spec:{playerName}");

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }

        public int Count(string prefix) => Snapshot().Count(e => e.StartsWith(prefix, StringComparison.Ordinal));

        private Task Record(string entry)
        {
            lock (_gate)
            {
                _log.Add(entry);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StatusProbe
    {
        private volatile LiveSyncState _current = LiveSyncState.Disconnected;

        public LiveSyncState Current => _current;

        public void Attach(SyncEngine engine) => engine.StatusChanged += s => _current = s;
    }
}

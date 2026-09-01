#region

using CS2DemoKit.Parser;
using Cs2VideoGenerator.Core;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.LiveSync;
using TUnit.Core.Exceptions;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     Verify-in-CS2 against the real mock ("mock test for range
///     arrival"): with an engine synced and holding, a verification suspends the pipeline (the
///     status probe reads Seeking… for the whole range), plays the pre/post-roll range live
///     (real <c>PlayTickRangeAsync</c>, deterministic paused arrival), and on completion the
///     engine realigns back to Holding at the trigger. The failure branch runs the runner with
///     no demo loaded: the range API never throws. The failure comes back as
///     <c>Success=false</c> with the error copy.
/// </summary>
[Category("Integration")]
[NotInParallel("csvg-port-50051")]
public class VerificationRunnerMockTests
{
    /// <summary>Identity mapper: frame i ↔ tick i (the mock demo has no real frames).</summary>
    private static TickMapper IdentityMapper(int ticks)
    {
        DemoFrame[] frames = new DemoFrame[ticks];
        int[] boundaries = new int[ticks];
        for (int i = 0; i < ticks; i++)
        {
            frames[i] = new DemoFrame
            {
                Command = "dem_packet",
                FrameNumber = i,
                HeaderLength = 0,
                IsCompressed = false,
                RawLength = 0,
                RawStart = 0,
                ServerTick = i
            };
            boundaries[i] = i;
        }

        return new TickMapper(frames, boundaries);
    }

    private static async Task WaitForKindAsync(StatusProbe probe, LiveSyncStateKind kind, string what,
        int timeoutMs, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (probe.Current.Kind != kind)
        {
            if (probe.Current.Kind == LiveSyncStateKind.Degraded)
            {
                throw new InvalidOperationException($"degraded while waiting for {what}: {probe.Current.Reason}");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"timed out waiting for {what}; engine is {probe.Current.Kind} ({probe.Current.Reason})");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task VerifyMoment_PlaysRange_SuspendsPipeline_AndRealignsToHolding(
        CancellationToken cancellationToken)
    {
        CsvgWebHost host;
        try
        {
            host = await CsvgWebHost.StartAsync(new LiveSyncSettings
                {
                    MockMode = true
                }, null,
                cancellationToken);
        }
        catch (LiveSyncPortInUseException ex)
        {
            throw new SkipTestException($"port {CsvgWebHost.GrpcPort} is owned by another process: {ex.Message}");
        }

        await using (host)
        {
            CsvgVideoSession session = host.Session;
            await session.StartWatchAsync(cancellationToken: cancellationToken);

            string demoPath = Path.Combine(Path.GetTempPath(), $"livesync-verify-{Guid.NewGuid():N}.dem");
            await File.WriteAllBytesAsync(demoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00],
                cancellationToken);
            try
            {
                TickMapper mapper = IdentityMapper(10_000);
                LiveSyncCapabilities capabilities = LiveSyncService.MapCapabilities(session.Engine.PluginCapabilities);
                SyncEngine engine = new(new CsvgSyncClientAdapter(session), SyncTimings.Default, capabilities);
                StatusProbe probe = new();
                probe.Attach(engine);
                session.TickUpdated += (_, tick) => engine.NotifyTick(tick, session.Engine.LastTickIsPaused);
                session.DemoPlaybackStatusChanged += (_, change) =>
                {
                    engine.NotifyPlaybackStatus(change.NewStatus);
                    return Task.CompletedTask;
                };

                await using (engine)
                {
                    engine.SetDesiredDemo(demoPath, 0, false);
                    await WaitForKindAsync(probe, LiveSyncStateKind.SyncedHolding, "initial Holding",
                        60_000, cancellationToken);

                    // ── The verification (service bracket + UI-free runner core) ──
                    engine.BeginVerification();
                    VerificationRunner.Outcome outcome;
                    try
                    {
                        await Assert.That(probe.Current.Kind).IsEqualTo(LiveSyncStateKind.SyncedSeekPending)
                            .Because("the chip reads Seeking… while the range plays");

                        outcome = await VerificationRunner.RunAsync(
                            session, mapper, 2000,
                            VerificationRunner.DefaultPreRollTicks, VerificationRunner.DefaultPostRollTicks,
                            "s1mple", cancellationToken);
                    }
                    finally
                    {
                        // Engine half: align desired to the trigger, then resume.
                        engine.SetDesiredTick(2000);
                        engine.SetDesiredPlaying(false);
                        engine.EndVerification();
                    }

                    await Assert.That(outcome.Success).IsTrue();
                    await Assert.That(outcome.TargetCs2Tick).IsEqualTo(2000);
                    await Assert.That(outcome.TargetFrameIndex).IsEqualTo(2000);
                    // Deterministic paused arrival: the tick stream reached the range.
                    await Assert.That(session.Engine.LastTick).IsNotNull();
                    await Assert.That(session.Engine.LastTick!.Value).IsGreaterThanOrEqualTo(1800);

                    // Post-verification the pipeline resumes and realigns to Holding at/near the
                    // trigger (the reconciler may issue a corrective seek from end-of-range).
                    await WaitForKindAsync(probe, LiveSyncStateKind.SyncedHolding,
                        "realigned Holding after verification", 60_000, cancellationToken);
                }
            }
            finally
            {
                File.Delete(demoPath);
                await session.StopAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task VerifyMoment_WithNoDemoLoaded_FailsAsSuccessFalse_NeverThrows(
        CancellationToken cancellationToken)
    {
        CsvgWebHost host;
        try
        {
            host = await CsvgWebHost.StartAsync(new LiveSyncSettings
                {
                    MockMode = true
                }, null,
                cancellationToken);
        }
        catch (LiveSyncPortInUseException ex)
        {
            throw new SkipTestException($"port {CsvgWebHost.GrpcPort} is owned by another process: {ex.Message}");
        }

        await using (host)
        {
            CsvgVideoSession session = host.Session;
            await session.StartWatchAsync(cancellationToken: cancellationToken);
            try
            {
                // No demo loaded this session: the range playback cannot run. The runner's
                // contract: every failure mode lands in the Outcome, never a throw.
                VerificationRunner.Outcome outcome = await VerificationRunner.RunAsync(
                    session, IdentityMapper(10_000), 2000,
                    VerificationRunner.DefaultPreRollTicks, VerificationRunner.DefaultPostRollTicks,
                    null, cancellationToken);

                await Assert.That(outcome.Success).IsFalse();
                await Assert.That(outcome.Error).IsNotNull()
                    .Because("the UI surfaces this copy — a silent false is not enough");
            }
            finally
            {
                await session.StopAsync(CancellationToken.None);
            }
        }
    }

    private sealed class StatusProbe
    {
        private volatile LiveSyncState _current = LiveSyncState.Disconnected;

        public LiveSyncState Current => _current;

        public void Attach(SyncEngine engine) => engine.StatusChanged += s => _current = s;
    }
}

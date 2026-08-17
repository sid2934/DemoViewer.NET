#region

using Cs2VideoGenerator.Core;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.LiveSync;
using TUnit.Core.Exceptions;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     Full outbound-pipeline integration: the real
///     <see cref="SyncEngine" /> driving the real bundled mock_server through
///     <see cref="CsvgSyncClientAdapter" /> — load → seek+pause fixup → confirmed Holding →
///     play intent → Following → pause intent → Holding → discrete seek → SeekPending →
///     confirmed. Everything the unit battery fakes (command execution, status echoes, the tick
///     stream incl. the while-paused cadence the v1.1 mock emits) is real here; only the DV
///     shell (observer) is absent — desired state is pushed directly, engine-level.
/// </summary>
[Category("Integration")]
[NotInParallel("csvg-port-50051")]
public class SyncEngineMockIntegrationTests
{
    private static async Task WaitForKindAsync(StatusProbe probe, LiveSyncStateKind kind, string what,
        int timeoutMs, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (probe.Current.Kind != kind)
        {
            if (probe.Current.Kind == LiveSyncStateKind.Degraded)
            {
                throw new InvalidOperationException(
                    $"engine degraded while waiting for {what}: {probe.Current.Reason}");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"timed out waiting for {what}; engine is {probe.Current.Kind} ({probe.Current.Reason})");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Engine_AgainstRealMock_SyncsSeeksAndTogglesEndToEnd(CancellationToken cancellationToken)
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

            string demoPath = Path.Combine(Path.GetTempPath(), $"livesync-engine-e2e-{Guid.NewGuid():N}.dem");
            await File.WriteAllBytesAsync(demoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00],
                cancellationToken);
            try
            {
                // Real capability latch — the v1.1 mock advertises seek-ack/command-ack etc., so
                // this end-to-end run exercises the ACKED seek path, engine-truth pause, and the
                // interactive-demo-UI load flag against the real wire.
                LiveSyncCapabilities capabilities = LiveSyncService.MapCapabilities(session.Engine.PluginCapabilities);
                SyncEngine engine = new(new CsvgSyncClientAdapter(session), SyncTimings.Default, capabilities);
                StatusProbe probe = new();
                probe.Attach(engine);
                session.TickUpdated += (_, tick) => engine.NotifyTick(tick);
                session.DemoPlaybackStatusChanged += (_, change) =>
                {
                    engine.NotifyPlaybackStatus(change.NewStatus);
                    return Task.CompletedTask;
                };

                await using (engine)
                {
                    // Load intent, paused at demo start — engine must close the loop through the
                    // real mock: load → (mock auto-plays) → seek+pause fixup → echoes + tick
                    // stream confirm → Holding.
                    engine.SetDesiredDemo(demoPath, 0, false);
                    await WaitForKindAsync(probe, LiveSyncStateKind.SyncedHolding,
                        "Synced·Holding after load fixup", 60_000, cancellationToken);

                    // Play intent → real Resume + real Playing echo → Following.
                    engine.SetDesiredPlaying(true);
                    await WaitForKindAsync(probe, LiveSyncStateKind.SyncedFollowing,
                        "Following after play intent", 20_000, cancellationToken);

                    // Pause intent → real Pause + real Paused echo → Holding.
                    engine.SetDesiredPlaying(false);
                    await WaitForKindAsync(probe, LiveSyncStateKind.SyncedHolding,
                        "Holding after pause intent", 20_000, cancellationToken);

                    // Discrete far seek while holding → real SetDemoTick → the mock's
                    // while-paused tick cadence reports the new position. The engine sits in
                    // SyncedHolding through the whole settle window, so a bare Holding wait
                    // would pass even if the seek were never sent — wait for CLIENT-side
                    // evidence of the seek's arrival first, then for the settled state.
                    // Target 3000: far beyond anything the short play leg could have reached
                    // organically, so the arrival evidence can only come from the seek itself.
                    engine.SetDesiredTick(3000);
                    await WaitForAsync(
                        () => session.Engine.LastTick is { } tick
                              && Math.Abs(tick - 3000) <= SyncEngine.SeekConfirmTolerance,
                        "the mock's tick stream to reach the seek target (3000)", 30_000, cancellationToken);
                    await WaitForKindAsync(probe, LiveSyncStateKind.SyncedHolding,
                        "Holding after confirmed discrete seek", 30_000, cancellationToken);
                    await Assert.That(Math.Abs((session.Engine.LastTick ?? long.MinValue) - 3000))
                        .IsLessThanOrEqualTo(SyncEngine.SeekConfirmTolerance)
                        .Because("the seek must actually arrive at the mock, not just settle locally");
                }
            }
            finally
            {
                File.Delete(demoPath);
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

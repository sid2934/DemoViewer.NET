#region

using Cs2VideoGenerator.Core;
using DemoViewer.NET.Configuration;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="CsvgWebHost" /> ↔ bundled mock_server integration battery: DV's private
///     Kestrel host starts on 50051,
///     CSVG's container resolves, a mock watch session connects, v1.1 capabilities arrive, a
///     demo loads and ticks flow, and teardown restores a startable state. Port 50051 is
///     machine-exclusive — the whole class is <c>[NotInParallel]</c> on a shared key and skips
///     when another process already owns the port.
/// </summary>
[Category("Integration")]
[NotInParallel("csvg-port-50051")]
public class CsvgWebHostMockTests
{
    private static LiveSyncSettings MockSettings() => new()
    {
        MockMode = true
    };

    private static async Task<CsvgWebHost> StartHostOrSkipAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await CsvgWebHost.StartAsync(MockSettings(), null, cancellationToken);
        }
        catch (LiveSyncPortInUseException ex)
        {
            throw new SkipTestException(
                $"port {CsvgWebHost.GrpcPort} is owned by another process (CSVG CLI or a stray host): {ex.Message}");
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task MockSession_Connects_LoadsDemo_StreamsTicks_And_TearsDown(CancellationToken cancellationToken)
    {
        await using CsvgWebHost host = await StartHostOrSkipAsync(cancellationToken);
        CsvgVideoSession session = host.Session;
        await Assert.That(session.State).IsEqualTo(CsvgSessionState.Disconnected);

        TaskCompletionSource firstTick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TickUpdated += (_, _) => firstTick.TrySetResult();

        await session.StartWatchAsync(cancellationToken: cancellationToken);
        try
        {
            await Assert.That(session.State).IsEqualTo(CsvgSessionState.Connected);

            // The v1.1 mock advertises the capability tokens (empty = a v1.0-era binary snuck in).
            await Assert.That(session.Engine.PluginCapabilities.Count).IsGreaterThan(0);

            // v1.1 load validation requires an existing file (A-P4) — mock included.
            string demoPath = Path.Combine(Path.GetTempPath(), $"livesync-webhost-{Guid.NewGuid():N}.dem");
            await File.WriteAllBytesAsync(demoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00],
                cancellationToken);
            try
            {
                await session.LoadDemoAsync(demoPath, cancellationToken: cancellationToken);
                await session.Engine.ResumeDemoAsync(cancellationToken);

                await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                await Assert.That(session.Engine.LastTick).IsNotNull();
            }
            finally
            {
                File.Delete(demoPath);
            }
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
        }

        await Assert.That(session.State).IsEqualTo(CsvgSessionState.Disconnected);
    }

    [Test]
    [Timeout(60_000)]
    public async Task SecondHost_OnSamePort_SurfacesPortInUse(CancellationToken cancellationToken)
    {
        await using CsvgWebHost first = await StartHostOrSkipAsync(cancellationToken);

        await Assert.ThrowsAsync<LiveSyncPortInUseException>(async () =>
            await CsvgWebHost.StartAsync(MockSettings(), null, cancellationToken));
    }
}

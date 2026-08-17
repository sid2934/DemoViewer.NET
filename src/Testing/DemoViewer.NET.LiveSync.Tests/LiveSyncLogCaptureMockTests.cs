#region

using Cs2VideoGenerator.Core;
using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Logging;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     Live-host verification of the telemetry-P1 log gate against the bundled mock_server: the
///     <see cref="OutputLogBridge" /> is the sole provider, and its framework-capture flag must
///     actually suppress / surface the Microsoft(.AspNetCore) + Grpc categories on a RUNNING Kestrel
///     host — proving no residual framework filter survives <c>WebApplication.CreateSlimBuilder</c>
///     to cap those logs above the bridge, and that flipping the flag takes effect with no host
///     restart. Port 50051 is machine-exclusive — skips when another process owns it.
/// </summary>
[Category("Integration")]
[NotInParallel("csvg-port-50051")]
public class LiveSyncLogCaptureMockTests
{
    private readonly List<(LogLevel Level, string Category, string Message)> _captured = [];
    private readonly Lock _gate = new();

    private void Sink(LogLevel level, string category, string message)
    {
        lock (_gate)
        {
            _captured.Add((level, category, message));
        }
    }

    private bool AnyFrameworkCaptured()
    {
        lock (_gate)
        {
            return _captured.Exists(r =>
                r.Category.StartsWith("Microsoft", StringComparison.Ordinal)
                || r.Category.StartsWith("Grpc", StringComparison.Ordinal));
        }
    }

    private void ResetCaptured()
    {
        lock (_gate)
        {
            _captured.Clear();
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task FrameworkCaptureFlag_GatesFrameworkLogs_OnLiveHost(CancellationToken cancellationToken)
    {
        LogLevel minLevel = LogLevel.Trace;
        bool includeFramework = false;

        OutputLogBridge bridge = new(Sink, () => minLevel, () => includeFramework);

        CsvgWebHost host;
        try
        {
            host = await CsvgWebHost.StartAsync(new LiveSyncSettings
            {
                MockMode = true
            }, bridge, cancellationToken);
        }
        catch (LiveSyncPortInUseException ex)
        {
            throw new SkipTestException($"port {CsvgWebHost.GrpcPort} busy: {ex.Message}");
        }

        await using (host)
        {
            CsvgVideoSession session = host.Session;
            TaskCompletionSource firstTick = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.TickUpdated += (_, _) => firstTick.TrySetResult();

            // Phase 1 — framework capture OFF: drive real gRPC traffic (each call is a Kestrel/
            // ASP.NET request that logs at Information), then assert NO framework category surfaced.
            await session.StartWatchAsync(cancellationToken: cancellationToken);
            string demoPath = Path.Combine(Path.GetTempPath(), $"logcap-{Guid.NewGuid():N}.dem");
            await File.WriteAllBytesAsync(demoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00], cancellationToken);
            try
            {
                await session.LoadDemoAsync(demoPath, cancellationToken: cancellationToken);
                await session.Engine.ResumeDemoAsync(cancellationToken);
                await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

                await Assert.That(AnyFrameworkCaptured())
                    .IsFalse(); // bridge drops Microsoft/Grpc while the flag is off

                // Phase 2 — enable framework capture LIVE (no restart), then drive more requests.
                includeFramework = true;
                minLevel = LogLevel.Information;
                ResetCaptured();

                await session.StopAsync(CancellationToken.None);
                // Re-connect to force fresh unary gRPC calls under the flipped flag.
                await session.StartWatchAsync(cancellationToken: cancellationToken);
                await session.LoadDemoAsync(demoPath, cancellationToken: cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken); // let request-finished logs flush

                await Assert.That(AnyFrameworkCaptured())
                    .IsTrue(); // framework lines now surface on the running host
            }
            finally
            {
                File.Delete(demoPath);
            }

            await session.StopAsync(CancellationToken.None);
        }
    }
}

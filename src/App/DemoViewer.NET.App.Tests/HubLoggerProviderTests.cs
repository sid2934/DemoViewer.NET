// CA1848 (prefer LoggerMessage delegates) is a hot-path perf rule; these tests deliberately drive the
// provider through the plain ILogger extension methods, exactly as a caller would.

#pragma warning disable CA1848

#region

using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="HubLoggerProvider" /> coverage — the bridge from the internal ILogger pillar to the
///     unified hub. Facts under test: records append to the hub with the right source tag (derived from
///     category) and level; the master switch and the min-level floor gate live. A synchronous-UI-post
///     hub makes <see cref="DiagnosticsTelemetryHub.Enqueue" /> drain inline, so no dispatcher is needed.
/// </summary>
public class HubLoggerProviderTests
{
    private static DiagnosticsTelemetryHub SyncHub() => new(() => 5000, a => a());

    [Test]
    public async Task Logs_AppendToHub_WithSourceTagAndLevel()
    {
        DiagnosticsTelemetryHub hub = SyncHub();
        HubLoggerProvider provider = new(hub, null, () => LogLevel.Trace, () => true);
        ILogger analysis = provider.CreateLogger("Analysis.Evaluator");
        ILogger app = provider.CreateLogger("App.Shell");

        analysis.LogInformation("started");
        app.LogWarning("careful");

        await Assert.That(hub.Logs.Count).IsEqualTo(2);
        await Assert.That(hub.Logs[0].Source).IsEqualTo("Analysis");
        await Assert.That(hub.Logs[0].Message).IsEqualTo("started");
        await Assert.That(hub.Logs[1].Source).IsEqualTo("App");
        await Assert.That(hub.Logs[1].Level).IsEqualTo(LogLevel.Warning);
    }

    [Test]
    public async Task MasterSwitch_GatesLive()
    {
        DiagnosticsTelemetryHub hub = SyncHub();
        bool enabled = false;
        HubLoggerProvider provider = new(hub, null, () => LogLevel.Trace, () => enabled);
        ILogger log = provider.CreateLogger("App.X");

        log.LogError("boom");
        await Assert.That(hub.Logs.Count).IsEqualTo(0); // master switch off

        enabled = true; // flipped live — no new logger needed
        log.LogError("boom2");
        await Assert.That(hub.Logs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MinLevel_DropsBelowFloor()
    {
        DiagnosticsTelemetryHub hub = SyncHub();
        HubLoggerProvider provider = new(hub, null, () => LogLevel.Warning, () => true);
        ILogger log = provider.CreateLogger("App.X");

        log.LogInformation("info"); // below Warning → dropped
        log.LogWarning("warn");

        await Assert.That(hub.Logs.Count).IsEqualTo(1);
        await Assert.That(hub.Logs[0].Message).IsEqualTo("warn");
    }
}

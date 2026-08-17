// CA1848 (prefer LoggerMessage delegates): these tests deliberately drive the ambient seam through
// plain ILogger, exactly as an external Analysis-assembly caller would.

#pragma warning disable CA1848

#region

using Cs2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     End-to-end coverage of the ambient logging seam exactly as the App wires it: a LoggerFactory
///     floored at Trace, wrapping <see cref="HubLoggerProvider" />, published to
///     <see cref="DiagnosticsLog" />. Proves that a coarse library log call (the shape
///     <c>EvaluatorLog</c> / <c>AppLog</c> emit) surfaces as a row on the hub the Diagnostics tab binds.
///     Mutates the process-wide seam, so <c>[NotInParallel]</c> + save/restore.
/// </summary>
[NotInParallel]
public class DiagnosticsSeamTests
{
    private static (DiagnosticsTelemetryHub hub, ILoggerFactory factory) WireLikeApp(
        Func<LogLevel> minLevel, Func<bool> enabled)
    {
        DiagnosticsTelemetryHub hub = new(() => 5000, a => a()); // sync drain — no dispatcher in tests
        ILoggerFactory factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new HubLoggerProvider(hub, null, minLevel, enabled));
        });
        return (hub, factory);
    }

    [Test]
    public async Task CoarseLibraryLog_SurfacesInHub_ThroughSeam()
    {
        (DiagnosticsTelemetryHub hub, ILoggerFactory factory) = WireLikeApp(() => LogLevel.Trace, () => true);
        ILoggerFactory prev = DiagnosticsLog.LoggerFactory;
        try
        {
            DiagnosticsLog.LoggerFactory = factory;

            // Resolve exactly like StateGraphEvaluator's _log / MainViewModel's DiagLog do.
            ILogger analysisLog = DiagnosticsLog.CreateLogger("Analysis.Evaluator");
            ILogger appLog = DiagnosticsLog.CreateLogger("App.Shell");

            analysisLog.LogInformation("Analysis started — 100 frames");
            appLog.LogInformation("Loaded demo 'match.dem' — 4200 frames");

            await Assert.That(hub.Logs.Count).IsEqualTo(2);
            await Assert.That(hub.Logs[0].Source).IsEqualTo("Analysis");
            await Assert.That(hub.Logs[1].Source).IsEqualTo("App");
            await Assert.That(hub.Logs[1].Message).Contains("Loaded demo");
        }
        finally
        {
            DiagnosticsLog.LoggerFactory = prev;
        }
    }

    [Test]
    public async Task DefaultFactory_IsNullLogger_NoThrow_NoRows()
    {
        // With no factory wired (the pre-startup / designer state), library log calls are safe no-ops.
        ILoggerFactory prev = DiagnosticsLog.LoggerFactory;
        try
        {
            DiagnosticsLog.LoggerFactory = null!; // reverts to NullLoggerFactory
            ILogger log = DiagnosticsLog.CreateLogger("Analysis.Evaluator");
            await Assert.That(log.IsEnabled(LogLevel.Information)).IsFalse();
            log.LogInformation("should be dropped silently");
        }
        finally
        {
            DiagnosticsLog.LoggerFactory = prev;
        }
    }
}

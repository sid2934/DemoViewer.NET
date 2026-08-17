#region

using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="OutputLogBridge" /> gating battery (telemetry P1). The bridge is the SOLE gate on
///     the CSVG host — it decides per record from two LIVE-read delegates: a minimum
///     <see cref="LogLevel" /> and a framework-capture flag. Facts under test: records below the
///     min level are dropped; framework categories (Microsoft/Grpc/System) are dropped unless the
///     flag is on; both delegates are re-read on every call (so a mid-session change takes effect
///     with no reconnect); <see cref="LogLevel.None" /> never emits; and the sink receives the
///     formatted <c>(level, category, message)</c>.
/// </summary>
[Category("Unit")]
public class OutputLogBridgeTests
{
    private static (OutputLogBridge Bridge, List<(LogLevel Level, string Category, string Message)> Sink)
        Build(Func<LogLevel> minLevel, Func<bool> includeFramework)
    {
        List<(LogLevel, string, string)> sink = [];
        OutputLogBridge bridge = new((l, c, m) => sink.Add((l, c, m)), minLevel, includeFramework);
        return (bridge, sink);
    }

    private static void Emit(ILogger logger, LogLevel level, string message) =>
        logger.Log(level, default, message, null, static (s, _) => s);

    [Test]
    public async Task DropsRecordsBelowMinLevel()
    {
        (OutputLogBridge bridge, List<(LogLevel Level, string Category, string Message)> sink) = Build(() => LogLevel.Information, () => false);
        ILogger logger = bridge.CreateLogger("Cs2VideoGenerator.Core.Session");

        await Assert.That(logger.IsEnabled(LogLevel.Debug)).IsFalse();
        await Assert.That(logger.IsEnabled(LogLevel.Information)).IsTrue();

        Emit(logger, LogLevel.Debug, "chatter");
        Emit(logger, LogLevel.Warning, "kept");

        await Assert.That(sink.Count).IsEqualTo(1);
        await Assert.That(sink[0].Message).IsEqualTo("kept");
    }

    [Test]
    public async Task FrameworkCategoriesGatedByFlag()
    {
        bool capture = false;
        (OutputLogBridge bridge, List<(LogLevel Level, string Category, string Message)> sink) = Build(() => LogLevel.Trace, () => capture);
        ILogger csvg = bridge.CreateLogger("Cs2VideoGenerator.Core.Session");
        ILogger aspnet = bridge.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        ILogger grpc = bridge.CreateLogger("Grpc.AspNetCore.Server.ServerCallHandler");

        // CSVG's own category is never framework-gated.
        await Assert.That(csvg.IsEnabled(LogLevel.Information)).IsTrue();
        // Framework categories are dropped while the flag is off...
        await Assert.That(aspnet.IsEnabled(LogLevel.Information)).IsFalse();
        await Assert.That(grpc.IsEnabled(LogLevel.Information)).IsFalse();

        // ...and surface once it flips — LIVE, without recreating the logger (mid-session change).
        capture = true;
        await Assert.That(aspnet.IsEnabled(LogLevel.Information)).IsTrue();
        await Assert.That(grpc.IsEnabled(LogLevel.Information)).IsTrue();
    }

    [Test]
    public async Task MinLevelIsReadLive()
    {
        LogLevel min = LogLevel.Warning;
        (OutputLogBridge bridge, _) = Build(() => min, () => false);
        ILogger logger = bridge.CreateLogger("Cs2VideoGenerator.Core.Session");

        await Assert.That(logger.IsEnabled(LogLevel.Information)).IsFalse();
        min = LogLevel.Debug; // lower verbosity threshold at runtime
        await Assert.That(logger.IsEnabled(LogLevel.Information)).IsTrue();
    }

    [Test]
    public async Task NoneNeverEmits()
    {
        (OutputLogBridge bridge, List<(LogLevel Level, string Category, string Message)> sink) = Build(() => LogLevel.Trace, () => true);
        ILogger logger = bridge.CreateLogger("Cs2VideoGenerator.Core.Session");

        await Assert.That(logger.IsEnabled(LogLevel.None)).IsFalse();
        Emit(logger, LogLevel.None, "should not appear");
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SinkReceivesLevelCategoryMessage()
    {
        (OutputLogBridge bridge, List<(LogLevel Level, string Category, string Message)> sink) = Build(() => LogLevel.Trace, () => false);
        ILogger logger = bridge.CreateLogger("Cs2VideoGenerator.Core.Session");

        Emit(logger, LogLevel.Warning, "hello");

        await Assert.That(sink.Count).IsEqualTo(1);
        await Assert.That(sink[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(sink[0].Category).IsEqualTo("Cs2VideoGenerator.Core.Session");
        await Assert.That(sink[0].Message).IsEqualTo("hello");
    }
}

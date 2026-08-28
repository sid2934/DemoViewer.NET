#region

using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     Bridges the CSVG host's <see cref="ILogger" /> output into a simple sink delegate
///     (— surfaced in DV's Output panel +
///     Diagnostics tab). The sink receives <c>(level, category, message)</c> and owns its own
///     thread marshaling; loggers may call it from any thread the host logs on.
///     <para>
///         <b>This is the SOLE log provider AND the SOLE gate</b> on the CSVG host: the host floors
///         MEL at <see cref="LogLevel.Trace" /> so nothing is pre-dropped, and this bridge decides
///         per record. Both gates are read <b>live</b> — <paramref name="minLevel" /> and
///         <paramref name="includeFramework" /> are re-read on every <see cref="ILogger.IsEnabled" />
///         — so the user can change verbosity on a running session with no reconnect. Because this
///         is the only provider, MEL's aggregate <c>IsEnabled</c> reflects <see cref="BridgeLogger" />,
///         so framework Trace/Debug records cost ~nothing while <paramref name="includeFramework" />
///         is off (source-generated framework loggers skip building state when IsEnabled is false).
///     </para>
/// </summary>
public sealed class OutputLogBridge(
    Action<LogLevel, string, string> sink,
    Func<LogLevel> minLevel,
    Func<bool> includeFramework) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new BridgeLogger(categoryName, sink, minLevel, includeFramework, IsFrameworkCategory(categoryName));

    public void Dispose()
    {
        // Stateless — loggers hold only the delegates. (The IOptionsMonitor.OnChange subscription
        // that feeds the live gate lives in LiveSyncService, which outlives per-reconnect bridges.)
    }

    // Framework noise = the ASP.NET Core / gRPC / BCL categories (a line per gRPC request from
    // Hosting.Diagnostics, transport detail, etc.). Gated behind CaptureFrameworkLogs; CSVG's own
    // categories never match these prefixes.
    private static bool IsFrameworkCategory(string category) =>
        category.StartsWith("Microsoft", StringComparison.Ordinal)
        || category.StartsWith("Grpc", StringComparison.Ordinal)
        || category.StartsWith("System", StringComparison.Ordinal);

    private sealed class BridgeLogger(
        string category,
        Action<LogLevel, string, string> sink,
        Func<LogLevel> minLevel,
        Func<bool> includeFramework,
        bool isFramework) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None
            && logLevel >= minLevel()
            && (!isFramework || includeFramework());

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} — {exception.GetType().Name}: {exception.Message}";
            }

            sink(logLevel, category, message);
        }
    }
}

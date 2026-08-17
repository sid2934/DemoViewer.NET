#region

using System.Globalization;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.Diagnostics;

/// <summary>
///     The custom <see cref="ILoggerProvider" /> that turns the first-party internal <see cref="ILogger" />
///     pillar (analysis / app lifecycle) into rows on the unified <see cref="DiagnosticsTelemetryHub" />
///     and — when configured — lines in the rolling <see cref="DiagnosticsFileLog" />. This is the App-side
///     realization of the ambient <c>DiagnosticsLog</c> seam: the App builds a factory around this provider
///     at startup and assigns it, so libraries' coarse log calls surface live in the Diagnostics tab.
///     <para>
///         Purely in-memory + local file — no OTLP/exporter, so it is WASM-safe (the file sink no-ops
///         where there is no filesystem). Gating is live: the <c>enabled</c> master switch and the
///         <c>minLevel</c> floor are re-read on every <see cref="ILogger.IsEnabled" />, so a settings
///         change takes effect with no restart.
///     </para>
/// </summary>
public sealed class HubLoggerProvider(
    DiagnosticsTelemetryHub hub,
    DiagnosticsFileLog? file,
    Func<LogLevel> minLevel,
    Func<bool> enabled) : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new HubLogger(categoryName, SourceOf(categoryName), hub, file, minLevel, enabled);

    /// <inheritdoc />
    public void Dispose() => file?.Dispose();

    // Provenance tag for the tab's Source column/filter. Analysis-assembly categories start "Analysis";
    // everything else the App logs is tagged "App". (CSVG rows are tagged by the LiveSync bridge itself.)
    private static string SourceOf(string category) =>
        category.StartsWith("Analysis", StringComparison.Ordinal) ? "Analysis" : "App";

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => "LOG"
    };

    private sealed class HubLogger(
        string category,
        string source,
        DiagnosticsTelemetryHub hub,
        DiagnosticsFileLog? file,
        Func<LogLevel> minLevel,
        Func<bool> enabled) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            enabled() && logLevel != LogLevel.None && logLevel >= minLevel();

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

            string label = LevelLabel(logLevel);
            DateTime now = DateTime.Now;

            // Coalesced onto the UI thread by the hub (background-safe) — one drain per burst.
            hub.Enqueue(new TelemetryLogRow(source, logLevel, label,
                category, message, now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));

            // Rolling file mirror carries a full date so a copied report is self-dating.
            file?.Write($"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} " +
                        $"{label,-5} {source}/{category}: {message}");
        }
    }
}

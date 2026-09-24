#region

using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The round facts evaluator's log seams, source-generated like <c>AppLog</c>: one line when the
///     ruleset is absent, one per source diagnostic, one per projection warning, one per failed write.
///     Low rate by construction; the absent-ruleset line is said once per process.
/// </summary>
internal static partial class RoundFactsLog
{
    /// <summary>Category (the "App" source tag) for round facts rows.</summary>
    public const string Category = "App.RoundFacts";

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{diagnostic}")]
    public static partial void RulesetAbsent(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "{fileName}: {diagnostic}")]
    public static partial void SourceDiagnostic(ILogger logger, string fileName, string diagnostic);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{fileName}: {warning}")]
    public static partial void ProjectionWarning(ILogger logger, string fileName, string warning);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "{fileName}: round facts were not written")]
    public static partial void WriteFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "round_facts ruleset failed composition; cached rows are kept")]
    public static partial void CompositionFailed(ILogger logger, Exception exception);
}

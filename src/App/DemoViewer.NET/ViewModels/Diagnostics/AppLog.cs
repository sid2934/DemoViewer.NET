#region

using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     Source-generated, coarse app-orchestration log messages (unified diagnostics pillar) — the
///     "App"-tagged counterpart of <c>Analysis.EvaluatorLog</c>. Only high-signal, low-rate, end-user-
///     meaningful lifecycle seams (a demo starts/finishes/fails to load) are logged, so the stream is
///     safe to surface live in the Diagnostics tab and useful for user-reported issue reports. Each
///     method is emitted by the <c>[LoggerMessage]</c> source generator (no boxing, compiler-inserted
///     <see cref="ILogger.IsEnabled" /> guard).
/// </summary>
internal static partial class AppLog
{
    /// <summary>Category (→ "App" source tag) for shell / load-orchestration rows.</summary>
    public const string ShellCategory = "App.Shell";

    /// <summary>Category (→ "Reels" source tag) for reel-generation lifecycle faults.</summary>
    public const string ReelsCategory = "Reels";

    /// <summary>Category for library-indexer lifecycle rows (cache prune, score backfill).</summary>
    public const string LibraryCategory = "App.Library";

    /// <summary>Category for demo-processing-queue faults.</summary>
    public const string QueueCategory = "App.Queue";

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Reel generation failed.\n{diagnostics}")]
    public static partial void ReelGenerationFailed(ILogger logger, string diagnostics);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Loading demo '{fileName}' ({bytes} bytes)")]
    public static partial void DemoLoadStarted(ILogger logger, string fileName, int bytes);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Loaded demo '{fileName}' — {frameCount} frames")]
    public static partial void DemoLoaded(ILogger logger, string fileName, int frameCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Failed to load demo '{fileName}': {error}")]
    public static partial void DemoLoadFailed(ILogger logger, string fileName, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Closed demo — released parse state and compacted the heap")]
    public static partial void DemoClosed(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Could not write the analysis cache for '{path}'")]
    public static partial void CacheWriteFailed(ILogger logger, string path, Exception exception);

    // v0.6.0 — the four user-relevant events that used to go to Console.WriteLine (invisible in a
    // windowed Release build) now land in the Diagnostics tab + rolling file like everything else.

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Library cache prune: dropped {count} metadata row(s) for demos that are no longer present")]
    public static partial void LibraryCachePruned(ILogger logger, int count);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Library score backfill: re-indexing {count} already-indexed demo(s) for final score")]
    public static partial void LibraryScoreBackfill(ILogger logger, int count);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "{count} library row(s) hold a half-resolved final score; the score is withheld "
                  + "from the card and offered for on-demand re-derivation")]
    public static partial void LibraryHalfResolvedScores(ILogger logger, int count);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "A demo-queue owner handler threw; the parse and other owners were unaffected")]
    public static partial void QueueOwnerHandlerFailed(ILogger logger, Exception exception);

    /// <summary>
    ///     v0.6.0 generic operation-failure row — the logging half of <c>UserFacingError</c>: the UI
    ///     shows clean text, THIS carries the full exception into the Diagnostics tab + file.
    /// </summary>
    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "{operation} failed")]
    public static partial void OperationFailed(ILogger logger, string operation, Exception exception);

    /// <summary>
    ///     A ruleset was dropped whole by v2 composition (CS2DemoKit 0.9.2) — an unresolvable
    ///     <c>show:</c> reference or an unsupported <c>per:</c> dimension is now rejected at
    ///     composition instead of failing later at build.
    ///     <para>
    ///         Logged at Warning because the failure is otherwise INVISIBLE and total: an excluded
    ///         ruleset contributes no nodes, so its stats and highlights simply never fire and the
    ///         surfaces that would have shown them render as though the rules had scored zero. One
    ///         mistyped column name costs the entire file, and without this row the only symptom is
    ///         a stat that quietly stopped existing.
    ///     </para>
    /// </summary>
    [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
        Message = "Ruleset '{rulesetId}' was excluded from the analysis graph and none of its stats "
                  + "or highlights can fire: {diagnostics}")]
    public static partial void RulesetExcluded(ILogger logger, string rulesetId, string diagnostics);
}

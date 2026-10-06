#region

using DemoViewer.NET.Extensions.Loading;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     Source-generated, coarse app-orchestration log messages (unified diagnostics pillar): the
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

    /// <summary>Category for the extension loader's one-time startup report.</summary>
    public const string ExtensionsCategory = "App.Extensions";

    [LoggerMessage(EventId = 15, Level = LogLevel.Information,
        Message = "Extension {name} {version} loaded ({source}) from '{location}'")]
    public static partial void ExtensionLoaded(ILogger logger, string name, string version, string source, string location);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning,
        Message = "Extension {name} {version} not loaded ({source}): {problem}")]
    public static partial void ExtensionIncompatible(ILogger logger, string name, string version, string source, string problem);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning,
        Message = "Staged extension at '{directory}' not loaded ({failure}): {detail}{logDetail}")]
    public static partial void ExtensionCandidateRejected(ILogger logger, string directory, LoadFailure failure, string detail, string logDetail);

    [LoggerMessage(EventId = 18, Level = LogLevel.Warning,
        Message = "Extension {packId} update check failed ({status}): {detail}")]
    public static partial void ExtensionFeedCheckFailed(ILogger logger, string packId, string status, string detail);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information,
        Message = "Extension {name} {version} staged at '{directory}'; it loads at the next start")]
    public static partial void ExtensionUpdateStaged(ILogger logger, string name, string version, string directory);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "Extension {name} {version} update refused: {detail}{logDetail}")]
    public static partial void ExtensionUpdateRefused(ILogger logger, string name, string version, string detail, string logDetail);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information,
        Message = "Removed staged extension directory '{directory}' ({reason})")]
    public static partial void ExtensionStagingRemoved(ILogger logger, string directory, string reason);

    /// <summary>
    ///     Extension code threw at a host call site and the host carried on. <paramref name="count" /> is the
    ///     extension's counted faults this session; a site that keeps throwing is logged a few times, then
    ///     rarely. <paramref name="exception" /> is null for a binding error, which has no exception.
    /// </summary>
    [LoggerMessage(EventId = 22, Level = LogLevel.Warning,
        Message = "Extension {name} failed in {site} ({count} counted this session)")]
    public static partial void ExtensionFaulted(ILogger logger, string name, string site, int count, Exception? exception);

    /// <summary>An extension crossed the fault threshold and was turned off until the app restarts.</summary>
    [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
        Message = "Extension {name} turned off for this session after {count} errors (last: {site})")]
    public static partial void ExtensionSuspended(ILogger logger, string name, int count, string site);

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

    // v0.6.0: the four user-relevant events that used to go to Console.WriteLine (invisible in a
    // windowed Release build) now land in the Diagnostics tab + rolling file like everything else.

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Library cache prune: dropped {count} metadata row(s) for demos that are no longer present")]
    public static partial void LibraryCachePruned(ILogger logger, int count);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Library backfill: re-indexing {count} already-indexed demo(s) for final score or parse record")]
    public static partial void LibraryScoreBackfill(ILogger logger, int count);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "{count} library row(s) hold a half-resolved final score; the score is withheld "
                  + "from the card and offered for on-demand re-derivation")]
    public static partial void LibraryHalfResolvedScores(ILogger logger, int count);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "A demo-queue owner handler threw; the parse and other owners were unaffected")]
    public static partial void QueueOwnerHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 24, Level = LogLevel.Debug,
        Message = "Pass {passId} still waits on an upstream pass after {demo} was read; it sat this visit out")]
    public static partial void PassStillWaitingOnUpstream(ILogger logger, string passId, string demo);

    /// <summary>
    ///     The queue read a demo. <paramref name="count" /> is the reads of that content this session; more
    ///     than one means a second parse that one visit should have served.
    /// </summary>
    [LoggerMessage(EventId = 25, Level = LogLevel.Information,
        Message = "Read {demo} ({read}): {count} read(s) of this demo this session")]
    public static partial void DemoRead(ILogger logger, string demo, string read, int count);

    /// <summary>An extension's pass overran its time budget too often and sits out the rest of the session.</summary>
    [LoggerMessage(EventId = 26, Level = LogLevel.Warning,
        Message = "Pass {passId} of {extension} overran its {budget} budget {count} times; it is off until the app restarts")]
    public static partial void PassQuarantined(ILogger logger, string passId, string extension, TimeSpan budget, int count);

    /// <summary>A pass was asked whether it wants a demo on the UI thread, which the host never does; it answered no.</summary>
    [LoggerMessage(EventId = 27, Level = LogLevel.Warning,
        Message = "Pass {passId} of {extension} was asked about {demo} on the UI thread; answered no")]
    public static partial void PassAskedOnUiThread(ILogger logger, string passId, string extension, string demo);

    /// <summary>An extension's settings or data file could not be read; it reads as empty or absent.</summary>
    [LoggerMessage(EventId = 28, Level = LogLevel.Warning,
        Message = "Extension {extension}: {path} is unreadable and reads as absent: {problem}")]
    public static partial void ExtensionStoreUnreadable(ILogger logger, string extension, string path, string problem);

    /// <summary>An extension's settings or data file could not be written; the change lives for the session only.</summary>
    [LoggerMessage(EventId = 29, Level = LogLevel.Warning,
        Message = "Extension {extension}: {path} could not be written: {problem}")]
    public static partial void ExtensionStoreWriteFailed(ILogger logger, string extension, string path, string problem);

    [LoggerMessage(EventId = 30, Level = LogLevel.Information,
        Message = "Library folder {folder}: {count} demo(s) listed in {elapsedMs} ms")]
    public static partial void LibraryFolderListed(ILogger logger, string folder, int count, long elapsedMs);

    /// <summary>A registered folder whose listing did not complete; nothing under it is pruned this scan.</summary>
    [LoggerMessage(EventId = 31, Level = LogLevel.Warning,
        Message = "Library folder {folder} was not reached: {reason}. Its cached rows are kept")]
    public static partial void LibraryFolderUnreached(ILogger logger, string folder, string reason);

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning,
        Message = "Library folder {folder}: {directory} could not be listed: {reason}. Cached rows under it are kept")]
    public static partial void LibraryDirectoryUnreached(ILogger logger, string folder, string directory, string reason);

    /// <summary>An empty listing over cached rows: an automount or mount point that is not up. Counted as not reached.</summary>
    [LoggerMessage(EventId = 34, Level = LogLevel.Warning,
        Message = "Library folder {folder} listed no demos while the cache holds {cached} row(s) under it; "
                  + "treated as not reached (a mount that is not up yet?). Its cached rows are kept")]
    public static partial void LibraryFolderListedEmpty(ILogger logger, string folder, int cached);

    [LoggerMessage(EventId = 33, Level = LogLevel.Information,
        Message = "Library scan listed {count} demo(s) from {reached} of {total} folder(s) in {elapsedMs} ms")]
    public static partial void LibraryScanListed(ILogger logger, int count, int reached, int total, long elapsedMs);

    /// <summary>
    ///     v0.6.0 generic operation-failure row, the logging half of <c>UserFacingError</c>: the UI
    ///     shows clean text, THIS carries the full exception into the Diagnostics tab + file.
    /// </summary>
    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "{operation} failed")]
    public static partial void OperationFailed(ILogger logger, string operation, Exception exception);

    /// <summary>
    ///     A ruleset was dropped whole by v2 composition (CS2DemoKit 0.9.2): an unresolvable
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

    /// <summary>
    ///     No baked collision geometry for the loaded demo's map, so per-tick visibility cannot be
    ///     computed and the visibility-gated aim columns (preaim, spotted accuracy, the timing
    ///     ladder) will be empty.
    ///     <para>
    ///         Informational, not a fault: bakes ship per map and the set is deliberately partial.
    ///         It is logged because an empty column is otherwise indistinguishable from a player who
    ///         genuinely never engaged, which is the same failure shape the capability probe exists
    ///         to make visible.
    ///     </para>
    /// </summary>
    [LoggerMessage(EventId = 14, Level = LogLevel.Information,
        Message = "No collision bake for map '{map}'; visibility-gated aim stats will be empty.")]
    public static partial void VisibilityBakeMissing(ILogger logger, string map);

    /// <summary>
    ///     An icon key a view asked for and the bake does not contain. Fired ONCE per distinct key by
    ///     <c>IconCatalogue</c>, so a key hit every frame costs one line. The view has already fallen back
    ///     to text and is still usable — this is how the fallback stops being silent.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The namespace-qualified key.</param>
    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "Icon key not in the bake, falling back to text: {key}")]
    public static partial void IconKeyMissing(this ILogger logger, string key);
}

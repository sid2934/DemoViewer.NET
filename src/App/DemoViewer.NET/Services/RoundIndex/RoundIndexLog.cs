#region

using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The round index's log seams, source-generated like <c>RoundFactsLog</c>: one line per failed
///     build, one per sidecar the loader could not read, one for the startup load's timing. Low rate
///     by construction.
/// </summary>
internal static partial class RoundIndexLog
{
    /// <summary>Category (the "App" source tag) for round index lines.</summary>
    public const string Category = "App.RoundIndex";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{fileName}: round index was not written")]
    public static partial void BuildFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "{fileName}: round index sidecar ignored ({reason})")]
    public static partial void SidecarIgnored(ILogger logger, string fileName, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "situation index loaded: {demos} demos, {postings} postings, {orphans} orphan sidecars removed, {elapsedMs} ms")]
    public static partial void Loaded(ILogger logger, int demos, int postings, int orphans, long elapsedMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "{fileName}: round {round} sample alive/team disagreed with round facts ({sideMismatches} side, {aliveMismatches} alive)")]
    public static partial void SampleDisagreedWithFacts(ILogger logger, string fileName, int round, int sideMismatches, int aliveMismatches);
}

#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The host's read API over cached round facts: the SDK's <see cref="IRoundFacts" /> plus the reads that
///     name the demo cache. Nothing here parses, evaluates or names a team.
/// </summary>
public interface IRoundFactsSource : IRoundFacts
{
    /// <summary>The rows of a record the caller already holds, so a join reads one snapshot.</summary>
    RoundFactsRows? TryGet(DemoCacheRecord record) => TryGet(record.Path);

    /// <summary>Every round in every demo with rows that matches <paramref name="filter" />. Reads sidecars: call it off the UI thread.</summary>
    IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter);
}

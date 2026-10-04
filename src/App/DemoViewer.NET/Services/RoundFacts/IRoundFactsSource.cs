#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The read API over cached round facts. Delegate-injected into its consumers (the Highlights
///     module precedent); nothing here parses, evaluates or names a team. Values are absolute per side
///     (<c>ct</c> / <c>t</c>): Team Identity turns them relative for display.
///     <para>
///         Core, with the row models beside it: the 2D Playback round track tints by
///         <see cref="RoundFacts.WinnerSide" /> and the tab resolves this source when the pack that
///         registers it is present. The writer and the implementation are the Strat Book extension's.
///     </para>
/// </summary>
public interface IRoundFactsSource
{
    /// <summary>The fact vocabulary version, so a tag document can say which list it was written against.</summary>
    int Schema { get; }

    /// <summary>One demo's rows: a sidecar read, served by the store's capacity-1 record cache. Null when none were written.</summary>
    RoundFactsRows? TryGet(string demoPath);

    /// <summary>The rows of a record the caller already holds, so a join reads one snapshot.</summary>
    RoundFactsRows? TryGet(DemoCacheRecord record) => TryGet(record.Path);

    /// <summary>The round whose window holds <paramref name="frameClockTick" />, or null before the first freeze end or without rows.</summary>
    RoundFacts? RoundAt(string demoPath, int frameClockTick);

    /// <summary>Every round in every demo with rows that matches <paramref name="filter" />. Reads sidecars: call it off the UI thread.</summary>
    IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter);

    /// <summary>
    ///     The parser-namespace facts of one round, one list, absolute per side.
    ///     The tick-anchored group (<c>phase</c>, <c>manCount.*</c>) is present only when
    ///     <paramref name="atTick" /> is given. Empty when the demo has no rows or no such round.
    /// </summary>
    IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null);

    /// <summary>A demo's rows were (re)written; the argument is its path.</summary>
    event Action<string>? Updated;
}

#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>The store-backed <see cref="IRoundFactsSource" />: the rows on each demo's cache record.</summary>
public sealed class RoundFactsSource : IRoundFactsSource
{
    private readonly DemoCacheStore _demoCache;

    /// <param name="demoCache">The unified demo cache the rows live in.</param>
    /// <param name="evaluator">The writer, whose <see cref="RoundFactsEvaluator.Updated" /> this forwards; null in a read-only host.</param>
    public RoundFactsSource(DemoCacheStore demoCache, RoundFactsEvaluator? evaluator = null)
    {
        _demoCache = demoCache;
        if (evaluator is not null)
        {
            evaluator.Updated += path => Updated?.Invoke(path);
        }
    }

    /// <inheritdoc />
    public int Schema => RoundFactsRecords.Schema;

    /// <inheritdoc />
    public event Action<string>? Updated;

    /// <summary>
    ///     The demo's rows. A row whose stamp claims rows its record does not hold (an index saved before a
    ///     later write replaced the record) is rewritten from the record here, so it stops claiming them and the
    ///     Round Facts pass wants the demo again; left alone, every reader that trusts the row re-parses it.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    public RoundFactsRows? TryGet(string demoPath)
    {
        if (_demoCache.TryLoadRecord(demoPath) is not { } record)
        {
            return null;
        }

        if (record.RoundFacts is null && _demoCache.TryGetIndex(demoPath)?.RoundFactsStamp() is { Schema: > 0 }
                                      && record.RoundFactsStamp() is not { Schema: > 0 })
        {
            _demoCache.Upsert(record);
            _demoCache.SaveIndex();
        }

        return record.RoundFacts;
    }

    /// <inheritdoc />
    public RoundFactsRows? TryGet(DemoCacheRecord record) => record.RoundFacts;

    /// <inheritdoc />
    public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
        TryGet(demoPath) is { } rows ? RoundFactsRules.FindRound(rows.Rounds, frameClockTick) : null;

    /// <inheritdoc />
    /// <remarks>
    ///     One demo answers once however many paths hold it. Its hits carry the path the filter named when it
    ///     names one, else the demo's primary path.
    /// </remarks>
    public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        List<(DemoCacheIndexEntry, RoundFacts)> hits = [];
        foreach (DemoCacheIndexEntry demo in _demoCache.Contents)
        {
            if (demo.RoundFactsStamp() is not { Schema: > 0 } || Named(demo, filter.Demos) is not { } entry
                || _demoCache.TryLoadRecord(entry.Path, false) is not { RoundFacts: { } rows })
            {
                continue;
            }

            // No tick here: a tick-anchored field passes when any tick of the live window does.
            int tickRate = rows.Clock?.TickRate ?? 0;
            foreach (RoundFacts round in rows.Rounds)
            {
                if (RoundFactsRules.Matches(entry.Path, round, filter) && (!filter.HasTickAnchored || RoundFactsRules.MatchesAnywhere(round, filter, tickRate)))
                {
                    hits.Add((entry, round));
                }
            }
        }

        return hits;
    }

    // The demo's row seen from the path the filter names, the primary first; null when it names none of them.
    private DemoCacheIndexEntry? Named(DemoCacheIndexEntry demo, IReadOnlySet<string>? demos)
    {
        if (demos is null || demos.Contains(demo.Path))
        {
            return demo;
        }

        return demo.Locations.FirstOrDefault(l => demos.Contains(l.Path)) is { } named ? _demoCache.TryGetIndex(named.Path) : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null)
    {
        RoundFacts? facts = TryGet(demoPath)?.Rounds.FirstOrDefault(r => r.Number == round);
        return facts is null ? [] : RoundFactsRules.Labels(facts, atTick);
    }
}

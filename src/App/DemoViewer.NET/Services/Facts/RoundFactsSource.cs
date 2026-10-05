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

    /// <inheritdoc />
    public RoundFactsRows? TryGet(string demoPath) => _demoCache.RoundFactsOf(demoPath);

    /// <inheritdoc />
    public RoundFactsRows? TryGet(DemoCacheRecord record) => record.RoundFacts;

    /// <inheritdoc />
    public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
        TryGet(demoPath) is { } rows ? RoundFactsRules.FindRound(rows.Rounds, frameClockTick) : null;

    /// <inheritdoc />
    public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        List<(DemoCacheIndexEntry, RoundFacts)> hits = [];
        foreach (DemoCacheRecord record in _demoCache.LoadRecords(e =>
                     e.RoundFactsStamp() is { Schema: > 0 } && (filter.Demos is null || filter.Demos.Contains(e.Path))))
        {
            if (record.RoundFacts is not { } rows || _demoCache.TryGetIndex(record.Path) is not { } entry)
            {
                continue;
            }

            // No tick here: a tick-anchored field passes when any tick of the live window does.
            int tickRate = rows.Clock?.TickRate ?? 0;
            foreach (RoundFacts round in rows.Rounds)
            {
                if (RoundFactsRules.Matches(record.Path, round, filter) && (!filter.HasTickAnchored || RoundFactsRules.MatchesAnywhere(round, filter, tickRate)))
                {
                    hits.Add((entry, round));
                }
            }
        }

        return hits;
    }

    /// <inheritdoc />
    public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null)
    {
        RoundFacts? facts = TryGet(demoPath)?.Rounds.FirstOrDefault(r => r.Number == round);
        return facts is null ? [] : RoundFactsRules.Labels(facts, atTick);
    }
}

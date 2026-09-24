#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The read API over cached round facts. Delegate-injected into its consumers (the Highlights
///     module precedent); nothing here parses, evaluates or names a team. Values are absolute per side
///     (<c>ct</c> / <c>t</c>): Team Identity turns them relative for display.
/// </summary>
public interface IRoundFactsSource
{
    /// <summary>The fact vocabulary version, so a tag document can say which list it was written against.</summary>
    int Schema { get; }

    /// <summary>One demo's rows: a sidecar read, served by the store's capacity-1 record cache. Null when none were written.</summary>
    RoundFactsRows? TryGet(string demoPath);

    /// <summary>The round whose window holds <paramref name="frameClockTick" />, or null before the first freeze end or without rows.</summary>
    RoundFacts? RoundAt(string demoPath, int frameClockTick);

    /// <summary>Every round in every demo with rows that matches <paramref name="filter" />. Reads sidecars: call it off the UI thread.</summary>
    IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter);

    /// <summary>
    ///     The parser-namespace facts of one round (overview correction 10), one list, absolute per side.
    ///     The tick-anchored group (<c>phase</c>, <c>manCount.*</c>) is present only when
    ///     <paramref name="atTick" /> is given. Empty when the demo has no rows or no such round.
    /// </summary>
    IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null);

    /// <summary>A demo's rows were (re)written; the argument is its path.</summary>
    event Action<string>? Updated;
}

/// <summary>The store-backed <see cref="IRoundFactsSource" />.</summary>
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
    public int Schema => DemoCacheRecord.RoundFactsSchema;

    /// <inheritdoc />
    public event Action<string>? Updated;

    /// <inheritdoc />
    public RoundFactsRows? TryGet(string demoPath) => _demoCache.TryLoadRecord(demoPath)?.RoundFacts;

    /// <inheritdoc />
    public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
        TryGet(demoPath) is { } rows ? FindRound(rows.Rounds, frameClockTick) : null;

    /// <inheritdoc />
    public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        List<(DemoCacheIndexEntry, RoundFacts)> hits = [];
        foreach (DemoCacheRecord record in _demoCache.LoadRecords(e =>
                     e.RoundFactsSchema > 0 && (filter.Demos is null || filter.Demos.Contains(e.Path))))
        {
            if (record.RoundFacts is not { } rows || _demoCache.TryGetIndex(record.Path) is not { } entry)
            {
                continue;
            }

            foreach (RoundFacts round in rows.Rounds)
            {
                if (Matches(round, filter))
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
        return facts is null ? [] : Labels(facts, atTick);
    }

    /// <summary>
    ///     The round whose window holds a tick: from its freeze end to the next round's, the last round
    ///     running to the end of the demo. Null before the first freeze end.
    /// </summary>
    /// <param name="rounds">Rounds in number order.</param>
    /// <param name="frameClockTick">Frame clock.</param>
    public static RoundFacts? FindRound(IReadOnlyList<RoundFacts> rounds, int frameClockTick)
    {
        ArgumentNullException.ThrowIfNull(rounds);

        RoundFacts? hit = null;
        foreach (RoundFacts round in rounds)
        {
            if (round.FreezeEndTick > frameClockTick)
            {
                break;
            }

            hit = round;
        }

        return hit;
    }

    /// <summary>The label list for one round: the vocabulary of overview correction 10 plus the design's per-side facts.</summary>
    /// <param name="round">The round.</param>
    /// <param name="atTick">The tick the <c>phase</c> group is asked at, or null to leave that group out.</param>
    public static IReadOnlyList<FactLabel> Labels(RoundFacts round, int? atTick = null)
    {
        ArgumentNullException.ThrowIfNull(round);

        List<FactLabel> labels =
        [
            new("side", "slots.ct", Join(round.Ct.Slots)),
            new("side", "slots.t", Join(round.T.Slots)),
            new("buy", "buy.ct", RoundFactsValues.LowerCamel(round.Ct.BuyType)),
            new("buy", "buy.t", RoundFactsValues.LowerCamel(round.T.BuyType)),
            new("buy", "equipment.ct", Text(round.Ct.EquipmentFreezeEnd)),
            new("buy", "equipment.t", Text(round.T.EquipmentFreezeEnd))
        ];

        Optional(labels, "buy", "money.ct", round.Ct.MoneyAtFreezeEnd);
        Optional(labels, "buy", "money.t", round.T.MoneyAtFreezeEnd);
        labels.Add(new FactLabel("buy", "moneyReliable.ct", Text(round.Ct.MoneyReliable)));
        labels.Add(new FactLabel("buy", "moneyReliable.t", Text(round.T.MoneyReliable)));

        labels.Add(new FactLabel("score", "round", Text(round.Number)));
        labels.Add(new FactLabel("score", "score.ct", Text(round.Ct.ScoreBefore)));
        labels.Add(new FactLabel("score", "score.t", Text(round.T.ScoreBefore)));
        Optional(labels, "score", "matchRound", round.MatchRoundNumber);
        labels.Add(new FactLabel("score", "half", RoundFactsValues.LowerCamel(round.Half)));

        labels.Add(new FactLabel("end", "winner", round.WinnerLabel));
        labels.Add(new FactLabel("end", "endReason", round.EndReason.ToString()));
        Optional(labels, "end", "endTick", round.EndTick);
        Optional(labels, "end", "roundTime", round.RoundTimeSeconds);

        labels.Add(new FactLabel("plant", "plantSite", RoundFactsValues.LowerCamel(round.PlantSite)));
        Optional(labels, "plant", "plantTick", round.PlantTick);
        Optional(labels, "plant", "plantSlot", round.PlanterSlot);
        Optional(labels, "plant", "defuseTick", round.DefuseTick);
        Optional(labels, "plant", "explodeTick", round.ExplodeTick);

        Optional(labels, "contact", "firstContactTick", round.FirstContactTick);
        Optional(labels, "contact", "openingKillTick", round.OpeningKillTick);

        if (atTick is int tick)
        {
            (int ct, int t) = RoundPhases.AliveAt(round, tick);
            labels.Add(new FactLabel("phase", "phase", RoundFactsValues.LowerCamel(RoundPhases.At(round, tick))));
            labels.Add(new FactLabel("phase", "manCount.ct", Text(ct)));
            labels.Add(new FactLabel("phase", "manCount.t", Text(t)));
        }

        foreach ((string column, object? value) in round.Extra)
        {
            if (RoundFactsValues.ToText(value) is { } text)
            {
                labels.Add(new FactLabel("extra", column, text));
            }
        }

        return labels;
    }

    private static bool Matches(RoundFacts round, RoundFactsFilter filter)
    {
        if (filter.LiveOnly && !round.IsLive)
        {
            return false;
        }

        if (filter.BuyCt is { } buyCt && round.Ct.BuyType != buyCt)
        {
            return false;
        }

        if (filter.BuyT is { } buyT && round.T.BuyType != buyT)
        {
            return false;
        }

        if (filter.WinnerSide is { } winner && round.WinnerSide != winner)
        {
            return false;
        }

        if (filter.EndReason is { } reason && round.EndReason != reason)
        {
            return false;
        }

        if (filter.PlantSite is { } site && round.PlantSite != site)
        {
            return false;
        }

        if (filter.Half is { } half && round.Half != half)
        {
            return false;
        }

        if (filter.Extra is not null)
        {
            foreach ((string column, string expected) in filter.Extra)
            {
                if (!round.Extra.TryGetValue(column, out object? value)
                    || !string.Equals(RoundFactsValues.ToText(value), expected, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Optional(List<FactLabel> labels, string group, string key, int? value)
    {
        if (value is int v)
        {
            labels.Add(new FactLabel(group, key, Text(v)));
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(bool value) => value ? "true" : "false";

    private static string Join(int[] slots) =>
        string.Join(",", slots.Select(s => s.ToString(CultureInfo.InvariantCulture)));
}

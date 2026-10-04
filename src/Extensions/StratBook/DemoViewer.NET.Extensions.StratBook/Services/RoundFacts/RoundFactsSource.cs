#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The store-backed <see cref="IRoundFactsSource" />. Gated with the pack like the writer: off, every
///     read answers "no rows" and nothing is forwarded, so rows written while the pack was on stop
///     surfacing (tints, joins, labels) the moment it goes off. The rows themselves stay on disk.
/// </summary>
public sealed class RoundFactsSource : IRoundFactsSource
{
    private readonly DemoCacheStore _demoCache;
    private readonly Func<bool> _enabled;

    /// <param name="demoCache">The unified demo cache the rows live in.</param>
    /// <param name="evaluator">The writer, whose <see cref="RoundFactsEvaluator.Updated" /> this forwards; null in a read-only host.</param>
    /// <param name="enabled">The owning pack's live gate; null means always on.</param>
    public RoundFactsSource(DemoCacheStore demoCache, RoundFactsEvaluator? evaluator = null, Func<bool>? enabled = null)
    {
        _demoCache = demoCache;
        _enabled = enabled ?? (() => true);
        if (evaluator is not null)
        {
            evaluator.Updated += path =>
            {
                if (_enabled())
                {
                    Updated?.Invoke(path);
                }
            };
        }
    }

    /// <inheritdoc />
    public int Schema => StratBookCache.RoundFactsSchema;

    /// <inheritdoc />
    public event Action<string>? Updated;

    /// <inheritdoc />
    public RoundFactsRows? TryGet(string demoPath) => _enabled() ? _demoCache.RoundFactsOf(demoPath) : null;

    /// <inheritdoc />
    public RoundFactsRows? TryGet(DemoCacheRecord record) => _enabled() ? _demoCache.RoundFactsOf(record) : null;

    /// <inheritdoc />
    public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
        TryGet(demoPath) is { } rows ? FindRound(rows.Rounds, frameClockTick) : null;

    /// <inheritdoc />
    public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!_enabled())
        {
            return [];
        }

        List<(DemoCacheIndexEntry, RoundFacts)> hits = [];
        foreach (DemoCacheRecord record in _demoCache.LoadRecords(e =>
                     e.RoundFactsStamp() is { Schema: > 0 } && (filter.Demos is null || filter.Demos.Contains(e.Path))))
        {
            if (_demoCache.RoundFactsOf(record) is not { } rows || _demoCache.TryGetIndex(record.Path) is not { } entry)
            {
                continue;
            }

            // No tick here: a tick-anchored field passes when any tick of the live window does.
            int tickRate = rows.Clock?.TickRate ?? 0;
            foreach (RoundFacts round in rows.Rounds)
            {
                if (Matches(record.Path, round, filter) && (!filter.HasTickAnchored || MatchesAnywhere(round, filter, tickRate)))
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

    /// <summary>The label list for one round: the shared label vocabulary plus the round's per-side facts.</summary>
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

    /// <summary>
    ///     Whether one round passes a filter's round-level fields, <see cref="RoundFactsFilter.Where" />
    ///     included. Public so the round index can apply the same filter to its hits through the same
    ///     rule; <see cref="RoundFactsFilter.Demos" /> is a demo-level field and is the caller's to
    ///     apply, and the tick-anchored fields are <see cref="MatchesAt" />'s.
    /// </summary>
    /// <param name="demoPath">The demo the round belongs to: what <see cref="RoundFactsFilter.Where" /> sees.</param>
    /// <param name="round">The round.</param>
    /// <param name="filter">The filter.</param>
    public static bool Matches(string demoPath, RoundFacts round, RoundFactsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(filter);

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

        if (filter.Score is { } score && !ScoreMatches(round, score))
        {
            return false;
        }

        return filter.Where is null || filter.Where(demoPath, round);
    }

    /// <summary>
    ///     Whether the tick-anchored fields pass at one tick: the phase, the clock band and the alive
    ///     counts, each read through <see cref="RoundPhases" />. True when the filter has none.
    /// </summary>
    /// <param name="round">The round the tick falls in.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="tick">Frame clock.</param>
    /// <param name="tickRate">Ticks per second, for the clock band; 0 fails a band rather than guessing.</param>
    public static bool MatchesAt(RoundFacts round, RoundFactsFilter filter, int tick, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.Phase is { } phase)
        {
            // Retake is the CT name for the post-plant interval; the filter asks with no side, so the
            // two names are one predicate.
            RoundPhase at = RoundPhases.At(round, tick);
            RoundPhase wanted = phase == RoundPhase.Retake ? RoundPhase.PostPlant : phase;
            if (at != wanted)
            {
                return false;
            }
        }

        if (filter.ClockBand is { } band && (tickRate <= 0 || BandOf(round, tick, tickRate) != band))
        {
            return false;
        }

        if (filter.ManCount is not null || filter.CtAlive is not null || filter.TAlive is not null)
        {
            (int ct, int t) = RoundPhases.AliveAt(round, tick);
            if (filter.CtAlive is { } wantCt && ct != wantCt)
            {
                return false;
            }

            if (filter.TAlive is { } wantT && t != wantT)
            {
                return false;
            }

            if (filter.ManCount is { } state && state != (ct == t ? ManCountState.Even : ct > t ? ManCountState.CtUp : ManCountState.TUp))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether some tick of the round's live window passes <see cref="MatchesAt" />. Every
    ///     tick-anchored predicate is piecewise constant between the round's own ticks (freeze end,
    ///     kills, plant, end) and the band edges, so testing each breakpoint is exact, not a sample.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="tickRate">Ticks per second.</param>
    public static bool MatchesAnywhere(RoundFacts round, RoundFactsFilter filter, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(filter);

        int end = round.EndTick ?? int.MaxValue;
        List<int> breakpoints = [round.FreezeEndTick];
        breakpoints.AddRange(round.Kills.Select(k => k.Tick));
        if (round.OpeningKillTick is int opening)
        {
            breakpoints.Add(opening);
        }

        if (round.PlantTick is int plant)
        {
            breakpoints.Add(plant);
        }

        if (tickRate > 0)
        {
            breakpoints.Add(round.FreezeEndTick + EarlyBandSeconds * tickRate);
            breakpoints.Add(round.FreezeEndTick + LateBandSeconds * tickRate);
        }

        return breakpoints.Any(tick => tick >= round.FreezeEndTick && tick < end && MatchesAt(round, filter, tick, tickRate));
    }

    /// <summary>Where <see cref="ClockBand.Early" /> ends, in seconds since the freeze end.</summary>
    public const int EarlyBandSeconds = 30;

    /// <summary>Where <see cref="ClockBand.Late" /> starts, in seconds since the freeze end.</summary>
    public const int LateBandSeconds = 75;

    private static ClockBand BandOf(RoundFacts round, int tick, int tickRate)
    {
        int seconds = (tick - round.FreezeEndTick) / tickRate;
        return seconds < EarlyBandSeconds ? ClockBand.Early : seconds < LateBandSeconds ? ClockBand.Middle : ClockBand.Late;
    }

    private static bool ScoreMatches(RoundFacts round, ScoreSituation score)
    {
        int ct = round.Ct.ScoreBefore;
        int t = round.T.ScoreBefore;
        return score switch
        {
            ScoreSituation.Tied => ct == t,
            ScoreSituation.CtLeading => ct > t,
            ScoreSituation.TLeading => t > ct,
            // One round from the regulation win: 12 under MR12. The thresholds are per side but carry
            // the same regulation length, so either side's copy answers.
            ScoreSituation.MatchPoint => Math.Max(ct, t) == round.Ct.Thresholds.RegulationRounds / 2,
            _ => true
        };
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

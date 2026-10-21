#region

using System.Globalization;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>How one demo ended for an account: its side's round wins against its losses.</summary>
public enum MatchOutcome
{
    Win,
    Loss,
    Tie
}

/// <summary>One of an account's demos and what it did there.</summary>
/// <param name="Match">The demo, as the directory lists it.</param>
/// <param name="HasFacet">Whether the demo's <c>player_match</c> facet is written; false is "stats pending".</param>
/// <param name="Stats">
///     The account's facet rows merged into one (<see cref="PlayerAggregate.Merge" />), by facet column; null when
///     the facet is not written or holds no active row for the account.
/// </param>
public sealed record PlayerDemo(PlayerMatch Match, bool HasFacet, IReadOnlyDictionary<string, double>? Stats)
{
    /// <summary>The columns <see cref="Others" /> keeps of each account: the inputs of the pooled HLTV rating.</summary>
    public static IReadOnlyList<string> OtherColumns { get; } = ColumnRules.For("HLTV")!.Inputs;

    /// <summary>Win, loss or tie by the account's own round counts; null without stats or rounds.</summary>
    public MatchOutcome? Outcome => PlayerAggregate.OutcomeOf(Stats);

    /// <summary>
    ///     Every other account of the demo's facet, its rows merged as the account's own are, by
    ///     <see cref="OtherColumns" />; null when the facet is not read.
    /// </summary>
    public IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, double>>? Others { get; init; }

    /// <summary>
    ///     Whether the demo's facet carries line-of-sight data: its stamp's <see cref="PackStamp.HasVisibility" />.
    ///     Without it the sight columns of <see cref="Stats" /> are zeros that measured nothing, and the pool leaves
    ///     them out.
    /// </summary>
    public bool HasSight { get; init; }

    /// <summary>
    ///     The account's rounds summed by side (<see cref="PlayerRoundFacet.Sides" />), by <see cref="PlayerRoundFacet.Column" />;
    ///     null when the demo's <c>player_round</c> facet is not written or holds no round of the account on a side.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Sides { get; init; }
}

/// <summary>
///     An account's stats over a set of demos: every <see cref="ColumnRules" /> column pooled over the demos'
///     merged facet rows, and the match record. Pure: it reads only the rows it is handed.
/// </summary>
/// <param name="Matches">Every demo in the set, with stats or not.</param>
/// <param name="WithStats">The demos whose stats are pooled.</param>
/// <param name="Wins">Demos won.</param>
/// <param name="Losses">Demos lost.</param>
/// <param name="Ties">Demos tied.</param>
/// <param name="Values">
///     The pooled value by catalogue key; empty when no demo has stats, never a column of zeros. A line-of-sight column
///     is pooled over the demos with sight data only, and is absent when there is none: "no data", not 0. Beside them, for
///     each side the demos with stats played, its summed rounds and counts and its ADR, KAST% and KD under
///     <see cref="PlayerRoundFacet.Column" /> (<c>CT:ADR</c>), pooled over the <see cref="WithSides" /> demos alone.
/// </param>
public sealed record PlayerAggregate(int Matches, int WithStats, int Wins, int Losses, int Ties, IReadOnlyDictionary<string, double> Values)
{
    // HLTV 2.0's constant terms (rules/player_stats.rules.yaml, HLTV): 0.1587 and the impact term's -0.41 x 0.2372.
    // Every other term is linear in a per-round count, so two slots' ratings add up to the account's plus one of these.
    private const double HltvConstant = 0.1587 - (0.2372 * 0.41);
    private const double HltvPerKastPercent = 0.73 / 100;

    private static readonly string[] Activity = ["Shots", "TotalD", "EnemyDmg"];

    // The rounds a slot's team won and lost on each side. A slot tallies every round its team finishes, not only
    // the rounds the account held it, so two slots of one account can tally the same round.
    private static readonly string[] TeamTally = ["CTW", "CTL", "TW", "TL"];

    /// <summary>The demos with stats whose facet carries line-of-sight data: the population of every sight column.</summary>
    public int WithSight { get; init; }

    /// <summary>The demos with stats whose rounds by side are pooled; fewer than <see cref="WithStats" /> while the library computes the rest.</summary>
    public int WithSides { get; init; }

    /// <summary>The pooled value of a catalogue column, or null when it does not pool or no demo has stats.</summary>
    /// <param name="key">A catalogue key.</param>
    public double? this[string key] => Values.TryGetValue(key, out double value) ? value : null;

    /// <summary>The pool of <paramref name="demos" />: every column by its <see cref="ColumnRule" />, over the demos with stats.</summary>
    /// <param name="demos">One account's demos, each once.</param>
    public static PlayerAggregate Of(IReadOnlyList<PlayerDemo> demos)
    {
        ArgumentNullException.ThrowIfNull(demos);
        List<IReadOnlyDictionary<string, double>> rows = new(demos.Count);
        List<IReadOnlyDictionary<string, double>> sighted = [];
        Dictionary<string, double> sides = new(StringComparer.Ordinal);
        int wins = 0, losses = 0, ties = 0, withSides = 0;
        foreach (PlayerDemo demo in demos)
        {
            if (demo.Stats is not { } stats)
            {
                continue;
            }

            rows.Add(stats);
            if (demo.HasSight)
            {
                sighted.Add(stats);
            }

            if (demo.Sides is { } split)
            {
                withSides++;
                foreach ((string key, double value) in split)
                {
                    sides[key] = sides.GetValueOrDefault(key) + value;
                }
            }

            switch (OutcomeOf(stats))
            {
                case MatchOutcome.Win:
                    wins++;
                    break;
                case MatchOutcome.Loss:
                    losses++;
                    break;
                case MatchOutcome.Tie:
                    ties++;
                    break;
            }
        }

        Dictionary<string, double> values = new(StringComparer.Ordinal);
        if (rows.Count > 0)
        {
            foreach ((string key, ColumnRule rule) in ColumnRules.All)
            {
                List<IReadOnlyDictionary<string, double>> over = ColumnCatalogue.RequiresVisibility(key) ? sighted : rows;
                if (over.Count > 0 && rule.Pool(over) is { } value)
                {
                    values[key] = value;
                }
            }
        }

        foreach (int side in (int[]) [PlayerRoundFacet.Ct, PlayerRoundFacet.T])
        {
            if (sides.GetValueOrDefault(PlayerRoundFacet.Column(side, "rounds")) is not (> 0 and var rounds))
            {
                continue;
            }

            double Sum(string count) => sides.GetValueOrDefault(PlayerRoundFacet.Column(side, count));
            values[PlayerRoundFacet.Column(side, "rounds")] = rounds;
            foreach (string count in PlayerRoundFacet.Counts)
            {
                values[PlayerRoundFacet.Column(side, count)] = Sum(count);
            }

            // A zero denominator reads 0, as the catalogue's ratios do.
            values[PlayerRoundFacet.Column(side, "ADR")] = Sum("damage") / rounds;
            values[PlayerRoundFacet.Column(side, "KAST%")] = Sum("kast") / rounds * 100;
            values[PlayerRoundFacet.Column(side, "KD")] = Sum("deaths") == 0 ? 0 : Sum("kills") / Sum("deaths");
        }

        return new PlayerAggregate(demos.Count, rows.Count, wins, losses, ties, values) { WithSight = sighted.Count, WithSides = withSides };
    }

    /// <summary>The demos of <paramref name="demos" /> that pass <paramref name="filter" />, in their order; <see cref="PlayerFilter.Last" /> applies last.</summary>
    /// <param name="demos">One account's demos, newest match first.</param>
    /// <param name="filter">Which demos.</param>
    public static IReadOnlyList<PlayerDemo> Window(IReadOnlyList<PlayerDemo> demos, PlayerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(demos);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter == PlayerFilter.All)
        {
            return demos;
        }

        IEnumerable<PlayerDemo> passing = demos.Where(d => Passes(d.Match, filter));
        return filter.Last is { } last ? [.. passing.Take(Math.Max(last, 0))] : [.. passing];
    }

    /// <summary>Whether a demo passes every member of <paramref name="filter" /> but <see cref="PlayerFilter.Last" />.</summary>
    internal static bool Passes(PlayerMatch match, PlayerFilter filter)
    {
        DemoCacheIndexEntry row = match.Row;
        return (filter.Map is null || string.Equals(row.Map, filter.Map, StringComparison.OrdinalIgnoreCase))
               && (filter.FromTicks is not { } from || match.TimeTicks >= from)
               && (filter.BeforeTicks is not { } before || match.TimeTicks < before)
               && (filter.Side is null || match.Side == filter.Side)
               && (filter.Source is null || string.Equals(row.MatchSource ?? row.SourceKind, filter.Source, StringComparison.OrdinalIgnoreCase))
               && (filter.GameMode is null || string.Equals(row.GameMode, filter.GameMode, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Win when the account's side won more rounds than it lost (<c>CTW + TW</c> against <c>CTL + TL</c>).</summary>
    /// <param name="stats">A merged facet row.</param>
    public static MatchOutcome? OutcomeOf(IReadOnlyDictionary<string, double>? stats)
    {
        if (stats is null)
        {
            return null;
        }

        double won = stats.GetValueOrDefault("CTW") + stats.GetValueOrDefault("TW");
        double lost = stats.GetValueOrDefault("CTL") + stats.GetValueOrDefault("TL");
        return won + lost == 0 ? null : won > lost ? MatchOutcome.Win : won < lost ? MatchOutcome.Loss : MatchOutcome.Tie;
    }

    /// <summary>
    ///     Every account but <paramref name="except" /> of one demo's accounts, each merged by <see cref="Merge" />,
    ///     cut to <paramref name="columns" />.
    /// </summary>
    /// <param name="accounts">The demo's accounts, each merged by <see cref="Merge" />.</param>
    /// <param name="except">The account left out.</param>
    /// <param name="columns">The columns kept.</param>
    public static IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, double>> Others(
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, double>> accounts, ulong except, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(columns);
        Dictionary<ulong, IReadOnlyDictionary<string, double>> others = new(accounts.Count);
        foreach ((ulong id, IReadOnlyDictionary<string, double> merged) in accounts)
        {
            if (id == except)
            {
                continue;
            }

            Dictionary<string, double> kept = new(columns.Count, StringComparer.Ordinal);
            foreach (string column in columns)
            {
                if (merged.TryGetValue(column, out double value))
                {
                    kept[column] = value;
                }
            }

            others[id] = kept;
        }

        return others;
    }

    /// <summary>
    ///     The account's rows of one demo's facet as one row (<c>docs/player-match-format.md</c>). A reconnect leaves a
    ///     ghost row on the old slot, so a row counts only when it ended on a side (team 2 or 3) or shows activity
    ///     (shots, deaths or damage). Counts and sums add up; <c>rounds</c> is the match's on every row; KAST rounds
    ///     and rounds survived add up to at most the match's rounds; HLTV adds up less the constant terms of each
    ///     extra slot. The side round tallies (<see cref="TeamTally" />) add up only while they fit in the match's
    ///     rounds; past that two slots tallied the same rounds, and the slot with the most shots keeps its four.
    ///     Null when no row of the account counts.
    /// </summary>
    /// <param name="rows">The facet table's rows.</param>
    /// <param name="steamId64">The account.</param>
    public static IReadOnlyDictionary<string, double>? Merge(IReadOnlyList<FactRow> rows, ulong steamId64)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string id = steamId64.ToString(CultureInfo.InvariantCulture);
        Dictionary<string, double>? merged = null;
        int slots = 0;
        double kastRounds = 0, kastPercents = 0;
        FactRow? busiest = null;
        foreach (FactRow row in rows)
        {
            if (!string.Equals(row.Dimensions.GetValueOrDefault("steam_id")?.AsText(), id, StringComparison.Ordinal)
                || (row.Dimensions.GetValueOrDefault("team")?.AsInteger() is not (2 or 3)
                    && !Activity.Any(c => row.Values.GetValueOrDefault(c)?.AsNumber() > 0)))
            {
                continue;
            }

            merged ??= new Dictionary<string, double>(row.Values.Count, StringComparer.Ordinal);
            slots++;
            foreach ((string column, FactValue cell) in row.Values)
            {
                double value = cell.AsNumber() ?? 0;
                merged[column] = column == ColumnRules.Rounds
                    ? Math.Max(merged.GetValueOrDefault(column), value)
                    : merged.GetValueOrDefault(column) + value;
            }

            if (busiest is null || Number(row, "Shots") > Number(busiest, "Shots"))
            {
                busiest = row;
            }

            double kast = row.Values.GetValueOrDefault("KAST%")?.AsNumber() ?? 0;
            kastPercents += kast;
            kastRounds += kast * (row.Values.GetValueOrDefault(ColumnRules.Rounds)?.AsNumber() ?? 0) / 100;
        }

        if (merged is null || slots == 1)
        {
            return merged;
        }

        double rounds = merged.GetValueOrDefault(ColumnRules.Rounds);
        if (TeamTally.Sum(c => merged.GetValueOrDefault(c)) > rounds)
        {
            foreach (string column in TeamTally)
            {
                merged[column] = Number(busiest!, column);
            }
        }

        if (merged.TryGetValue("Survived", out double survived))
        {
            merged["Survived"] = Math.Min(survived, rounds);
        }

        double kastPercent =rounds == 0 ? 0 : Math.Min(kastRounds, rounds) / rounds * 100;
        merged["KAST%"] = kastPercent;
        merged["HLTV"] = merged.GetValueOrDefault("HLTV") - ((slots - 1) * HltvConstant) - ((kastPercents - kastPercent) * HltvPerKastPercent);
        return merged;
    }

    private static double Number(FactRow row, string column) => row.Values.GetValueOrDefault(column)?.AsNumber() ?? 0;
}

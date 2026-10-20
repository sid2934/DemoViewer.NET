#region

using static DemoViewer.NET.ViewModels.Stats.ColumnRule;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>
///     How one catalogue column pools across demos, read off the <c>player_match</c> facet's columns
///     (<c>docs/player-overview.md</c>, "Aggregation rules"). A ratio is never averaged: it is recomputed
///     from the summed components, so the pool of one demo is that demo's Stats tab value and the pool of
///     many weights each demo by what it contributed.
/// </summary>
public abstract record ColumnRule
{
    private ColumnRule()
    {
    }

    /// <summary>The facet columns the rule reads.</summary>
    public abstract IReadOnlyList<string> Inputs { get; }

    /// <summary>
    ///     The pooled value over <paramref name="rows" />, one facet row per demo, or null for a column that does
    ///     not pool. A missing cell reads as zero, and so does a rate over a zero denominator, as the engine's
    ///     division does: a player with no opening duels reads Duel% 0 on the Stats tab.
    /// </summary>
    /// <param name="rows">Facet rows, by column label.</param>
    public abstract double? Pool(IReadOnlyList<IReadOnlyDictionary<string, double>> rows);

    private static double Total(IReadOnlyList<IReadOnlyDictionary<string, double>> rows, IReadOnlyList<string> columns) =>
        rows.Sum(r => columns.Sum(c => r.GetValueOrDefault(c)));

    /// <summary>A count or a sum: the pool is the total.</summary>
    public sealed record Sum(string Column) : ColumnRule
    {
        public override IReadOnlyList<string> Inputs => [Column];

        public override double? Pool(IReadOnlyList<IReadOnlyDictionary<string, double>> rows) => Total(rows, Inputs);
    }

    /// <summary>A signed difference of two counts (FK±): the difference of their totals.</summary>
    public sealed record DifferenceOfSums(string Minuend, string Subtrahend) : ColumnRule
    {
        public override IReadOnlyList<string> Inputs => [Minuend, Subtrahend];

        public override double? Pool(IReadOnlyList<IReadOnlyDictionary<string, double>> rows) =>
            Total(rows, [Minuend]) - Total(rows, [Subtrahend]);
    }

    /// <summary>
    ///     A rate: the summed numerator over the summed denominator, times <paramref name="Scale" />. Each side
    ///     may add several columns (HS% divides by enemy plus team kills).
    /// </summary>
    /// <param name="Numerator">Columns summed into the numerator.</param>
    /// <param name="Denominator">Columns summed into the denominator.</param>
    /// <param name="Scale">100 for a percentage.</param>
    public sealed record RatioOfSums(IReadOnlyList<string> Numerator, IReadOnlyList<string> Denominator, double Scale = 1) : ColumnRule
    {
        public override IReadOnlyList<string> Inputs => [.. Numerator, .. Denominator.Except(Numerator, StringComparer.Ordinal)];

        public override double? Pool(IReadOnlyList<IReadOnlyDictionary<string, double>> rows)
        {
            double denominator = Total(rows, Denominator);
            return denominator == 0 ? 0 : Total(rows, Numerator) / denominator * Scale;
        }
    }

    /// <summary>
    ///     A rate the facet stores whole because it is linear in per-round terms (KAST%, HLTV): the mean of the
    ///     per-demo values weighted by <paramref name="Weight" />, the match's round count.
    /// </summary>
    public sealed record WeightedMean(string Value, string Weight) : ColumnRule
    {
        public override IReadOnlyList<string> Inputs => [Value, Weight];

        public override double? Pool(IReadOnlyList<IReadOnlyDictionary<string, double>> rows)
        {
            double weight = Total(rows, [Weight]);
            return weight == 0 ? 0 : rows.Sum(r => r.GetValueOrDefault(Value) * r.GetValueOrDefault(Weight)) / weight;
        }
    }

    /// <summary>A column with no cross-demo value, and why.</summary>
    public sealed record NotAggregable(string Reason) : ColumnRule
    {
        public override IReadOnlyList<string> Inputs => [];

        public override double? Pool(IReadOnlyList<IReadOnlyDictionary<string, double>> rows) => null;
    }
}

/// <summary>
///     The <see cref="ColumnRule" /> of every <see cref="ColumnCatalogue" /> column, keyed like the catalogue.
///     The <c>player_match</c> facet's columns are exactly the inputs of these rules; a catalogue column with no
///     rule here fails <c>ColumnRuleSweepTests</c>.
/// </summary>
public static class ColumnRules
{
    /// <summary>The facet column holding the match's round count: the denominator of every per-round rate.</summary>
    public const string Rounds = "rounds";

    private const string Sight = "needs line-of-sight geometry, which the background run that writes the facet does not have";
    private const string RoundBoard = "a per-round cell of the round board, not a match total";

    private static readonly Dictionary<string, ColumnRule> _byKey = Build();

    /// <summary>Every rule, by catalogue key.</summary>
    public static IReadOnlyDictionary<string, ColumnRule> All => _byKey;

    /// <summary>The facet columns the rules read, the round count among them, in first-use order.</summary>
    public static IReadOnlyList<string> FacetColumns { get; } = [.. _byKey.Values.SelectMany(r => r.Inputs).Distinct(StringComparer.Ordinal)];

    /// <summary>The rule for a catalogue key, or null for a column the catalogue does not know.</summary>
    public static ColumnRule? For(string key) => _byKey.GetValueOrDefault(key);

    private static RatioOfSums PerRound(string column, double scale = 1) => new([column], [Rounds], scale);

    private static RatioOfSums Percent(string numerator, string denominator) => new([numerator], [denominator], 100);

    private static Dictionary<string, ColumnRule> Build()
    {
        Dictionary<string, ColumnRule> rules = new(StringComparer.Ordinal)
        {
            // ── Core, rating ──
            ["ADR"] = PerRound("EnemyDmg"),
            ["KAST%"] = new WeightedMean("KAST%", Rounds),
            ["HLTV"] = new WeightedMean("HLTV", Rounds),
            ["KD"] = new RatioOfSums(["TotalK"], ["TotalD"]),
            ["KPR"] = PerRound("TotalK"),
            ["HS%"] = new RatioOfSums(["TotalHS"], ["TotalK", "TeamKills"], 100),
            ["Surv%"] = PerRound("Survived", 100),

            // ── Accuracy: the hit rates ──
            ["Acc%"] = Percent("HitFoe", "Shots"),
            ["HSAcc%"] = Percent("head_hits", "HitFoe"),
            ["HSDmg%"] = Percent("head_damage", "EnemyDmg"),
            ["CS%"] = Percent("cs_clean", "CSAtt"),
            ["CSAll%"] = Percent("cs_clean", "Shots"),
            ["Linear%"] = Percent("moving_shots", "Shots"),
            ["Spray"] = new RatioOfSums(["spray_error_sum"], ["SprayN"]),

            // ── Means over a population the facet counts ──
            ["AvgHP→Dmg"] = new RatioOfSums(["health_at_damage_sum"], ["damage_instances"]),
            ["AvgBlind"] = new RatioOfSums(["blind_duration_sum"], ["EFlash"]),
            ["Equip"] = new RatioOfSums(["equipment_sum"], ["equipment_rounds"]),

            // ── Opening duels ──
            ["FK±"] = new DifferenceOfSums("TotalFK", "TotalFD"),
            ["Duel%"] = new RatioOfSums(["TotalFK"], ["TotalFK", "TotalFD"], 100)
        };

        foreach (string key in (string[])
                 [
                     "TotalK", "TotalD", "TotalA", "TotalHS", "FlashAst", "TrdK", "TradedD", "Clutch", "RapidKills",
                     "CSAtt", "SprayN", "EnemyDmg", "TeamDmg", "SelfDmg", "HitFoe", "HitTeam", "Shots",
                     "TotalFK", "TotalFD", "CTFK", "CTFD", "TFK", "TFD",
                     "AWP", "Pistol", "Rifle", "SMG", "Knife", "DeagleHSRnds",
                     "NoScope", "WB", "Smoke", "Blind", "Revenge", "FlashK",
                     "HE", "Flash", "Smokes", "Molly", "EFlash", "Plants", "Defuses", "Armor",
                     "2K", "3K", "4K", "5K", "CTW", "CTL", "TW", "TL", "Survived"
                 ])
        {
            rules.Add(key, new Sum(key));
        }

        foreach (string key in (string[]) ["Kills", "Deaths", "Assists", "Damage", "UtilDmg", "Traded", "HasKAST", "HSKills", "EKills", "Flashed", "FK", "FD", "DeagleHS"])
        {
            rules.Add(key, new NotAggregable(RoundBoard));
        }

        foreach (ColumnMeta meta in ColumnCatalogue.All.Where(m => m.RequiresVisibility))
        {
            rules.Add(meta.Key, new NotAggregable(Sight));
        }

        return rules;
    }
}

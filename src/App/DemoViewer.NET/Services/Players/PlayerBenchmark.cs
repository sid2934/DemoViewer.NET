#region

using System.Globalization;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>A span of CS Ratings the benchmarks can be held to: one of Premier's colour tiers, 5,000 wide.</summary>
/// <param name="Low">The lowest rating in the band.</param>
/// <param name="High">The highest, or null for the open top band.</param>
public readonly record struct RankBand(int Low, int? High)
{
    private const int Width = 5000;
    private const int Top = 30000;

    /// <summary>"Premier 15,000-19,999", or "Premier 30,000+".</summary>
    public string Label => High is { } high
        ? string.Create(CultureInfo.InvariantCulture, $"Premier {Low:N0}-{high:N0}")
        : string.Create(CultureInfo.InvariantCulture, $"Premier {Low:N0}+");

    /// <summary>The tier <paramref name="rating" /> is in.</summary>
    /// <param name="rating">A CS Rating.</param>
    public static RankBand For(int rating)
    {
        int low = Math.Clamp(rating, 0, Top) / Width * Width;
        return low >= Top ? new RankBand(Top, null) : new RankBand(low, low + Width - 1);
    }

    /// <summary>Whether <paramref name="rating" /> is in the band.</summary>
    public bool Contains(int rating) => rating >= Low && (High is not { } high || rating <= high);
}

/// <summary>Where one of the subject's numbers sits among the players it is compared with.</summary>
/// <param name="Key">The catalogue key, or "Win%".</param>
/// <param name="Value">The subject's value.</param>
/// <param name="Population">How many players have a value for the key.</param>
/// <param name="Percentile">
///     The share of those players below the subject, counting a tie as half, 0 to 100. Raw: a lower-is-better
///     key is not flipped.
/// </param>
/// <param name="Median">The players' median.</param>
public sealed record BenchmarkStat(string Key, double Value, int Population, double Percentile, double Median);

/// <summary>
///     The subject's numbers as percentiles among other players of the same library: every account with at least
///     a minimum count of demos with stats under the same filter, the subject left out. Pure: it reads only the
///     pools it is handed.
/// </summary>
/// <param name="Population">How many players the subject is compared with.</param>
/// <param name="Stats">One entry per key the subject has a value for and at least one player does too.</param>
public sealed record PlayerBenchmark(int Population, IReadOnlyList<BenchmarkStat> Stats)
{
    /// <summary>The fewest demos with stats a player needs under the filter to be compared with.</summary>
    public const int DefaultMinimumMatches = 5;

    /// <summary>The key of the win rate, which pools from the match record rather than a column.</summary>
    public const string WinRate = "Win%";

    /// <summary>
    ///     Every account but <paramref name="subject" /> with at least <paramref name="minimumMatches" /> demos with
    ///     stats among those that pass <paramref name="filter" />, each with its pool over them.
    /// </summary>
    /// <param name="library">Every account's demos.</param>
    /// <param name="subject">The account left out.</param>
    /// <param name="filter">The subject's filter, applied to each account's demos as to the subject's.</param>
    /// <param name="minimumMatches">The fewest demos with stats.</param>
    public static IReadOnlyList<(ulong SteamId64, PlayerAggregate Pool)> PopulationOf(LibraryRows library, ulong subject, PlayerFilter filter,
        int minimumMatches)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(filter);
        List<(ulong, PlayerAggregate)> population = [];
        foreach ((ulong steamId64, IReadOnlyList<PlayerDemo> demos) in library.Accounts)
        {
            if (steamId64 == subject || demos.Count(d => d.Stats is not null) < minimumMatches)
            {
                continue;
            }

            PlayerAggregate pool = PlayerAggregate.Of(PlayerAggregate.Window(demos, filter));
            if (pool.WithStats >= minimumMatches)
            {
                population.Add((steamId64, pool));
            }
        }

        return population;
    }

    /// <summary>Where <paramref name="subject" />'s value of each key sits among <paramref name="population" />'s.</summary>
    /// <param name="subject">The subject's pool.</param>
    /// <param name="population">The pools compared with.</param>
    /// <param name="keys">Catalogue keys, or <see cref="WinRate" />.</param>
    public static PlayerBenchmark Of(PlayerAggregate subject, IReadOnlyList<PlayerAggregate> population, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(population);
        ArgumentNullException.ThrowIfNull(keys);
        List<BenchmarkStat> stats = new(keys.Count);
        foreach (string key in keys)
        {
            if (ValueOf(subject, key) is not { } value)
            {
                continue;
            }

            List<double> values = new(population.Count);
            foreach (PlayerAggregate pool in population)
            {
                if (ValueOf(pool, key) is { } v)
                {
                    values.Add(v);
                }
            }

            if (values.Count == 0)
            {
                continue;
            }

            values.Sort();
            stats.Add(new BenchmarkStat(key, value, values.Count, Percentile(value, values), Median(values)));
        }

        return new PlayerBenchmark(population.Count, stats);
    }

    /// <summary>
    ///     A pool's value of <paramref name="key" />: the win rate over decided and tied demos, else the pooled
    ///     column. Null when the pool has none, or when the column's catalogue gate says the sample is too thin to
    ///     judge (opening duels under the minimum attempts).
    /// </summary>
    /// <param name="pool">The pool.</param>
    /// <param name="key">A catalogue key, or <see cref="WinRate" />.</param>
    public static double? ValueOf(PlayerAggregate pool, string key)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (key == WinRate)
        {
            int played = pool.Wins + pool.Losses + pool.Ties;
            return played > 0 ? 100.0 * pool.Wins / played : null;
        }

        if (ColumnCatalogue.Resolve(key).Scale is { ColourGateColumns: { Count: > 0 } gate } spec
            && gate.Sum(c => pool[c] ?? 0) < spec.ColourGateMinimum)
        {
            return null;
        }

        return pool[key];
    }

    /// <summary>The share of <paramref name="sorted" /> below <paramref name="value" />, a tie counting half, 0 to 100.</summary>
    /// <param name="value">The value placed.</param>
    /// <param name="sorted">The values it is placed among, ascending; not empty.</param>
    public static double Percentile(double value, IReadOnlyList<double> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int below = 0, equal = 0;
        foreach (double v in sorted)
        {
            if (v < value)
            {
                below++;
            }
            else if (v == value)
            {
                equal++;
            }
            else
            {
                break;
            }
        }

        return 100.0 * (below + (equal / 2.0)) / sorted.Count;
    }

    private static double Median(List<double> sorted)
    {
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

#region

using System.Globalization;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The <c>player_round</c> facet (<c>docs/player-round-format.md</c>): a core stamped ruleset whose one table
///     holds a row per player slot per round, with the side played and the round's kills, deaths, assists, damage,
///     survival and KAST. <see cref="Sides" /> sums each account's rounds by side.
/// </summary>
public static class PlayerRoundFacet
{
    /// <summary>The ruleset's id.</summary>
    public const string RulesetId = "player_round";

    /// <summary>The one table it declares.</summary>
    public const string Table = "player_round";

    /// <summary>The side cell of a CT round.</summary>
    public const int Ct = 3;

    /// <summary>The side cell of a T round.</summary>
    public const int T = 2;

    /// <summary>The counts <see cref="Sides" /> sums per side, as table columns; <c>rounds</c> is added beside them.</summary>
    public static IReadOnlyList<string> Counts { get; } = ["kills", "deaths", "assists", "damage", "survived", "kast"];

    /// <summary>The table's key in the facts store.</summary>
    public static FactKey Key { get; } = new(RulesetId, Table);

    /// <summary>The key <see cref="Sides" /> files a side's count under: <c>CT:kills</c>, <c>T:rounds</c>.</summary>
    /// <param name="side"><see cref="Ct" /> or <see cref="T" />.</param>
    /// <param name="count">A column of <see cref="Counts" />, or <c>rounds</c>.</param>
    public static string Column(int side, string count) => (side == Ct ? "CT:" : "T:") + count;

    /// <summary>
    ///     Every account's rounds summed by side: <c>rounds</c> and each of <see cref="Counts" /> under
    ///     <see cref="Column" />. A round counts on the side the account's slot was on at its freeze end; a row on
    ///     no side (the slot was empty or spectating) counts nowhere. A reconnect splits an account over slots, so
    ///     its rows are summed, a round counts once per side, and survival and KAST hold when any slot had them.
    /// </summary>
    /// <param name="rows">The table's rows.</param>
    public static Dictionary<ulong, Dictionary<string, double>> Sides(IReadOnlyList<FactRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Dictionary<ulong, Dictionary<(int Side, long Round), Dictionary<string, double>>> rounds = [];
        foreach (FactRow row in rows)
        {
            if (!ulong.TryParse(row.Dimensions.GetValueOrDefault("steam_id")?.AsText(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
                || id == 0
                || row.Values.GetValueOrDefault("side")?.AsInteger() is not { } side
                || side is not (Ct or T)
                || row.Dimensions.GetValueOrDefault("round_number")?.AsInteger() is not { } round)
            {
                continue;
            }

            if (!rounds.TryGetValue(id, out Dictionary<(int, long), Dictionary<string, double>>? played))
            {
                rounds[id] = played = [];
            }

            if (!played.TryGetValue(((int)side, round), out Dictionary<string, double>? line))
            {
                played[((int)side, round)] = line = new Dictionary<string, double>(StringComparer.Ordinal);
            }

            foreach (string count in Counts)
            {
                double value = Number(row.Values.GetValueOrDefault(count));
                line[count] = count is "kast" or "survived" ? Math.Max(line.GetValueOrDefault(count), value > 0 ? 1 : 0) : line.GetValueOrDefault(count) + value;
            }
        }

        Dictionary<ulong, Dictionary<string, double>> sides = new(rounds.Count);
        foreach ((ulong id, Dictionary<(int Side, long Round), Dictionary<string, double>> played) in rounds)
        {
            Dictionary<string, double> sums = new(StringComparer.Ordinal);
            foreach (((int side, _), Dictionary<string, double> line) in played)
            {
                string key = Column(side, "rounds");
                sums[key] = sums.GetValueOrDefault(key) + 1;
                foreach ((string count, double value) in line)
                {
                    key = Column(side, count);
                    sums[key] = sums.GetValueOrDefault(key) + value;
                }
            }

            sides[id] = sums;
        }

        return sides;
    }

    // A count, or the kast flag, which the table stores as a boolean.
    private static double Number(FactValue? cell) => cell?.AsNumber() ?? (cell?.AsBoolean() == true ? 1 : 0);
}

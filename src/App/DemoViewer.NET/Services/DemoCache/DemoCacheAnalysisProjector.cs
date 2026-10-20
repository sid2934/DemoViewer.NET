#region

using System.Globalization;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Projects a demo's <c>player_match</c> facet into the scoreboard Match Overview renders from the cache and the
///     side wins the index carries.
///     <para>
///         The facet is the one producer: the facts pass writes it on every visit, the open's included, so a
///         demo's cached scoreboard does not depend on which route stored it. A record's own
///         <see cref="DemoCacheRecord.Scoreboard" /> is what earlier versions wrote, read only while the demo has
///         no facet.
///     </para>
///     <para>
///         The values are the Stats tab's: the facet's counts are copies of its scoreboard columns, and ADR and
///         HLTV are rounded to that scoreboard's <c>F1</c> and <c>F2</c>, which is what its game table holds.
///     </para>
/// </summary>
public static class DemoCacheAnalysisProjector
{
    /// <summary>
    ///     One <see cref="CachedStatRow" /> per row of <paramref name="facet" />, keyed by slot. A row without a
    ///     <c>player_slot</c> is dropped: the page joins rows to the roster by slot, and a row that cannot be
    ///     joined would render as a blank name.
    /// </summary>
    /// <param name="facet">The demo's <c>player_match</c> table.</param>
    public static List<CachedStatRow> ProjectScoreboard(FactTable facet)
    {
        ArgumentNullException.ThrowIfNull(facet);

        List<CachedStatRow> rows = [];
        foreach (FactRow row in facet.Rows)
        {
            if (row.Dimensions.GetValueOrDefault("player_slot")?.AsInteger() is not { } slot)
            {
                continue;
            }

            double rounds = Number(row, "rounds");
            rows.Add(new CachedStatRow
            {
                Slot = (int)slot,
                Team = (int)(row.Dimensions.GetValueOrDefault("team")?.AsInteger() ?? 0),
                Kills = Whole(row, "TotalK"),
                Deaths = Whole(row, "TotalD"),
                Assists = Whole(row, "TotalA"),
                Adr = rounds > 0 ? Formatted(Number(row, "EnemyDmg") / rounds, "F1") : 0,
                Rating = Formatted(Number(row, "HLTV"), "F2"),
                CtWins = Whole(row, "CTW"),
                TWins = Whole(row, "TW")
            });
        }

        return rows;
    }

    /// <summary>The match's round count: the facet's <c>rounds</c>, the same on every row, or 0 when it has no rows.</summary>
    /// <param name="facet">The demo's <c>player_match</c> table.</param>
    public static int Rounds(FactTable facet)
    {
        ArgumentNullException.ThrowIfNull(facet);
        return facet.Rows.Count == 0 ? 0 : facet.Rows.Max(r => Whole(r, "rounds"));
    }

    /// <summary>
    ///     Rounds won by each SIDE across the match. Every row of a team carries that team's own
    ///     <see cref="CachedStatRow.CtWins" />/<see cref="CachedStatRow.TWins" />, so one row per team gives the
    ///     match-wide split; teams whose rows disagree, or no row on either team, yield nulls: a missing number
    ///     beats a wrong one.
    /// </summary>
    /// <param name="rows">The scoreboard rows.</param>
    public static (int? Ct, int? T) ComputeSideWins(IReadOnlyList<CachedStatRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Dictionary<int, (int Ct, int T)> perTeam = [];
        foreach (CachedStatRow row in rows)
        {
            if (row.Team is not (2 or 3))
            {
                continue;
            }

            (int Ct, int T) pair = (row.CtWins, row.TWins);
            if (perTeam.TryGetValue(row.Team, out (int Ct, int T) seen))
            {
                // Rows of one team disagreeing means the per-side columns are not team-wide totals on this
                // demo; refuse the whole derivation rather than pick one row's version of the match.
                if (seen != pair)
                {
                    return (null, null);
                }

                continue;
            }

            perTeam[row.Team] = pair;
        }

        if (perTeam.Count == 0)
        {
            return (null, null);
        }

        int ctSide = 0;
        int tSide = 0;
        foreach ((int Ct, int T) pair in perTeam.Values)
        {
            ctSide += pair.Ct;
            tSide += pair.T;
        }

        return (ctSide, tSide);
    }

    private static double Number(FactRow row, string column) => row.Values.GetValueOrDefault(column)?.AsNumber() ?? 0;

    private static int Whole(FactRow row, string column) => (int)Math.Round(Number(row, column));

    // Rounded the way a stat's format: rounds it, on the exact binary value: 1697 / 20 is 84.8 under F1, where
    // Math.Round scales first and reads 84.85 as a tie.
    private static double Formatted(double value, string format) =>
        double.Parse(value.ToString(format, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}

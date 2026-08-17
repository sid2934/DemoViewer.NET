#region

using System.Globalization;
using Cs2DemoKit.Analysis.Output;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Projects an analysis run's per-player match table into the unified cache's tier-3 scoreboard.
///     <para>
///         Lives here rather than in either caller because there are two producers and they must agree byte
///         for byte: the interactive open (which has a snapshot-bearing run in hand and pays nothing extra to
///         store it) and any future explicit stats pass. A scoreboard that differed depending on which one
///         happened to run last would be the same class of bug as the two-scores-for-one-match problem the
///         Match Overview page is built to avoid.
///     </para>
///     <para>
///         The column names are the analysis engine's own (<c>TotalK</c>, <c>ADR</c>, <c>HLTV</c>, …) — the
///         same keys the Stats tab and Match Overview's live scoreboard read, so the cached render shows the
///         numbers the interactive run produced rather than a second projection that can drift from them.
///     </para>
/// </summary>
public static class DemoCacheAnalysisProjector
{
    /// <summary>
    ///     One <see cref="CachedStatRow" /> per player row of <paramref name="gameTable" />, keyed by slot.
    ///     <para>
    ///         Rows without a resolvable <c>player_slot</c> are dropped: the record stores players once and
    ///         references them by slot, so a row that cannot be joined back to the roster would render as a
    ///         blank name — worse than not being there. Totals rows are excluded the same way (they carry no
    ///         slot).
    ///     </para>
    /// </summary>
    public static List<CachedStatRow> ProjectScoreboard(MetricTable gameTable)
    {
        ArgumentNullException.ThrowIfNull(gameTable);

        List<CachedStatRow> rows = [];
        foreach (MetricRow row in gameTable.Rows)
        {
            if (row.Dimensions.GetValueOrDefault("player_slot") is not { } slotValue)
            {
                continue;
            }

            rows.Add(new CachedStatRow
            {
                Slot = ToInt(slotValue),
                Team = ToInt(row.Dimensions.GetValueOrDefault("team")),
                Kills = ToInt(row.Values.GetValueOrDefault("TotalK")),
                Deaths = ToInt(row.Values.GetValueOrDefault("TotalD")),
                Assists = ToInt(row.Values.GetValueOrDefault("TotalA")),
                Adr = ToDouble(row.Values.GetValueOrDefault("ADR")),
                Rating = ToDouble(row.Values.GetValueOrDefault("HLTV")),
                CtWins = ToInt(row.Values.GetValueOrDefault("CTW")),
                TWins = ToInt(row.Values.GetValueOrDefault("TW"))
            });
        }

        return rows;
    }

    /// <summary>
    ///     Rounds won by each SIDE across the match. Every row of a team carries that team's own
    ///     <c>CTW</c>/<c>TW</c>, so one row per team gives the match-wide split; teams whose rows disagree, or
    ///     a table missing the columns entirely, yield nulls — a missing number beats a wrong one.
    /// </summary>
    public static (int? Ct, int? T) ComputeSideWins(MetricTable gameTable)
    {
        ArgumentNullException.ThrowIfNull(gameTable);

        Dictionary<int, (int Ct, int T)> perTeam = [];
        foreach (MetricRow row in gameTable.Rows)
        {
            if (row.Values.GetValueOrDefault("CTW") is not { } ctw
                || row.Values.GetValueOrDefault("TW") is not { } tw
                || row.Dimensions.GetValueOrDefault("team") is not { } teamValue)
            {
                continue;
            }

            int team = ToInt(teamValue);
            if (team is not (2 or 3))
            {
                continue;
            }

            (int Ct, int T) pair = (ToInt(ctw), ToInt(tw));
            if (perTeam.TryGetValue(team, out (int Ct, int T) seen))
            {
                // Rows of one team disagreeing means the per-side columns are not team-wide totals on this
                // demo; refuse the whole derivation rather than pick one row's version of the match.
                if (seen != pair)
                {
                    return (null, null);
                }

                continue;
            }

            perTeam[team] = pair;
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

    private static int ToInt(object? raw) => raw is null
        ? 0
        : Convert.ToInt32(Convert.ToDouble(raw, CultureInfo.InvariantCulture));

    private static double ToDouble(object? raw) =>
        raw is null ? 0 : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
}

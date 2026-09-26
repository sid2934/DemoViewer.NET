#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Builds a <see cref="MapPoolRecord" /> for one team over <see cref="TeamIdentityService.SidesOf" />
///     (plan.md §3, Map Pool Record, Phase 5): the demo-derivable substitute for the veto model (F12).
///     Stateless: the Dossier tab calls <see cref="Build" /> on selection and on every
///     <see cref="TeamIdentityService.Changed" />, the way the Teams tab re-projects its own selection.
/// </summary>
public static class MapPoolRecordService
{
    /// <summary>One counted demo, before it is grouped into map rows or a decider group.</summary>
    private readonly record struct DemoResult(string Map, bool? Won, int CtRounds, int TRounds, Guid? Opponent, long OrderTicks);

    /// <param name="teams">The service <see cref="TeamIdentityService.SidesOf" /> reads.</param>
    /// <param name="demoCache">The cache a demo's map, score and side-round totals come from.</param>
    /// <param name="teamId">The team the record is for.</param>
    public static MapPoolRecord Build(TeamIdentityService teams, DemoCacheStore demoCache, Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);

        List<DemoResult> results = [];
        foreach ((DemoRef demo, int side, TeamAssignment assignment) in teams.SidesOf(teamId))
        {
            DemoCacheIndexEntry? entry = demoCache.TryGetIndex(demo.Path);
            if (entry?.Map is not { Length: > 0 } map)
            {
                continue;
            }

            bool? won = null;
            if (entry.CtScore is { } ct && entry.TScore is { } t && ct != t)
            {
                int teamScore = side == 3 ? ct : t;
                int opponentScore = side == 3 ? t : ct;
                won = teamScore > opponentScore;
            }

            // The side-round totals live only on the full record (F9's fat sidecar), not on the index
            // row; a team's demo count is small enough that loading each one here is the documented cost
            // the Strat Record Panel and the Matrix already pay for their own evidence passes.
            int ctRounds = 0;
            int tRounds = 0;
            if (demoCache.TryLoadRecord(demo.Path) is { } record)
            {
                ctRounds = record.CtSideWins ?? 0;
                tRounds = record.TSideWins ?? 0;
            }

            Guid? opponentId = assignment.Side(side == 2 ? 3 : 2).TeamId;
            results.Add(new DemoResult(map, won, ctRounds, tRounds, opponentId, entry.ModifiedTicks));
        }

        List<MapPoolMapRow> mapRows =
        [
            .. results
                .GroupBy(r => r.Map, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new MapPoolMapRow
                {
                    Map = g.Key,
                    Played = g.Count(),
                    Wins = g.Count(r => r.Won == true),
                    Losses = g.Count(r => r.Won == false),
                    Undetermined = g.Count(r => r.Won is null),
                    CtRoundsWon = g.Sum(r => r.CtRounds),
                    TRoundsWon = g.Sum(r => r.TRounds)
                })
        ];

        return new MapPoolRecord
        {
            TeamId = teamId,
            Maps = mapRows,
            Deciders = BuildDeciders(results),
            TotalDemos = results.Count
        };
    }

    // A decider is inferable only from a recognizable series length (plan.md D5: manual veto entry
    // only, no scraping, so nothing here reads a series id) — three demos (best of three) or five (best
    // of five) against the same opponent, played the same calendar day by file time (Team Identity's own
    // OrderTicks proxy for a match date, SideKeys.From / TeamClusterer.DateOf). The last demo in such a
    // group, by that same order, is the decider; any other group size, or an opponent that never
    // resolved, says nothing and is left out of the record entirely rather than guessed at.
    private static DeciderRecord BuildDeciders(List<DemoResult> results)
    {
        int played = 0;
        int wins = 0;
        int losses = 0;
        IEnumerable<IGrouping<(Guid Opponent, DateOnly Day), DemoResult>> series = results
            .Where(r => r.Opponent is not null)
            .GroupBy(r => (Opponent: r.Opponent!.Value, Day: DateOnly.FromDateTime(new DateTime(r.OrderTicks))));

        foreach (IGrouping<(Guid Opponent, DateOnly Day), DemoResult> group in series)
        {
            if (group.Count() is not (3 or 5))
            {
                continue;
            }

            DemoResult decider = group.OrderBy(r => r.OrderTicks).Last();
            if (decider.Won is not { } won)
            {
                continue;
            }

            played++;
            if (won)
            {
                wins++;
            }
            else
            {
                losses++;
            }
        }

        return new DeciderRecord { Played = played, Wins = wins, Losses = losses };
    }
}

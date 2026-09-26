#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Builds a team's <see cref="PeriodDiffSet" /> (plan.md §3, Period Diff, Phase 5): its last
///     <c>windowSize</c> demos against the <c>windowSize</c> before those, over
///     <see cref="TeamIdentityService.SidesOf" />, newest first, the same order every other Dossier
///     section reads. Stateless and synchronous, like <see cref="MapPoolRecordService" />: nothing here
///     opens a demo Team Identity or the Map Pool Record has not already loaded for the same team.
///     <para>
///         <b>Rosters are the diff's own signal</b> (team-identity.md §3: "Period Diff reads rosters,
///         'roster to roster'"). A roster does not change on ordinary attrition — one or two members
///         still match the same anchor and stay stamped a stand-in (design §3.3) — so
///         <see cref="PeriodDiffSet.RosterChanged" /> firing means a real boundary: the user started a
///         new roster, or the team's demos this far back belong to a different, earlier-founded one.
///     </para>
/// </summary>
public static class PeriodDiffService
{
    /// <summary>Demos per period when the caller asks for none in particular.</summary>
    public const int DefaultWindowSize = 5;

    /// <param name="teams">The service <see cref="TeamIdentityService.SidesOf" /> and the roster labels read.</param>
    /// <param name="demoCache">The cache a demo's map, score and side-round totals come from.</param>
    /// <param name="teamId">The team the diff is for.</param>
    /// <param name="windowSize">Demos per period; <see cref="DefaultWindowSize" /> when zero or negative.</param>
    public static PeriodDiffSet Build(TeamIdentityService teams, DemoCacheStore demoCache, Guid teamId, int windowSize = DefaultWindowSize)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        if (windowSize <= 0)
        {
            windowSize = DefaultWindowSize;
        }

        Team? team = teams.AllTeams.FirstOrDefault(t => t.Id == teamId);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<PeriodDiffDemoRow> rows = [];

        // SidesOf is already newest first; a team sits on at most one side of a demo (Team Identity never
        // gives both sides of one demo to the same roster), but a duplicate path is skipped defensively
        // the way Situational Behaviour and Map Pool Record both do over the same call.
        foreach ((DemoRef demo, int side, TeamAssignment assignment) in teams.SidesOf(teamId))
        {
            if (!seen.Add(demo.Path))
            {
                continue;
            }

            DemoCacheIndexEntry? entry = demoCache.TryGetIndex(demo.Path);
            string map = entry?.Map is { Length: > 0 } m ? m : "";

            bool? won = null;
            if (entry?.CtScore is { } ctScore && entry.TScore is { } tScore && ctScore != tScore)
            {
                int teamScore = side == 3 ? ctScore : tScore;
                int opponentScore = side == 3 ? tScore : ctScore;
                won = teamScore > opponentScore;
            }

            int ctRounds = 0;
            int tRounds = 0;
            if (demoCache.TryLoadRecord(demo.Path) is { } record)
            {
                ctRounds = record.CtSideWins ?? 0;
                tRounds = record.TSideWins ?? 0;
            }

            SideAssignment sideAssignment = assignment.Side(side);
            string? rosterId = sideAssignment.RosterId;
            string rosterLabel = rosterId is null
                ? ""
                : team?.Rosters.FirstOrDefault(r => string.Equals(r.Id, rosterId, StringComparison.Ordinal))?.Label ?? rosterId;

            rows.Add(new PeriodDiffDemoRow(demo.Path, demo.Sha256, map, entry?.ModifiedTicks ?? 0, side,
                rosterId, rosterLabel, sideAssignment.StandIn, won, ctRounds, tRounds));
        }

        PeriodDiffPeriod recent = BuildPeriod("last", [.. rows.Take(windowSize)]);
        PeriodDiffPeriod previous = BuildPeriod("previous", [.. rows.Skip(windowSize).Take(windowSize)]);

        bool rosterChanged = recent.DominantRoster is { } r && previous.DominantRoster is { } p
                              && !string.Equals(r.RosterId, p.RosterId, StringComparison.Ordinal);

        return new PeriodDiffSet
        {
            TeamId = teamId,
            WindowSize = windowSize,
            TotalDemos = rows.Count,
            Recent = recent,
            Previous = previous,
            RosterChanged = rosterChanged
        };
    }

    private static PeriodDiffPeriod BuildPeriod(string label, List<PeriodDiffDemoRow> demos) => new()
    {
        Label = label,
        Demos = demos,
        Rosters =
        [
            .. demos
                .Where(d => d.RosterId is not null)
                .GroupBy(d => d.RosterId, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PeriodDiffRosterRow(g.Key!, g.First().RosterLabel, g.Count()))
        ]
    };
}

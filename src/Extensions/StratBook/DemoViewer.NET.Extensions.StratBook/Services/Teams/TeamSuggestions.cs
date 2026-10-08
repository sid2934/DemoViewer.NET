#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>What a suggestion proposes. Every suggestion waits for the user; none is applied on its own.</summary>
public enum TeamSuggestionKind
{
    /// <summary>The players you queue with most, as your team.</summary>
    Squad,

    /// <summary>A team's players changed: start a new roster in the same team.</summary>
    RosterChange,

    /// <summary>Two teams carry the same clan tag: merge them.</summary>
    MergeByTag
}

/// <summary>One player named by a suggestion, with the count that earned the place. Names are raw.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="Name">Its raw last-seen name.</param>
/// <param name="Games">Games that count toward the suggestion (with you, or in the team's last ten).</param>
public sealed record SuggestedPlayer(string SteamId64, string Name, int Games);

/// <summary>
///     A pending suggestion. <see cref="Id" /> is stable for the same proposal, which is what a dismissal
///     records, so a dismissed suggestion comes back only when what it proposes changes.
/// </summary>
public sealed record TeamSuggestion
{
    public required string Id { get; init; }
    public required TeamSuggestionKind Kind { get; init; }

    /// <summary>Squad: the proposed core, you first. Roster change: the players who joined.</summary>
    public IReadOnlyList<SuggestedPlayer> Players { get; init; } = [];

    /// <summary>Roster change: the lineup players no longer in the active roster.</summary>
    public IReadOnlyList<SuggestedPlayer> Left { get; init; } = [];

    /// <summary>Squad: games the whole proposed core played together on your side.</summary>
    public int GamesTogether { get; init; }

    /// <summary>Squad: games you played in the library.</summary>
    public int MyGames { get; init; }

    /// <summary>Roster change: the team. Merge: the team kept.</summary>
    public Guid? TeamId { get; init; }

    /// <summary>Merge: the team folded in.</summary>
    public Guid? OtherTeamId { get; init; }

    /// <summary>Roster change: when the newcomer first appears in the window. Approximate: the order is file date.</summary>
    public DateOnly? Since { get; init; }

    /// <summary>The team or tag the suggestion is about, raw.</summary>
    public string Subject { get; init; } = "";
}

/// <summary>
///     The suggestion rules, pure over the two team files so every rule is a fixture. Thresholds are named
///     constants; the roster rule is Valve's own active-roster rule from the Regional Standings model
///     (<c>ValveSoftware/counter-strike_regional_standings</c>, <c>model/team.js</c>, <c>setActiveRoster</c>):
///     the players with five or more of the team's last ten matches, newest first, at most five.
/// </summary>
public static class TeamSuggestions
{
    /// <summary>
    ///     The whole squad must have played this many games together on your side, and so each partner at
    ///     least this many with you. No share bar: on a 266-replay library the trio played 17 together
    ///     while the single most frequent partner reached only 47, so any share that admits the
    ///     trio admits nearly everyone.
    /// </summary>
    public const int SquadMinGames = 8;

    /// <summary>The largest squad offered: you plus four.</summary>
    public const int SquadMaxSize = 5;

    /// <summary>The smallest squad offered: you plus two, the trio a user describes.</summary>
    public const int SquadMinSize = 3;

    /// <summary>Valve's active-roster window: the team's last ten matches.</summary>
    public const int ActiveWindow = 10;

    /// <summary>Valve's active-roster bar: five or more of those ten.</summary>
    public const int ActiveMinAppearances = 5;

    /// <summary>
    ///     Who plays for a team now, by Valve's rule over the team's sides newest first. Every roster of
    ///     the team counts, so the answer does not depend on how its rosters were cut.
    /// </summary>
    /// <param name="index">The derived index.</param>
    /// <param name="teamId">The team.</param>
    public static IReadOnlyList<SuggestedPlayer> ActiveRoster(TeamIndexFile index, Guid teamId) =>
        ActiveOf([.. SidesOf(index, teamId, null)]);

    /// <summary>Every pending suggestion, dismissed ones left out.</summary>
    /// <param name="teams">The user file.</param>
    /// <param name="index">The derived index.</param>
    public static IReadOnlyList<TeamSuggestion> Compute(TeamsFile teams, TeamIndexFile index) =>
        [.. ComputeAll(teams, index).Where(s => !IsDismissed(s, teams))];

    /// <summary>The suggestions the user dismissed that still hold: what "Show settled" lists, with Restore.</summary>
    /// <param name="teams">The user file.</param>
    /// <param name="index">The derived index.</param>
    public static IReadOnlyList<TeamSuggestion> Dismissed(TeamsFile teams, TeamIndexFile index) =>
        [.. ComputeAll(teams, index).Where(s => IsDismissed(s, teams))];

    /// <summary>Every suggestion the files support, dismissed or not.</summary>
    /// <param name="teams">The user file.</param>
    /// <param name="index">The derived index.</param>
    public static IReadOnlyList<TeamSuggestion> ComputeAll(TeamsFile teams, TeamIndexFile index)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(index);
        List<TeamSuggestion> all = [];
        if (Squad(teams, index) is { } squad)
        {
            all.Add(squad);
        }

        all.AddRange(RosterChanges(teams, index));
        all.AddRange(MergesByTag(teams));
        return all;
    }

    /// <summary>
    ///     The dismissed ids that hold <paramref name="suggestion" /> back: its own id, or for a squad or a roster
    ///     change one whose players differ by at most one SteamID and share at least one, so a single player
    ///     changing does not bring a dismissed suggestion back. Your own accounts are left out of a squad's
    ///     players, since every squad holds them. A roster change matches only within its team and roster.
    /// </summary>
    /// <param name="suggestion">The suggestion.</param>
    /// <param name="teams">The user file: the dismissed ids and your accounts.</param>
    public static IEnumerable<string> DismissalsOf(TeamSuggestion suggestion, TeamsFile teams)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(teams);
        (string Prefix, HashSet<string> Players)? own = PlayersOf(suggestion.Id, teams.Me.SteamIds);
        foreach (string id in teams.DismissedSuggestions)
        {
            if (string.Equals(id, suggestion.Id, StringComparison.Ordinal))
            {
                yield return id;
                continue;
            }

            if (own is not { } mine || PlayersOf(id, teams.Me.SteamIds) is not { } other
                || !string.Equals(mine.Prefix, other.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int shared = mine.Players.Count(other.Players.Contains);
            if (shared >= 1 && Math.Max(mine.Players.Count, other.Players.Count) - shared <= 1)
            {
                yield return id;
            }
        }
    }

    /// <summary>Whether a dismissal holds <paramref name="suggestion" /> back.</summary>
    /// <param name="suggestion">The suggestion.</param>
    /// <param name="teams">The user file.</param>
    public static bool IsDismissed(TeamSuggestion suggestion, TeamsFile teams) => DismissalsOf(suggestion, teams).Any();

    // "squad:a,b,c" and "roster:<team>:<roster>:a,b": the part that must match exactly, and the players.
    private static (string Prefix, HashSet<string> Players)? PlayersOf(string id, IEnumerable<string> me)
    {
        int cut = id.LastIndexOf(':');
        if (cut < 0 || !(id.StartsWith("squad:", StringComparison.Ordinal) || id.StartsWith("roster:", StringComparison.Ordinal)))
        {
            return null;
        }

        HashSet<string> players = new(id[(cut + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (id.StartsWith("squad:", StringComparison.Ordinal))
        {
            players.ExceptWith(me);
        }

        return (id[..cut], players);
    }

    /// <summary>
    ///     The players on your side of each demo you played, with their counts, you excluded. What the
    ///     squad editor lists and the squad suggestion reads.
    /// </summary>
    /// <param name="teams">The user file (for the me accounts).</param>
    /// <param name="index">The derived index.</param>
    public static (IReadOnlyList<SuggestedPlayer> CoPlayers, int MyGames, string? MainAccount) CoPlayers(TeamsFile teams, TeamIndexFile index)
    {
        HashSet<string> me = new(teams.Me.SteamIds, StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        Dictionary<string, int> mine = new(StringComparer.Ordinal);
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        int games = 0;
        if (me.Count == 0)
        {
            return ([], 0, null);
        }

        foreach (TeamIndexDemo row in index.Demos.Values.OrderBy(d => d.OrderTicks))
        {
            if (MySide(row, me) is not { } side)
            {
                continue;
            }

            games++;
            for (int i = 0; i < side.Key.Count; i++)
            {
                string id = side.Key[i];
                names[id] = i < side.Names.Count ? side.Names[i] : id;
                if (me.Contains(id))
                {
                    mine[id] = mine.GetValueOrDefault(id) + 1;
                }
                else
                {
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            }
        }

        string? main = mine.OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key).FirstOrDefault();
        List<SuggestedPlayer> players =
        [
            .. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => new SuggestedPlayer(c.Key, names[c.Key], c.Value))
        ];
        return (players, games, main);
    }

    // The group you queue with: your main account, then partners in count order, each kept only while
    // the whole group still played SquadMinGames together, so the core is people who actually play as one.
    private static TeamSuggestion? Squad(TeamsFile teams, TeamIndexFile index)
    {
        if (teams.Teams.Any(t => t.IsUs && t.Rosters.Any(r => r.IsSquad)))
        {
            return null;
        }

        (IReadOnlyList<SuggestedPlayer> coPlayers, int myGames, string? main) = CoPlayers(teams, index);
        if (main is null || myGames == 0)
        {
            return null;
        }

        HashSet<string> me = new(teams.Me.SteamIds, StringComparer.Ordinal);
        List<HashSet<string>> mySides =
        [
            .. index.Demos.Values.Select(r => MySide(r, me)).OfType<TeamIndexSide>()
                .Select(s => new HashSet<string>(s.Key, StringComparer.Ordinal))
        ];
        List<SuggestedPlayer> core = [];
        HashSet<string> group = new(StringComparer.Ordinal) { main };
        foreach (SuggestedPlayer candidate in coPlayers)
        {
            if (group.Count >= SquadMaxSize)
            {
                break;
            }

            if (candidate.Games < SquadMinGames)
            {
                break;
            }

            group.Add(candidate.SteamId64);
            if (mySides.Count(s => group.IsSubsetOf(s)) >= SquadMinGames)
            {
                core.Add(candidate);
            }
            else
            {
                group.Remove(candidate.SteamId64);
            }
        }

        if (group.Count < SquadMinSize)
        {
            return null;
        }

        int together = mySides.Count(s => group.IsSubsetOf(s));
        string mainName = index.Demos.Values.Select(r => MySide(r, me)).OfType<TeamIndexSide>()
            .Select(s => s.Key.IndexOf(main) is var i and >= 0 && i < s.Names.Count ? s.Names[i] : null)
            .LastOrDefault(n => n is not null) ?? main;
        return new TeamSuggestion
        {
            Id = "squad:" + string.Join(",", group.Order(StringComparer.Ordinal)),
            Kind = TeamSuggestionKind.Squad,
            Players = [new SuggestedPlayer(main, mainName, myGames), .. core],
            GamesTogether = together,
            MyGames = myGames
        };
    }

    // Valve's active roster over each fixed-five roster's own sides: a player in five of its last ten who
    // is not in the five means the lineup changed. Three of the five still active keeps it the same team
    // by Valve's continuity rule, so the proposal is a new roster in this team, not a new team.
    private static IEnumerable<TeamSuggestion> RosterChanges(TeamsFile teams, TeamIndexFile index)
    {
        foreach (Team team in teams.Teams.Where(t => !t.Hidden))
        {
            foreach (Roster roster in team.Rosters.Where(r => r.HasCoreLineup && !r.IsSquad))
            {
                List<(TeamIndexDemo Row, TeamIndexSide Side)> sides = [.. SidesOf(index, team.Id, roster.Id)];
                IReadOnlyList<SuggestedPlayer> active = ActiveOf(sides);
                HashSet<string> lineup = new(roster.CoreLineup!, StringComparer.Ordinal);
                List<SuggestedPlayer> joined = [.. active.Where(p => !lineup.Contains(p.SteamId64))];
                if (joined.Count == 0 || active.Count(p => lineup.Contains(p.SteamId64)) < TeamClusterer.Continuity)
                {
                    continue;
                }

                // The newcomer's first side inside the window, oldest first.
                HashSet<string> joinedIds = new(joined.Select(p => p.SteamId64), StringComparer.Ordinal);
                long firstTicks = sides.Take(ActiveWindow)
                    .Where(s => s.Side.Key.Any(joinedIds.Contains))
                    .Min(s => s.Row.OrderTicks);
                DateOnly since = TeamClusterer.DateOf(firstTicks);
                if (team.Rosters.Any(r => r.UserStarted && r.Since >= since))
                {
                    continue;
                }

                HashSet<string> activeIds = new(active.Select(p => p.SteamId64), StringComparer.Ordinal);
                Dictionary<string, string> names = NamesOf(sides.Select(s => s.Side));
                yield return new TeamSuggestion
                {
                    Id = $"roster:{team.Id:N}:{roster.Id}:{string.Join(",", joinedIds.Order(StringComparer.Ordinal))}",
                    Kind = TeamSuggestionKind.RosterChange,
                    Players = joined,
                    Left =
                    [
                        .. roster.CoreLineup!.Where(id => !activeIds.Contains(id))
                            .Select(id => new SuggestedPlayer(id, names.GetValueOrDefault(id, id), 0))
                    ],
                    TeamId = team.Id,
                    Since = since,
                    Subject = team.Name
                };
            }
        }
    }

    // Two visible teams named from the same clan tag, after the case fold the names already use: fold the
    // newer into the one founded first.
    private static IEnumerable<TeamSuggestion> MergesByTag(TeamsFile teams)
    {
        foreach (IGrouping<string, Team> group in teams.Teams
                     .Where(t => !t.Hidden && t.NameSource == TeamNameSource.ClanTag)
                     .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            List<Team> ordered = [.. group.OrderBy(t => t.Rosters.Count == 0 ? DateOnly.MaxValue : t.Rosters.Min(r => r.Since))];
            Team into = ordered[0];
            foreach (Team from in ordered.Skip(1))
            {
                yield return new TeamSuggestion
                {
                    Id = $"merge:{into.Id:N}:{from.Id:N}",
                    Kind = TeamSuggestionKind.MergeByTag,
                    TeamId = into.Id,
                    OtherTeamId = from.Id,
                    Subject = into.Name
                };
            }
        }
    }

    // A team's sides newest first, optionally one roster's.
    private static IEnumerable<(TeamIndexDemo Row, TeamIndexSide Side)> SidesOf(TeamIndexFile index, Guid teamId, string? rosterId) =>
        index.Demos.Values
            .SelectMany(row => row.Sides.Values.Select(side => (Row: row, Side: side)))
            .Where(p => p.Side.TeamId == teamId && (rosterId is null || string.Equals(p.Side.RosterId, rosterId, StringComparison.Ordinal)))
            .OrderByDescending(p => p.Row.OrderTicks)
            .ThenByDescending(p => p.Row.Path, StringComparer.Ordinal);

    // model/team.js setActiveRoster: the last ten matches, players with five or more, ordered by how
    // recently they last played, at most five.
    private static IReadOnlyList<SuggestedPlayer> ActiveOf(IReadOnlyList<(TeamIndexDemo Row, TeamIndexSide Side)> newestFirst)
    {
        Dictionary<string, (int Count, int MostRecent)> seen = new(StringComparer.Ordinal);
        List<(TeamIndexDemo Row, TeamIndexSide Side)> window = [.. newestFirst.Take(ActiveWindow)];
        for (int i = 0; i < window.Count; i++)
        {
            foreach (string id in window[i].Side.Key)
            {
                seen[id] = seen.TryGetValue(id, out (int Count, int MostRecent) s) ? (s.Count + 1, s.MostRecent) : (1, i);
            }
        }

        Dictionary<string, string> names = NamesOf(window.Select(w => w.Side));
        return
        [
            .. seen.Where(s => s.Value.Count >= ActiveMinAppearances)
                .OrderBy(s => s.Value.MostRecent).ThenByDescending(s => s.Value.Count).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(s => new SuggestedPlayer(s.Key, names.GetValueOrDefault(s.Key, s.Key), s.Value.Count))
        ];
    }

    // The newest name first: the sides arrive newest first, so the first name seen wins.
    private static Dictionary<string, string> NamesOf(IEnumerable<TeamIndexSide> newestFirst)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (TeamIndexSide side in newestFirst)
        {
            for (int i = 0; i < side.Key.Count; i++)
            {
                names.TryAdd(side.Key[i], i < side.Names.Count ? side.Names[i] : side.Key[i]);
            }
        }

        return names;
    }

    // The side a me account sat on, when exactly one side has one.
    private static TeamIndexSide? MySide(TeamIndexDemo row, HashSet<string> me)
    {
        TeamIndexSide? t = row.Side(2);
        TeamIndexSide? ct = row.Side(3);
        bool onT = t?.Key.Any(me.Contains) ?? false;
        bool onCt = ct?.Key.Any(me.Contains) ?? false;
        return onT == onCt ? null : onT ? t : ct;
    }

    /// <summary>Formats a player list for a line of text; raw names, sanitize at the render boundary.</summary>
    public static string Join(IEnumerable<SuggestedPlayer> players) =>
        string.Join(", ", players.Select(p => p.Name));

    /// <summary>A count with its noun, pluralised.</summary>
    public static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}

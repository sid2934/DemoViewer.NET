#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     The clustering core (design §3.3): side keys assigned to rosters in <c>orderTicks</c> order against
///     a two-tier anchor, the fixed five where one is established and the rolling extended core of up to
///     seven otherwise, at Valve's continuity of three of five.
///     <para>
///         <b>Seeded, then incremental.</b> The rosters in <c>teams.json</c> are seeded first with their
///         anchors (the fixed five, else the extended-core snapshot until a side joins), so a rebuild
///         re-matches every side against the same identities and keeps their ids. A side that matches
///         nothing becomes a one-side candidate; a second side sharing three or more with it founds a new
///         auto roster in a new auto team. Single sightings never create a team, so a library of solo
///         queues creates none.
///     </para>
///     <para>
///         <b>Order-dependent by nature.</b> The first side seen founds the roster and the first repeated
///         five fixes its lineup; the caller feeds demos in <c>orderTicks</c> ascending, then path.
///         Everything else is deterministic: ties go to a roster with an established five, then to the
///         roster founded first, and equal counts in the extended core break by first seen, then by id.
///     </para>
/// </summary>
public sealed class TeamClusterer
{
    /// <summary>
    ///     Valve's continuity rule: at least three of the roster must play in each match
    ///     (tournament-operation-requirements §3.2.5(a), forfeited under §3.10.1). Measured on the owner's
    ///     277 replays: k = 4 breaks a roster on one stand-in, k = 2 chains 50 sides into one non-roster.
    /// </summary>
    public const int Continuity = 3;

    /// <summary>
    ///     The extended core's cap: Valve's maximum registered persons (five, a coach, a substitute), and
    ///     the size at which the measurement stops chaining (a cap of 5 splits a roster when the fourth
    ///     rotates; 10 starts chaining again).
    /// </summary>
    public const int ExtendedCoreCap = 7;

    private readonly List<RosterState> _rosters = [];

    // Which rosters to consult for a member: every roster the member has ever sat on, plus the seeded
    // rosters whose snapshot names them. A superset of the anchors, so no candidate is missed and no
    // side compares against every one-side candidate in the library.
    private readonly Dictionary<string, List<RosterState>> _postings = new(StringComparer.Ordinal);

    // Rosters the user started that have no side yet: their anchor is borrowed from the roster they
    // succeed, so they are consulted for every side of their team's dates rather than through postings.
    private readonly List<RosterState> _emptyStarted = [];

    private readonly Dictionary<string, TeamIndexDemo> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _tombstones;
    private readonly TeamsFile _teams;

    /// <param name="teams">The user file whose rosters seed the state. Mutated by <see cref="Finish" />.</param>
    public TeamClusterer(TeamsFile teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        _teams = teams;
        _tombstones = [.. teams.Tombstones];
        Seed();
    }

    /// <summary>The rows assigned so far, keyed by stable key.</summary>
    public IReadOnlyDictionary<string, TeamIndexDemo> Rows => _rows;

    /// <summary>
    ///     Assigns both sides of one demo. Sides are assigned before either is applied, so a side never
    ///     matches the candidate the other side just founded; a roster never takes both sides.
    /// </summary>
    /// <param name="demo">The demo's side inputs.</param>
    /// <param name="overrides">Per side (2 or 3): the team a user pinned the side to, or null for "not a team".</param>
    public TeamIndexDemo Assign(DemoSideInput demo, IReadOnlyDictionary<int, Guid?>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        DateOnly date = DateOf(demo.OrderTicks);
        TeamIndexDemo row = new()
        {
            Path = demo.Path,
            Sha256 = demo.Sha256,
            OrderTicks = demo.OrderTicks
        };
        _rows[demo.StableKey] = row;

        Pending t = Prepare(demo, 2, date, overrides, row);
        Pending ct = Prepare(demo, 3, date, overrides, row);

        // A roster never takes both sides of one demo: the larger overlap keeps it, T on a tie, and the
        // other side founds its own candidate (a user override fixes a genuine mirror match).
        if (t.Match is { } tm && ct.Match is { } cm && ReferenceEquals(tm.Roster, cm.Roster))
        {
            if (tm.Overlap >= cm.Overlap)
            {
                ct = ct with { Match = null };
            }
            else
            {
                t = t with { Match = null };
            }
        }

        Apply(demo, t);
        Apply(demo, ct);
        return row;
    }

    /// <summary>
    ///     Writes the derived result back: the index file, and the user file's new auto teams, snapshots,
    ///     established fives and auto names. Auto teams that received no side are deleted; a user-named
    ///     team with none survives as "no demos".
    /// </summary>
    /// <param name="nowTicks">The build stamp.</param>
    public TeamIndexFile Finish(long nowTicks)
    {
        TeamIndexFile index = new()
        {
            BuiltAtTicks = nowTicks,
            Demos = new Dictionary<string, TeamIndexDemo>(_rows, StringComparer.Ordinal)
        };

        foreach (RosterState state in _rosters)
        {
            if (state.Team is null)
            {
                if (state.Sides.Count == 1)
                {
                    index.Unaffiliated.Add(new TeamIndexUnaffiliated
                    {
                        Demo = state.Sides[0].Demo.StableKey,
                        Side = state.Sides[0].Side
                    });
                }

                continue;
            }

            if (state.Keys.Count > 0)
            {
                state.Roster!.ExtendedCore = [.. TopMembers(state, ExtendedCoreCap)];
            }

            if (state.Lineup is not null)
            {
                state.Roster!.CoreLineup = [.. state.Lineup.Order(StringComparer.Ordinal)];
            }

            if (state.Members.Count > 0)
            {
                Dictionary<string, TeamIndexMember> members = new(StringComparer.Ordinal);
                foreach ((string id, MemberState m) in state.Members)
                {
                    members[id] = new TeamIndexMember
                    {
                        Count = m.Count,
                        LastName = m.LastName,
                        FirstTicks = m.FirstTicks,
                        LastTicks = m.LastTicks,
                        StandInCount = m.StandIns
                    };
                }

                string teamKey = state.Team.Id.ToString();
                if (!index.Members.TryGetValue(teamKey, out Dictionary<string, Dictionary<string, TeamIndexMember>>? perRoster))
                {
                    perRoster = new Dictionary<string, Dictionary<string, TeamIndexMember>>(StringComparer.Ordinal);
                    index.Members[teamKey] = perRoster;
                }

                perRoster[state.Roster!.Id] = members;
            }
        }

        // Auto teams that received no side this pass are clustering's own leftovers and go; anything
        // the user touched stays.
        HashSet<Guid> withSides = [.. _rosters.Where(r => r.Team is not null && r.Sides.Count > 0).Select(r => r.Team!.Id)];
        _teams.Teams.RemoveAll(team => team.IsAuto && !withSides.Contains(team.Id));

        foreach (Team team in _teams.Teams)
        {
            if (team.NameSource != TeamNameSource.User)
            {
                NameTeam(team);
            }
        }

        return index;
    }

    // ── Seeding ────────────────────────────────────────────────────────────────────────────────

    private void Seed()
    {
        List<(Team Team, Roster Roster, int FileOrder)> seeds = [];
        int order = 0;
        foreach (Team team in _teams.Teams)
        {
            foreach (Roster roster in team.Rosters)
            {
                seeds.Add((team, roster, order++));
            }
        }

        // Founded-first among seeds is the roster's date, then the file's order.
        foreach ((Team team, Roster roster, int _) in seeds.OrderBy(s => s.Roster.Since).ThenBy(s => s.FileOrder))
        {
            RosterState state = new()
            {
                Team = team,
                Roster = roster,
                Since = roster.Since,
                FoundedOrder = _rosters.Count,
                Lineup = roster.HasCoreLineup ? new HashSet<string>(roster.CoreLineup!, StringComparer.Ordinal) : null,
                SeedAnchor = roster.ExtendedCore is { Count: > 0 }
                    ? new HashSet<string>(roster.ExtendedCore, StringComparer.Ordinal)
                    : null
            };
            _rosters.Add(state);
            foreach (string member in state.Lineup ?? state.SeedAnchor ?? [])
            {
                Post(member, state);
            }
        }

        // A started roster succeeds the team's latest earlier roster and borrows its anchor until a
        // side of its own joins.
        foreach (RosterState state in _rosters.Where(s => s.Roster!.UserStarted))
        {
            state.Predecessor = _rosters
                .Where(s => !ReferenceEquals(s, state) && ReferenceEquals(s.Team, state.Team) && s.Since <= state.Since)
                .OrderByDescending(s => s.Since)
                .ThenByDescending(s => s.FoundedOrder)
                .FirstOrDefault();
            if (state.Lineup is null && state.SeedAnchor is null)
            {
                _emptyStarted.Add(state);
            }
        }
    }

    // ── Matching ───────────────────────────────────────────────────────────────────────────────

    private Pending Prepare(DemoSideInput demo, int side, DateOnly date, IReadOnlyDictionary<int, Guid?>? overrides,
        TeamIndexDemo row)
    {
        SideInput input = demo.Side(side);
        TeamIndexSide rowSide = new()
        {
            Key = [.. input.Key],
            Names = [.. input.Names],
            Clan = input.Clan
        };
        row.Sides[side.ToString(CultureInfo.InvariantCulture)] = rowSide;

        if (!input.IsClusterable)
        {
            return new Pending(side, input, rowSide, null, null, false);
        }

        if (overrides is not null && overrides.TryGetValue(side, out Guid? pinned))
        {
            rowSide.Override = true;
            return new Pending(side, input, rowSide, null, pinned, true);
        }

        return new Pending(side, input, rowSide, Compete(input, date), null, false);
    }

    private Match? Compete(SideInput input, DateOnly date)
    {
        HashSet<string> key = new(input.Key, StringComparer.Ordinal);
        int k = Math.Min(Continuity, key.Count);
        HashSet<RosterState> seen = [];
        Match? best = null;

        void Consider(RosterState state)
        {
            if (!seen.Add(state) || !IsEligible(state, date))
            {
                return;
            }

            HashSet<string> anchor = Anchor(state);
            int overlap = 0;
            foreach (string member in key)
            {
                if (anchor.Contains(member))
                {
                    overlap++;
                }
            }

            if (overlap < k)
            {
                return;
            }

            int tier = state.Lineup is not null ? 1 : 2;
            // Candidates compete by overlap; a tie goes first to an established five, then founded-first.
            if (best is null
                || overlap > best.Overlap
                || (overlap == best.Overlap && (tier < best.Tier
                                                || (tier == best.Tier && state.FoundedOrder < best.Roster.FoundedOrder))))
            {
                best = new Match(state, overlap, tier);
            }
        }

        foreach (string member in key)
        {
            if (_postings.TryGetValue(member, out List<RosterState>? states))
            {
                foreach (RosterState state in states)
                {
                    Consider(state);
                }
            }
        }

        foreach (RosterState state in _emptyStarted)
        {
            Consider(state);
        }

        return best;
    }

    // A started roster takes its team's sides from its date on; the team's earlier rosters keep the
    // sides before it. Rosters the user has not dated are never excluded.
    private bool IsEligible(RosterState state, DateOnly date)
    {
        if (state.Team is null)
        {
            return true;
        }

        if (state.Roster!.UserStarted && state.Since > date)
        {
            return false;
        }

        RosterState? inForce = null;
        foreach (RosterState other in _rosters)
        {
            if (ReferenceEquals(other.Team, state.Team) && other.Roster!.UserStarted && other.Since <= date
                && (inForce is null || other.Since > inForce.Since))
            {
                inForce = other;
            }
        }

        return inForce is null || ReferenceEquals(inForce, state) || state.Since >= inForce.Since;
    }

    private static HashSet<string> Anchor(RosterState state)
    {
        if (state.Lineup is not null)
        {
            return state.Lineup;
        }

        if (state.Keys.Count > 0)
        {
            return state.AnchorCache ??= [.. TopMembers(state, ExtendedCoreCap)];
        }

        if (state.SeedAnchor is not null)
        {
            return state.SeedAnchor;
        }

        return state.Predecessor is { } predecessor ? Anchor(predecessor) : [];
    }

    // Count descending, then FIRST seen, then id. The design text says "then last seen", but its
    // measured row (24 rosters, 5 fives, 18 of 29, 428 unaffiliated) was taken with the script's
    // insertion order, which is first seen; last seen gives 20 of 31 and 430 on the same corpus. The
    // corpus fixture pins this order, and a longer-standing member outranking a newer one at equal
    // count is also the reading Valve's roster continuity suggests.
    private static IEnumerable<string> TopMembers(RosterState state, int cap) =>
        state.Members
            .OrderByDescending(m => m.Value.Count)
            .ThenBy(m => m.Value.FirstTicks)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .Take(cap)
            .Select(m => m.Key);

    // ── Applying ───────────────────────────────────────────────────────────────────────────────

    private void Apply(DemoSideInput demo, Pending pending)
    {
        if (!pending.Input.IsClusterable)
        {
            return;
        }

        if (pending.IsOverride)
        {
            if (pending.PinnedTeam is { } teamId && _teams.Find(teamId) is { } team)
            {
                Join(PinnedRoster(team, pending.Input, DateOf(demo.OrderTicks)), demo, pending, 0, 0, overridden: true);
            }

            return;
        }

        if (pending.Match is { } match)
        {
            Join(match.Roster, demo, pending, match.Overlap, match.Tier, overridden: false);
            return;
        }

        Found(demo, pending);
    }

    // An override names a team; the side joins the team's roster it best fits, else the team's latest,
    // and a team with no roster yet gets one so the side has somewhere to sit.
    private RosterState PinnedRoster(Team team, SideInput input, DateOnly date)
    {
        RosterState? best = null;
        int bestOverlap = -1;
        foreach (RosterState state in _rosters)
        {
            if (!ReferenceEquals(state.Team, team) || !IsEligible(state, date))
            {
                continue;
            }

            int overlap = input.Key.Count(m => Anchor(state).Contains(m));
            if (overlap > bestOverlap || (overlap == bestOverlap && state.Since > best!.Since))
            {
                best = state;
                bestOverlap = overlap;
            }
        }

        if (best is not null)
        {
            return best;
        }

        Roster roster = new()
        {
            Id = NextRosterId(team),
            Since = date
        };
        team.Rosters.Add(roster);
        RosterState fresh = new()
        {
            Team = team,
            Roster = roster,
            Since = date,
            FoundedOrder = _rosters.Count
        };
        _rosters.Add(fresh);
        return fresh;
    }

    private void Join(RosterState state, DemoSideInput demo, Pending pending, int overlap, int tier, bool overridden)
    {
        HashSet<string> anchorBefore = Anchor(state);
        HashSet<string> key = new(pending.Input.Key, StringComparer.Ordinal);
        bool standIn = overlap is 3 or 4 && key.Any(m => !anchorBefore.Contains(m));

        if (state.Team is null)
        {
            Promote(state);
        }

        state.Keys.Add(key);
        state.Sides.Add((demo, pending.Side));
        state.AnchorCache = null;
        // Consulted through postings from here on, like every roster with a side.
        _emptyStarted.Remove(state);

        foreach (string member in key)
        {
            if (!state.Members.TryGetValue(member, out MemberState? m))
            {
                m = new MemberState { FirstTicks = demo.OrderTicks };
                state.Members[member] = m;
                Post(member, state);
            }

            m.Count++;
            m.LastTicks = demo.OrderTicks;
            m.LastName = pending.Input.NameOf(member);
            if (standIn && !anchorBefore.Contains(member))
            {
                m.StandIns++;
            }
        }

        if (pending.Input.Clan is { } clan)
        {
            state.ClanCounts[clan] = state.ClanCounts.GetValueOrDefault(clan) + 1;
            state.ClanSpelling[clan] = clan;
        }

        // The fixed five: the first time the same five is seen twice on this roster, and never again.
        if (state.Lineup is null && key.Count == 5 && state.Keys.Count(k => k.SetEquals(key)) >= 2)
        {
            state.Lineup = key;
            state.AnchorCache = null;
        }

        pending.Row.TeamId = state.Team!.Id;
        pending.Row.RosterId = state.Roster!.Id;
        pending.Row.Overlap = overlap;
        pending.Row.Tier = tier;
        pending.Row.StandIn = standIn;
        pending.Row.Override = overridden;
    }

    private void Found(DemoSideInput demo, Pending pending)
    {
        RosterState state = new()
        {
            Since = DateOf(demo.OrderTicks),
            FoundedOrder = _rosters.Count,
            Founder = (demo.StableKey, pending.Side)
        };
        _rosters.Add(state);
        HashSet<string> key = new(pending.Input.Key, StringComparer.Ordinal);
        state.Keys.Add(key);
        state.Sides.Add((demo, pending.Side));
        foreach (string member in key)
        {
            state.Members[member] = new MemberState
            {
                Count = 1,
                FirstTicks = demo.OrderTicks,
                LastTicks = demo.OrderTicks,
                LastName = pending.Input.NameOf(member)
            };
            Post(member, state);
        }

        if (pending.Input.Clan is { } clan)
        {
            state.ClanCounts[clan] = 1;
            state.ClanSpelling[clan] = clan;
        }
    }

    // A candidate's second side makes it a roster in a new auto team; the founder side is stamped now.
    private void Promote(RosterState state)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        } while (_tombstones.Contains(id));

        Team team = new()
        {
            Id = id,
            NameSource = TeamNameSource.Auto
        };
        Roster roster = new()
        {
            Id = "r1",
            Since = state.Since
        };
        team.Rosters.Add(roster);
        _teams.Teams.Add(team);
        state.Team = team;
        state.Roster = roster;

        if (state.Founder is { } founder && _rows.TryGetValue(founder.StableKey, out TeamIndexDemo? row)
            && row.Side(founder.Side) is { } founderSide)
        {
            founderSide.TeamId = team.Id;
            founderSide.RosterId = roster.Id;
            founderSide.Overlap = founderSide.Key.Count;
            founderSide.Tier = 2;
            founderSide.StandIn = false;
        }
    }

    private void Post(string member, RosterState state)
    {
        if (!_postings.TryGetValue(member, out List<RosterState>? states))
        {
            states = [];
            _postings[member] = states;
        }

        states.Add(state);
    }

    // ── Names ──────────────────────────────────────────────────────────────────────────────────

    // Clan tags seed the name after a case-insensitive fold ("FURIA" x4 and "Furia" x1 are one name,
    // shown in the most recent spelling); with no tag anywhere, "Team of A, B" from the two most
    // frequent anchor members. Re-evaluated on every pass until the user renames.
    private void NameTeam(Team team)
    {
        List<RosterState> states = [.. _rosters.Where(s => ReferenceEquals(s.Team, team))];
        Dictionary<string, int> tags = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> spelling = new(StringComparer.OrdinalIgnoreCase);
        foreach (RosterState state in states)
        {
            foreach ((string tag, int count) in state.ClanCounts)
            {
                tags[tag] = tags.GetValueOrDefault(tag) + count;
                spelling[tag] = state.ClanSpelling[tag];
            }
        }

        if (tags.Count > 0)
        {
            string tag = tags.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase).First().Key;
            team.Name = spelling[tag];
            team.NameSource = TeamNameSource.ClanTag;
            return;
        }

        RosterState? latest = states.Where(s => s.Members.Count > 0)
            .OrderByDescending(s => s.Since).ThenByDescending(s => s.FoundedOrder).FirstOrDefault();
        if (latest is null)
        {
            if (string.IsNullOrEmpty(team.Name))
            {
                team.Name = "Team";
                team.NameSource = TeamNameSource.Auto;
            }

            return;
        }

        HashSet<string> anchor = Anchor(latest);
        List<string> names =
        [
            .. latest.Members.Where(m => anchor.Contains(m.Key))
                .OrderByDescending(m => m.Value.Count)
                .ThenByDescending(m => m.Value.LastTicks)
                .ThenBy(m => m.Key, StringComparer.Ordinal)
                .Take(2)
                .Select(m => m.Value.LastName)
        ];
        team.Name = names.Count == 0 ? "Team" : $"Team of {string.Join(", ", names)}";
        team.NameSource = TeamNameSource.Auto;
    }

    private static string NextRosterId(Team team)
    {
        int n = team.Rosters.Count + 1;
        while (team.Rosters.Any(r => string.Equals(r.Id, $"r{n}", StringComparison.Ordinal)))
        {
            n++;
        }

        return $"r{n}";
    }

    /// <summary>The calendar date of an order stamp (UTC ticks), the unit rosters are dated in.</summary>
    /// <param name="ticks">The order stamp.</param>
    public static DateOnly DateOf(long ticks) =>
        ticks <= 0 ? DateOnly.MinValue : DateOnly.FromDateTime(new DateTime(Math.Min(ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc));

    private sealed record Match(RosterState Roster, int Overlap, int Tier);

    private sealed record Pending(int Side, SideInput Input, TeamIndexSide Row, Match? Match, Guid? PinnedTeam, bool IsOverride);

    private sealed class MemberState
    {
        public int Count;
        public long FirstTicks;
        public string LastName = "";
        public long LastTicks;
        public int StandIns;
    }

    private sealed class RosterState
    {
        public readonly Dictionary<string, int> ClanCounts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ClanSpelling = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<HashSet<string>> Keys = [];
        public readonly Dictionary<string, MemberState> Members = new(StringComparer.Ordinal);
        public readonly List<(DemoSideInput Demo, int Side)> Sides = [];
        public HashSet<string>? AnchorCache;
        public int FoundedOrder;
        public (string StableKey, int Side)? Founder;
        public HashSet<string>? Lineup;
        public RosterState? Predecessor;
        public Roster? Roster;
        public HashSet<string>? SeedAnchor;
        public DateOnly Since;
        public Team? Team;
    }
}

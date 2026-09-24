#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>Where a team's display name came from. A user name is never overwritten.</summary>
public enum TeamNameSource
{
    User,
    ClanTag,
    Auto
}

/// <summary>Which rule resolved "our side" on a demo (design §3.5): the first that fires wins.</summary>
public enum OurSideSource
{
    None,
    Override,
    Team,
    Me
}

/// <summary>
///     One roster of a team: a persisted id, a <c>since</c> date and the anchor its sides are matched
///     against. Valve's rules make the roster the unit of identity (five specific people) and the team the
///     continuity a user asserts across rosters, which is why the anchor lives here and not on the team.
/// </summary>
public sealed class Roster
{
    public string Id { get; set; } = "";

    /// <summary>The date of the roster's first side, or the date the user chose for a started roster.</summary>
    public DateOnly Since { get; set; }

    /// <summary>
    ///     The fixed five, written once when the same five is seen on two sides of this roster and never
    ///     rewritten: a rebuild must re-match against the same five. Null until established.
    /// </summary>
    public List<string>? CoreLineup { get; set; }

    /// <summary>
    ///     The up-to-seven most frequent members, a snapshot rewritten whenever the derived top seven
    ///     changes, so the user file stands alone when the cache is wiped. The anchor only while
    ///     <see cref="CoreLineup" /> is null.
    /// </summary>
    public List<string>? ExtendedCore { get; set; }

    /// <summary>Optional user label ("post-KSCERATO roster").</summary>
    public string? Label { get; set; }

    /// <summary>
    ///     True for a roster the user started at a date: sides of the team dated on or after
    ///     <see cref="Since" /> are matched to it rather than to the team's earlier rosters, and it
    ///     establishes its own five from them.
    /// </summary>
    public bool UserStarted { get; set; }

    [JsonIgnore]
    public bool HasCoreLineup => CoreLineup is { Count: 5 };
}

/// <summary>A team: user-owned continuity over one or more rosters.</summary>
public sealed class Team
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public TeamNameSource NameSource { get; set; } = TeamNameSource.Auto;

    /// <summary>At most one team carries this.</summary>
    public bool IsUs { get; set; }

    /// <summary>The user chose not to see this team in lists.</summary>
    public bool Hidden { get; set; }

    public List<Roster> Rosters { get; set; } = [];

    /// <summary>Ids of teams folded into this one; each is tombstoned in the file.</summary>
    public List<Guid> MergedFrom { get; set; } = [];

    public string Notes { get; set; } = "";

    /// <summary>
    ///     An auto team is one clustering made and nobody touched: a rebuild that hands it no sides deletes
    ///     it. A user name, the us mark or a merge makes it the user's.
    /// </summary>
    [JsonIgnore]
    public bool IsAuto => NameSource != TeamNameSource.User && !IsUs && MergedFrom.Count == 0;
}

/// <summary>The owner's own accounts: what makes "our side" resolvable when no roster matches.</summary>
public sealed class MeAccounts
{
    public List<string> SteamIds { get; set; } = [];
}

/// <summary>
///     A manual per-demo side assignment. User truth, so it keys by content hash; until Content Identity
///     has hashed the demo it keys by the cache's stable path key and is upgraded when the hash appears.
/// </summary>
public sealed class TeamOverride
{
    public string? DemoSha256 { get; set; }

    public string? DemoStableKey { get; set; }

    /// <summary>2 = T, 3 = CT, the end-of-demo side.</summary>
    public int Side { get; set; }

    /// <summary>The team the side belongs to, or null for "this side is not a team".</summary>
    public Guid? TeamId { get; set; }
}

/// <summary>
///     <c>&lt;config&gt;/teams.json</c>: what the user authored, beside <c>settings.json</c>. Small, never
///     rebuilt, and refused rather than overwritten when it cannot be read.
/// </summary>
public sealed class TeamsFile
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public MeAccounts Me { get; set; } = new();

    public List<Team> Teams { get; set; } = [];

    /// <summary>Ids of teams merged away. A rebuild never recreates or reuses them.</summary>
    public List<Guid> Tombstones { get; set; } = [];

    public List<TeamOverride> Overrides { get; set; } = [];

    /// <summary>Both team files share one serializer shape: camel case, enums by name, indented.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Team? Find(Guid id) => Teams.FirstOrDefault(t => t.Id == id);
}

/// <summary>One side of one demo in the derived index: the key it carried and what it matched.</summary>
public sealed class TeamIndexSide
{
    /// <summary>The side key: non-bot, non-coach SteamID64s, sorted.</summary>
    public List<string> Key { get; set; } = [];

    /// <summary>The raw last-seen names, parallel to <see cref="Key" />. Sanitize at the render boundary.</summary>
    public List<string> Names { get; set; } = [];

    /// <summary>The side's clan tag at the last frame, when the demo carried one.</summary>
    public string? Clan { get; set; }

    public Guid? TeamId { get; set; }

    public string? RosterId { get; set; }

    public int Overlap { get; set; }

    /// <summary>1 = matched a fixed five, 2 = matched an extended core, 0 = unaffiliated or overridden.</summary>
    public int Tier { get; set; }

    /// <summary>Overlap 3 or 4 with a member outside the anchor. Stored at both tiers, surfaced at tier 1.</summary>
    public bool StandIn { get; set; }

    /// <summary>True when a user override placed this side; the tier is then 0 and no anchor was consulted.</summary>
    public bool Override { get; set; }

    [JsonIgnore]
    public bool IsClusterable => Key.Count > 0;
}

/// <summary>One demo's row in the derived index.</summary>
public sealed class TeamIndexDemo
{
    public string Path { get; set; } = "";

    public string? Sha256 { get; set; }

    /// <summary><c>DemoCacheRecord.ModifiedTicks</c> until a real match date exists.</summary>
    public long OrderTicks { get; set; }

    /// <summary>Keyed "2" and "3": the end-of-demo sides.</summary>
    public Dictionary<string, TeamIndexSide> Sides { get; set; } = [];

    public int? OurSide { get; set; }

    public OurSideSource OurSideSource { get; set; }

    public Guid? OpponentTeamId { get; set; }

    public TeamIndexSide? Side(int side) => Sides.GetValueOrDefault(side.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Per team, per roster: who appeared how often.</summary>
public sealed class TeamIndexMember
{
    public int Count { get; set; }

    /// <summary>RAW last-seen name. Sanitize at the render boundary.</summary>
    public string LastName { get; set; } = "";

    public long FirstTicks { get; set; }

    public long LastTicks { get; set; }

    /// <summary>Sides on which this member sat outside the anchor at match time.</summary>
    public int StandInCount { get; set; }
}

/// <summary>A one-side candidate: a side that matched nothing and may found a roster later.</summary>
public sealed class TeamIndexUnaffiliated
{
    public string Demo { get; set; } = "";

    public int Side { get; set; }
}

/// <summary>
///     <c>&lt;config&gt;/cache/team-index.json</c>: the derived assignment, rebuildable from the sidecars
///     and from <c>teams.json</c>. Carries no <c>clock</c> block: nothing here is a tick, so there is no
///     tick anchor to declare (F15); a consumer that joins a team to ticks carries its own.
/// </summary>
public sealed class TeamIndexFile
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public long BuiltAtTicks { get; set; }

    /// <summary>Keyed by <c>DemoCacheStore.StableKey(path)</c>, like the sidecars.</summary>
    public Dictionary<string, TeamIndexDemo> Demos { get; set; } = [];

    /// <summary>team id → roster id → SteamID64 → member.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, TeamIndexMember>>> Members { get; set; } = [];

    public List<TeamIndexUnaffiliated> Unaffiliated { get; set; } = [];
}

/// <summary>The assignment of one side, as the query API hands it out.</summary>
/// <param name="Key">The side key.</param>
/// <param name="TeamId">The team, or null when unaffiliated.</param>
/// <param name="RosterId">The roster within the team, or null.</param>
/// <param name="Overlap">Members shared with the anchor.</param>
/// <param name="Tier">1 = fixed five, 2 = extended core, 0 = unaffiliated or overridden.</param>
/// <param name="StandIn">Overlap 3 or 4 with a member outside the anchor.</param>
public sealed record SideAssignment(
    IReadOnlyList<string> Key, Guid? TeamId, string? RosterId, int Overlap, int Tier, bool StandIn)
{
    public bool IsAssigned => TeamId is not null;
}

/// <summary>
///     One demo's assignment. The sides are END-OF-DEMO sides; a consumer that needs the side a team
///     played in round N asks <see cref="TeamIdentityService.SideAtRound" />.
/// </summary>
public sealed record TeamAssignment(
    string DemoPath, string? Sha256,
    SideAssignment T, SideAssignment Ct,
    int? OurSide, OurSideSource Source, Guid? OpponentTeamId)
{
    /// <summary>The assignment of an end-of-demo side.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    public SideAssignment Side(int side) => side == 3 ? Ct : T;

    /// <summary>The side that is not ours, or null when ours is unresolved.</summary>
    public int? TheirSide => OurSide switch
    {
        2 => 3,
        3 => 2,
        _ => null
    };
}

/// <summary>One side of one demo, as Split and the overrides name it.</summary>
/// <param name="DemoPath">The demo.</param>
/// <param name="Side">2 = T, 3 = CT, the end-of-demo side.</param>
public sealed record DemoSideRef(string DemoPath, int Side);

/// <summary>The account the share heuristic proposes as "me" (design §3.5), never written without confirmation.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="LastName">Its raw last-seen name.</param>
/// <param name="DemoCount">Clusterable demos it appears in.</param>
/// <param name="Share">Its share of the clusterable demos, above one half by construction.</param>
public sealed record MeSuggestion(string SteamId64, string LastName, int DemoCount, double Share);

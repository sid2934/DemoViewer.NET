#region

using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     How far a demo has been processed. The tiers are CUMULATIVE and each is stamped independently, which
///     is the whole point of the split: a rules-config change invalidates T3 for the whole library without
///     touching 719 demos' rosters, and a schema bump to one tier does not discard the others.
/// </summary>
public enum DemoCacheTier
{
    /// <summary>Nothing but the file itself: path, size, mtime.</summary>
    Identity = 0,

    /// <summary>The cheap header read: map, server, demo version.</summary>
    Header = 1,

    /// <summary>
    ///     A full parse: duration, tick rate, roster with teams, rounds, final score. NO analysis-engine run.
    ///     This is the pass that already covers ~80% of a real library.
    /// </summary>
    Parse = 2,

    /// <summary>
    ///     A rules-engine run: per-player scoreboard, per-side split, highlights. Expensive, and the reason
    ///     <c>Compute full stats</c> is per-demo rather than ambient.
    /// </summary>
    Analysis = 3
}

/// <summary>The outcome of the last tier-3 (analysis) attempt for a demo.</summary>
public enum DemoAnalysisState
{
    /// <summary>Never run, or invalidated by a config-fingerprint change.</summary>
    Pending,

    /// <summary>Ran successfully under <see cref="DemoCacheRecord.ConfigFingerprint" />.</summary>
    Indexed,

    /// <summary>Ran and threw. Retryable from the Match Overview completeness chip.</summary>
    Failed
}

/// <summary>
///     Per-tier version + timestamp. Carried separately for every tier so a schema bump invalidates only its
///     own tier: the single whole-record <c>CurrentSchema</c> the library cache uses today is exactly why
///     that cache has been conservative about growing new fields.
/// </summary>
public sealed class TierStamp
{
    /// <summary>Schema version of the tier's payload. 0 = the tier has never been written.</summary>
    public int Schema { get; set; }

    /// <summary>When the tier was last written (UTC ticks). 0 = never.</summary>
    public long ComputedAtTicks { get; set; }

    [JsonIgnore]
    public bool IsPresent => Schema > 0;
}

/// <summary>A player as the parse saw them. Widens the library cache's names-only list.</summary>
public sealed class CachedPlayerInfo
{
    public int Slot { get; set; }

    /// <summary>RAW name: the CSVG <c>spec_player</c> currency. Sanitize at the render boundary, never here.</summary>
    public string Name { get; set; } = "";

    public string SteamId64 { get; set; } = "";

    /// <summary>2 = T, 3 = CT (parser convention); anything else is a spectator/observer.</summary>
    public int Team { get; set; }

    public bool IsBot { get; set; }

    /// <summary>
    ///     <c>CCSPlayerController.m_iCoachingTeam != 0</c> at the last frame: a registered coach, who sits on
    ///     a side without being one of its five. Additive (no schema bump, per this cache's convention): a
    ///     record written before the field reads false, which is what every matchmaking replay measures
    ///     anyway. Team Identity keeps coaches out of side keys.
    /// </summary>
    public bool IsCoach { get; set; }
}

/// <summary>One player of one side on an index row: a human who is not a coach and carries a SteamID.</summary>
public sealed class IndexSidePlayer
{
    public string SteamId64 { get; set; } = "";

    /// <summary>RAW name, as <see cref="CachedPlayerInfo.Name" />.</summary>
    public string Name { get; set; } = "";

    /// <summary>Every slot the account held: one, or more after a reconnect.</summary>
    public List<int> Slots { get; set; } = [];
}

/// <summary>A round boundary. Needed by clip lead-in flooring and by the round count.</summary>
public sealed class CachedRound
{
    public int Number { get; set; }

    public int StartTickFrameClock { get; set; }
}

/// <summary>
///     Adapters between the cache's mutable JSON rows and the packaged clip pipeline's neutral
///     inputs (<c>CS2DemoKit.Analysis.Clips</c>). Both sides are FRAME CLOCK: the conversion is a
///     shape change only, never a clock change.
/// </summary>
public static class CachedRoundExtensions
{
    /// <summary>Projects cached rounds onto the clip pipeline's round input.</summary>
    /// <param name="rounds">The record's rounds.</param>
    public static IReadOnlyList<ClipRound> ToClipRounds(this IReadOnlyList<CachedRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        return [.. rounds.Select(r => new ClipRound(r.Number, r.StartTickFrameClock))];
    }

    /// <summary>Materializes derived rounds (frame clock) as cache rows.</summary>
    /// <param name="rounds">Rounds from <c>ClipRounds.Derive</c>.</param>
    public static List<CachedRound> ToCachedRounds(this IReadOnlyList<ClipRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        return
        [
            .. rounds.Select(r => new CachedRound
            {
                Number = r.Number,
                StartTickFrameClock = r.StartTickFrameClock
            })
        ];
    }
}

/// <summary>One scoreboard row, from the analysis engine's own per-player table.</summary>
public sealed class CachedStatRow
{
    public int Slot { get; set; }

    /// <summary>2 = T, 3 = CT: the side the player FINISHED on.</summary>
    public int Team { get; set; }

    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public double Adr { get; set; }
    public double Rating { get; set; }

    /// <summary>Rounds won while on the CT side. Unreliable on HLTV demos: see the side-split reconcile.</summary>
    public int CtWins { get; set; }

    /// <summary>Rounds won while on the T side.</summary>
    public int TWins { get; set; }
}

/// <summary>
///     One fired highlight. Player identity is by <see cref="PlayerSlot" /> into
///     <see cref="DemoCacheRecord.Players" /> rather than re-stored per event: the highlights cache repeats
///     name + steamId on every row today, which is pure redundancy and makes renames incoherent.
/// </summary>
public sealed class CachedHighlightEvent
{
    public string RulesetId { get; set; } = "";

    public string HighlightId { get; set; } = "";

    public int FrameIndex { get; set; }

    /// <summary>Frame clock: NOT server-tick space. Never subtract ServerStartTick from this.</summary>
    public int Tick { get; set; }

    /// <summary>
    ///     Frame-clock tick of the FIRST contributing event of the round for a count-based highlight
    ///     (e.g. the first kill of a 4K), or <c>null</c>. The reel's clip window reaches its start back
    ///     to this so a multi-event sequence longer than the lead-in is not cut off (still floored by
    ///     round start). Old sidecars written before this field deserialize to <c>null</c> = the
    ///     pre-existing lead-in-only behavior.
    /// </summary>
    public int? ClipStartTick { get; set; }

    public int PlayerSlot { get; set; }

    public int RoundNumber { get; set; }

    /// <summary>
    ///     The title as rendered at emission. Embeds the player's name as it was then, deliberately. It is a
    ///     rendering artifact captured at a point in time, not a live projection.
    /// </summary>
    public string RenderedTitle { get; set; } = "";

    /// <summary>
    ///     Authored ranking weight (0–100) folding rarity × coolness: the reel orders firings by this
    ///     (higher first). Old sidecars written before this field deserialize to 0; harmless until re-scan.
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    ///     The editorial track (skill <see cref="HighlightKind.Highlight" />, <see cref="HighlightKind.Funny" />,
    ///     or <see cref="HighlightKind.Lowlight" />) this firing belongs to. It routes it into the right reel.
    ///     Old sidecars deserialize to the default (<c>Highlight</c>).
    /// </summary>
    public HighlightKind Kind { get; set; }

    [JsonIgnore]
    public string TypeKey => $"{RulesetId}.{HighlightId}";
}

/// <summary>
///     The full per-demo record: ONE demo's worth of every tier. Persisted as its own sidecar file and
///     loaded lazily, because the surfaces that want the fat payload (Match Overview, the reel tray) want it
///     for one demo at a time. The always-loaded projection is <see cref="DemoCacheIndexEntry" />.
/// </summary>
public sealed class DemoCacheRecord : IJsonOnDeserialized
{
    /// <summary>Bump when a tier's payload shape changes. Each is independent: see <see cref="TierStamp" />.</summary>
    public const int HeaderSchema = 1;

    public const int ParseSchema = 1;
    public const int AnalysisSchema = 1;

    // ── T0 identity ──────────────────────────────────────────────────────────
    public string Path { get; set; } = "";

    public long Size { get; set; }

    public long ModifiedTicks { get; set; }

    /// <summary>Content hash: the dedup key. Null until something has computed it.</summary>
    public string? Sha256 { get; set; }

    /// <summary>
    ///     The cheap fingerprint taken in the same read as <see cref="Sha256" />. Null when that read could
    ///     not vouch for it (the file was not settled), or the hash came from an older build.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DemoContentFingerprint? ContentFingerprint { get; set; }

    // ── T1 header ────────────────────────────────────────────────────────────
    public TierStamp Header { get; set; } = new();

    public string? Map { get; set; }
    public string? Server { get; set; }
    public string? DemoVersion { get; set; }

    /// <summary>
    ///     The engine classifier's verdict on the file header (<c>DemoSourceKind</c> by name), written at
    ///     tier 2 from the parse's profile. Additive: a record written before the field reads null, and
    ///     Demo Provenance Labels then classifies from <see cref="Server" /> alone.
    /// </summary>
    public string? SourceKind { get; set; }

    // ── T2 parse ─────────────────────────────────────────────────────────────
    public TierStamp Parse { get; set; } = new();

    public double DurationSeconds { get; set; }
    public int TickRate { get; set; }
    public int TickCount { get; set; }
    public int ServerStartTick { get; set; }

    public List<CachedPlayerInfo> Players { get; set; } = [];
    public List<CachedRound> Rounds { get; set; } = [];

    /// <summary>
    ///     Round count as a scalar. Normally just <c>Rounds.Count</c>, but carried separately because a
    ///     migrated legacy row has a round count with no round BOUNDARIES behind it: the old library cache
    ///     stored the number and nothing else. Without this the count would be silently lost for every
    ///     already-indexed demo.
    /// </summary>
    public int RoundCount { get; set; }

    public int? CtScore { get; set; }
    public int? TScore { get; set; }
    public string? CtClan { get; set; }
    public string? TClan { get; set; }

    // ── T3 analysis ──────────────────────────────────────────────────────────
    public TierStamp Analysis { get; set; } = new();

    public DemoAnalysisState AnalysisState { get; set; } = DemoAnalysisState.Pending;

    public List<CachedStatRow> Scoreboard { get; set; } = [];
    public List<CachedHighlightEvent> Highlights { get; set; } = [];

    /// <summary>Rounds won by the CT SIDE across the match, or null when the run could not resolve it.</summary>
    public int? CtSideWins { get; set; }

    /// <summary>Rounds won by the T SIDE across the match, or null when the run could not resolve it.</summary>
    public int? TSideWins { get; set; }

    /// <summary>Rounds the analysis resolved, or 0 when unknown.</summary>
    public int AnalysisRoundCount { get; set; }

    public string? ProfileName { get; set; }

    /// <summary>Rules-config fingerprint this tier-3 payload was produced under; a mismatch marks it stale.</summary>
    public string? ConfigFingerprint { get; set; }

    /// <summary>Per-highlight-definition hashes, for finer-grained staleness than the combined fingerprint.</summary>
    public Dictionary<string, string> HighlightHashes { get; set; } = new();

    // ── Round facts ──────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>round_facts</c> ruleset's output: one row per round, two sides each, or null when none was
    ///     written. Stamped in <see cref="PackStamps" /> under facet <c>roundfacts</c> with the ruleset's own
    ///     identity, so a threshold edit re-runs Round Facts alone and never the highlight scan. Rows an
    ///     older build kept in the Strat Book's payload are lifted here on read.
    /// </summary>
    public RoundFactsRows? RoundFacts { get; set; }

    // ── Packs ────────────────────────────────────────────────────────────────
    // A pack's data rides the record as one opaque JSON object under its id, read and written typed by
    // the pack through IPackPayloads and never deserialised by core. What core needs for the backlog and
    // the fill state (schema, fingerprint, outcome) is promoted to PackStamps and mirrored on the index
    // row, so a staleness pass opens no sidecar and no payload. Payloads that are too large to ride a
    // record Match Overview re-reads on every property touch (the round index, the grenade rows) stay in
    // their own sidecars; only their stamps are here.

    /// <summary>Per-pack payloads keyed by pack id. Opaque here; typed through <see cref="IPackPayloads" />.</summary>
    public Dictionary<string, JsonElement> Packs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>One stamp per pack facet written for this demo. See <see cref="PackStamp" />.</summary>
    public List<PackStamp> PackStamps { get; set; } = [];

    /// <summary>
    ///     Members no property claims, held only between the read and <c>OnDeserialized</c>: a sidecar written
    ///     before <see cref="Packs" /> existed carries the pack's fields flat, and the fold reads them from
    ///     here. Always null afterwards, so unknown members are still dropped on the next write. Serializer
    ///     plumbing, not an API: nothing should read or set it.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownMembers { get; set; }

    /// <summary>
    ///     Sets <see cref="Sha256" /> and the fingerprint taken in the same read. A null fingerprint keeps the
    ///     stored one only while the hash is unchanged: a fingerprint never outlives the hash it was taken with.
    /// </summary>
    /// <param name="sha256">The content hash just computed or confirmed.</param>
    /// <param name="fingerprint">The fingerprint from that read, or null when it could not vouch for one.</param>
    public void SetContentHash(string sha256, DemoContentFingerprint? fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        if (fingerprint is not null || !string.Equals(Sha256, sha256, StringComparison.Ordinal))
        {
            ContentFingerprint = fingerprint;
        }

        Sha256 = sha256;
    }

    /// <summary>The stamp with <paramref name="id" />, or null when the facet was never written.</summary>
    /// <param name="id">The facet id (<see cref="PackStamp.Id" />).</param>
    public PackStamp? Stamp(string id) => PackStamps.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Replaces the stamp with <paramref name="stamp" />'s id, or adds it.</summary>
    /// <param name="stamp">The new stamp.</param>
    public void SetStamp(PackStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        int at = PackStamps.FindIndex(s => string.Equals(s.Id, stamp.Id, StringComparison.Ordinal));
        if (at < 0)
        {
            PackStamps.Add(stamp);
        }
        else
        {
            PackStamps[at] = stamp;
        }
    }

    /// <summary>Marks a facet's last write as failed, keeping whatever else its stamp carries.</summary>
    /// <param name="id">The facet id.</param>
    public void MarkFailed(string id) =>
        SetStamp((Stamp(id) ?? new PackStamp(id, 0, null)) with { State = DemoAnalysisState.Failed });

    /// <summary>Lifts a failed facet back to pending so the derived backlog takes it again. No-op otherwise.</summary>
    /// <param name="id">The facet id.</param>
    public void ClearFailed(string id)
    {
        if (Stamp(id) is { State: DemoAnalysisState.Failed } failed)
        {
            SetStamp(failed with { State = DemoAnalysisState.Pending });
        }
    }

    /// <summary>Is the facet current at <paramref name="schema" /> under <paramref name="fingerprint" />? See <see cref="PackStamp.IsCurrent" />.</summary>
    /// <param name="id">The facet id.</param>
    /// <param name="schema">The schema in force.</param>
    /// <param name="fingerprint">The fingerprint in force, or null when the facet has none.</param>
    public bool IsPackCurrent(string id, int schema, string? fingerprint) =>
        Stamp(id)?.IsCurrent(schema, fingerprint) ?? false;

    /// <summary>
    ///     Does the facet want its evaluator? Derived like <see cref="NeedsAnalysis" />, with
    ///     <see cref="DemoAnalysisState.Failed" /> excluded for the same reason: retry is an explicit user
    ///     action, never a heavy job on every pass.
    /// </summary>
    /// <param name="id">The facet id.</param>
    /// <param name="schema">The schema in force.</param>
    /// <param name="fingerprint">The fingerprint in force, or null when the facet has none.</param>
    public bool NeedsPack(string id, int schema, string? fingerprint) =>
        Stamp(id) is not { State: DemoAnalysisState.Failed } && !IsPackCurrent(id, schema, fingerprint);

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (UnknownMembers is { Count: > 0 } members)
        {
            LegacyPackFields.Fold(this, members);
        }

        UnknownMembers = null;
        // After the flat fold, which may have just built the payload the rows ride.
        LegacyPackFields.LiftRoundFacts(this);
    }

    /// <summary>The highest tier actually present.</summary>
    [JsonIgnore]
    public DemoCacheTier Tier =>
        Analysis.IsPresent ? DemoCacheTier.Analysis
        : Parse.IsPresent ? DemoCacheTier.Parse
        : Header.IsPresent ? DemoCacheTier.Header
        : DemoCacheTier.Identity;

    /// <summary>Roster members only (team 2/3): excludes observers, coaches and the GOTV proxy.</summary>
    [JsonIgnore]
    public IEnumerable<CachedPlayerInfo> Roster => Players.Where(p => p.Team is 2 or 3);

    /// <summary>
    ///     True when the roster carries a real team split. False for a MIGRATED legacy row, whose players
    ///     came from the old names-only list and have no team: the case the Match Overview roster cards must
    ///     present as "team split needs a re-index" rather than as two empty teams.
    /// </summary>
    [JsonIgnore]
    public bool HasTeamSplit => Players.Any(p => p.Team is 2 or 3);

    /// <summary>Named entries with no team: observers, coaches, admins.</summary>
    [JsonIgnore]
    public IEnumerable<CachedPlayerInfo> Spectators => Players.Where(p => p.Team is not (2 or 3));

    /// <summary>
    ///     Is the tier-3 payload still valid under <paramref name="currentFingerprint" />? A null current
    ///     fingerprint means "unknown", which is treated as current rather than as stale. Invalidating the
    ///     whole library because the rules failed to load once would be worse than showing slightly old stats.
    /// </summary>
    public bool IsAnalysisCurrent(string? currentFingerprint) =>
        Analysis.IsPresent
        && AnalysisState == DemoAnalysisState.Indexed
        && (currentFingerprint is null
            || string.Equals(ConfigFingerprint, currentFingerprint, StringComparison.Ordinal));

    /// <summary>
    ///     Does this demo want a highlights scan? The scan backlog is DERIVED from this, not stored.
    ///     <para>
    ///         <b>Why derived.</b> The old highlights cache carried an explicit <c>Pending</c> marker, which
    ///         doubled as "queued" and as "no tier-3 data". Those are different facts, and merging them into
    ///         one persisted field forces a choice between two wrong behaviours: writing <c>Pending</c> on a
    ///         rules change blanks the highlight section of every demo in the library until each is rescanned,
    ///         and not writing it loses the backlog. Deriving keeps the payload visible while its demo waits.
    ///         A stale harvest is still the best answer available, and the page says so.
    ///     </para>
    ///     <para>
    ///         <b><see cref="DemoAnalysisState.Failed" /> is excluded deliberately.</b> A demo whose scan threw
    ///         is never current, so a pure staleness rule would re-queue it forever, one corrupt file
    ///         re-parsed on every pass, at the cost of a heavy job each time. Retry is an explicit user action.
    ///     </para>
    /// </summary>
    /// <param name="currentFingerprint">The rules fingerprint in force, or null when it cannot be computed.</param>
    public bool NeedsAnalysis(string? currentFingerprint) =>
        AnalysisState != DemoAnalysisState.Failed && !IsAnalysisCurrent(currentFingerprint);

    /// <summary>
    ///     Does this record still describe the file on disk? Size + mtime, exactly as the library cache keys
    ///     freshness today: cheap, and a content hash is not affordable per reconcile pass.
    /// </summary>
    public bool MatchesFile(long size, long modifiedTicks) =>
        Size == size && ModifiedTicks == modifiedTicks;

    /// <summary>The small always-loaded projection of this record.</summary>
    public DemoCacheIndexEntry ToIndexEntry() => new()
    {
        Path = Path,
        Size = Size,
        ModifiedTicks = ModifiedTicks,
        Sha256 = Sha256,
        ContentFingerprint = ContentFingerprint,
        Map = Map,
        Server = Server,
        DemoVersion = DemoVersion,
        SourceKind = SourceKind,
        DurationSeconds = DurationSeconds,
        // The Library card prints player NAMES, so the index has to carry them; the richer per-player record
        // (slot / steamId / team / bot) stays in the sidecar. This is why an index row is ~780 B rather than
        // the ~300 B a pure-identity row would be, which is simply what library.json already costs today.
        //
        // The fallback is load-bearing, not defensive tidiness: a MIGRATED legacy row has names with no team,
        // so filtering to the roster would return nothing and every one of the user's already-indexed demos
        // would render "…", reading as un-indexed when it is merely un-re-indexed.
        PlayerNames = HasTeamSplit
            ? [.. Roster.Select(p => p.Name)]
            : [.. Players.Where(p => p.Name.Length > 0).Select(p => p.Name)],
        RoundCount = Rounds.Count > 0 ? Rounds.Count
            : RoundCount > 0 ? RoundCount
            : AnalysisRoundCount,
        CtScore = CtScore,
        TScore = TScore,
        CtClan = CtClan,
        TClan = TClan,
        HeaderSchema = Header.Schema,
        ParseSchema = Parse.Schema,
        AnalysisSchema = Analysis.Schema,
        AnalysisState = AnalysisState,
        ConfigFingerprint = ConfigFingerprint,
        HighlightCount = Highlights.Count,
        PackStamps = [.. PackStamps],
        CtPlayers = Parse.IsPresent ? SidePlayers(3) : null,
        TPlayers = Parse.IsPresent ? SidePlayers(2) : null,
        CtSideWins = CtSideWins,
        TSideWins = TSideWins
    };

    /// <summary>
    ///     True for a roster entry that counts as one of a side's players: no bot, no coach, a real SteamID.
    ///     The team clustering keys sides on exactly this set.
    /// </summary>
    public static bool IsSidePlayer(CachedPlayerInfo player) =>
        !player.IsBot
        && !player.IsCoach
        && player.Team is 2 or 3
        && !string.IsNullOrEmpty(player.SteamId64)
        && !string.Equals(player.SteamId64, "0", StringComparison.Ordinal);

    /// <summary>
    ///     The side's players by <see cref="IsSidePlayer" />, sorted ordinally by SteamID. One account in two
    ///     slots (a reconnect) is listed once, under the first slot's name, with both slots.
    /// </summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    public List<IndexSidePlayer> SidePlayers(int side)
    {
        List<IndexSidePlayer> players = [];
        foreach (CachedPlayerInfo player in Players.Where(p => p.Team == side && IsSidePlayer(p))
                     .OrderBy(p => p.SteamId64, StringComparer.Ordinal))
        {
            if (players.Count > 0 && string.Equals(players[^1].SteamId64, player.SteamId64, StringComparison.Ordinal))
            {
                players[^1].Slots.Add(player.Slot);
                continue;
            }

            players.Add(new IndexSidePlayer { SteamId64 = player.SteamId64, Name = player.Name, Slots = [player.Slot] });
        }

        return players;
    }
}

/// <summary>
///     One row of <c>index.json</c>: everything the Library grid needs and nothing else. Loaded in full at
///     startup; the fat <see cref="DemoCacheRecord" /> behind it is read only when a surface asks for that
///     specific demo.
/// </summary>
public sealed class DemoCacheIndexEntry : IJsonOnDeserialized
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public string? Sha256 { get; set; }

    /// <summary>See <see cref="DemoCacheRecord.ContentFingerprint" />.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DemoContentFingerprint? ContentFingerprint { get; set; }

    public string? Map { get; set; }
    public string? Server { get; set; }
    public string? DemoVersion { get; set; }

    /// <summary>The classifier's <c>DemoSourceKind</c> by name; see <see cref="DemoCacheRecord.SourceKind" />. Mirrored so a label needs no sidecar.</summary>
    public string? SourceKind { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>Roster names only: what the Library card renders.</summary>
    public List<string> PlayerNames { get; set; } = [];

    public int RoundCount { get; set; }

    public int? CtScore { get; set; }
    public int? TScore { get; set; }
    public string? CtClan { get; set; }
    public string? TClan { get; set; }

    // Tier presence, mirrored from the record so the grid can show fill state without touching a sidecar.
    public int HeaderSchema { get; set; }
    public int ParseSchema { get; set; }
    public int AnalysisSchema { get; set; }

    public DemoAnalysisState AnalysisState { get; set; } = DemoAnalysisState.Pending;

    /// <summary>
    ///     Rules fingerprint the tier-3 payload was produced under. Mirrored onto the index row, ~64 bytes on
    ///     a ~780-byte row, because the scan BACKLOG is derived from it: without it, deciding which demos want
    ///     a scan would mean opening every sidecar on every staleness pass instead of reading a dictionary.
    /// </summary>
    public string? ConfigFingerprint { get; set; }

    /// <summary>
    ///     How many highlights the sidecar holds. Carried here so the cross-demo clip picker can decide which
    ///     sidecars are worth loading instead of reading all 719.
    /// </summary>
    public int HighlightCount { get; set; }

    /// <summary>
    ///     The record's <see cref="DemoCacheRecord.PackStamps" />, mirrored for the same reason
    ///     <see cref="ConfigFingerprint" /> is: a pack's backlog and the strip's counts derive from the
    ///     index without opening a sidecar. A stamp with every optional member costs about 100 bytes.
    /// </summary>
    public List<PackStamp> PackStamps { get; set; } = [];

    /// <summary>
    ///     The players who ended the demo as CT (<see cref="DemoCacheRecord.SidePlayers" />). Null when the
    ///     demo is not parsed, or the row was written by an index older than version 3.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<IndexSidePlayer>? CtPlayers { get; set; }

    /// <summary>The players who ended the demo as T, by the rule of <see cref="CtPlayers" />.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<IndexSidePlayer>? TPlayers { get; set; }

    /// <summary>The record's <see cref="DemoCacheRecord.CtSideWins" />, so a per-team record needs no sidecar read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CtSideWins { get; set; }

    /// <summary>The record's <see cref="DemoCacheRecord.TSideWins" />.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TSideWins { get; set; }

    /// <summary>See <see cref="DemoCacheRecord.UnknownMembers" />: a row written before <see cref="PackStamps" /> carries the flat fields.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownMembers { get; set; }

    /// <summary>Index-level twin of <see cref="DemoCacheRecord.Stamp" />.</summary>
    /// <param name="id">The facet id.</param>
    public PackStamp? Stamp(string id) => PackStamps.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Index-level twin of <see cref="DemoCacheRecord.IsPackCurrent" />: same rule, no sidecar read.</summary>
    /// <param name="id">The facet id.</param>
    /// <param name="schema">The schema in force.</param>
    /// <param name="fingerprint">The fingerprint in force, or null when the facet has none.</param>
    public bool IsPackCurrent(string id, int schema, string? fingerprint) =>
        Stamp(id)?.IsCurrent(schema, fingerprint) ?? false;

    /// <summary>Index-level twin of <see cref="DemoCacheRecord.NeedsPack" />: same rule, no sidecar read.</summary>
    /// <param name="id">The facet id.</param>
    /// <param name="schema">The schema in force.</param>
    /// <param name="fingerprint">The fingerprint in force, or null when the facet has none.</param>
    public bool NeedsPack(string id, int schema, string? fingerprint) =>
        Stamp(id) is not { State: DemoAnalysisState.Failed } && !IsPackCurrent(id, schema, fingerprint);

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (UnknownMembers is { Count: > 0 } members)
        {
            LegacyPackFields.Fold(this, members);
        }

        UnknownMembers = null;
    }

    [JsonIgnore]
    public DemoCacheTier Tier =>
        AnalysisSchema > 0 ? DemoCacheTier.Analysis
        : ParseSchema > 0 ? DemoCacheTier.Parse
        : HeaderSchema > 0 ? DemoCacheTier.Header
        : DemoCacheTier.Identity;

    [JsonIgnore]
    public bool HasScore => CtScore is int c && TScore is int t && c + t > 0;

    /// <summary>
    ///     Index-level twin of <see cref="DemoCacheRecord.NeedsAnalysis" />: same rule, no sidecar read.
    /// </summary>
    /// <param name="currentFingerprint">The rules fingerprint in force, or null when it cannot be computed.</param>
    public bool NeedsAnalysis(string? currentFingerprint) =>
        AnalysisState != DemoAnalysisState.Failed
        && !(AnalysisSchema > 0
             && AnalysisState == DemoAnalysisState.Indexed
             && (currentFingerprint is null
                 || string.Equals(ConfigFingerprint, currentFingerprint, StringComparison.Ordinal)));

    public bool MatchesFile(long size, long modifiedTicks) =>
        Size == size && ModifiedTicks == modifiedTicks;
}

/// <summary>
///     How one store hands a demo to another. Derived stores key by <see cref="StableKey" /> (the cache
///     sidecar rule) and user-truth stores key by <see cref="Sha256" /> (a moved file keeps its tags), so a
///     consumer crossing that line needs both in hand rather than converting keys on its own: the bridges
///     are <see cref="DemoCacheStore.TryGetIndex" /> and <see cref="DemoCacheStore.TryGetByContentId" />
///     (<see cref="DemoCacheStore.TryGetIndexBySha256" /> is the same lookup).
///     Defined here, beside the index row it is projected from, so every store agrees on the shape.
/// </summary>
/// <param name="Path">The demo's path as the library knows it.</param>
/// <param name="StableKey"><see cref="DemoCacheStore.StableKey" /> of <paramref name="Path" />.</param>
/// <param name="Sha256">Lowercase-hex content hash, or null when the demo has not reached tier 2.</param>
public sealed record DemoRef(string Path, string StableKey, string? Sha256)
{
    /// <summary>The reference for an index row.</summary>
    /// <param name="entry">The row to reference.</param>
    public static DemoRef From(DemoCacheIndexEntry entry) =>
        new(entry.Path, DemoCacheStore.StableKey(entry.Path), entry.Sha256);
}

/// <summary>The on-disk shape of <c>index.json</c>: a versioned wrapper so migrations have a hook.</summary>
public sealed class DemoCacheIndexFile
{
    /// <summary>
    ///     Version of the INDEX container itself, independent of the per-tier record schemas. 2: record
    ///     sidecars are gzipped <c>&lt;key&gt;.json.gz</c>; a version-1 <c>&lt;key&gt;.json</c> is still read.
    ///     3: parsed rows carry each side's players (<see cref="DemoCacheIndexEntry.CtPlayers" />) and the
    ///     rounds each side won; a row loaded from an older index has neither until its record is read again.
    ///     4: a row may carry its <see cref="DemoCacheIndexEntry.ContentFingerprint" />; an older row has none
    ///     until its content hash is next computed.
    /// </summary>
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    ///     Which revision of the one-shot legacy migration has run. 0 = never.
    ///     <para>
    ///         An explicit marker rather than "does index.json exist": the library indexer dual-writes into
    ///         this store from its first pass, so the file is created long before any migration. Gating on
    ///         its existence would skip the migration forever and silently drop the legacy data.
    ///     </para>
    /// </summary>
    public int LegacyMigrationVersion { get; set; }

    public List<DemoCacheIndexEntry> Entries { get; set; } = [];
}

#region

using System.Text.Json.Serialization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

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
public sealed class DemoCacheRecord
{
    /// <summary>Bump when a tier's payload shape changes. Each is independent: see <see cref="TierStamp" />.</summary>
    public const int HeaderSchema = 1;

    public const int ParseSchema = 1;
    public const int AnalysisSchema = 1;

    /// <summary>The <see cref="RoundFacts" /> payload shape. Folded into <see cref="RoundFactsFingerprint" />, so a bump re-runs the evaluator alone.</summary>
    public const int RoundFactsSchema = 1;

    /// <summary>The <c>.dvri.json</c> sidecar shape. Folded into <see cref="RoundIndexFingerprint" />, so a bump re-indexes alone.</summary>
    public const int RoundIndexSchema = 1;

    // ── T0 identity ──────────────────────────────────────────────────────────
    public string Path { get; set; } = "";

    public long Size { get; set; }

    public long ModifiedTicks { get; set; }

    /// <summary>Content hash: the dedup key. Null until something has computed it.</summary>
    public string? Sha256 { get; set; }

    // ── T1 header ────────────────────────────────────────────────────────────
    public TierStamp Header { get; set; } = new();

    public string? Map { get; set; }
    public string? Server { get; set; }
    public string? DemoVersion { get; set; }

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

    /// <summary>
    ///     The <c>round_facts</c> ruleset's output: one row per round, two sides each. An analysis output
    ///     that rides the tier-2 parse, so it sits in this tier but is stamped by its own fingerprint and
    ///     schema rather than by <see cref="Analysis" />: a threshold edit re-runs only this evaluator,
    ///     never the highlight scan. Null on every sidecar written before the field existed.
    /// </summary>
    public RoundFactsRows? RoundFacts { get; set; }

    /// <summary>
    ///     The resolved identity of the effective <c>round_facts</c> ruleset (user override included)
    ///     folded with <see cref="RoundFactsSchema" />, as it was when <see cref="RoundFacts" /> was
    ///     written. A mismatch marks the rows stale.
    /// </summary>
    public string? RoundFactsFingerprint { get; set; }

    // ── Round index ──────────────────────────────────────────────────────────
    // The stamp lives here; the rows do not. The sidecar under cache/round-index/ carries 55 to 90 KB
    // of runs per demo, and this record is re-read on every Match Overview property touch, so putting
    // them here would make every Library arrow-key a ten-times heavier read for a surface that never
    // looks at them.

    /// <summary>When the demo's <c>.dvri.json</c> was last written, at which schema. Never written = no index.</summary>
    public TierStamp RoundIndex { get; set; } = new();

    public RoundIndexState RoundIndexState { get; set; } = RoundIndexState.Pending;

    /// <summary>The <see cref="RoundIndexFingerprint" /> the sidecar was built under; a mismatch marks it stale.</summary>
    public string? RoundIndexFingerprint { get; set; }

    /// <summary>Sampled rows in the sidecar, for the status strip; 0 when absent.</summary>
    public int RoundIndexRowCount { get; set; }

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
    ///     Are the round facts still valid under <paramref name="currentFingerprint" />? Rows written at
    ///     another schema or another ruleset identity are stale; a null current fingerprint means there is
    ///     no <c>round_facts</c> ruleset to run, so nothing can be current.
    /// </summary>
    /// <param name="currentFingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public bool IsRoundFactsCurrent(string? currentFingerprint) =>
        RoundFacts is { Schema: RoundFactsSchema }
        && currentFingerprint is not null
        && string.Equals(RoundFactsFingerprint, currentFingerprint, StringComparison.Ordinal);

    /// <summary>
    ///     Does this demo want the round facts evaluator? Derived, like <see cref="NeedsAnalysis" />, and
    ///     false whenever there is no ruleset to run: the rows are an output of that ruleset, so its
    ///     absence is "nothing to do", never "everything is stale".
    /// </summary>
    /// <param name="currentFingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public bool NeedsRoundFacts(string? currentFingerprint) =>
        currentFingerprint is not null && !IsRoundFactsCurrent(currentFingerprint);

    /// <summary>Is the round index sidecar still valid under <paramref name="currentFingerprint" />?</summary>
    /// <param name="currentFingerprint">The fingerprint in force for this demo's map.</param>
    public bool IsRoundIndexCurrent(string currentFingerprint) =>
        RoundIndex.IsPresent
        && RoundIndexState == RoundIndexState.Indexed
        && string.Equals(RoundIndexFingerprint, currentFingerprint, StringComparison.Ordinal);

    /// <summary>
    ///     Does this demo want the round index evaluator? Derived like <see cref="NeedsAnalysis" />, with
    ///     <see cref="RoundIndexState.Failed" /> excluded for the same reason: retry is an explicit user
    ///     action, never a heavy job on every pass.
    /// </summary>
    /// <param name="currentFingerprint">The fingerprint in force for this demo's map.</param>
    public bool NeedsRoundIndex(string currentFingerprint) =>
        RoundIndexState != RoundIndexState.Failed && !IsRoundIndexCurrent(currentFingerprint);

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
        Map = Map,
        Server = Server,
        DemoVersion = DemoVersion,
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
        RoundFactsSchema = RoundFacts?.Schema ?? 0,
        RoundFactsFingerprint = RoundFactsFingerprint,
        RoundIndexSchema = RoundIndex.Schema,
        RoundIndexComputedAtTicks = RoundIndex.ComputedAtTicks,
        RoundIndexState = RoundIndexState,
        RoundIndexFingerprint = RoundIndexFingerprint,
        RoundIndexRowCount = RoundIndexRowCount
    };
}

/// <summary>
///     One row of <c>index.json</c>: everything the Library grid needs and nothing else. Loaded in full at
///     startup; the fat <see cref="DemoCacheRecord" /> behind it is read only when a surface asks for that
///     specific demo.
/// </summary>
public sealed class DemoCacheIndexEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public string? Sha256 { get; set; }

    public string? Map { get; set; }
    public string? Server { get; set; }
    public string? DemoVersion { get; set; }

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
    ///     Schema of the sidecar's round facts, 0 when it holds none. Mirrored with its fingerprint for
    ///     the same reason <see cref="ConfigFingerprint" /> is: the evaluator's backlog derives from the
    ///     index without opening a sidecar.
    /// </summary>
    public int RoundFactsSchema { get; set; }

    /// <summary>The fingerprint the sidecar's round facts were written under; see <see cref="DemoCacheRecord.RoundFactsFingerprint" />.</summary>
    public string? RoundFactsFingerprint { get; set; }

    // The round index stamp, mirrored (about 50 bytes on a ~780-byte row) so the index backlog and the
    // status strip's counts derive from the index without opening a sidecar, like NeedsAnalysis.

    /// <summary>Schema of the demo's <c>.dvri.json</c>, 0 when it has never been written.</summary>
    public int RoundIndexSchema { get; set; }

    /// <summary>When the sidecar was written (UTC ticks): the Watched Situations watermark reads this.</summary>
    public long RoundIndexComputedAtTicks { get; set; }

    public RoundIndexState RoundIndexState { get; set; } = RoundIndexState.Pending;

    public string? RoundIndexFingerprint { get; set; }

    public int RoundIndexRowCount { get; set; }

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

    /// <summary>
    ///     Index-level twin of <see cref="DemoCacheRecord.NeedsRoundFacts" />: same rule, no sidecar read.
    /// </summary>
    /// <param name="currentFingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public bool NeedsRoundFacts(string? currentFingerprint) =>
        currentFingerprint is not null
        && !(RoundFactsSchema == DemoCacheRecord.RoundFactsSchema
             && string.Equals(RoundFactsFingerprint, currentFingerprint, StringComparison.Ordinal));

    /// <summary>Index-level twin of <see cref="DemoCacheRecord.IsRoundIndexCurrent" />: same rule, no sidecar read.</summary>
    /// <param name="currentFingerprint">The fingerprint in force for this demo's map.</param>
    public bool IsRoundIndexCurrent(string currentFingerprint) =>
        RoundIndexSchema > 0
        && RoundIndexState == RoundIndexState.Indexed
        && string.Equals(RoundIndexFingerprint, currentFingerprint, StringComparison.Ordinal);

    /// <summary>Index-level twin of <see cref="DemoCacheRecord.NeedsRoundIndex" />: same rule, no sidecar read.</summary>
    /// <param name="currentFingerprint">The fingerprint in force for this demo's map.</param>
    public bool NeedsRoundIndex(string currentFingerprint) =>
        RoundIndexState != RoundIndexState.Failed && !IsRoundIndexCurrent(currentFingerprint);

    public bool MatchesFile(long size, long modifiedTicks) =>
        Size == size && ModifiedTicks == modifiedTicks;
}

/// <summary>
///     How one store hands a demo to another. Derived stores key by <see cref="StableKey" /> (the cache
///     sidecar rule) and user-truth stores key by <see cref="Sha256" /> (a moved file keeps its tags), so a
///     consumer crossing that line needs both in hand rather than converting keys on its own: the bridges
///     are <see cref="DemoCacheStore.TryGetIndex" /> and <see cref="DemoCacheStore.TryGetIndexBySha256" />.
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
    /// <summary>Version of the INDEX container itself, independent of the per-tier record schemas.</summary>
    public const int CurrentVersion = 1;

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

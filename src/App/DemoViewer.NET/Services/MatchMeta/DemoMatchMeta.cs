#region

using System.Text.Json.Serialization;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.MatchMeta;

/// <summary>
///     Where a demo's match time came from, in precedence order: the first one present wins.
///     <see cref="UserOverride" />, <see cref="ValveMatchInfo" /> and <see cref="FileWriteTime" /> are written today;
///     <see cref="ExternalMetadata" /> is reserved for an online source.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchTimeSource>))]
public enum MatchTimeSource
{
    UserOverride,
    ValveMatchInfo,
    ExternalMetadata,
    FileWriteTime,
    Unknown
}

/// <summary>Where a rank row's post-match values came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RankSource>))]
public enum RankSource
{
    /// <summary>The server's end-of-match rank update (net message 352).</summary>
    ServerRankUpdate,

    /// <summary>The controller's prediction for the result the player got. Premier only.</summary>
    Predicted,

    /// <summary>The controller alone: the pre-match rank, nothing after it.</summary>
    Controller
}

/// <summary>
///     One player's rank in one demo. A null rank is unranked or unknown, never 0: an account still in
///     placement has rank 0 in both the controller and the update.
/// </summary>
public sealed class MatchRankRow
{
    public string SteamId64 { get; set; } = "";

    /// <summary>The rank system: 11 premier, 12 competitive.</summary>
    public int RankType { get; set; }

    /// <summary>The rank going into the match, or null when unranked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RankOld { get; set; }

    /// <summary>The rank coming out of it, or null when unranked or unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RankNew { get; set; }

    /// <summary><see cref="RankNew" /> minus <see cref="RankOld" />, or null when either is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RankChange { get; set; }

    /// <summary>Wins after the match, or null when the result is unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Wins { get; set; }

    public RankSource RankSource { get; set; }

    /// <summary>No rank on either side of the match.</summary>
    [JsonIgnore]
    public bool IsUnranked => RankOld is null && RankNew is null;
}

/// <summary>
///     When and which match a demo is: the <c>matchmeta</c> record pass's output, on
///     <see cref="DemoCache.DemoCacheRecord.MatchMeta" />. See <c>docs/match-meta-format.md</c>.
/// </summary>
public sealed class DemoMatchMeta
{
    /// <summary>The shape this build writes; the <c>matchmeta</c> stamp's schema.</summary>
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>When the match was played (UTC ticks), or null when <see cref="MatchTimeSource" /> is <see cref="MatchMeta.MatchTimeSource.Unknown" />.</summary>
    public long? MatchTimeUtcTicks { get; set; }

    public MatchTimeSource MatchTimeSource { get; set; } = MatchTimeSource.Unknown;

    /// <summary>Valve's matchmaking reservation id as a decimal string, or null for a demo that is not a Valve match.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReservationId { get; set; }

    /// <summary>The second number of a Valve file name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutcomeId { get; set; }

    /// <summary>The third number of a Valve file name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Token { get; set; }

    /// <summary>
    ///     The server's game mode, <c>svc_ServerInfo</c>'s <c>gamemode</c> ("premier", "competitive", ...), else
    ///     the players' rank system; null until the demo's visit has read it, or when it has neither.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameMode { get; set; }

    /// <summary>Where the demo came from: the engine's <c>DemoSourceKind</c> by name, a FACEIT server name read as <c>Faceit</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchSource { get; set; }

    /// <summary>One row per ranked or placement human, by SteamID64; null when the demo carries no rank.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MatchRankRow>? Players { get; set; }

    /// <summary>True when the time is a guess (the file's write time) rather than the match's own.</summary>
    [JsonIgnore]
    public bool IsApproximate => MatchTimeSource is not (MatchTimeSource.UserOverride or MatchTimeSource.ValveMatchInfo or MatchTimeSource.ExternalMetadata);
}

/// <summary>
///     History order over index rows: newest match first, by <see cref="DemoCacheIndexEntry.MatchTimeUtcTicks" />
///     or the file's write time while a row has none, then the higher reservation id (Valve numbers them in
///     order), then the path, so the order never depends on the order rows arrived in.
/// </summary>
public sealed class NewestMatchFirst : IComparer<DemoCacheIndexEntry>
{
    public static readonly NewestMatchFirst Instance = new();

    public int Compare(DemoCacheIndexEntry? x, DemoCacheIndexEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? 1 : -1;
        }

        int byTime = TimeOf(y).CompareTo(TimeOf(x));
        if (byTime != 0)
        {
            return byTime;
        }

        int byReservation = ReservationOf(y).CompareTo(ReservationOf(x));
        return byReservation != 0 ? byReservation : StringComparer.Ordinal.Compare(x.Path, y.Path);
    }

    private static long TimeOf(DemoCacheIndexEntry entry) => entry.MatchTimeUtcTicks ?? entry.ModifiedTicks;

    private static ulong ReservationOf(DemoCacheIndexEntry entry) =>
        ulong.TryParse(entry.ReservationId, out ulong id) ? id : 0;
}

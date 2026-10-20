#region

using System.Text.Json.Serialization;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.MatchMeta;

/// <summary>
///     Where a demo's match time came from, in precedence order: the first one present wins. Only
///     <see cref="ValveMatchInfo" /> and <see cref="FileWriteTime" /> are written today; <see cref="UserOverride" />
///     and <see cref="ExternalMetadata" /> are reserved for an override store and an online source.
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

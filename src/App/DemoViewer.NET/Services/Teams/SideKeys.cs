#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>One side's input to clustering: the key, the names behind it and the clan tag.</summary>
/// <param name="Key">Non-bot, non-coach SteamID64s, sorted ordinally; empty when the side is unclusterable.</param>
/// <param name="Names">Raw names, parallel to <paramref name="Key" />.</param>
/// <param name="Clan">The side's clan tag at the last frame, or null.</param>
public sealed record SideInput(IReadOnlyList<string> Key, IReadOnlyList<string> Names, string? Clan)
{
    public static SideInput Empty { get; } = new([], [], null);

    public bool IsClusterable => Key.Count > 0;

    /// <summary>The raw name of a member, or the id when the side did not carry it.</summary>
    public string NameOf(string steamId)
    {
        for (int i = 0; i < Key.Count; i++)
        {
            if (string.Equals(Key[i], steamId, StringComparison.Ordinal))
            {
                return i < Names.Count ? Names[i] : steamId;
            }
        }

        return steamId;
    }
}

/// <summary>
///     Everything clustering needs from one demo, lifted out of the cache record once so a rebuild after
///     a merge or a split reads no sidecar: the same rows are persisted in <c>team-index.json</c>.
/// </summary>
public sealed class DemoSideInput
{
    public required string StableKey { get; init; }

    public required string Path { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>The assignment order: file date until a real match date exists.</summary>
    public long OrderTicks { get; init; }

    public required SideInput T { get; init; }

    public required SideInput Ct { get; init; }

    public SideInput Side(int side) => side == 3 ? Ct : T;

    /// <summary>Both sides carry a key: the demo took part in clustering.</summary>
    public bool IsClusterable => T.IsClusterable || Ct.IsClusterable;

    /// <summary>The same keys, the same clans: a re-parse that changed nothing that matters here.</summary>
    public bool SameSides(DemoSideInput other) =>
        OrderTicks == other.OrderTicks
        && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal)
        && T.Key.SequenceEqual(other.T.Key, StringComparer.Ordinal)
        && Ct.Key.SequenceEqual(other.Ct.Key, StringComparer.Ordinal)
        && string.Equals(T.Clan, other.T.Clan, StringComparison.Ordinal)
        && string.Equals(Ct.Clan, other.Ct.Clan, StringComparison.Ordinal);
}

/// <summary>The side-key projection of a cache record (design §3.1).</summary>
public static class SideKeys
{
    /// <summary>Is this entry a member of a side key? Bots, coaches and empty or zero ids are not.</summary>
    /// <param name="player">The cached roster entry.</param>
    public static bool IsKeyMember(CachedPlayerInfo player) =>
        !player.IsBot
        && !player.IsCoach
        && player.Team is 2 or 3
        && !string.IsNullOrEmpty(player.SteamId64)
        && !string.Equals(player.SteamId64, "0", StringComparison.Ordinal);

    /// <summary>The key of one end-of-demo side.</summary>
    /// <param name="record">The record, at tier 2 or above.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    public static SideInput Side(DemoCacheRecord record, int side)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<CachedPlayerInfo> members =
        [
            .. record.Players.Where(p => p.Team == side && IsKeyMember(p))
                .OrderBy(p => p.SteamId64, StringComparer.Ordinal)
        ];
        // Two userinfo slots can carry one account when a player reconnects; the key is a set.
        List<string> key = [];
        List<string> names = [];
        foreach (CachedPlayerInfo member in members)
        {
            if (key.Count > 0 && string.Equals(key[^1], member.SteamId64, StringComparison.Ordinal))
            {
                continue;
            }

            key.Add(member.SteamId64);
            names.Add(member.Name);
        }

        string? clan = side == 3 ? record.CtClan : record.TClan;
        return new SideInput(key, names, string.IsNullOrWhiteSpace(clan) ? null : clan);
    }

    /// <summary>The clustering input of a record, or null below tier 2 (no roster has been parsed).</summary>
    /// <param name="record">The record.</param>
    public static DemoSideInput? From(DemoCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.Parse.IsPresent || string.IsNullOrEmpty(record.Path))
        {
            return null;
        }

        return new DemoSideInput
        {
            StableKey = DemoCacheStore.StableKey(record.Path),
            Path = record.Path,
            Sha256 = record.Sha256,
            OrderTicks = record.ModifiedTicks,
            T = Side(record, 2),
            Ct = Side(record, 3)
        };
    }

    /// <summary>The clustering input a persisted index row carries, so a rebuild reads no sidecar.</summary>
    /// <param name="stableKey">The row's key.</param>
    /// <param name="row">The row.</param>
    public static DemoSideInput From(string stableKey, TeamIndexDemo row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new DemoSideInput
        {
            StableKey = stableKey,
            Path = row.Path,
            Sha256 = row.Sha256,
            OrderTicks = row.OrderTicks,
            T = FromRow(row.Side(2)),
            Ct = FromRow(row.Side(3))
        };
    }

    private static SideInput FromRow(TeamIndexSide? side) =>
        side is null ? SideInput.Empty : new SideInput([.. side.Key], [.. side.Names], side.Clan);
}

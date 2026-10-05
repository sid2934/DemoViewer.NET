#region

using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

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

    /// <summary>The effective <c>DemoSourceKind</c> by name (<see cref="TeamSourcePolicy.EffectiveKind" />), or null when unknown.</summary>
    public string? SourceKind { get; init; }

    /// <summary>Both end-of-demo sides carried a clan tag.</summary>
    public bool BothClanTags => T.Clan is not null && Ct.Clan is not null;

    public required SideInput T { get; init; }

    public required SideInput Ct { get; init; }

    public SideInput Side(int side) => side == 3 ? Ct : T;

    /// <summary>Both sides carry a key: the demo took part in clustering.</summary>
    public bool IsClusterable => T.IsClusterable || Ct.IsClusterable;

    /// <summary>The same keys, the same clans: a re-parse that changed nothing that matters here.</summary>
    public bool SameSides(DemoSideInput other) =>
        OrderTicks == other.OrderTicks
        && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal)
        && string.Equals(SourceKind, other.SourceKind, StringComparison.Ordinal)
        && T.Key.SequenceEqual(other.T.Key, StringComparer.Ordinal)
        && Ct.Key.SequenceEqual(other.Ct.Key, StringComparer.Ordinal)
        && string.Equals(T.Clan, other.T.Clan, StringComparison.Ordinal)
        && string.Equals(Ct.Clan, other.Ct.Clan, StringComparison.Ordinal);
}

/// <summary>The side-key projection of a library row, or of a demo's record.</summary>
public static class SideKeys
{
    /// <summary>Is this player a member of a side key? Bots, coaches and players without an id are not.</summary>
    /// <param name="player">The roster entry.</param>
    public static bool IsKeyMember(LibraryPlayer player) =>
        !player.IsBot && !player.IsCoach && player.Team is 2 or 3 && player.SteamId64 != 0;

    /// <summary>The key of one end-of-demo side from the demo's players.</summary>
    /// <param name="detail">The demo's record.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    public static SideInput Side(LibraryDemoDetail detail, int side)
    {
        ArgumentNullException.ThrowIfNull(detail);
        List<(string Id, string Name)> members =
        [
            .. detail.Players.Where(p => p.Team == side && IsKeyMember(p))
                .Select(p => (DemoKeys.SteamIdText(p.SteamId64), p.Name))
                .OrderBy(p => p.Item1, StringComparer.Ordinal)
        ];
        return Keyed(members, side == 3 ? detail.Demo.CtClan : detail.Demo.TClan);
    }

    /// <summary>The key of one end-of-demo side from the library's row, or null when the row carries no sides.</summary>
    /// <param name="demo">The row.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    public static SideInput? Side(LibraryDemo demo, int side)
    {
        ArgumentNullException.ThrowIfNull(demo);
        if (demo.PlayersOn(side) is not { } players)
        {
            return null;
        }

        return Keyed([.. players.Select(p => (DemoKeys.SteamIdText(p.SteamId64), p.Name)).OrderBy(p => p.Item1, StringComparer.Ordinal)],
            side == 3 ? demo.CtClan : demo.TClan);
    }

    // Two slots can carry one account when a player reconnects; the key is a set, the first name kept.
    private static SideInput Keyed(List<(string Id, string Name)> members, string? clan)
    {
        List<string> key = [];
        List<string> names = [];
        foreach ((string id, string name) in members)
        {
            if (key.Count > 0 && string.Equals(key[^1], id, StringComparison.Ordinal))
            {
                continue;
            }

            key.Add(id);
            names.Add(name);
        }

        return new SideInput(key, names, string.IsNullOrWhiteSpace(clan) ? null : clan);
    }

    /// <summary>
    ///     The clustering input of a library row, or null when the demo is not parsed or its row carries no
    ///     sides (a row written before the library kept them).
    /// </summary>
    /// <param name="demo">The row.</param>
    public static DemoSideInput? From(LibraryDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        if (demo.State < LibraryDemoState.Parsed || Side(demo, 2) is not { } t || Side(demo, 3) is not { } ct)
        {
            return null;
        }

        return Input(demo, t, ct);
    }

    /// <summary>The clustering input of a demo's record, or null when the demo is not parsed.</summary>
    /// <param name="detail">The record.</param>
    public static DemoSideInput? From(LibraryDemoDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail.Demo.State < LibraryDemoState.Parsed ? null : Input(detail.Demo, Side(detail, 2), Side(detail, 3));
    }

    private static DemoSideInput Input(LibraryDemo demo, SideInput t, SideInput ct) => new()
    {
        StableKey = DemoKeys.StableKey(demo.FilePath),
        Path = demo.FilePath,
        Sha256 = demo.Sha256,
        OrderTicks = demo.Modified.Ticks,
        SourceKind = TeamSourcePolicy.EffectiveKind(demo.SourceKind, demo.Server).ToString(),
        T = t,
        Ct = ct
    };

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
            SourceKind = row.SourceKind,
            T = FromRow(row.Side(2)),
            Ct = FromRow(row.Side(3))
        };
    }

    private static SideInput FromRow(TeamIndexSide? side) =>
        side is null ? SideInput.Empty : new SideInput([.. side.Key], [.. side.Names], side.Clan);
}

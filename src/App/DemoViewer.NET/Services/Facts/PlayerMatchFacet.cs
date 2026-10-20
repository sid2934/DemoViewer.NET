#region

using System.Text.RegularExpressions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The <c>player_match</c> facet (<c>docs/player-match-format.md</c>): a core stamped ruleset whose one table
///     holds a row per player slot with the totals and rate components the Player Overview pools across demos.
///     The facts pass writes it like any facet; this is what it adds on the record beside the stamp.
/// </summary>
public static partial class PlayerMatchFacet
{
    /// <summary>The ruleset's id.</summary>
    public const string RulesetId = "player_match";

    /// <summary>The one table it declares.</summary>
    public const string Table = "player_match";

    /// <summary>The table's key in the facts store.</summary>
    public static FactKey Key { get; } = new(RulesetId, Table);

    /// <summary>
    ///     The accounts in the table: one entry per SteamID, with every slot its rows sit on, sorted ordinally by
    ///     SteamID. A row with no SteamID (a bot, a slot the roster never named) is left out, and so is an account
    ///     the roster has only as a coach or a bot: the directory reads this list as the demo's players.
    ///     <para>
    ///         Names come from the parse's roster by slot. The forward run's <c>player_name</c> can carry an
    ///         earlier occupant of the slot or the engine's <c>Player N</c> placeholder, so it is used only when the
    ///         roster has no name for the account, and never when it is a placeholder.
    ///     </para>
    /// </summary>
    /// <param name="rows">The table's rows.</param>
    /// <param name="roster">The demo's cached roster.</param>
    public static List<FacetPlayer> Players(IReadOnlyList<FactRow> rows, IReadOnlyList<CachedPlayerInfo> roster)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(roster);
        SortedDictionary<string, List<(int Slot, string? Name)>> bySteamId = new(StringComparer.Ordinal);
        foreach (FactRow row in rows)
        {
            if (row.Dimensions.GetValueOrDefault("steam_id")?.AsText() is not { Length: > 0 } steamId
                || string.Equals(steamId, "0", StringComparison.Ordinal)
                || row.Dimensions.GetValueOrDefault("player_slot")?.AsInteger() is not { } slot)
            {
                continue;
            }

            if (!bySteamId.TryGetValue(steamId, out List<(int Slot, string? Name)>? slots))
            {
                bySteamId[steamId] = slots = [];
            }

            slots.Add(((int)slot, row.Dimensions.GetValueOrDefault("player_name")?.AsText()));
        }

        List<FacetPlayer> players = [];
        foreach ((string steamId, List<(int Slot, string? Name)> slots) in bySteamId)
        {
            List<CachedPlayerInfo> entries = [.. roster.Where(p => string.Equals(p.SteamId64, steamId, StringComparison.Ordinal))];
            if (entries.Count > 0 && entries.All(p => p.IsCoach || p.IsBot))
            {
                continue;
            }

            slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            List<string> names = [];
            foreach ((int slot, _) in slots)
            {
                if (roster.FirstOrDefault(p => p.Slot == slot && string.Equals(p.SteamId64, steamId, StringComparison.Ordinal)) is { Name.Length: > 0 } known
                    && !names.Contains(known.Name, StringComparer.Ordinal))
                {
                    names.Add(known.Name);
                }
            }

            if (names.Count == 0)
            {
                names.AddRange(slots.Select(s => s.Name).OfType<string>().Where(n => n.Length > 0 && !Placeholder().IsMatch(n)).Distinct(StringComparer.Ordinal));
            }

            players.Add(new FacetPlayer { SteamId64 = steamId, Names = names, Slots = [.. slots.Select(s => s.Slot)] });
        }

        return players;
    }

    [GeneratedRegex(@"^Player \d+$")]
    private static partial Regex Placeholder();
}

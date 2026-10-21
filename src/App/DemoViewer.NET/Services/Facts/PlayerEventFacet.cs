#region

using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>One kill: who killed whom, when and with what, both accounts named.</summary>
/// <param name="Round">The round it happened in.</param>
/// <param name="Tick">The frame-clock tick of the kill.</param>
/// <param name="Killer">The killer's SteamID64 as a decimal string; null for a bot or a slot the roster never named.</param>
/// <param name="Victim">The victim's SteamID64, by the same rule.</param>
/// <param name="Weapon">The weapon, as the game names it ("ak47"); null when the event carried none.</param>
/// <param name="Headshot">True for a headshot kill.</param>
public sealed record PlayerKill(int Round, long Tick, string? Killer, string? Victim, string? Weapon, bool Headshot);

/// <summary>
///     The <c>player_event</c> facet (<c>docs/player-event-format.md</c>): a core stamped ruleset of two event
///     tables, a row per kill under the killer and a row per death under the victim. Neither row can name the
///     other account, only its slot, so <see cref="Kills" /> joins the two into kills that name both.
/// </summary>
public static class PlayerEventFacet
{
    /// <summary>The ruleset's id.</summary>
    public const string RulesetId = "player_event";

    /// <summary>A row per kill, under the killer, with the victim's slot.</summary>
    public const string KillsTable = "player_kills";

    /// <summary>A row per death, under the victim, with the killer's slot.</summary>
    public const string DeathsTable = "player_deaths";

    /// <summary>The kills table's key in the facts store.</summary>
    public static FactKey KillsKey { get; } = new(RulesetId, KillsTable);

    /// <summary>The deaths table's key in the facts store.</summary>
    public static FactKey DeathsKey { get; } = new(RulesetId, DeathsTable);

    /// <summary>
    ///     Every kill in the demo with both accounts: each kill row paired with the death row of the same tick,
    ///     killer slot and victim slot. A kill whose death row is missing keeps a null victim.
    /// </summary>
    /// <param name="kills">The <see cref="KillsTable" /> rows.</param>
    /// <param name="deaths">The <see cref="DeathsTable" /> rows.</param>
    public static List<PlayerKill> Kills(IReadOnlyList<FactRow> kills, IReadOnlyList<FactRow> deaths)
    {
        ArgumentNullException.ThrowIfNull(kills);
        ArgumentNullException.ThrowIfNull(deaths);
        Dictionary<(long Tick, long Killer, long Victim), string?> victims = new(deaths.Count);
        foreach (FactRow death in deaths)
        {
            if (Whole(death.Dimensions, "tick") is { } tick && Whole(death.Dimensions, "player_slot") is { } victim
                                                            && Whole(death.Values, "attacker_slot") is { } killer)
            {
                victims.TryAdd((tick, killer, victim), SteamId(death.Dimensions));
            }
        }

        List<PlayerKill> result = new(kills.Count);
        foreach (FactRow kill in kills)
        {
            if (Whole(kill.Dimensions, "tick") is not { } tick || Whole(kill.Dimensions, "player_slot") is not { } killer)
            {
                continue;
            }

            string? victim = Whole(kill.Values, "victim_slot") is { } slot ? victims.GetValueOrDefault((tick, killer, slot)) : null;
            result.Add(new PlayerKill(
                (int) (Whole(kill.Dimensions, "round_number") ?? 0),
                tick,
                SteamId(kill.Dimensions),
                victim,
                kill.Values.GetValueOrDefault("weapon")?.AsText() is { Length: > 0 } weapon ? weapon : null,
                kill.Values.GetValueOrDefault("headshot") is { } headshot && (headshot.AsBoolean() ?? headshot.AsNumber() > 0) == true));
        }

        return result;
    }

    private static long? Whole(IReadOnlyDictionary<string, FactValue> cells, string name) =>
        cells.GetValueOrDefault(name)?.AsNumber() is { } value ? (long) value : null;

    private static string? SteamId(IReadOnlyDictionary<string, FactValue> dimensions) =>
        dimensions.GetValueOrDefault("steam_id")?.AsText() is { Length: > 0 } id && id != "0" ? id : null;
}

#region

using Avalonia.Media;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Frame game event view model.</summary>
/// <remarks>Initializes a new <see cref="FrameGameEventViewModel" /> instance.</remarks>
public sealed class FrameGameEventViewModel(GameEvent e, Func<int, string> playerName)
{
    private static readonly IBrush _brushBlind = new SolidColorBrush(Color.Parse("#C09C27B0")); // purple
    private static readonly IBrush _brushBomb = new SolidColorBrush(Color.Parse("#C0FFC107")); // amber

    private static readonly IBrush _brushConnect = new SolidColorBrush(Color.Parse("#C0009688")); // teal

    // Pre-allocated brush singletons — one per event category, shared across all row instances.
    private static readonly IBrush _brushDeath = new SolidColorBrush(Color.Parse("#C0F44336")); // red
    private static readonly IBrush _brushDefault = new SolidColorBrush(Color.Parse("#C0888888")); // grey
    private static readonly IBrush _brushFire = new SolidColorBrush(Color.Parse("#C0FF5722")); // deep-orange
    private static readonly IBrush _brushGrenade = new SolidColorBrush(Color.Parse("#C000BCD4")); // cyan
    private static readonly IBrush _brushHurt = new SolidColorBrush(Color.Parse("#C0FF9800")); // orange
    private static readonly IBrush _brushRound = new SolidColorBrush(Color.Parse("#C04CAF50")); // green
    private static readonly IBrush _brushWeapon = new SolidColorBrush(Color.Parse("#C02196F3")); // blue

    /// <summary>Accent brush.</summary>
    public IBrush AccentBrush { get; } = BuildBrush(e.Name);

    /// <summary>Event name.</summary>
    public string EventName { get; } = e.Name;

    /// <summary>Summary.</summary>
    public string Summary { get; } = BuildSummary(e, playerName);

    private static IBrush BuildBrush(string name) => name switch
    {
        "player_death" => _brushDeath,
        "player_hurt" => _brushHurt,
        "weapon_fire" => _brushWeapon,
        "player_blind" => _brushBlind,
        "bomb_planted" or "bomb_defused" or "bomb_exploded"
            or "bomb_beginplant" or "bomb_begindefuse" => _brushBomb,
        "round_end" or "round_officially_ended"
            or "round_freeze_end" or "round_start" => _brushRound,
        "inferno_startburn" or "molotov_detonate"
            or "inferno_expire" => _brushFire,
        "flashbang_detonate" or "hegrenade_detonate"
            or "decoy_detonate" or "grenade_thrown" => _brushGrenade,
        "player_connect" or "player_disconnect"
            or "player_team" => _brushConnect,
        _ => _brushDefault
    };

    private static string BuildSummary(GameEvent e, Func<int, string> playerName) => e.Payload switch
    {
        PlayerDeathEvent d => $"{playerName(d.Attacker)} → {playerName(d.UserId)}  {d.Weapon}{(d.Headshot ? " hs" : "")}",
        PlayerHurtEvent h => $"{playerName(h.Attacker)} → {playerName(h.UserId)}  -{h.DmgHealth}hp  {h.Weapon}",
        WeaponFireEvent w => $"{playerName(w.UserId)}  {w.Weapon}",
        PlayerBlindEvent b => $"{playerName(b.Attacker)} → {playerName(b.UserId)}  {b.BlindDuration:F2}s",
        BombPlantedEvent b => $"{playerName(b.UserId)}  site={b.Site}",
        BombDefusedEvent b => $"{playerName(b.UserId)}  site={b.Site}",
        BombExplodedEvent b => $"site={b.Site}",
        FlashbangDetonateEvent g => $"{playerName(g.UserId)}  ({g.X:F0},{g.Y:F0},{g.Z:F0})",
        HegrenadeDetonateEvent g => $"{playerName(g.UserId)}  ({g.X:F0},{g.Y:F0},{g.Z:F0})",
        GrenadeThrownEvent g => $"{playerName(g.UserId)}  {g.Weapon}",
        RoundEndEvent r => $"winner={r.Winner}  reason={r.Reason}",
        RoundFreezeEndEvent _ => "buy time over — play begins",
        RoundOfficiallyEndedEvent _ => "round over",
        PlayerTeamEvent t => $"{playerName(t.UserId)}  team={t.Team}",
        PlayerConnectEvent c => $"{c.Name}  (slot {c.UserId})",
        PlayerDisconnectEvent d => $"{playerName(d.UserId)}",
        InfernoExpireEvent i => $"at ({i.X:F0},{i.Y:F0},{i.Z:F0})",
        OtherDeathEvent o => $"{o.OtherType}  killed by {playerName(o.Attacker)}  {o.Weapon}",
        _ => string.Empty
    };
}

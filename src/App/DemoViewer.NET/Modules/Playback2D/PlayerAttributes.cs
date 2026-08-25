#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The attributes-panel row for one player. An <see cref="ObservableObject" /> so the panel
///     updates in place each push without rebuilding the list (the ItemsControl keeps one row per slot).
///     Every field is copied out of the transient/pooled <c>PlayerState</c> INSIDE the <c>Advanced</c>
///     callback; missing fields render "—" and never crash.
/// </summary>
public sealed partial class PlayerAttributes : ObservableObject
{
    [ObservableProperty]
    private string _activeWeapon = "—";

    /// <summary>Average damage per round ("ADR") = total damage / rounds played — the headline impact stat.</summary>
    [ObservableProperty]
    private string _adr = "—";

    [ObservableProperty]
    private string _armor = "—";

    [ObservableProperty]
    private string _cash = "—";

    /// <summary>Match-total damage dealt (cumulative) — the raw stat behind ADR.</summary>
    [ObservableProperty]
    private string _damage = "—";

    [ObservableProperty]
    private string _equipmentValue = "—";

    [ObservableProperty]
    private string _grenades = "—";

    [ObservableProperty]
    private bool _hasDefuser;

    [ObservableProperty]
    private bool _hasHelmet;

    [ObservableProperty]
    private bool _hasLivePawn;

    [ObservableProperty]
    private string _health = "—";

    /// <summary>
    ///     True for an actual playing-team (T/CT) participant — gates panel visibility so coach /
    ///     GOTV / spectator roster entries don't show as empty grayed cards.
    /// </summary>
    [ObservableProperty]
    private bool _inMatch;

    [ObservableProperty]
    private bool _isAlive;

    /// <summary>
    ///     True for the one card the 2D camera is following. Drives the card's followed treatment and the
    ///     "requested" chip — spectate has no readback, so the UI never claims the pick was confirmed.
    /// </summary>
    [ObservableProperty]
    private bool _isFollowed;

    /// <summary>Match-total kills/deaths/assists ("K/D/A") — the cumulative scoreboard stat.</summary>
    [ObservableProperty]
    private string _kda = "—";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _roundKills = "—";

    [ObservableProperty]
    private string _score = "—";

    [ObservableProperty]
    private int _team;

    public PlayerAttributes(int slot) => Slot = slot;

    /// <summary>Stable join key into the roster.</summary>
    public int Slot { get; }

    /// <summary>Dead players' cards gray out (dimmed) instead of being removed from the panel.</summary>
    public double RowOpacity => IsAlive ? 1.0 : 0.45;

    /// <summary>
    ///     True when this player is on T (Team 2). Drives the team-chip colour CLASS in the view, which sets the
    ///     chip background to the theme-aware <c>Pb2dTeamT</c> token — so the HUD team colour tracks the theme
    ///     (and matches the canvas markers) from the ONE token source, instead of a hardcoded dark hex. CT and
    ///     the neutral (spectator/unassigned) case are handled the same way (<c>Pb2dTeamCt</c> / neutral).
    /// </summary>
    public bool IsT => Team == 2;

    /// <summary>True when this player is on CT (Team 3) — the sibling of <see cref="IsT" />; drives the CT chip class.</summary>
    public bool IsCt => Team == 3;

    /// <summary>The team label for the panel header.</summary>
    public string TeamLabel => Team switch
    {
        2 => "T",
        3 => "CT",
        _ => "—"
    };

    // RowOpacity derives from IsAlive — re-raise it when IsAlive changes.
    partial void OnIsAliveChanged(bool value) => OnPropertyChanged(nameof(RowOpacity));

    // IsT / IsCt / TeamLabel derive from Team — re-raise them when Team changes.
    partial void OnTeamChanged(int value)
    {
        OnPropertyChanged(nameof(IsT));
        OnPropertyChanged(nameof(IsCt));
        OnPropertyChanged(nameof(TeamLabel));
    }
}

#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     Round-level game-info panel state. Read ONCE per push (not per-player) from the
///     <c>CCSGameRulesProxy</c> entity (the <c>m_pGameRules.</c>-prefixed keys, verified against
///     <c>FreezePeriodProvider</c> and the impl-time field probe) plus the two playing <c>CCSTeam</c>
///     entities (score by <c>m_iTeamNum</c> 2=T / 3=CT). An <see cref="ObservableObject" /> so the panel
///     updates in place. Missing fields render placeholders, never crash.
/// </summary>
public sealed partial class GameInfo : ObservableObject
{
    [ObservableProperty]
    private string _bombState = "—";

    /// <summary>
    ///     The C4 detonation countdown's source. <c>true</c> when a live ticking <c>CPlantedC4</c> is
    ///     present and the main countdown (<see cref="RoundTime" /> / <see cref="RoundSeconds" />) shows
    ///     time-to-detonation (<c>m_flC4Blow − correctedCurtime</c>) instead of the round clock (#5).
    /// </summary>
    [ObservableProperty]
    private bool _bombTicking;

    [ObservableProperty]
    private int _ctScore;

    /// <summary>True while a defuse is in progress (<c>m_bBeingDefused</c>): drives second-timer visibility.</summary>
    [ObservableProperty]
    private bool _defuseInProgress;

    /// <summary>
    ///     The defuse kit state of the active defuser, reflected so the UI can label the defuse timer
    ///     "with kit" (5s) vs "no kit" (10s). The length itself is read directly from
    ///     <c>m_flDefuseLength</c>, which already encodes kit vs no-kit. "—" when no defuse is in progress.
    /// </summary>
    [ObservableProperty]
    private string _defuseKitNote = "—";

    /// <summary>The defuse-completion remaining as a number (#5). NaN when no defuse is in progress.</summary>
    [ObservableProperty]
    private double _defuseSeconds = double.NaN;

    /// <summary>
    ///     The SECOND timer shown next to the main countdown during a defuse-in-progress (#5): the
    ///     defuse-completion remaining (<c>m_flDefuseCountDown − correctedCurtime</c>) as a string,
    ///     creating the defuse-vs-detonation race the main countdown shows the other half of. "—" when no
    ///     defuse is in progress.
    /// </summary>
    [ObservableProperty]
    private string _defuseTime = "—";

    [ObservableProperty]
    private string _phase = "—";

    [ObservableProperty]
    private string _roundNumber = "—";

    /// <summary>
    ///     The main countdown's remaining seconds as a number (the round clock, or, once the bomb is
    ///     planted, the C4 detonation countdown). Negative / NaN when no countdown is active. Backs the
    ///     <see cref="RoundTime" /> string and lets tests assert with tolerance rather than string-match.
    /// </summary>
    [ObservableProperty]
    private double _roundSeconds = double.NaN;

    [ObservableProperty]
    private string _roundTime = "—";

    /// <summary>
    ///     A short note on the source of the round clock. The round length is now read from the networked
    ///     <c>m_iRoundTime</c> and the clock is offset-corrected against the entity time base (#4), so the
    ///     value is exact rather than an mp_roundtime assumption.
    /// </summary>
    [ObservableProperty]
    private string _roundTimeNote =
        "from networked m_iRoundTime; clock offset-corrected to the round-start base";

    [ObservableProperty]
    private int _tScore;
}

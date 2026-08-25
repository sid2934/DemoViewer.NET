namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Round-level state read once per frame off <c>CCSGameRulesProxy</c> plus the two playing
///     <c>CCSTeam</c> entities. Drives the HUD — the XAML panel today, B4's <c>ClockLayer</c> later.
///     Missing fields render placeholders, never throw.
/// </summary>
/// <param name="Phase">"Warmup" | "Freeze" | "Live" | "—".</param>
/// <param name="BombState">"Defused" | "Planted" | "Dropped" | "—".</param>
/// <param name="RoundNumber">1-based round number; 0 = unknown.</param>
/// <param name="RoundsPlayed">
///     <c>m_totalRoundsPlayed</c>; -1 = unknown. The ADR denominator the attributes panel divides by.
/// </param>
/// <param name="RoundSeconds">
///     The main countdown's remaining seconds — the round clock, or the C4 detonation countdown once a
///     live ticking bomb owns the timer. NaN when no countdown is active.
/// </param>
/// <param name="RoundTime"><see cref="RoundSeconds" /> formatted m:ss, or "freeze" / "—".</param>
/// <param name="BombTicking">True while a live ticking <c>CPlantedC4</c> owns the main countdown.</param>
/// <param name="DefuseInProgress">True while <c>m_bBeingDefused</c> — drives the second timer.</param>
/// <param name="DefuseKitNote">"with kit" (5s) / "no kit" (10s) / "—".</param>
/// <param name="DefuseSeconds">Defuse-completion remaining. NaN when not defusing.</param>
/// <param name="DefuseTime"><see cref="DefuseSeconds" /> formatted m:ss, or "—".</param>
/// <param name="TScore">T-side score (<c>CCSTeam.m_iScore</c> where <c>m_iTeamNum</c> is 2).</param>
/// <param name="CtScore">CT-side score (<c>m_iTeamNum</c> 3).</param>
public readonly record struct SceneGameInfo(
    string Phase,
    string BombState,
    int RoundNumber,
    int RoundsPlayed,
    double RoundSeconds,
    string RoundTime,
    bool BombTicking,
    bool DefuseInProgress,
    string DefuseKitNote,
    double DefuseSeconds,
    string DefuseTime,
    int TScore,
    int CtScore)
{
    /// <summary>The placeholder state a frame carries before the game-rules entity has been read.</summary>
    public static readonly SceneGameInfo Empty = new(
        "—", "—", 0, -1, double.NaN, "—", false, false, "—", double.NaN, "—", 0, 0);
}

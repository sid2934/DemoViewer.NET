namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     One kill in the scene's kill feed, with the modifiers the event factory enriched. Tick-granular
///     (the parse carries no sub-tick timing for events).
///     <para>
///         This is the <b>one</b> declaration of the row shape: <c>KillFeedTimeline.Window</c> and its HUD
///         layer operate on this record and must not redeclare it in Pipeline. The XAML feed's
///         <c>DataTemplate</c> binds straight to it too, so there is exactly one row shape between the
///         window, the view and the exported HUD.
///     </para>
/// </summary>
/// <param name="Tick">The DV frame clock the kill happened on.</param>
/// <param name="Attacker">Killer's display name, or "world".</param>
/// <param name="Assister">Assister's display name, or null when there was none.</param>
/// <param name="Victim">Victim's display name.</param>
/// <param name="Weapon">Weapon short name.</param>
/// <param name="Headshot">Whether the killing shot was a headshot.</param>
/// <param name="Penetrated">Whether the shot went through at least one surface (wallbang).</param>
/// <param name="NoScope">Sniper kill taken without scoping.</param>
/// <param name="ThroughSmoke">The shot crossed a smoke cloud.</param>
/// <param name="AttackerBlind">The attacker was flashed at the moment of the kill.</param>
/// <param name="AttackerInAir">The attacker was airborne.</param>
/// <param name="AssistedFlash">The assist was a flash assist rather than damage.</param>
/// <param name="AttackerTeam">
///     The killer's side at the kill's own tick: 2 = T, 3 = CT, <b>0 = the demo could not say</b>. The
///     encoding is <c>TimelineEventKeys.Team</c>'s, so a kill marker on the timeline and a kill row in the
///     feed cannot disagree about which side gets the colour.
///     <para>
///         <b>Trailing and defaulted, deliberately.</b> This record is the one row shape the XAML feed and
///         the exported <c>hud.killfeed</c> layer share, so widening it in the middle would break every
///         construction site at once. 0 is a first-class answer: a demo that never emitted
///         <c>player_team</c> for a slot must still get its kill drawn, in the neutral colour.
///     </para>
/// </param>
/// <param name="VictimTeam">The victim's side at the kill's own tick, same encoding.</param>
public readonly record struct KillFeedRow(
    int Tick,
    string Attacker,
    string? Assister,
    string Victim,
    string Weapon,
    bool Headshot,
    bool Penetrated,
    bool NoScope,
    bool ThroughSmoke,
    bool AttackerBlind,
    bool AttackerInAir,
    bool AssistedFlash,
    int AttackerTeam = 0,
    int VictimTeam = 0)
{
    /// <summary>True when the kill had an assister: drives the "+name" chip's visibility in the XAML feed.</summary>
    public bool HasAssist => !string.IsNullOrEmpty(Assister);
}

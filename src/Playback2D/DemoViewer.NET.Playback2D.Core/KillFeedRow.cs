namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     One kill in the scene's kill feed, with the modifiers the event factory enriched. Tick-granular
///     (the parse carries no sub-tick timing for events).
///     <para>
///         This is the <b>one</b> declaration of the row shape: B4's <c>KillFeedTimeline.Window</c> and
///         its HUD layer operate on this record and must not redeclare it in Pipeline (registry
///         correction 5). B4 also deleted the App's parallel <c>KillFeedEntry</c> and pointed the XAML
///         feed's <c>DataTemplate</c> straight at this record (D5), so there is now exactly one row shape
///         between the window, the view and the exported HUD.
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
    bool AssistedFlash)
{
    /// <summary>True when the kill had an assister — drives the "+name" chip's visibility in the XAML feed.</summary>
    public bool HasAssist => !string.IsNullOrEmpty(Assister);
}

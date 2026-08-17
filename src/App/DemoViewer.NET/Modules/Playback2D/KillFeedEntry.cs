namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     One kill in the 2D kill feed (A4). The whole feed is PRE-BUILT once at load from the demo's
///     player_death timeline (resolved slots → roster names, modifiers read from the enriched event fields)
///     and the visible rows are a TICK-WINDOW filter over it at render time — so display is decoupled from
///     the playback notification cadence (no kill is lost to a render-skipped frame) and seeking shows the
///     right kills for any moment. Immutable; ordered for display by <see cref="Tick" />.
///     <para>
///         Sub-tick timing is not available from the parse (events carry ServerTick / GameTick only), so
///         <see cref="Tick" /> is tick-granular.
///     </para>
/// </summary>
public sealed record KillFeedEntry(
    int Tick,
    string KillerName,
    string? AssisterName,
    string VictimName,
    string Weapon,
    bool IsHeadshot,
    bool IsWallbang,
    bool IsNoScope,
    bool IsThroughSmoke,
    bool AttackerBlind,
    bool AttackerInAir,
    bool IsFlashAssist)
{
    /// <summary>True when the kill had an assister (drives the "+name" chip visibility).</summary>
    public bool HasAssist => !string.IsNullOrEmpty(AssisterName);
}

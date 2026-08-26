namespace DemoViewer.NET.Playback2D.Core.Hud;

/// <summary>
///     Round-level HUD state as a <b>pure function of tick</b> (design §5.1). No wall clock, no playback
///     position, no side effects — the same tick answers the same snapshot forever, which is what makes
///     an exported HUD deterministic and a headless render possible.
///     <para>
///         Why a data source and not a field on <c>Scene2DFrame</c> (plan D4): the kill feed is a
///         pre-built timeline queried by tick, not per-frame world state, and adding a member to B0's
///         frame record from B4 would be a guaranteed merge conflict for no gain.
///     </para>
/// </summary>
public interface IHudDataSource
{
    /// <summary>The HUD state at a tick.</summary>
    /// <param name="tick">Server tick.</param>
    HudSnapshot At(int tick);
}

/// <summary>
///     What the two HUD layers draw for one tick.
///     <para>
///         A <b>Core</b> type (integrator correction 4): <see cref="IHudDataSource" /> returns it and
///         Core cannot see Pipeline. <see cref="KillRows" /> carries B0's Core
///         <see cref="KillFeedRow" /> — Pipeline must not declare a second one.
///     </para>
///     <para>
///         <see cref="KillRows" /> and <see cref="Roster" /> are borrowed from the source and are valid
///         until the next <see cref="IHudDataSource.At" /> call on that source. A layer reads them during
///         <c>Render</c> in the same frame; nothing retains them.
///     </para>
/// </summary>
/// <param name="Tick">The tick this snapshot answers for.</param>
/// <param name="RoundNumber">Display text for the round, e.g. <c>"12"</c>, or <c>"—"</c> when unknown.</param>
/// <param name="TScore">T-side score.</param>
/// <param name="CtScore">CT-side score.</param>
/// <param name="CountdownSeconds">
///     The main countdown — the round clock, or the C4 detonation countdown once
///     <paramref name="BombTicking" />. <c>NaN</c> when no countdown is running.
/// </param>
/// <param name="BombTicking">True while a live ticking C4 owns the main countdown.</param>
/// <param name="DefuseInProgress">True while a defuse is under way.</param>
/// <param name="DefuseSeconds">Defuse-completion remaining, or <c>NaN</c>.</param>
/// <param name="KillRows">The kill rows visible at this tick, oldest first.</param>
/// <param name="Roster">
///     Every player card at this tick, in slot order — the two sides are split by the layer, not here, so
///     one list serves both edges of the frame. Empty when the source was built without a roster reader,
///     which is what a fixture render and a HUD-less export both look like.
/// </param>
public readonly record struct HudSnapshot(
    int Tick,
    string RoundNumber,
    int TScore,
    int CtScore,
    double CountdownSeconds,
    bool BombTicking,
    bool DefuseInProgress,
    double DefuseSeconds,
    IReadOnlyList<KillFeedRow> KillRows,
    IReadOnlyList<HudPlayerRow> Roster)
{
    /// <summary>The snapshot a layer draws before any data has arrived. Renders placeholders, never throws.</summary>
    public static HudSnapshot Empty { get; } =
        new(0, "—", 0, 0, double.NaN, false, false, double.NaN, [], []);
}

/// <summary>
///     Colours and metrics for the export HUD. A record so a caller can <c>with</c> one value; every
///     field has a default, so <c>new HudStyle()</c> is the shipped look.
/// </summary>
/// <param name="FontSizePx">Em size of the HUD text.</param>
/// <param name="MarginPx">Inset from the pane edge.</param>
/// <param name="TextArgb">Primary text colour.</param>
/// <param name="PanelArgb">Backing-panel fill, drawn behind the text so a light radar cannot swallow it.</param>
/// <param name="DimTextArgb">Secondary text: the round caption, a card's weapon and K/D/A.</param>
/// <param name="OnTeamArgb">
///     Text drawn <b>on</b> a team-coloured fill. Near-black rather than the primary near-white, because
///     both team tokens (amber and mid blue) are light enough that white-on-team is the unreadable pairing.
/// </param>
/// <param name="MoneyArgb">Cash figure on a roster card.</param>
/// <param name="ArmorArgb">The armour bar under a card's health bar; brighter with a helmet.</param>
/// <param name="TrackArgb">The unfilled remainder of a card's health/armour bar.</param>
/// <param name="RosterCardWidthPx">
///     Preferred card width. Clamped down against the pane so a small export keeps its map — see
///     <c>RosterLayer</c>, which draws nothing at all rather than a roster wider than the radar.
/// </param>
/// <param name="RosterRowHeightPx">Preferred card height; shrunk to fit the pane before anything is dropped.</param>
/// <param name="RosterRowGapPx">Vertical gap between cards.</param>
public sealed record HudStyle(
    float FontSizePx = 14f,
    float MarginPx = 12f,
    uint TextArgb = 0xFFF2F2F2u,
    uint PanelArgb = 0x99101010u,
    uint DimTextArgb = 0xFF9AA4AFu,
    uint OnTeamArgb = 0xFF12161Au,
    uint MoneyArgb = 0xFF7BC96Fu,
    uint ArmorArgb = 0xFF8FA3B8u,
    uint TrackArgb = 0x66000000u,
    float RosterCardWidthPx = 160f,
    float RosterRowHeightPx = 46f,
    float RosterRowGapPx = 5f);

namespace DemoViewer.NET.Playback2D.Core.Hud;

/// <summary>
///     Round-level HUD state as a <b>pure function of tick</b> (design §5.1). No wall clock, no playback
///     position, no side effects: the same tick answers the same snapshot forever, so an exported HUD is
///     deterministic and a headless render is possible.
///     <para>
///         A separate type rather than a field on <c>Scene2DFrame</c>: the kill feed is a pre-built
///         timeline queried by tick, not per-frame world state, and does not belong in the frame record.
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
///         A <b>Core</b> type: <see cref="IHudDataSource" /> returns it and Core cannot see Pipeline.
///         <see cref="KillRows" /> carries the Core <see cref="KillFeedRow" />; Pipeline must not declare
///         a second one.
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
///     The main countdown: the round clock, or the C4 detonation countdown once
///     <paramref name="BombTicking" />. <c>NaN</c> when no countdown is running.
/// </param>
/// <param name="BombTicking">True while a live ticking C4 owns the main countdown.</param>
/// <param name="DefuseInProgress">True while a defuse is under way.</param>
/// <param name="DefuseSeconds">Defuse-completion remaining, or <c>NaN</c>.</param>
/// <param name="KillRows">The kill rows visible at this tick, oldest first.</param>
/// <param name="Roster">
///     Every player card at this tick, in slot order: the two sides are split by the layer, not here, so
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
///     field has a default, so <c>new HudStyle()</c> is the shipped look. Only what a caller actually
///     varies lives here; the rest of each layer's palette is a constant in the layer that draws it.
/// </summary>
/// <param name="FontSizePx">Em size of the HUD text.</param>
/// <param name="MarginPx">Inset from the pane edge.</param>
/// <param name="TextArgb">Primary text colour.</param>
/// <param name="PanelArgb">Backing-panel fill, drawn behind the text so a light radar cannot swallow it.</param>
public sealed record HudStyle(
    float FontSizePx = 14f,
    float MarginPx = 12f,
    uint TextArgb = 0xFFF2F2F2u,
    uint PanelArgb = 0x99101010u);

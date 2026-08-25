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
///         <see cref="KillRows" /> is borrowed from the source and is valid until the next
///         <see cref="IHudDataSource.At" /> call on that source. A layer reads it during
///         <c>Render</c> in the same frame; nothing retains it.
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
public readonly record struct HudSnapshot(
    int Tick,
    string RoundNumber,
    int TScore,
    int CtScore,
    double CountdownSeconds,
    bool BombTicking,
    bool DefuseInProgress,
    double DefuseSeconds,
    IReadOnlyList<KillFeedRow> KillRows)
{
    /// <summary>The snapshot a layer draws before any data has arrived. Renders placeholders, never throws.</summary>
    public static HudSnapshot Empty { get; } =
        new(0, "—", 0, 0, double.NaN, false, false, double.NaN, []);
}

/// <summary>
///     Colours and metrics for the export HUD. A record so a caller can <c>with</c> one value; every
///     field has a default, so <c>new HudStyle()</c> is the shipped look.
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

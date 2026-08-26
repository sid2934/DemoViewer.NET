namespace DemoViewer.NET.Playback2D.Core.Hud;

/// <summary>
///     One player card in the export HUD's roster (registry D0 §3.2).
///     <para>
///         A <b>Core</b> type for the same reason <c>HudSnapshot</c> is one: <see cref="IHudDataSource" />
///         returns it and Core cannot see Pipeline. Nothing in Pipeline may declare a second shape.
///     </para>
///     <para>
///         <b>None of this reaches the scene through <c>PlayerMarker</c>.</b> A marker carries only what
///         the canvas needs to place a disc — slot, team, position, yaw, alive, label. Health, armour,
///         weapon, money and K/D/A existed <i>only</i> app-side in <c>PlayerAttributes</c>, which an export
///         cannot see; D3b lifted those entity reads into <c>SceneFrameBuilder</c> so the app, the export
///         and <c>dv2d</c> read them once, from the same place.
///     </para>
/// </summary>
/// <param name="Slot">Roster slot — the stable join key, and the marker's.</param>
/// <param name="Team">CS2 team number: 2 = T, 3 = CT, anything else = neither playing side.</param>
/// <param name="Name">
///     Display tag. The <b>same</b> string <c>SceneFrameInput.LabelForSlot</c> gives the marker, so a card
///     and the disc it describes are matched by eye without a legend. Not the full networked name: the
///     embedded face is Latin-only (see <c>TextBlobCache</c>) and a full roster of them would not fit the
///     card anyway.
/// </param>
/// <param name="IsAlive">False for a dead pawn; the card grays and its bars empty rather than vanishing.</param>
/// <param name="Health">Current health, 0 when dead or unread.</param>
/// <param name="Armor">Armour value 0..100.</param>
/// <param name="HasHelmet">Whether the armour includes a helmet.</param>
/// <param name="HasDefuser">Whether this CT carries a kit.</param>
/// <param name="Weapon">Active weapon's short name, or <c>"—"</c> when it did not resolve.</param>
/// <param name="Money">Cash in hand.</param>
/// <param name="Kills">Match-total kills.</param>
/// <param name="Deaths">Match-total deaths.</param>
/// <param name="Assists">Match-total assists.</param>
public readonly record struct HudPlayerRow(
    int Slot,
    int Team,
    string Name,
    bool IsAlive,
    int Health,
    int Armor,
    bool HasHelmet,
    bool HasDefuser,
    string Weapon,
    int Money,
    int Kills,
    int Deaths,
    int Assists);

#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Stroke widths in device-independent pixels, carried alongside the palette because the pre-move
///     control expressed them as pen widths rather than colours.
///     <para>
///         The parameter defaults apply to <c>new SceneStrokeWidths()</c>, not to
///         <c>default(SceneStrokeWidths)</c> — the latter zeroes every field and makes strokes vanish.
///         Use <see cref="Default" /> rather than relying on struct zero-init.
///     </para>
/// </summary>
/// <param name="MinorGrid">Minor grid line width.</param>
/// <param name="MajorGrid">Major grid line width.</param>
/// <param name="Sightline">Could-see segment width.</param>
/// <param name="BombTrack">The bomb ring's unfilled track width.</param>
/// <param name="BombDetonation">The depleting detonation arc width.</param>
/// <param name="BombDefuse">The depleting defuse arc width.</param>
/// <param name="SmokeStroke">The smoke disc's outline width.</param>
public readonly record struct SceneStrokeWidths(
    float MinorGrid = 1f,
    float MajorGrid = 1f,
    float Sightline = 1f,
    float BombTrack = 2f,
    float BombDetonation = 3f,
    float BombDefuse = 3f,
    float SmokeStroke = 1f)
{
    /// <summary>
    ///     The widths the pre-move control used. Prefer this over <c>default</c>. Spelled out rather
    ///     than written <c>new()</c> because that form reads as "the default value" to both a human and
    ///     CA1805, while it actually means "the parameter defaults" — the exact confusion this field exists
    ///     to remove.
    /// </summary>
    public static readonly SceneStrokeWidths Default = new(1f, 1f, 1f, 2f, 3f, 3f, 1f);
}

/// <summary>
///     Every colour the scene layers draw with, resolved once by the host and handed to
///     <c>SceneRenderContext</c>. Compositor state, not frame state: the theme changes on a variant
///     switch, not on a tick.
/// </summary>
/// <param name="Background">Canvas fill.</param>
/// <param name="MinorGrid">512-unit grid lines.</param>
/// <param name="MajorGrid">Every fourth grid line.</param>
/// <param name="Label">Floor labels and marker initials.</param>
/// <param name="TeamT">T-side marker fill.</param>
/// <param name="TeamCt">CT-side marker fill.</param>
/// <param name="Neutral">Marker fill for a player on neither playing team.</param>
/// <param name="SightlineT">T-side could-see segment.</param>
/// <param name="SightlineCt">CT-side could-see segment.</param>
/// <param name="ConeT">T-side view-cone fill.</param>
/// <param name="ConeCt">CT-side view-cone fill.</param>
/// <param name="ConeNeutral">Neutral view-cone fill.</param>
/// <param name="RingShooting">Ring while shooting.</param>
/// <param name="RingDamage">Ring while taking damage.</param>
/// <param name="RingBlinded">Ring while blinded.</param>
/// <param name="RingDead">Ring when dead.</param>
/// <param name="Bomb">Bomb marker fill.</param>
/// <param name="BombTrack">The unfilled ring track behind both bomb arcs.</param>
/// <param name="BombDetonation">The depleting detonation arc.</param>
/// <param name="BombDefuse">The depleting defuse arc.</param>
/// <param name="Smoke">Smoke disc fill.</param>
/// <param name="SmokeStroke">Smoke disc outline.</param>
/// <param name="Fire">Inferno cell fill.</param>
/// <param name="TrailHe">HE grenade flight trail.</param>
/// <param name="TrailFlash">Flashbang flight trail.</param>
/// <param name="TrailSmoke">Smoke grenade flight trail.</param>
/// <param name="TrailMolotov">Molotov / incendiary flight trail.</param>
/// <param name="TrailDecoy">Decoy flight trail.</param>
/// <param name="MarkerRingT">T-side marker outline.</param>
/// <param name="MarkerRingCt">CT-side marker outline.</param>
/// <param name="MarkerRingNeutral">Neutral marker outline.</param>
/// <param name="Strokes">Stroke widths.</param>
public readonly record struct ScenePalette(
    SKColor Background,
    SKColor MinorGrid,
    SKColor MajorGrid,
    SKColor Label,
    SKColor TeamT,
    SKColor TeamCt,
    SKColor Neutral,
    SKColor SightlineT,
    SKColor SightlineCt,
    SKColor ConeT,
    SKColor ConeCt,
    SKColor ConeNeutral,
    SKColor RingShooting,
    SKColor RingDamage,
    SKColor RingBlinded,
    SKColor RingDead,
    SKColor Bomb,
    SKColor BombTrack,
    SKColor BombDetonation,
    SKColor BombDefuse,
    SKColor Smoke,
    SKColor SmokeStroke,
    SKColor Fire,
    SKColor TrailHe,
    SKColor TrailFlash,
    SKColor TrailSmoke,
    SKColor TrailMolotov,
    SKColor TrailDecoy,
    SKColor MarkerRingT,
    SKColor MarkerRingCt,
    SKColor MarkerRingNeutral,
    SceneStrokeWidths Strokes)
{
    /// <summary>
    ///     The Dark-variant fallbacks the pre-move <c>Playback2DViewport.BuildPalette</c> hard-coded,
    ///     colour for colour and in the same order. In B1 the App builds a palette from its theme tokens
    ///     and <c>CanvasPalette</c> becomes a factory over this; a test then asserts the two agree.
    /// </summary>
    public static readonly ScenePalette Dark = new(
        SKColor.Parse("#15181C"),
        SKColor.Parse("#22272E"),
        SKColor.Parse("#2E3742"),
        SKColor.Parse("#9AA4AF"),
        SKColor.Parse("#E0A030"),
        SKColor.Parse("#4A90D9"),
        SKColor.Parse("#888888"),
        SKColor.Parse("#70E0A030"),
        SKColor.Parse("#704A90D9"),
        SKColor.Parse("#3CE0A030"),
        SKColor.Parse("#3C4A90D9"),
        SKColor.Parse("#2C888888"),
        SKColor.Parse("#FFD400"),
        SKColor.Parse("#F44336"),
        SKColor.Parse("#FFFFFFFF"),
        SKColor.Parse("#555B62"),
        SKColor.Parse("#F03A2E"),
        SKColor.Parse("#40FFFFFF"),
        SKColor.Parse("#FF5040"),
        SKColor.Parse("#40C4FF"),
        SKColor.Parse("#66AEB6BD"),
        SKColor.Parse("#88C8CED4"),
        SKColor.Parse("#78FF6A1A"),
        SKColor.Parse("#FF5252"),
        SKColor.Parse("#FFE082"),
        SKColor.Parse("#B0BEC5"),
        SKColor.Parse("#FF7043"),
        SKColor.Parse("#81C784"),
        SKColor.Parse("#C8881F"),
        SKColor.Parse("#357ABD"),
        SKColor.Parse("#666666"),
        SceneStrokeWidths.Default);

    /// <summary>The team-coloured marker fill for a CS2 team number (2 = T, 3 = CT).</summary>
    public SKColor TeamFill(int team) => team switch
    {
        2 => TeamT,
        3 => TeamCt,
        _ => Neutral
    };
}

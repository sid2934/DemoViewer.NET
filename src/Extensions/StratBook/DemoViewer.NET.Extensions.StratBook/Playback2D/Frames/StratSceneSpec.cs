#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;

/// <summary>
///     Everything <see cref="StratFrameSource" /> needs to play a strat, on the strat frame clock.
///     Built by the App from a checked-out strat and the chosen branch path; the
///     strat JSON reader stays in the App, so nothing here knows the <c>.dvstrat.json</c> shape.
/// </summary>
/// <param name="Tracks">The path's token tracks. The source samples them and never edits them.</param>
/// <param name="Schedule">The path's step windows.</param>
/// <param name="Ink">
///     A private session over a <c>Reset</c> copy of the projected document, <b>never the live one</b>
///     (<c>SceneLayerCatalog.CreateSceneStack</c>'s rule for an export). The source does not draw it; the
///     caller passes it to the stack with <c>playback2d.annotations</c> named.
/// </param>
/// <param name="Labels">Per slot, the marker text and the team it draws in.</param>
/// <param name="MapName">The map, e.g. <c>de_mirage</c>.</param>
/// <param name="Radars">The bundle's decoded radar layers.</param>
/// <param name="MapBounds">The bundle's world rectangle, so the camera frames the map on frame 0.</param>
/// <param name="SectionHeights">The map's floor boundaries, or null for a single-level map.</param>
/// <param name="Utility">The path's throws, in any order.</param>
/// <param name="RoundSeconds">The strat's round length; the HUD clock counts down from it.</param>
/// <param name="StartTick">First strat tick rendered.</param>
/// <param name="EndTick">Last strat tick the range covers, inclusive.</param>
/// <param name="Fps">Output frame rate.</param>
/// <param name="Speed">Playback-rate multiplier; 1 is realtime.</param>
public sealed record StratSceneSpec(
    TokenTrackSet Tracks,
    StepSchedule Schedule,
    AnnotationSession Ink,
    IReadOnlyList<TokenLabel> Labels,
    string MapName,
    IReadOnlyList<MapRadarImage> Radars,
    WorldBounds MapBounds,
    IReadOnlyList<double>? SectionHeights,
    IReadOnlyList<UtilityCue> Utility,
    int RoundSeconds,
    int StartTick,
    int EndTick,
    int Fps,
    double Speed)
{
    /// <summary>
    ///     Whether a moving token shows its way ahead as a faint line (<see cref="Scene2DFrame.Routes" />): set when
    ///     the tracks follow the map's nav. Off draws exactly what a strat drew before routing.
    /// </summary>
    public bool Routes { get; init; }

    /// <summary>
    ///     Whether the clock counts up from the strat's trigger rather than down from <see cref="RoundSeconds" />: tick 0
    ///     reads 0:00 and each second after it one more.
    /// </summary>
    public bool CountsUp { get; init; }
}

/// <summary>What one token slot draws as: its marker text and its side.</summary>
/// <param name="Slot">One of <see cref="TokenSlots.All" />.</param>
/// <param name="Label">The slot's resolved initials; null or empty draws the slot letter instead.</param>
/// <param name="Team">2 for T, 3 for CT, as <see cref="PlayerMarker.Team" /> carries it.</param>
public readonly record struct TokenLabel(string Slot, string? Label, int Team);

/// <summary>A grenade going off on the strat frame clock, and the flight that brought it there.</summary>
/// <param name="Tick">The moment it goes off: its effect starts here.</param>
/// <param name="Kind">The grenade.</param>
/// <param name="X">World X of the landing.</param>
/// <param name="Y">World Y of the landing.</param>
/// <param name="Z">World Z the effect is drawn at, which picks its floor pane.</param>
public readonly record struct UtilityCue(int Tick, GrenadeKind Kind, float X, float Y, float Z)
{
    /// <summary>The thrower's side, 2 = T and 3 = CT, which colours the flight; 0 colours it by kind.</summary>
    public int Team { get; init; }

    /// <summary>
    ///     The flight from release to rest, ticks ascending, at least two points; null draws no projectile.
    ///     Its last tick is at or before <see cref="Tick" />.
    /// </summary>
    public IReadOnlyList<FlightPoint>? Flight { get; init; }

    /// <summary>The release tick: the flight's first, else <see cref="Tick" />.</summary>
    public int ThrowTick => Flight is { Count: > 0 } flight ? flight[0].Tick : Tick;
}

/// <summary>One point of a strat grenade's flight.</summary>
/// <param name="Tick">Strat tick the projectile is here.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z, which picks the floor pane.</param>
public readonly record struct FlightPoint(int Tick, float X, float Y, float Z);

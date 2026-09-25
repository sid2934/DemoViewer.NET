#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Frames;

/// <summary>
///     Everything <see cref="StratFrameSource" /> needs to play a strat, on the strat frame clock
///     (step-authoring.md §3.6). Built by the App from a checked-out strat and the chosen branch path; the
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
/// <param name="Utility">Landings from steps that carry a landing point, in any order.</param>
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
    double Speed);

/// <summary>What one token slot draws as: its marker text and its side.</summary>
/// <param name="Slot">One of <see cref="TokenSlots.All" />.</param>
/// <param name="Label">The slot's resolved initials; null or empty draws the slot letter instead.</param>
/// <param name="Team">2 for T, 3 for CT, as <see cref="PlayerMarker.Team" /> carries it.</param>
public readonly record struct TokenLabel(string Slot, string? Label, int Team);

/// <summary>A grenade landing on the strat frame clock, from a step's <c>utility.landing</c>.</summary>
/// <param name="Tick">The step's tick: the moment it lands.</param>
/// <param name="Kind">The grenade. Only a smoke and a molotov leave anything on the floor.</param>
/// <param name="X">World X of the landing.</param>
/// <param name="Y">World Y of the landing.</param>
/// <param name="Z">World Z the effect is drawn at, which picks its floor pane.</param>
public readonly record struct UtilityCue(int Tick, GrenadeKind Kind, float X, float Y, float Z);

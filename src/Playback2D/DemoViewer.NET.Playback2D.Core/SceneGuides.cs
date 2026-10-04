#region

using System.Numerics;

#endregion

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>A token drawn hollow: where a dragged token stood, or where a step sends one.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">Marker Z, which picks its floor pane.</param>
/// <param name="Team">2 = T, 3 = CT.</param>
/// <param name="Label">The token's letter, or empty.</param>
public readonly record struct GuideToken(float X, float Y, float Z, int Team, string Label);

/// <summary>Where a step sends one token, and the way there.</summary>
/// <param name="At">The arrival.</param>
/// <param name="Route">From where the token leaves to the arrival, world space.</param>
/// <param name="Earlier">An arrival the playhead has passed: drawn faint, with no route.</param>
public sealed record GuidePin(GuideToken At, IReadOnlyList<GrenadeTrailPoint> Route, bool Earlier = false);

/// <summary>
///     The strat canvas's editing guides over a frame: a drag's ghost (where the token stood, the dashed route it would
///     take, the place under the pointer) and the selected step's destination pins and via marks. Not part of a frame, so
///     an export, a fixture and a golden never carry one: the host hands them to <c>GuideLayer</c> directly.
/// </summary>
public sealed class SceneGuides
{
    /// <summary>No guides.</summary>
    public static readonly SceneGuides None = new();

    /// <summary>Where the dragged token stood when the drag began; null with no drag.</summary>
    public GuideToken? Ghost { get; init; }

    /// <summary>The route the drop would store, dashed; empty for none.</summary>
    public IReadOnlyList<GrenadeTrailPoint> GhostRoute { get; init; } = [];

    /// <summary>The ghost route's side colour, 2 = T and 3 = CT.</summary>
    public int GhostTeam { get; init; }

    /// <summary>The place under the pointer: its outline edges in world space; empty for none.</summary>
    public IReadOnlyList<(Vector2 A, Vector2 B)> DropOutline { get; init; } = [];

    /// <summary>The outline's marker Z, for its floor pane.</summary>
    public float DropOutlineZ { get; init; }

    /// <summary>Where the selected step sends each token it moves.</summary>
    public IReadOnlyList<GuidePin> Pins { get; init; } = [];

    /// <summary>The selected step's vias, as small marks on its routes.</summary>
    public IReadOnlyList<GrenadeTrailPoint> ViaMarks { get; init; } = [];

    /// <summary>Whether there is anything to draw.</summary>
    public bool IsEmpty => Ghost is null && GhostRoute.Count == 0 && DropOutline.Count == 0 && Pins.Count == 0 && ViaMarks.Count == 0;
}

#region

using DemoViewer.NET.Playback2D.Core.Query;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Overlay;

/// <summary>
///     One alive player at one matched state: a world position and the side it was on. What the
///     heatmap stacks; nothing else about the player survives into the overlay, because the picture is
///     "where they were", not "who they were".
/// </summary>
/// <param name="WorldX">World X.</param>
/// <param name="WorldY">World Y.</param>
/// <param name="WorldZ">World Z, kept for the pane assignment on a multi-floor map.</param>
/// <param name="Side">The side, so the two teams' density can be tinted apart.</param>
public readonly record struct OverlayPoint(float WorldX, float WorldY, float WorldZ, QuerySide Side);

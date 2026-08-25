namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Constants the pre-v2 <c>Playback2DViewport</c> held as private consts and that more than one
///     layer now needs. Values are unchanged — every one of them is visible in a golden image.
/// </summary>
public static class SceneDefaults
{
    /// <summary>Half-extent of the fixed rectangle drawn before any position is observed.</summary>
    public const double WorldExtent = 3000;

    /// <summary>Grid spacing in world units — one CS2 cell width, matching <c>PositionUtil.CellWidth</c>.</summary>
    public const double GridStepWorld = 512;

    /// <summary>The baked radar is drawn slightly muted so bright markers pop.</summary>
    public const double RadarOpacity = 0.9;

    /// <summary>Marker disc radius in screen pixels.</summary>
    public const float MarkerRadius = 9f;

    /// <summary>How far past the disc edge the yaw stub reaches, in screen pixels.</summary>
    public const float MarkerHeadingLength = 8f;

    /// <summary>Marker label em size.</summary>
    public const float MarkerLabelSize = 10f;

    /// <summary>Floor label em size.</summary>
    public const float FloorLabelSize = 11f;

    /// <summary>Grid lines per axis past which the grid bails out entirely (a zoomed-way-out guard).</summary>
    public const int MaxGridLines = 400;
}

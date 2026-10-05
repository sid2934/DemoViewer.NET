#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Cameras;

/// <summary>
///     One camera behaviour: given a pane and the frame, where should that pane's camera be heading?
///     <para>
///         A rig is <b>pure</b>: it computes a target and never touches the camera. <c>CameraAdvancer</c>
///         owns the lerp, the settle test and the "manual override wins" rule, so a new rig cannot
///         accidentally reintroduce the pre-v2 habit of mutating camera state inside a draw pass.
///     </para>
///     <para>
///         Returning <c>null</c> means "no target this frame: hold" (no alive players on this level, a
///         followed slot with no marker, or a mode that is static by design). That is the pre-v2
///         <c>TryComputeTarget</c> returning false, and it is not an error.
///     </para>
/// </summary>
public interface ICameraRig
{
    /// <summary>Stable key, for diagnostics and export camera scripts.</summary>
    string Id { get; }

    /// <summary>The transform this pane's camera should be lerping toward, or null to hold.</summary>
    /// <param name="pane">The pane being advanced. Read-only to a rig.</param>
    /// <param name="frame">The frame being advanced to.</param>
    ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame);
}

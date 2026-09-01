#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Cameras;

/// <summary>
///     A policy that owns every pane's camera for a whole run, applied once per frame <b>after</b> pane
///     reconciliation and <b>before</b> the submission snapshot is captured.
///     <para>
///         The generalisation of <c>HeadlessSceneRenderer.Camera</c> (which pins one transform to every
///         pane): B4's export camera scripts need a different transform per level, and a follow script
///         needs to step them, and both must land inside the same <c>Advance</c> call. A camera written
///         after the snapshot is one frame late, and a camera written before reconciliation is discarded
///         by it.
///     </para>
///     <para>
///         A policy is called on the frame-producing thread and mutates <c>LevelPane.Camera</c> in place.
///         It must not allocate per frame (design §6).
///     </para>
/// </summary>
public interface IPaneCameraPolicy
{
    /// <summary>Writes this frame's cameras onto the panes.</summary>
    /// <param name="panes">The reconciled pane set.</param>
    /// <param name="frame">The frame being advanced to.</param>
    /// <param name="time">The injected clock.</param>
    void Apply(PaneSet panes, Scene2DFrame frame, in SceneTime time);
}

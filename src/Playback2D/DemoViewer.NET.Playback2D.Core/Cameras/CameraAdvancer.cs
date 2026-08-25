#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Cameras;

/// <summary>
///     Steps every pane's camera toward its rig's target once per rendered frame. Port of
///     <c>AdvanceCameras</c> (viewport lines 606-641), verbatim including its two termination rules:
///     a manual-override pane is skipped entirely, and a pane close enough to its target snaps the
///     residual so the self-terminating render loop can actually stop.
/// </summary>
public static class CameraAdvancer
{
    /// <summary>
    ///     Exponential-decay rate. Higher is snappier; frame-rate independent because the step is
    ///     <c>1 - exp(-rate · dt)</c> rather than a fixed fraction.
    /// </summary>
    public const double LerpResponse = 7.0;

    /// <summary>
    ///     Advances every pane. Returns true while any pane is still settling, which is what keeps the
    ///     host's animation-frame loop armed.
    /// </summary>
    /// <param name="panes">The panes to advance.</param>
    /// <param name="frame">The frame being advanced to.</param>
    /// <param name="time">The injected clock; only <c>DeltaSeconds</c> is read.</param>
    public static bool Advance(PaneSet panes, Scene2DFrame frame, in SceneTime time)
    {
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(frame);

        double t = 1 - Math.Exp(-LerpResponse * time.DeltaSeconds);
        bool anyMoving = false;

        IReadOnlyList<LevelPane> list = panes.Panes;
        for (int i = 0; i < list.Count; i++)
        {
            LevelPane pane = list[i];
            if (pane.Camera.ManualOverride)
            {
                continue; // a manual gesture pauses this pane's auto camera until a mode is re-picked.
            }

            if (pane.Rig.ComputeTarget(pane, frame) is not { } target)
            {
                continue; // no target this frame — hold.
            }

            if (pane.Camera.IsSettledAt(target))
            {
                pane.Camera.Current = target; // snap the residual so the loop can terminate.
                continue;
            }

            pane.Camera = pane.Camera.StepToward(target, t);
            anyMoving = true;
        }

        return anyMoving;
    }
}

/// <summary>
///     Maps the App's camera-mode vocabulary onto rigs. Lives in Core so export camera scripts (B4)
///     and the CLI can build the same rigs without the App's <c>CameraMode</c> enum.
/// </summary>
public static class CameraRigFactory
{
    /// <summary>The four rig kinds B1 ships.</summary>
    public enum Kind
    {
        /// <summary>One-shot fit, static thereafter.</summary>
        Fit,

        /// <summary>Continuously frame the alive players on this level.</summary>
        Alive,

        /// <summary>Continuously frame the map's playable bounds.</summary>
        Map,

        /// <summary>Continuously centre on one roster slot.</summary>
        FollowPlayer
    }

    /// <summary>
    ///     The rig for a mode. <see cref="Kind.Fit" /> deliberately returns <see cref="ManualRig" />:
    ///     the one-shot fit is applied by <c>PaneSet.FitAll</c>, and the rig's job afterwards is to hold
    ///     (plan decision D-3).
    /// </summary>
    /// <param name="kind">The requested mode.</param>
    /// <param name="followSlot">The slot for <see cref="Kind.FollowPlayer" />.</param>
    /// <param name="deadzoneHalfWorld">Follow deadzone half-extent; 0 reproduces the pre-v2 feel.</param>
    public static ICameraRig For(Kind kind, int followSlot = -1, double deadzoneHalfWorld = 180) => kind switch
    {
        Kind.Alive => FitAliveRig.Instance,
        Kind.Map => FitMapRig.Instance,
        Kind.FollowPlayer => new FollowPlayerRig(followSlot, deadzoneHalfWorld: deadzoneHalfWorld),
        _ => ManualRig.Instance
    };
}

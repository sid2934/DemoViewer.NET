#region

using System.Collections.Immutable;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     Turns a <see cref="CameraScript" /> into the panes' cameras for one export frame.
///     <para>
///         It runs <b>after</b> pane reconciliation and <b>before</b> the submission snapshot is taken,
///         which is the only ordering that lets a level appearing mid-export get its scripted camera in
///         the same frame rather than one frame late.
///     </para>
///     <para>
///         <b>Allocation-free per frame</b> (design §6): the follow rig is built once in the constructor
///         and re-aimed, and every pass over panes and markers is an index loop.
///     </para>
/// </summary>
public sealed class CameraScriptResolver : IPaneCameraPolicy
{
    private readonly CameraScript _script;
    private readonly FollowPlayerRig? _rig;

    /// <summary>Creates a resolver for one export's script.</summary>
    /// <param name="script">The camera behaviour for the whole run.</param>
    public CameraScriptResolver(CameraScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = script;

        if (script is CameraScript.FollowPlayer follow)
        {
            // Slot is re-aimed every frame from the SteamId; -1 until the target first appears, which is
            // exactly the rig's "hold" state.
            _rig = new FollowPlayerRig(-1, deadzoneHalfWorld: follow.DeadzoneHalfExtentWorld);
        }
    }

    /// <summary>
    ///     The roster slot the follow script resolved to on the last <see cref="Apply" />, or -1 when the
    ///     script is not a follow or the target is not in this frame. Diagnostics and tests.
    /// </summary>
    public int ResolvedSlot => _rig?.Slot ?? -1;

    /// <summary>
    ///     Whether the last <see cref="Apply" /> moved any camera. False means every pane held — which is
    ///     the correct answer for a fixed script, and for a follow whose target is unresolvable.
    /// </summary>
    public bool MovedAnyCamera { get; private set; }

    /// <summary>Writes this frame's cameras onto the panes.</summary>
    /// <param name="panes">The reconciled pane set. Cameras are mutated in place.</param>
    /// <param name="frame">The frame being exported; markers resolve a follow target.</param>
    /// <param name="time">The injected clock. Only <c>DeltaSeconds</c> and <c>IsDiscontinuity</c> are read.</param>
    public void Apply(PaneSet panes, Scene2DFrame frame, in SceneTime time)
    {
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(frame);

        MovedAnyCamera = false;

        switch (_script)
        {
            case CameraScript.Fixed fixedScript:
                ApplyFixed(panes, fixedScript.PaneTransforms);
                break;
            case CameraScript.MirrorLiveView mirror:
                ApplyCaptured(panes, mirror.Panes);
                break;
            case CameraScript.FollowPlayer follow:
                ApplyFollow(panes, frame, in time, follow.SteamId);
                break;
        }
    }

    // Fixed and MirrorLiveView are the same behaviour once captured (plan D12): pin the stored transform,
    // re-fitted to THIS export's pane size so a 1080p export of a 700 px pane keeps the same world
    // framing rather than the same pixel scale.
    private void ApplyFixed(PaneSet panes, IReadOnlyDictionary<MapLevelId, ViewportTransform> transforms)
    {
        IReadOnlyList<LevelPane> list = panes.Panes;
        for (int i = 0; i < list.Count; i++)
        {
            LevelPane pane = list[i];
            if (!transforms.TryGetValue(pane.LevelId, out ViewportTransform stored))
            {
                continue; // a level the script says nothing about keeps the fit its pane was born with.
            }

            Pin(pane, stored);
        }
    }

    private void ApplyCaptured(PaneSet panes, ImmutableArray<PaneCameraSnapshot> captured)
    {
        IReadOnlyList<LevelPane> list = panes.Panes;
        for (int i = 0; i < list.Count; i++)
        {
            LevelPane pane = list[i];
            for (int j = 0; j < captured.Length; j++)
            {
                PaneCameraSnapshot snapshot = captured[j];
                if (snapshot.LevelId != pane.LevelId)
                {
                    continue;
                }

                Pin(pane, snapshot.Transform);
                break;
            }
        }
    }

    private void ApplyFollow(PaneSet panes, Scene2DFrame frame, in SceneTime time, ulong steamId)
    {
        FollowPlayerRig rig = _rig!;

        if (time.IsDiscontinuity)
        {
            // After a seek the deadzone must not hold the camera where the player used to be.
            rig.ResetDeadzone();
        }

        rig.Slot = SlotForSteamId(frame, steamId);

        // The same exponential-decay step the interactive path uses, driven by the FIXED timestep rather
        // than a real frame delta — so an export looks like the live view and two runs agree exactly.
        double t = 1 - Math.Exp(-CameraAdvancer.LerpResponse * time.DeltaSeconds);

        IReadOnlyList<LevelPane> list = panes.Panes;
        for (int i = 0; i < list.Count; i++)
        {
            LevelPane pane = list[i];

            // A scripted export owns the camera outright; a pane that was born with ManualOverride (or
            // inherited one from a MirrorLiveView capture in an earlier run) must not silently opt out.
            pane.Camera.ManualOverride = false;

            if (rig.ComputeTarget(pane, frame) is not { } target)
            {
                continue; // unresolvable or dead-and-off-this-level → hold the last transform.
            }

            if (pane.Camera.IsSettledAt(target))
            {
                pane.Camera.Current = target; // snap the residual, so a still scene is bit-stable.
                continue;
            }

            pane.Camera = pane.Camera.StepToward(target, t);
            MovedAnyCamera = true;
        }

        panes.SyncCameraEpochs();
    }

    private void Pin(LevelPane pane, ViewportTransform stored)
    {
        ViewportTransform fitted = stored.WithViewport(pane.ViewportRect.Width, pane.ViewportRect.Height);
        if (!pane.Camera.IsSettledAt(fitted))
        {
            MovedAnyCamera = true;
        }

        pane.Camera.Current = fitted;
        pane.Camera.ManualOverride = true; // a scripted camera is data, not a target to lerp toward.
        pane.SyncCameraEpoch();
    }

    private static int SlotForSteamId(Scene2DFrame frame, ulong steamId)
    {
        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].SteamId == steamId)
            {
                return markers[i].Slot;
            }
        }

        return -1;
    }
}

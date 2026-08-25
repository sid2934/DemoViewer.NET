namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     The injected clock for one rendered scene (design §5.1). Every motion in the pipeline —
///     marker smoothing, camera lerps, ink fades, trail decay — consumes <see cref="DeltaSeconds" /> or
///     <see cref="Tick" />, never a wall clock, so an interactive RAF loop and a fixed-timestep export
///     produce identical motion.
///     <para>
///         <see cref="Tick" /> is the <b>DV frame clock</b> (<c>DemoFrame.ServerTick</c>), never a CS2
///         tick: the LiveSync servo bends the playhead and tick mapping is a per-demo affair, so
///         annotations and timelines never touch it.
///     </para>
/// </summary>
/// <param name="Tick">The DV frame clock for this scene.</param>
/// <param name="FrameIndex">Index into the demo's frame list — the timeline's x-axis domain.</param>
/// <param name="DemoSeconds">ServerTick / tickRate − clockBase.</param>
/// <param name="DeltaSeconds">Real frame dt when interactive; exactly 1/fps on export.</param>
/// <param name="IsDiscontinuity">A seek or jump — layers reset smoothing and clear trails.</param>
public readonly record struct SceneTime(
    int Tick,
    int FrameIndex,
    double DemoSeconds,
    double DeltaSeconds,
    bool IsDiscontinuity);

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>Which half of a layer's frame is being measured.</summary>
public enum LayerPhase
{
    /// <summary>The mutating pre-render step: <see cref="ISceneLayer.Advance" />.</summary>
    Advance = 0,

    /// <summary>The pure draw: <see cref="ISceneLayer.Render" />, once per pane.</summary>
    Render = 1
}

/// <summary>What the picture cache did for one layer draw.</summary>
public enum PictureCacheOutcome
{
    /// <summary>
    ///     The layer drew straight to the canvas: <see cref="LayerCacheHint.Dynamic" />, or caching
    ///     switched off wholesale. Neither a hit nor a miss: there was no cache in the path.
    /// </summary>
    Uncached = 0,

    /// <summary>A miss: the layer re-recorded its <c>SKPicture</c> this frame.</summary>
    Recorded = 1,

    /// <summary>A hit: a cached <c>SKPicture</c> was replayed.</summary>
    Replayed = 2
}

/// <summary>
///     The optional per-layer measurement seam on <see cref="SceneCompositor" /> (plan
///     <c>P1-perf-instrumentation</c> §3.1). Null on the default path, where the whole mechanism costs
///     one field read and one predicted branch per layer per phase.
///     <para>
///         <b>There is no clock in this interface, and that is the point.</b>
///         <see cref="System.Diagnostics.Stopwatch" /> is banned outright in Core, the entire type, not
///         just its timestamp, because a render that can observe wall time is a render that cannot be
///         reproduced (design §5.1), and <c>BannedApiTests</c> enforces it against compiled IL. So the
///         compositor reports <i>events</i>: began, ended, cache did this. Whoever implements this
///         interface does the timestamping, from a namespace allowed to own a stopwatch:
///         <c>Pipeline.Benchmarking</c>, exactly where the benchmark harness already lives for exactly
///         the same reason.
///     </para>
///     <para>
///         <b>Contract.</b> Calls are strictly nested per phase and arrive on one thread at a time.
///         <c>index</c> is the layer's position in <see cref="SceneCompositor.Layers" />,
///         which is stable for the lifetime of a stack; <c>layerId</c> is passed on every
///         <see cref="BeginLayer" /> so an implementation can relabel a slot if the stack is rebuilt
///         mid-run. A layer drawn into several panes produces several
///         <see cref="BeginLayer" />/<see cref="EndLayer" /> pairs in one frame.
///     </para>
/// </summary>
public interface ISceneProfiler
{
    /// <summary>One layer's phase is starting.</summary>
    /// <param name="index">The layer's position in <see cref="SceneCompositor.Layers" />.</param>
    /// <param name="layerId">The layer's stable id, for labelling.</param>
    /// <param name="phase">Which half of the frame.</param>
    void BeginLayer(int index, string layerId, LayerPhase phase);

    /// <summary>The matching close of a <see cref="BeginLayer" />.</summary>
    /// <param name="index">The layer's position in <see cref="SceneCompositor.Layers" />.</param>
    /// <param name="phase">Which half of the frame.</param>
    void EndLayer(int index, LayerPhase phase);

    /// <summary>
    ///     What the picture cache did for the draw just measured. Reported from the branch the
    ///     compositor already takes, so it computes nothing new.
    /// </summary>
    /// <param name="index">The layer's position in <see cref="SceneCompositor.Layers" />.</param>
    /// <param name="outcome">Hit, miss, or no cache in the path.</param>
    void RecordPicture(int index, PictureCacheOutcome outcome);
}

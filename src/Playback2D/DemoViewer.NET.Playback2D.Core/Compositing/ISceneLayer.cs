#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     One drawable band of the scene (design §5.2, verbatim plus <see cref="ContentVersion" />).
///     <para>
///         <b>The Advance/Render purity split is the point.</b> The pre-v2 control mutated camera and
///         marker state <i>inside</i> <c>Control.Render</c>. Here <see cref="Advance" /> runs on the UI
///         thread before submission and owns all mutation; <see cref="Render" /> is pure and consumes
///         only the immutable frame plus the camera snapshot captured at submission.
///     </para>
/// </summary>
public interface ISceneLayer : IDisposable
{
    /// <summary>Stable key — feature gates, settings and the layer panel all persist it. Never renamed.</summary>
    string Id { get; }

    /// <summary>The coarse z-band this layer draws in.</summary>
    LayerSlot Slot { get; }

    /// <summary>Sort key within <see cref="Slot" />.</summary>
    int Order { get; }

    /// <summary>How cacheable this layer's drawing is.</summary>
    LayerCacheHint Cache { get; }

    /// <summary>Whether the compositor advances and draws this layer. The overlay toggles map onto this.</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    ///     Bumped by the layer whenever its cacheable content changes; ignored when <see cref="Cache" />
    ///     is <see cref="LayerCacheHint.Dynamic" />. Declared here rather than added by B1 so there is one
    ///     interface shape for the whole track.
    /// </summary>
    int ContentVersion { get; }

    /// <summary>
    ///     UI-thread pre-render step: the only place a layer may mutate. Returns true to keep the
    ///     self-terminating render loop armed (i.e. this layer is still animating).
    /// </summary>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="frame">
    ///     The frame being advanced to. Valid only for this call — see <see cref="Scene2DFrame" />.
    /// </param>
    bool Advance(in SceneTime time, Scene2DFrame frame);

    /// <summary>
    ///     Pure draw. Reads caches built in <see cref="Advance" /> and must not mutate; called once per
    ///     pane, so mutating here would multiply-apply on a multi-level layout.
    /// </summary>
    /// <param name="canvas">The pane's canvas, already clipped and translated to pane-local space.</param>
    /// <param name="ctx">The pane's render context.</param>
    void Render(SKCanvas canvas, SceneRenderContext ctx);
}

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Why a scene is being rendered (design §5.1).
///     <para>
///         <b>RESERVED: no layer reads this, and the three values render identically.</b> The value is
///         threaded end to end (<c>SceneSubmission.Purpose</c> → <c>SceneCompositor</c> →
///         <c>SceneRenderContext.Purpose</c>, which every <c>ISceneLayer.Draw</c> receives), but nothing
///         branches on it yet: design §5.1's "layers may trade quality for latency on it" is an intent,
///         not an implemented contract.
///     </para>
///     <para>
///         Kept as the seam a future fidelity/latency split would attach to, rather than re-threading
///         three types through Core, Pipeline, the CLI and the export session later. <c>RenderPurposeTests</c>
///         asserts that <see cref="Interactive" />, <see cref="Export" /> and <see cref="Thumbnail" />
///         render byte-identical pixels for the same scene, so this paragraph and the code cannot drift
///         apart.
///     </para>
/// </summary>
public enum RenderPurpose
{
    /// <summary>
    ///     On-screen playback. INTENDED to let latency win over fidelity; today identical to the others.
    ///     What <c>Scene2DHost</c> submits.
    /// </summary>
    Interactive,

    /// <summary>
    ///     Offscreen video/still export: fixed timestep, INTENDED to let fidelity win. What
    ///     <c>SceneExportSession</c>, <c>HeadlessSceneRenderer</c>, <c>dv2d render|golden</c> and the
    ///     pipeline benchmark submit. Today identical to the others.
    /// </summary>
    Export,

    /// <summary>
    ///     A small preview still: the cheapest acceptable output. <b>Never produced.</b> Nothing in
    ///     production or in the test suite submits it; there is no thumbnail surface. Left declared so
    ///     the vocabulary is complete rather than re-invented, and named here so the next reader does not
    ///     go looking for the code that makes one.
    /// </summary>
    Thumbnail
}

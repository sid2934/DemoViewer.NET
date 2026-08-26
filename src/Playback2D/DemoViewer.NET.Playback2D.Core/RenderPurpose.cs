namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Why a scene is being rendered (design §5.1).
///     <para>
///         <b>RESERVED — no layer reads this, and the three values render identically.</b> The value is
///         threaded end to end (<c>SceneSubmission.Purpose</c> → <c>SceneCompositor</c> →
///         <c>SceneRenderContext.Purpose</c>, which every <c>ISceneLayer.Draw</c> receives) and
///         <c>SceneCompositor</c>'s copy into the context is the only production READ of it anywhere.
///         Design §5.1's "layers may trade quality for latency on it" describes an intent, not an
///         implemented contract, and D6 finding 28 is that the doc read as though it were shipped.
///     </para>
///     <para>
///         <b>Why it is kept rather than deleted.</b> It costs one enum field on a submission struct, and
///         it is the seam a fidelity/latency split would need at exactly the place a layer can act on it.
///         Deleting it would mean re-threading three types through Core, Pipeline, the CLI and the export
///         session to get it back. What it must not do is keep claiming to work:
///         <c>RenderPurposeTests</c> asserts that the same scene at <see cref="Interactive" />,
///         <see cref="Export" /> and <see cref="Thumbnail" /> produces byte-identical pixels, so this
///         paragraph and the code cannot drift apart — the commit that gives a layer a real
///         purpose-dependent branch is the commit that has to rewrite that test and this doc together.
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
    ///     Offscreen video/still export — fixed timestep, INTENDED to let fidelity win. What
    ///     <c>SceneExportSession</c>, <c>HeadlessSceneRenderer</c>, <c>dv2d render|golden</c> and the
    ///     pipeline benchmark submit. Today identical to the others.
    /// </summary>
    Export,

    /// <summary>
    ///     A small preview still — the cheapest acceptable output. <b>Never produced.</b> Nothing in
    ///     production or in the test suite submits it; there is no thumbnail surface. Left declared so
    ///     the vocabulary is complete rather than re-invented, and named here so the next reader does not
    ///     go looking for the code that makes one.
    /// </summary>
    Thumbnail
}

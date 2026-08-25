namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     How a consumer wants its render backend chosen (plans/C2-gpu-provider.md §6.2). This is a
///     <i>preference</i>, not a backend: what actually gets used is
///     <see cref="RenderSurfaceProbe.Backend" />, because a GPU that is asked for but absent must still
///     produce a working renderer.
///     <para>
///         The highest-precedence source wins: explicit API argument → CLI flag →
///         <c>DV2D_RENDER_BACKEND</c> → the persisted setting → auto-probe. An operator standing at a
///         terminal beats a stored preference, and CI sets the environment variable expecting it to beat
///         whatever a settings file says. <see cref="RenderBackendPreferenceParser.Resolve" /> applies
///         that chain.
///     </para>
/// </summary>
public enum RenderBackendPreference
{
    /// <summary>Probe; use the GPU if it works, CPU otherwise. The default everywhere.</summary>
    Auto,

    /// <summary>Never probe the GPU. <c>--cpu</c>, <c>DV2D_RENDER_BACKEND=cpu</c>.</summary>
    ForceCpu,

    /// <summary>Probe the GPU first and fall back to CPU silently. <c>--gpu</c>, <c>…=gpu</c>.</summary>
    PreferGpu,

    /// <summary>
    ///     Probe the GPU and <b>throw</b> if it is unavailable. Reachable only from the API or
    ///     <c>--backend force-gpu</c>, so a CI lane can assert that it really exercised the GPU path
    ///     instead of silently measuring the software one.
    /// </summary>
    ForceGpu
}

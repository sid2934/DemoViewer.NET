#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     The once-per-process backend decision, <b>as data</b> (plans/C2-gpu-provider.md §6.2). A probe
///     never throws and never fails a render: "there is no GPU here" is an ordinary answer, carried in
///     <see cref="Reason" /> so a log line or a bug report can say <i>why</i>.
///     <para>
///         <see cref="Renderer" /> is the field that catches the nastiest failure mode: ANGLE loading
///         successfully but rendering through WARP on a machine that has a real GPU, which looks like a
///         win in the log and is a 20× loss in the numbers (plan §10 R2).
///     </para>
/// </summary>
/// <param name="Backend">The backend that will actually be used.</param>
/// <param name="GpuAvailable">Whether a GPU-backed provider can be created in this process.</param>
/// <param name="Reason">
///     Why this decision was reached: <c>angle-d3d11</c>, <c>egl-surfaceless</c>,
///     <c>egl-default-display</c>, <c>no-egl-library: …</c>, <c>browser</c>, <c>macos-deferred</c>,
///     <c>forced-cpu</c>, <c>all-backends-failed: …</c>.
/// </param>
/// <param name="Renderer"><c>GL_RENDERER</c>, when a GL context was actually made.</param>
/// <param name="Vendor"><c>GL_VENDOR</c>, when a GL context was actually made.</param>
/// <param name="Version"><c>GL_VERSION</c>, when a GL context was actually made.</param>
/// <param name="Duration">How long the probe took: a slow probe is itself a diagnosis.</param>
public readonly record struct RenderSurfaceProbe(
    RenderBackend Backend,
    bool GpuAvailable,
    string Reason,
    string? Renderer,
    string? Vendor,
    string? Version,
    TimeSpan Duration)
{
    /// <summary>Renderer strings known to mean "this is a software rasterizer wearing a GPU's name".</summary>
    private static readonly string[] _softwareRenderers =
    [
        "llvmpipe", "softpipe", "swiftshader", "microsoft basic render driver", "warp", "d3d11 warp",
        "generic renderer", "gdi generic"
    ];

    /// <summary>
    ///     Whether <see cref="Renderer" /> names a known software rasterizer. Correctness suites still
    ///     run against these, a WARP or llvmpipe lane is a real exercise of the GPU code path, but a
    ///     throughput assertion against one measures nothing, so it skips instead (plan §7.2).
    /// </summary>
    public bool IsSoftwareRenderer =>
        Renderer is { Length: > 0 } name &&
        _softwareRenderers.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>The single line a consumer logs. One probe, one line, everything a bug report needs.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"[render] backend={Backend} gpuAvailable={GpuAvailable} reason={Reason} " +
        $"renderer='{Renderer ?? "-"}' vendor='{Vendor ?? "-"}' version='{Version ?? "-"}' " +
        $"probe={Duration.TotalMilliseconds:F0}ms");
}

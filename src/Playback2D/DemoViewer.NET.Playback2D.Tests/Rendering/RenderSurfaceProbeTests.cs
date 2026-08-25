#region

using DemoViewer.NET.Playback2D.Core.Rendering;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     The probe's behavioural contract (plans/C2-gpu-provider.md §7.1): once per process, thread-safe,
///     one log line, and <b>never an exception</b> — "no GPU here" is an answer, not a failure.
///     <para>
///         Shares the <see cref="ProbeSerialization.Key" /> constraint with every other suite that
///         touches the process-wide probe cache or the render-backend environment variables. The cache
///         and the variables are global state; two of these classes running at once would be testing
///         each other.
///     </para>
/// </summary>
[NotInParallel(ProbeSerialization.Key)]
public class RenderSurfaceProbeTests
{
    [Test]
    public async Task Probe_IsIdempotent_AndLogsExactlyOnce()
    {
        using ProbeEnvironment env = ProbeEnvironment.Clean();

        List<string> lines = [];
        RenderSurfaceProbe first = RenderSurfaceProviderFactory.Probe(lines.Add);
        RenderSurfaceProbe second = RenderSurfaceProviderFactory.Probe(lines.Add);

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(lines).HasCount().EqualTo(1);
        await Assert.That(lines[0]).Contains("backend=");
        await Assert.That(lines[0]).Contains("reason=");
    }

    /// <summary>
    ///     The escape hatch pointed at nothing. An explicit <c>DV2D_ANGLE_LIBRARY</c> is the <i>only</i>
    ///     candidate when it is set — falling through to the shipped ANGLE would make "test against this
    ///     other build" silently test the default one, which is worse than failing.
    /// </summary>
    [Test]
    public async Task Probe_NeverThrows_WhenTheEglLibraryIsMissing()
    {
        using ProbeEnvironment env = ProbeEnvironment.WithMissingEglLibrary();

        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.Probe();

        await Assert.That(probe.Backend).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(probe.GpuAvailable).IsFalse();
        await Assert.That(probe.Reason).StartsWith("no-egl-library");
        await Assert.That(probe.Renderer).IsNull();
    }

    [Test]
    public async Task Create_ForceCpu_DoesNotProbeGpu()
    {
        using ProbeEnvironment env = ProbeEnvironment.Clean();

        List<string> lines = [];
        using IRenderSurfaceProvider provider =
            RenderSurfaceProviderFactory.Create(RenderBackendPreference.ForceCpu, lines.Add);

        await Assert.That(provider.Backend).IsEqualTo(RenderBackend.CpuRaster);
        // Nothing logged means nothing probed: Probe() is the only thing that writes to this callback,
        // and it writes on its first call.
        await Assert.That(lines).IsEmpty();
        await Assert.That(RenderSurfaceProviderFactory.Probe(lines.Add).Reason).IsNotNull();
        await Assert.That(lines).HasCount().EqualTo(1);
    }

    [Test]
    public async Task Create_ForceGpu_WithoutGpu_ThrowsCarryingTheProbeReason()
    {
        using ProbeEnvironment env = ProbeEnvironment.WithMissingEglLibrary();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            RenderSurfaceProviderFactory.Create(RenderBackendPreference.ForceGpu));

        await Assert.That(thrown.Message).Contains("no-egl-library");
    }

    [Test]
    public async Task Create_WithoutGpu_FallsBackToCpu()
    {
        using ProbeEnvironment env = ProbeEnvironment.WithMissingEglLibrary();

        using IRenderSurfaceProvider provider =
            RenderSurfaceProviderFactory.Create(RenderBackendPreference.PreferGpu);

        await Assert.That(provider.Backend).IsEqualTo(RenderBackend.CpuRaster);
    }

    /// <summary>
    ///     <c>DV2D_RENDER_BACKEND=cpu</c> must reach every construction site without each one threading a
    ///     flag through — that is what makes the CI "forced CPU" lane a real second pass over the suite
    ///     rather than a branch that merely compiles.
    /// </summary>
    [Test]
    public async Task Create_HonoursForcedCpuFromTheEnvironment()
    {
        using ProbeEnvironment env = ProbeEnvironment.WithBackend("cpu");

        using IRenderSurfaceProvider provider = RenderSurfaceProviderFactory.Create();
        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.Probe();

        await Assert.That(provider.Backend).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(probe.GpuAvailable).IsFalse();
        await Assert.That(probe.Reason).IsEqualTo("forced-cpu");
    }

    /// <summary>
    ///     An explicit argument outranks the environment (§2.5). Asserted through the failure message,
    ///     because on a machine that <i>does</i> have a GPU the successful path proves nothing about
    ///     precedence — the throw does.
    /// </summary>
    [Test]
    public async Task Create_ExplicitPreference_OutranksTheEnvironment()
    {
        using ProbeEnvironment env = ProbeEnvironment.WithBackend("cpu", missingEglLibrary: true);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            RenderSurfaceProviderFactory.Create(RenderBackendPreference.ForceGpu));

        await Assert.That(thrown.Message).Contains("force-gpu");
    }

    /// <summary>
    ///     WASM surfaces belong to Avalonia's compositor and there is no EGL to bind to, so the browser
    ///     branch must never reach the GPU attempt. Asserted through the injected platform rather than by
    ///     running the suite on WASM — the branch is what needs proving, not the runtime.
    /// </summary>
    [Test]
    public async Task ProbeCore_OnBrowser_ShortCircuits() =>
        await AssertShortCircuits(ProbeHostPlatform.Browser, "browser");

    [Test]
    public async Task ProbeCore_OnMacOs_ShortCircuits() =>
        await AssertShortCircuits(ProbeHostPlatform.MacOs, "macos-deferred");

    [Test]
    public async Task ProbeCore_OnAnUnsupportedPlatform_ShortCircuits() =>
        await AssertShortCircuits(ProbeHostPlatform.Other, "unsupported-platform");

    [Test]
    public async Task ProbeCore_ForcedCpu_ShortCircuitsEvenOnAGpuPlatform()
    {
        int attempts = 0;

        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.ProbeCore(ProbeHostPlatform.Windows,
            RenderBackendPreference.ForceCpu, _ =>
            {
                attempts++;
                return new GpuProbeResult(true, RenderBackend.Angle, "angle-d3d11", "r", "v", "3.2");
            }, TimeProvider.System);

        await Assert.That(attempts).IsEqualTo(0);
        await Assert.That(probe.Reason).IsEqualTo("forced-cpu");
    }

    [Test]
    public async Task ProbeCore_CarriesTheRendererStringsThroughOnSuccess()
    {
        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.ProbeCore(ProbeHostPlatform.Windows,
            RenderBackendPreference.Auto,
            _ => new GpuProbeResult(true, RenderBackend.Angle, "angle-d3d11",
                "ANGLE (Intel, Direct3D11)", "Google Inc.", "OpenGL ES 3.0"), TimeProvider.System);

        await Assert.That(probe.Backend).IsEqualTo(RenderBackend.Angle);
        await Assert.That(probe.GpuAvailable).IsTrue();
        await Assert.That(probe.Renderer).IsEqualTo("ANGLE (Intel, Direct3D11)");
        await Assert.That(probe.Describe()).Contains("ANGLE (Intel, Direct3D11)");
    }

    /// <summary>
    ///     Plan §10 R2: ANGLE loading over WARP on a machine that has a real GPU looks like a win in the
    ///     log and is a 20× loss in the numbers. Throughput assertions skip on these; correctness ones
    ///     do not.
    /// </summary>
    [Test]
    [Arguments("llvmpipe (LLVM 15.0.7, 256 bits)", true)]
    [Arguments("ANGLE (Microsoft, Microsoft Basic Render Driver Direct3D11)", true)]
    [Arguments("SwiftShader Device", true)]
    [Arguments("ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Direct3D11)", false)]
    [Arguments(null, false)]
    public async Task IsSoftwareRenderer_NamesTheKnownRasterizers(string? renderer, bool expected)
    {
        RenderSurfaceProbe probe = new(RenderBackend.Angle, true, "angle-d3d11", renderer, null, null,
            TimeSpan.Zero);

        await Assert.That(probe.IsSoftwareRenderer).IsEqualTo(expected);
    }

    private static async Task AssertShortCircuits(ProbeHostPlatform platform, string expectedReason)
    {
        int attempts = 0;

        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.ProbeCore(platform,
            RenderBackendPreference.Auto, _ =>
            {
                attempts++;
                return GpuProbeResult.Failed("should-not-run");
            }, TimeProvider.System);

        await Assert.That(attempts).IsEqualTo(0);
        await Assert.That(probe.Backend).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(probe.GpuAvailable).IsFalse();
        await Assert.That(probe.Reason).IsEqualTo(expectedReason);
    }
}

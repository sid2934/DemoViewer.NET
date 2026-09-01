#region

using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Core.Rendering.Interop;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     The <c>[NotInParallel]</c> constraint key shared by every suite that touches the process-wide
///     probe cache or the render-backend environment variables.
/// </summary>
internal static class ProbeSerialization
{
    /// <summary>The constraint key. One string, so a new suite cannot invent a second lane by typo.</summary>
    public const string Key = "render-surface-probe";
}

/// <summary>
///     Puts the render-backend environment into a known state for one test and puts it back afterwards,
///     resetting the process-wide probe cache on both edges.
///     <para>
///         Both halves matter. Without the reset a test would inherit whichever answer ran first;
///         without the restore it would leak its environment into the next suite. The variables are
///         process-global, which is why every consumer carries
///         <see cref="ProbeSerialization.Key" />.
///     </para>
/// </summary>
internal sealed class ProbeEnvironment : IDisposable
{
    private const string MissingLibraryPath = "/nonexistent/dv2d-tests/no-such-angle-library.dll";

    private readonly string? _previousAngleLibrary;
    private readonly string? _previousBackend;

    private ProbeEnvironment(string? backend, string? angleLibrary)
    {
        _previousBackend = Environment.GetEnvironmentVariable(
            RenderBackendPreferenceParser.EnvironmentVariable);
        _previousAngleLibrary = Environment.GetEnvironmentVariable(Egl.LibraryOverrideVariable);

        Environment.SetEnvironmentVariable(RenderBackendPreferenceParser.EnvironmentVariable, backend);
        Environment.SetEnvironmentVariable(Egl.LibraryOverrideVariable, angleLibrary);
        Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RenderBackendPreferenceParser.EnvironmentVariable,
            _previousBackend);
        Environment.SetEnvironmentVariable(Egl.LibraryOverrideVariable, _previousAngleLibrary);
        Reset();
    }

    /// <summary>No overrides at all: whatever this machine actually has.</summary>
    public static ProbeEnvironment Clean() => new(null, null);

    /// <summary>
    ///     The EGL override pointed at a path that does not exist, so the probe fails deterministically
    ///     on a machine that does have a GPU. This is the only way the no-GPU branches are testable in
    ///     CI on a runner that happens to have one.
    /// </summary>
    public static ProbeEnvironment WithMissingEglLibrary() => new(null, MissingLibraryPath);

    /// <summary>A specific <c>DV2D_RENDER_BACKEND</c> value, optionally with EGL made unavailable.</summary>
    /// <param name="backend">The raw environment value to set.</param>
    /// <param name="missingEglLibrary">Whether to also point the EGL override at nothing.</param>
    public static ProbeEnvironment WithBackend(string? backend, bool missingEglLibrary = false) =>
        new(backend, missingEglLibrary ? MissingLibraryPath : null);

    private static void Reset()
    {
        RenderSurfaceProviderFactory.ResetForTests();
        Egl.ResetForTests();
    }
}

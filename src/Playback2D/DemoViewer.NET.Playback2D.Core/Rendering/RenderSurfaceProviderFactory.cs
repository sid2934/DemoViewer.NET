#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     The single construction site for render surfaces (plans/C2-gpu-provider.md §6.2). Export, the
///     CLI, tests and thumbnails all come through here, which is what makes "swap CPU for GPU" a
///     one-line change instead of a search-and-replace.
///     <para>
///         <b>The probe runs once per process and its result — success <i>or</i> failure — is cached.</b>
///         An explicit lock plus a nullable result field, deliberately not <c>Lazy&lt;T&gt;</c>: a
///         faulted <c>Lazy</c> re-throws its stored exception forever, which is the trap the repo
///         already documents in <c>HeadlessSession</c>. Here a failed probe is ordinary data anyway.
///     </para>
/// </summary>
public static class RenderSurfaceProviderFactory
{
    /// <summary>
    ///     The probe reason that means "the ambient environment asked for CPU", as opposed to the
    ///     capability reasons (<c>no-egl-library</c>, <c>all-backends-failed</c>, <c>browser</c>,
    ///     <c>macos-deferred</c>). <see cref="Create" /> has to tell the two apart: only a capability
    ///     answer may veto a caller that outranks the environment.
    /// </summary>
    private const string ForcedCpuReason = "forced-cpu";

    private static readonly Lock _gate = new();
    private static RenderSurfaceProbe? _probe;

    /// <summary>
    ///     Probes once per process and caches the result, including failure. Never throws, thread-safe.
    ///     The first call invokes <paramref name="log" /> with one line; later calls are silent, so a
    ///     hot path may pass a logger without spamming it.
    /// </summary>
    /// <param name="log">Where the single decision line goes. Null discards it.</param>
    public static RenderSurfaceProbe Probe(Action<string>? log = null)
    {
        lock (_gate)
        {
            if (_probe is { } cached)
            {
                return cached;
            }

            RenderSurfaceProbe result = ProbeCore(CurrentPlatform(),
                RenderBackendPreferenceParser.FromEnvironment(), AttemptGpu, TimeProvider.System);
            _probe = result;
            log?.Invoke(result.Describe());
            return result;
        }
    }

    /// <summary>
    ///     The entry point every consumer uses. Honours <paramref name="preference" />, falling back to
    ///     the CPU provider whenever the GPU is unavailable — the only case that throws is
    ///     <see cref="RenderBackendPreference.ForceGpu" />, which exists precisely so a CI lane can fail
    ///     rather than silently measure software rendering.
    ///     <para>
    ///         Passing <see cref="RenderBackendPreference.Auto" /> — including by omitting the argument —
    ///         consults <c>DV2D_RENDER_BACKEND</c>, so <c>DV2D_RENDER_BACKEND=cpu</c> forces the whole
    ///         process onto the CPU path without every call site threading a flag through. An explicitly
    ///         non-Auto argument outranks the environment, which is §2.5's precedence exactly.
    ///     </para>
    /// </summary>
    /// <param name="preference">What the caller wants. Auto defers to the environment, then the probe.</param>
    /// <param name="log">Where the probe's decision line goes on first use.</param>
    public static IRenderSurfaceProvider Create(
        RenderBackendPreference preference = RenderBackendPreference.Auto,
        Action<string>? log = null)
    {
        RenderBackendPreference effective = preference == RenderBackendPreference.Auto
            ? RenderBackendPreferenceParser.FromEnvironment()
            : preference;

        if (effective == RenderBackendPreference.ForceCpu)
        {
            // Deliberately before Probe(): "force CPU" must not pay for, or log, a GPU probe.
            return CreateCpu();
        }

        RenderSurfaceProbe probe = Probe(log);
        string failure = probe.Reason;

        // The cached probe short-circuits to "forced-cpu" when the AMBIENT DV2D_RENDER_BACKEND says cpu.
        // That is a policy answer, not a capability one, and an explicit argument outranks the
        // environment (§2.5) — control only reaches here with a non-Auto preference, because Auto
        // resolved to ForceCpu and returned above. Letting the ambient variable stand would make
        // Create(ForceGpu) throw on a machine whose GPU works perfectly, contradicting §6.2's "throws
        // only ... when no GPU backend is available", and would let a stale shell variable silently
        // override a --gpu flag or the export dialog's advanced option.
        bool declinedByPolicy = !probe.GpuAvailable && string.Equals(probe.Reason, ForcedCpuReason,
            StringComparison.Ordinal);

        // A probe that said "GPU available" can still lose the context between then and now — a driver
        // reset, a display change, a second provider on a thread the first one owns. Treat it as a
        // fallback, not an invariant violation.
        if (probe.GpuAvailable || declinedByPolicy)
        {
            if (TryCreateGpu(out IRenderSurfaceProvider? gpu, out string reason))
            {
                return gpu;
            }

            failure = reason;
        }

        if (effective == RenderBackendPreference.ForceGpu)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"force-gpu was requested but no GPU render surface backend is available: {failure}"));
        }

        log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"[render] falling back to the CPU provider: {failure}"));
        return CreateCpu();
    }

    /// <summary>The always-available baseline. Never probes, never fails.</summary>
    public static IRenderSurfaceProvider CreateCpu() => new CpuSurfaceProvider();

    /// <summary>
    ///     The decision itself, with its two environmental inputs injected: the host platform and the
    ///     GPU attempt. Split out so the browser and macOS short-circuits are unit-testable on a
    ///     developer's desktop — running the suite on WASM to prove a WASM branch is not a trade worth
    ///     making (plan §7.1).
    /// </summary>
    /// <param name="platform">The host platform to decide for.</param>
    /// <param name="preference">The environment's preference, used only for the forced-CPU shortcut.</param>
    /// <param name="attemptGpu">Tries to stand a GPU backend up and reports what happened.</param>
    /// <param name="time">The clock the duration is measured on.</param>
    internal static RenderSurfaceProbe ProbeCore(ProbeHostPlatform platform,
        RenderBackendPreference preference, Func<ProbeHostPlatform, GpuProbeResult> attemptGpu,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(attemptGpu);
        ArgumentNullException.ThrowIfNull(time);

        long start = time.GetTimestamp();

        string? shortCircuit = preference == RenderBackendPreference.ForceCpu ? ForcedCpuReason
            : platform switch
            {
                // WASM surfaces belong to Avalonia's compositor; the CPU provider is the only offscreen
                // path there (design §8), and there is no EGL to bind to anyway.
                ProbeHostPlatform.Browser => "browser",
                ProbeHostPlatform.MacOs => "macos-deferred",
                ProbeHostPlatform.Other => "unsupported-platform",
                _ => null
            };

        if (shortCircuit is not null)
        {
            return new RenderSurfaceProbe(RenderBackend.CpuRaster, false, shortCircuit, null, null, null,
                time.GetElapsedTime(start));
        }

        GpuProbeResult attempt = attemptGpu(platform);
        TimeSpan duration = time.GetElapsedTime(start);

        return attempt.Success
            ? new RenderSurfaceProbe(attempt.Backend, true, attempt.Reason, attempt.Renderer,
                attempt.Vendor, attempt.Version, duration)
            : new RenderSurfaceProbe(RenderBackend.CpuRaster, false, attempt.Reason, null, null, null,
                duration);
    }

    /// <summary>
    ///     Forgets the cached probe so a test can exercise a different environment. Not a public API:
    ///     a process that re-probes is a process whose logs lie about how many backends it tried.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _probe = null;
        }
    }

    /// <summary>
    ///     Stands a GPU provider up, reads what the driver says it is, and throws the provider away
    ///     again. The probe deliberately does not keep it: a cached provider would pin an EGL context to
    ///     whichever thread happened to probe first, and thread affinity is exactly what
    ///     <see cref="GpuSurfaceProvider" /> guards against.
    /// </summary>
    private static GpuProbeResult AttemptGpu(ProbeHostPlatform _)
    {
        if (!GpuSurfaceProvider.TryCreate(out GpuSurfaceProvider? provider, out string reason))
        {
            return GpuProbeResult.Failed(reason);
        }

        using (provider)
        {
            return new GpuProbeResult(true, provider.Backend, reason, provider.RendererName,
                provider.VendorName, provider.VersionName);
        }
    }

    private static bool TryCreateGpu([NotNullWhen(true)] out IRenderSurfaceProvider? provider,
        out string reason)
    {
        bool created = GpuSurfaceProvider.TryCreate(out GpuSurfaceProvider? gpu, out reason);
        provider = gpu;
        return created;
    }

    private static ProbeHostPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsBrowser())
        {
            return ProbeHostPlatform.Browser;
        }

        if (OperatingSystem.IsWindows())
        {
            return ProbeHostPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return ProbeHostPlatform.MacOs;
        }

        return OperatingSystem.IsLinux() ? ProbeHostPlatform.Linux : ProbeHostPlatform.Other;
    }
}

/// <summary>The host families the probe decides differently for.</summary>
internal enum ProbeHostPlatform
{
    /// <summary>Windows — ANGLE over D3D11.</summary>
    Windows,

    /// <summary>Linux — EGL, surfaceless first so containers work.</summary>
    Linux,

    /// <summary>macOS — deferred by design §5.8 point 3.</summary>
    MacOs,

    /// <summary>WASM — Avalonia owns the surface; CPU is the only offscreen path.</summary>
    Browser,

    /// <summary>Anything else, including mobile heads.</summary>
    Other
}

/// <summary>One GPU-backend attempt, reported as data.</summary>
/// <param name="Success">Whether a usable GPU context was stood up.</param>
/// <param name="Backend">Which backend succeeded. Meaningless when <paramref name="Success" /> is false.</param>
/// <param name="Reason">The probe reason string, on success and on failure alike.</param>
/// <param name="Renderer"><c>GL_RENDERER</c>, when a context was made.</param>
/// <param name="Vendor"><c>GL_VENDOR</c>, when a context was made.</param>
/// <param name="Version"><c>GL_VERSION</c>, when a context was made.</param>
internal readonly record struct GpuProbeResult(
    bool Success,
    RenderBackend Backend,
    string Reason,
    string? Renderer,
    string? Vendor,
    string? Version)
{
    /// <summary>A failed attempt carrying the reason it failed.</summary>
    /// <param name="reason">Why no backend could be created.</param>
    public static GpuProbeResult Failed(string reason) =>
        new(false, RenderBackend.CpuRaster, reason, null, null, null);
}

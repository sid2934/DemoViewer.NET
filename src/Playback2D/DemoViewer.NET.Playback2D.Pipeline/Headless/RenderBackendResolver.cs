#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>A resolved render backend plus why it was chosen.</summary>
/// <param name="Provider">The surface provider. The caller disposes it.</param>
/// <param name="Requested">What the caller asked for, before any fallback.</param>
/// <param name="Reason">A one-line explanation, printed when the request was not honoured.</param>
internal sealed record ResolvedBackend(IRenderSurfaceProvider Provider, string Requested, string? Reason)
{
    /// <summary>
    ///     The backend actually in use, <b>captured at construction</b> rather than read through
    ///     <see cref="Provider" /> on demand.
    ///     <para>
    ///         <c>GoldenCommand</c> keeps the first entry's <see cref="ResolvedBackend" /> for its summary
    ///         payload but disposes each entry's plan, and with it that provider, at the end of every
    ///         loop iteration. Reading the provider afterwards is a use-after-dispose: inert against
    ///         <c>CpuSurfaceProvider</c> (constant property, no-op <c>Dispose</c>), a fault against
    ///         <c>GpuSurfaceProvider</c>, which owns an EGL context and is handed over by this very type.
    ///     </para>
    /// </summary>
    public RenderBackend Backend { get; } = Provider.Backend;
}

/// <summary>
///     Turns a settled <see cref="RenderBackendPreference" /> into a surface provider. Every
///     construction goes through <see cref="RenderSurfaceProviderFactory" />, the single site in the
///     repo that knows how to stand an EGL context up. A
///     <see cref="RenderBackendPreference.PreferGpu" /> request on a machine without one degrades to CPU
///     with a stated reason; <see cref="RenderBackendPreference.ForceGpu" /> turns that degradation into
///     <see cref="BackendUnavailableException" />, never a silent software-rendered measurement.
///     <para>
///         Where the preference came from is the caller's business. Flags, an environment variable and a
///         lane default are argument handling, and none of them is read here.
///     </para>
/// </summary>
internal static class RenderBackendResolver
{
    /// <summary>Resolves a preference into a provider.</summary>
    /// <param name="preference">The settled preference. Nothing further is consulted.</param>
    /// <param name="log">Where the factory narrates its probe, or null to drop it.</param>
    /// <exception cref="BackendUnavailableException">A strict request cannot be satisfied.</exception>
    public static ResolvedBackend Resolve(RenderBackendPreference preference,
        Action<string>? log = null)
    {
        string requested = Token(preference);

        IRenderSurfaceProvider provider;
        try
        {
            provider = RenderSurfaceProviderFactory.Create(preference, log);
        }
        catch (InvalidOperationException e)
        {
            // The factory's one throw: force-gpu with no GPU. Re-shaped so a caller can route "this
            // machine cannot do what you asked" somewhere other than its generic runtime-failure
            // channel.
            throw new BackendUnavailableException(e.Message, e);
        }

        string? reason = null;
        if (provider.Backend == RenderBackend.CpuRaster &&
            preference is RenderBackendPreference.PreferGpu or RenderBackendPreference.ForceGpu)
        {
            // Read from the cached probe rather than scraped out of the log callback: the reason is
            // data on RenderSurfaceProbe, so it never has to be parsed back out of a sentence.
            reason = string.Create(CultureInfo.InvariantCulture,
                $"the GPU surface provider is unavailable ({RenderSurfaceProviderFactory.Probe().Reason}); using CpuRaster");
        }

        return new ResolvedBackend(provider, requested, reason);
    }

    /// <summary>The token echoed back in JSON payloads as <c>backend_requested</c>.</summary>
    /// <param name="preference">The resolved preference.</param>
    public static string Token(RenderBackendPreference preference) => preference switch
    {
        RenderBackendPreference.ForceCpu => "cpu",
        RenderBackendPreference.PreferGpu => "gpu",
        RenderBackendPreference.ForceGpu => "force-gpu",
        _ => "auto"
    };
}

/// <summary>Raised when a strictly-requested backend cannot be provided.</summary>
internal sealed class BackendUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the backend is unavailable.</param>
    public BackendUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Parameterless overload required by CA1032.</summary>
    public BackendUnavailableException()
    {
    }

    /// <summary>Wrapping overload required by CA1032.</summary>
    /// <param name="message">Why the backend is unavailable.</param>
    /// <param name="innerException">The cause.</param>
    public BackendUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

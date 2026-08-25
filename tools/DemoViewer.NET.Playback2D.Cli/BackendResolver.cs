#region

using DemoViewer.NET.Playback2D.Core.Rendering;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>A resolved render backend plus why it was chosen.</summary>
/// <param name="Provider">The surface provider. The caller disposes it.</param>
/// <param name="Requested">What the caller asked for, before any fallback.</param>
/// <param name="Reason">A one-line explanation, printed when the request was not honoured.</param>
internal sealed record ResolvedBackend(IRenderSurfaceProvider Provider, string Requested, string? Reason)
{
    /// <summary>The backend actually in use.</summary>
    public RenderBackend Backend => Provider.Backend;
}

/// <summary>
///     Applies design §5.8's precedence for the surface backend: explicit flag →
///     <c>DV2D_RENDER_BACKEND</c> → auto-probe. The app's fourth rung (<c>AppSettings.Playback2D
///     .RenderBackend</c>) is deliberately absent: a headless tool reads no UI state (§7.7).
///     <para>
///         <b>C2 owns the GPU half.</b> Until <c>GpuSurfaceProvider</c> and its probe land, a
///         <c>--gpu</c> request degrades to CPU with a stated reason, or fails with
///         <see cref="ExitCode.EnvironmentUnavailable" /> under <c>--strict-backend</c> — never silently.
///     </para>
/// </summary>
internal static class BackendResolver
{
    /// <summary>The environment variable consulted when no backend flag is given.</summary>
    public const string EnvironmentVariable = "DV2D_RENDER_BACKEND";

    /// <summary>Resolves the backend. Consumes <c>--cpu</c>, <c>--gpu</c> and <c>--strict-backend</c>.</summary>
    /// <param name="args">The parsed arguments.</param>
    /// <exception cref="CliUsageException">Both <c>--cpu</c> and <c>--gpu</c> were given.</exception>
    /// <exception cref="BackendUnavailableException">A strict request cannot be satisfied.</exception>
    public static ResolvedBackend Resolve(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool cpu = args.Flag("cpu");
        bool gpu = args.Flag("gpu");
        bool strict = args.Flag("strict-backend");

        if (cpu && gpu)
        {
            throw new CliUsageException("--cpu and --gpu are mutually exclusive.");
        }

        string requested = cpu ? "cpu"
            : gpu ? "gpu"
            : (Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "auto").Trim().ToLowerInvariant();

        if (requested is not ("cpu" or "gpu" or "auto"))
        {
            throw new CliUsageException(
                $"{EnvironmentVariable} must be one of auto|cpu|gpu, got '{requested}'.");
        }

        if (requested == "gpu")
        {
            const string reason =
                "the GPU surface provider is not in this build (C2 owns GpuSurfaceProvider); using CpuRaster";
            if (strict)
            {
                throw new BackendUnavailableException(reason);
            }

            return new ResolvedBackend(new CpuSurfaceProvider(), requested, reason);
        }

        // "auto" probes for a GPU and falls back; with no GPU provider compiled in, the probe is a
        // constant. C2 replaces this line with RenderSurfaceProviderFactory.
        return new ResolvedBackend(new CpuSurfaceProvider(), requested, null);
    }
}

/// <summary>Raised when a strictly-requested backend cannot be provided. Maps to exit 6.</summary>
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

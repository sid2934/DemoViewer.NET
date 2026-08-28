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
    /// <summary>
    ///     The backend actually in use, <b>captured at construction</b> rather than read through
    ///     <see cref="Provider" /> on demand.
    ///     <para>
    ///         <c>GoldenCommand</c> keeps the first entry's <see cref="ResolvedBackend" /> for its summary
    ///         payload but disposes each entry's plan, and with it that provider, at the end of every
    ///         loop iteration. Reading the provider afterwards is a use-after-dispose: inert against
    ///         <c>CpuSurfaceProvider</c> (constant property, no-op <c>Dispose</c>), a fault against C2's
    ///         <c>GpuSurfaceProvider</c>, which owns an EGL context and is handed over by this very type.
    ///     </para>
    /// </summary>
    public RenderBackend Backend { get; } = Provider.Backend;
}

/// <summary>
///     Applies design §5.8's precedence for the surface backend: explicit flag → <c>--backend</c> →
///     <c>DV2D_RENDER_BACKEND</c> → auto-probe. The app's fourth rung (
///     <c>
///         AppSettings.Playback2D
///         .RenderBackend
///     </c>
///     ) is deliberately absent: a headless tool reads no UI state (§7.7).
///     <para>
///         Every construction goes through <see cref="RenderSurfaceProviderFactory" />, the single site
///         in the repo that knows how to stand an EGL context up. A <c>--gpu</c> request on a machine
///         without one degrades to CPU with a stated reason; <c>--strict-backend</c> (equivalently
///         <c>--backend force-gpu</c>) turns that degradation into
///         <see cref="ExitCode.EnvironmentUnavailable" />, never a silent software-rendered
///         measurement.
///     </para>
/// </summary>
internal static class BackendResolver
{
    /// <summary>The grammar both <c>--backend</c> and the environment variable accept.</summary>
    private const string Grammar = "auto|cpu|gpu|angle|gl|force-gpu";

    /// <summary>The environment variable consulted when no backend flag is given.</summary>
    public const string EnvironmentVariable = RenderBackendPreferenceParser.EnvironmentVariable;

    /// <summary>
    ///     Resolves the backend. Consumes <c>--cpu</c>, <c>--gpu</c>, <c>--backend</c> and
    ///     <c>--strict-backend</c>.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="fallback">
    ///     What the bottom rung of the chain means when nobody expressed a preference.
    ///     <see cref="RenderBackendPreference.Auto" /> everywhere except the golden lane, which pins
    ///     <see cref="RenderBackendPreference.ForceCpu" />: the committed corpus lives in
    ///     <c>goldens/cpu/</c> and is compared byte-exact, so auto-probing onto a GPU would report a
    ///     rasterizer difference as a pixel regression on any developer machine that happens to have one.
    /// </param>
    /// <exception cref="CliUsageException">Conflicting flags, or an unparseable backend name.</exception>
    /// <exception cref="BackendUnavailableException">A strict request cannot be satisfied.</exception>
    public static ResolvedBackend Resolve(CliArgs args,
        RenderBackendPreference fallback = RenderBackendPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(args);

        RenderBackendPreference preference = Preference(args, fallback);
        string requested = Token(preference);

        IRenderSurfaceProvider provider;
        try
        {
            provider = RenderSurfaceProviderFactory.Create(preference, ConsoleOut.Info);
        }
        catch (InvalidOperationException e)
        {
            // The factory's one throw: force-gpu with no GPU. Re-shaped into the tool's exit-6 channel
            // rather than the generic runtime-failure exit 3. "This machine cannot do what you asked"
            // is an environment answer, and CI distinguishes the two.
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

    /// <summary>
    ///     Applies the precedence chain to the flags actually given. Split from
    ///     <see cref="Resolve" /> so <c>dv2d probe</c> can ask what was requested without building a
    ///     provider.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="fallback">What "nobody said" resolves to. See <see cref="Resolve" />.</param>
    /// <exception cref="CliUsageException">Conflicting flags, or an unparseable backend name.</exception>
    public static RenderBackendPreference Preference(CliArgs args,
        RenderBackendPreference fallback = RenderBackendPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool cpu = args.Flag("cpu");
        bool gpu = args.Flag("gpu");
        bool strict = args.Flag("strict-backend");
        string? backend = args.String("backend");

        if (cpu && gpu)
        {
            throw new CliUsageException("--cpu and --gpu are mutually exclusive.");
        }

        if (backend is not null && (cpu || gpu))
        {
            throw new CliUsageException("--backend cannot be combined with --cpu or --gpu.");
        }

        if (backend is not null && !RenderBackendPreferenceParser.TryParse(backend, out _))
        {
            throw new CliUsageException($"--backend expects one of {Grammar}, got '{backend}'.");
        }

        // The parser treats an unrecognised environment value as "unset", which is right for a library
        // and wrong for a tool: a CI lane that typo'd DV2D_RENDER_BACKEND=gpuu would silently measure
        // the CPU path and report a green budget. Reject it here instead.
        string? environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment) &&
            !RenderBackendPreferenceParser.TryParse(environment, out _))
        {
            throw new CliUsageException(
                $"{EnvironmentVariable} must be one of {Grammar}, got '{environment}'.");
        }

        RenderBackendPreference preference = RenderBackendPreferenceParser.Resolve(
            cpu ? RenderBackendPreference.ForceCpu : gpu ? RenderBackendPreference.PreferGpu : null,
            backend,
            environment,
            // The parser's fourth rung is AppSettings for the app; dv2d reads none (§7.7), so the slot
            // carries the CALLER's default instead. It is reached only when neither a flag nor the
            // environment said anything, so an explicit `--backend auto` still means auto.
            Token(fallback));

        // --strict-backend is the flag spelling of force-gpu, kept because it composes with --gpu and
        // with an environment-supplied preference alike. It only ever tightens.
        return strict && preference == RenderBackendPreference.PreferGpu
            ? RenderBackendPreference.ForceGpu
            : preference;
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

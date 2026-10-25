#region

using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Reads the surface-backend flags and applies their precedence: explicit flag → <c>--backend</c> →
///     <c>DV2D_RENDER_BACKEND</c> → auto-probe. The app's fourth rung (
///     <c>
///         AppSettings.Playback2D
///         .RenderBackend
///     </c>
///     ) is deliberately absent: a headless tool reads no UI state.
///     <para>
///         Only the reading lives here. Standing a provider up from the settled preference is
///         <see cref="RenderBackendResolver" />, which is also where <c>--strict-backend</c> turns a
///         missing GPU into <see cref="ExitCode.EnvironmentUnavailable" /> rather than a silent
///         software-rendered measurement.
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
    /// <param name="fallback">What "nobody said" resolves to. See <see cref="Preference" />.</param>
    /// <exception cref="CliUsageException">Conflicting flags, or an unparseable backend name.</exception>
    /// <exception cref="BackendUnavailableException">A strict request cannot be satisfied.</exception>
    public static ResolvedBackend Resolve(CliArgs args,
        RenderBackendPreference fallback = RenderBackendPreference.Auto) =>
        RenderBackendResolver.Resolve(Preference(args, fallback), ConsoleOut.Info);

    /// <summary>
    ///     Applies the precedence chain to the flags actually given. Split from
    ///     <see cref="Resolve" /> so <c>dv2d probe</c> can ask what was requested without building a
    ///     provider.
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
            // The parser's fourth rung is AppSettings for the app; dv2d reads none, so the slot
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
    public static string Token(RenderBackendPreference preference) =>
        RenderBackendResolver.Token(preference);
}

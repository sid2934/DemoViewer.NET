using System.Security;

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     Turns the strings the outside world speaks — a CLI flag, an environment variable, a persisted
///     settings value — into a <see cref="RenderBackendPreference" />, and applies the precedence chain
///     between them (plans/C2-gpu-provider.md §2.5, §6.2).
///     <para>
///         <b>Nothing here throws.</b> An unrecognised value is a warning-worthy typo, not a reason to
///         fail somebody's export: it resolves to <see cref="RenderBackendPreference.Auto" />, which is
///         the behaviour they would have got by not setting it at all.
///     </para>
/// </summary>
public static class RenderBackendPreferenceParser
{
    /// <summary>
    ///     The environment variable CI lanes and support instructions use. A <b>public contract</b> —
    ///     its spelling is depended on outside this repo, so it is a constant rather than a literal.
    /// </summary>
    public const string EnvironmentVariable = "DV2D_RENDER_BACKEND";

    /// <summary>
    ///     Parses <c>auto | cpu | gpu | angle | gl | force-gpu</c>, case- and whitespace-insensitive.
    ///     <para>
    ///         <c>angle</c> and <c>gl</c> are accepted as aliases for <c>gpu</c>: the grammar reserves
    ///         them (§2.6) but v1 exposes no per-API forcing — which specific GL stack gets used is the
    ///         probe's decision, reported in <see cref="RenderSurfaceProbe.Reason" />. Accepting and
    ///         mapping them beats rejecting a spelling the documented grammar promises.
    ///     </para>
    /// </summary>
    /// <param name="value">The text to parse. Null, empty or unrecognised returns false.</param>
    /// <param name="preference">The parsed preference, or <see cref="RenderBackendPreference.Auto" />.</param>
    public static bool TryParse(string? value, out RenderBackendPreference preference)
    {
        preference = RenderBackendPreference.Auto;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                preference = RenderBackendPreference.Auto;
                return true;
            case "cpu":
            case "cpuraster":
                preference = RenderBackendPreference.ForceCpu;
                return true;
            case "gpu":
            case "angle":
            case "gl":
            case "opengl":
                preference = RenderBackendPreference.PreferGpu;
                return true;
            case "force-gpu":
            case "forcegpu":
                preference = RenderBackendPreference.ForceGpu;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///     Reads the backend preference from the environment. Unset or unrecognised is
    ///     <see cref="RenderBackendPreference.Auto" />; never throws.
    /// </summary>
    /// <param name="variable">The variable to read. Defaults to <see cref="EnvironmentVariable" />.</param>
    public static RenderBackendPreference FromEnvironment(string variable = EnvironmentVariable)
    {
        string? value;
        try
        {
            value = Environment.GetEnvironmentVariable(variable);
        }
        catch (SecurityException)
        {
            // A sandboxed host that refuses environment reads is not an error condition for us: it
            // simply means nobody expressed a preference here.
            return RenderBackendPreference.Auto;
        }

        return TryParse(value, out RenderBackendPreference preference)
            ? preference
            : RenderBackendPreference.Auto;
    }

    /// <summary>
    ///     Applies the §2.5 precedence chain: explicit API argument → command-line flag → environment
    ///     variable → persisted setting → <see cref="RenderBackendPreference.Auto" />. Any argument may
    ///     be null or absent, and an unparseable string is skipped rather than short-circuiting the
    ///     chain — a typo in a settings file must not mask a valid <c>--cpu</c> on the command line.
    /// </summary>
    /// <param name="explicitArgument">What a caller asked for in code. Wins outright when present.</param>
    /// <param name="commandLineValue">The raw <c>--backend</c> value, if one was given.</param>
    /// <param name="environmentValue">The raw <c>DV2D_RENDER_BACKEND</c> value, if one was set.</param>
    /// <param name="settingValue">The raw persisted <c>AppSettings.Playback2D.RenderBackend</c> value.</param>
    public static RenderBackendPreference Resolve(
        RenderBackendPreference? explicitArgument,
        string? commandLineValue,
        string? environmentValue,
        string? settingValue)
    {
        if (explicitArgument is { } explicitPreference)
        {
            return explicitPreference;
        }

        if (TryParse(commandLineValue, out RenderBackendPreference fromCommandLine))
        {
            return fromCommandLine;
        }

        if (TryParse(environmentValue, out RenderBackendPreference fromEnvironment))
        {
            return fromEnvironment;
        }

        return TryParse(settingValue, out RenderBackendPreference fromSetting)
            ? fromSetting
            : RenderBackendPreference.Auto;
    }
}

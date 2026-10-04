namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     One line, no newline: every value <see cref="PackCompatibility.Check" /> judges, plus its verdict.
///     Item 37 prints it; item 36 logs it.
/// </summary>
public static class CompatibilityReport
{
    /// <summary><paramref name="manifest" />'s three requirements, <paramref name="host" />'s three values, and the verdict.</summary>
    public static string Describe(ExtensionManifest manifest, ExtensionHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(host);
        PackCompatibility result = PackCompatibility.Check(manifest, host);
        string verdict = result.IsCompatible ? "Compatible." : $"Incompatible: {result.Describe(manifest, manifest.Id)}.";

        return $"{manifest.Name} {manifest.Version} ({manifest.Id}): requiresHost {manifest.RequiresHost}, "
            + $"requiresCs2DemoKit {manifest.RequiresCs2DemoKit}, minAppVersion {Floor(manifest.MinAppVersion)}; "
            + $"host contract {host.ContractVersion}, CS2DemoKit {host.Cs2DemoKitVersion}, app {App(host.AppVersion)}. {verdict}";
    }

    private static string Floor(SemVersion? minAppVersion) => minAppVersion?.ToString() ?? "any";

    private static string App(SemVersion? appVersion) => appVersion?.ToString() ?? "unstamped";
}

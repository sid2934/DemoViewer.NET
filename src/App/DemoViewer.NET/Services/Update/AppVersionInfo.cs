#region

using System.Reflection;

#endregion

namespace DemoViewer.NET.Services.Update;

/// <summary>
///     The running app version, normalized to the bare x.y.z the release tags use. Read from the
///     App assembly's informational version (NBGV stamps it), NOT from the updater — Velopack only
///     knows a version on installed builds, while the "What's new" gate must also work on a
///     side-loaded or dev build so the flow is testable outside packaging.
/// </summary>
public static class AppVersionInfo
{
    /// <summary>The normalized x.y.z of this build, or null when no version is stamped.</summary>
    public static string? CurrentReleaseVersion { get; } = GitHubReleaseNotesService.NormalizeVersion(
        typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersionInfo).Assembly.GetName().Version?.ToString());
}

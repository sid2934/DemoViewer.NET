#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Redirects every app-data store to a per-run temp location before anything else in the assembly
///     runs, so no test ever touches the developer's REAL config folder.
///     <para>
///         <see cref="AppPaths.ConfigRoot" /> is pointed at a per-run temp directory via
///         <see cref="AppPaths.ConfigDirEnvVar" /> (<c>DEMOVIEWER_CONFIG_DIR</c>). Every AppPaths-routed
///         store (the consolidated <c>settings.json</c> — which also holds the UI session-restore
///         snapshot and recents, formerly the standalone <c>session.json</c> / <c>recent-files.json</c> — plus
///         the still-separate bookmarks, graph breakpoints, and library cache) resolves under temp. Without
///         this a fresh <c>MainViewModel</c> would restore the DEVELOPER'S REAL app session and recents, so
///         test outcomes depended on what the developer last did in the app.
///     </para>
///     <para>
///         Critically, the override also SUPPRESSES the legacy <c>~/.config → ~/Library/Application Support</c>
///         macOS migration (AppPaths skips the move when the override is set). A fresh <c>MainViewModel</c>
///         eagerly constructs <c>BookmarkStore</c>/<c>GraphBreakpointStore</c>, which without this would fire
///         that migration against the developer's REAL <c>~/.config</c> files.
///     </para>
///     A module initializer runs at assembly load — before any TUnit hook, before the headless session,
///     before any store's field initializer.
/// </summary>
internal static class SessionIsolation
{
    [ModuleInitializer]
    internal static void RedirectAppDataStores()
    {
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar,
            Path.Combine(Path.GetTempPath(), $"dvtest-config-{Guid.NewGuid():N}"));
    }
}

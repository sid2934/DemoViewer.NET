#region

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     The single owner of the App layer's app-data file paths. Every persisted store (the consolidated
///     config file, plus the still-separate per-demo bookmarks / graph breakpoints and the
///     library cache) and the desktop crash log resolves its location here instead of reaching for
///     <see cref="Environment.SpecialFolder.ApplicationData" />
///     directly, so the whole app writes under ONE cross-platform root
///     (<see cref="RuleSetLocator.GetConfigRoot" />):
///     <c>~/Library/Application Support/DemoViewer.NET</c> on macOS,
///     <c>%APPDATA%\DemoViewer.NET</c> on Windows,
///     <c>$XDG_CONFIG_HOME/DemoViewer.NET</c> (default <c>~/.config/DemoViewer.NET</c>) on Linux.
///     <para>
///         There is no filesystem in the WASM/browser sandbox, so <see cref="ConfigRoot" /> and every
///         file path is <c>null</c> there. Callers mirror the stores' existing no-op-on-WASM behavior.
///     </para>
///     <para>
///         <b>macOS migration.</b> The stores historically resolved
///         <see cref="Environment.SpecialFolder.ApplicationData" />, which .NET maps to <c>~/.config</c>
///         on Unix (macOS included), NOT the unified macOS root above. Requesting a path therefore runs
///         a one-time, best-effort move of any legacy <c>~/.config/DemoViewer.NET/&lt;file&gt;</c> into the
///         new location (see the private migration helper).
///     </para>
/// </summary>
public static class AppPaths
{
    private const string AppConfigDirName = "DemoViewer.NET";

    /// <summary>
    ///     Environment override that replaces <see cref="ConfigRoot" /> wholesale, a test seam that keeps
    ///     tests out of the real user config dir. Being set also suppresses the legacy macOS migration, so
    ///     a test run never touches the developer's real <c>~/.config</c> files.
    /// </summary>
    public const string ConfigDirEnvVar = "DEMOVIEWER_CONFIG_DIR";

    // Files whose legacy migration has already been attempted this process, so the move is genuinely
    // one-time per file rather than re-stat-ing the old location on every path access. File.Exists(target)
    // already makes the move idempotent; this set is purely the re-stat optimization on top of that.
    private static readonly HashSet<string> _migrationAttempted = new(StringComparer.Ordinal);
    private static readonly object _migrationLock = new();

    /// <summary>
    ///     The app-data root, or <c>null</c> on WASM (no filesystem). The <see cref="ConfigDirEnvVar" />
    ///     override wins; otherwise <see cref="RuleSetLocator.GetConfigRoot" />.
    /// </summary>
    public static string? ConfigRoot
    {
        get
        {
            if (OperatingSystem.IsBrowser())
            {
                return null;
            }

            string? overrideDir = Environment.GetEnvironmentVariable(ConfigDirEnvVar);
            return !string.IsNullOrEmpty(overrideDir) ? overrideDir : RuleSetLocator.GetConfigRoot();
        }
    }

    /// <summary>
    ///     The single consolidated per-user config file: <c>settings.json</c>. Besides preferences
    ///     it now also carries the UI session-restore snapshot and the recents list (the former
    ///     <c>session.json</c> / <c>recent-files.json</c>, folded in by <see cref="Configuration.SettingsService" />).
    ///     <c>null</c> on WASM.
    /// </summary>
    public static string? SettingsFile => Resolve("settings.json");

    /// <summary>
    ///     Persisted bookmarks: <c>SessionState.json</c>. Per-demo working data; kept SEPARATE from the config file.
    ///     <c>null</c> on
    ///     WASM.
    /// </summary>
    public static string? BookmarksFile => Resolve("SessionState.json");

    /// <summary>
    ///     Persisted analysis-graph breakpoints: <c>GraphBreakpoints.json</c>. Per-demo; kept SEPARATE.
    ///     <c>null</c> on WASM.
    /// </summary>
    public static string? GraphBreakpointsFile => Resolve("GraphBreakpoints.json");

    /// <summary>
    ///     Demo-library metadata cache: <c>library.json</c>. Rebuildable cache; kept SEPARATE. <c>null</c> on
    ///     WASM.
    /// </summary>
    public static string? LibraryCacheFile => Resolve("library.json");

    /// <summary>Desktop last-chance crash log: <c>crash.log</c>. Not a config store; kept SEPARATE. <c>null</c> on WASM.</summary>
    public static string? CrashLogFile => Resolve("crash.log");

    /// <summary>
    ///     Library-wide highlights cache: <c>highlights.json</c>.
    ///     A rebuildable cache like <see cref="LibraryCacheFile" />; kept SEPARATE. <c>null</c> on WASM.
    /// </summary>
    public static string? HighlightsCacheFile => Resolve("highlights.json");

    /// <summary>
    ///     The unified demo-information cache directory: <c>&lt;config&gt;/cache/</c>, holding
    ///     <c>index.json</c> plus one sidecar per demo under <c>demos/</c>
    ///     . Supersedes <see cref="LibraryCacheFile" /> and
    ///     <see cref="HighlightsCacheFile" />, which remain here only so the one-shot migration can read them.
    ///     A PURE path: the store creates directories when it first writes. <c>null</c> on WASM.
    /// </summary>
    public static string? DemoCacheDir
    {
        get
        {
            string? root = ConfigRoot;
            return root is null ? null : Path.Combine(root, "cache");
        }
    }

    /// <summary>
    ///     The user theme drop-in directory: <c>&lt;config&gt;/themes/</c> (central theme system,
    ///     the design notes in git history, T3). Each <c>*.json</c> here is loaded as a custom theme. A PURE path
    ///     (no directory creation, a side-effect-free getter keeps VM construction hermetic in tests);
    ///     <see cref="EnsureThemesDirectory" /> creates it once at app startup. <c>null</c> on WASM (no filesystem).
    /// </summary>
    public static string? ThemesDirectory
    {
        get
        {
            string? root = ConfigRoot;
            return root is null ? null : Path.Combine(root, "themes");
        }
    }

    /// <summary>
    ///     Directory for the unified diagnostics rolling log files: <c>&lt;config&gt;/logs/</c>. A stable,
    ///     discoverable location under the app-data root (NOT the OS temp dir, which is too ephemeral for
    ///     "attach recent logs to a user-reported issue"). A PURE path: no directory creation, so getter
    ///     access stays side-effect-free; <see cref="EnsureLogsDirectory" /> creates it once at startup.
    ///     <c>null</c> on WASM (no filesystem).
    /// </summary>
    public static string? LogsDir
    {
        get
        {
            string? root = ConfigRoot;
            return root is null ? null : Path.Combine(root, "logs");
        }
    }

    /// <summary>
    ///     Claims the config-directory name for this application, before anything can resolve a
    ///     path through it.
    ///     <para>
    ///         <see cref="RuleSetLocator" /> lives in CS2DemoKit.Analysis, which is a general
    ///         library with its own default name. Leaving that default in place would silently
    ///         move every existing install's settings, session state, recents, bookmarks, library
    ///         cache and user rules to a directory that has never had anything in it. The data is
    ///         not lost, but the app comes up looking factory-fresh, which is worse than an error.
    ///     </para>
    ///     <para>
    ///         A module initializer rather than a call from a startup path, because the library's
    ///         rules loading resolves the user-rules directory on its own and does not necessarily
    ///         touch <see cref="AppPaths" /> first. This runs when the assembly is first used,
    ///         which precedes both.
    ///     </para>
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "This assembly is application code; it is a class library only because the "
                        + "Desktop, Browser and capture heads each reference it. The rule targets "
                        + "general-purpose libraries, where a module initializer surprises the "
                        + "consumer. Here the consumer is our own entry point, and running before "
                        + "it is the entire point — the library's rules loading can resolve the "
                        + "user-rules directory without going through AppPaths first.")]
    public static void ClaimConfigDirectoryName() => RuleSetLocator.AppConfigDirName = AppConfigDirName;

    /// <summary>
    ///     Best-effort creates <see cref="ThemesDirectory" /> so a user has somewhere to drop theme files
    ///     (called once at startup, off the hot path). No-op on WASM; a failure is swallowed, the registry's
    ///     scan tolerates a missing directory (no drop-ins). Returns the path (or <c>null</c> on WASM).
    /// </summary>
    public static string? EnsureThemesDirectory()
    {
        string? dir = ThemesDirectory;
        if (dir is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Best-effort, the scan (LoadUserThemes) checks Directory.Exists and no-ops when absent.
        }

        return dir;
    }

    /// <summary>
    ///     Best-effort creates <see cref="LogsDir" /> so the rolling file sink has somewhere to write
    ///     (called once at startup, off the hot path). No-op / <c>null</c> on WASM; a failure is swallowed,
    ///     the sink tolerates a missing directory by staying disabled.
    /// </summary>
    public static string? EnsureLogsDirectory()
    {
        string? dir = LogsDir;
        if (dir is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Best-effort, the sink checks writeability and disables itself on failure.
        }

        return dir;
    }

    /// <summary>
    ///     The diagnostics log files present under <see cref="LogsDir" />, newest first (by last-write
    ///     time), for the copy-diagnostics attachment. Returns an empty array on WASM or when the directory
    ///     is absent/unreadable, never throws.
    /// </summary>
    public static IReadOnlyList<string> LatestLogFiles()
    {
        string? dir = LogsDir;
        if (dir is null)
        {
            return [];
        }

        try
        {
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "diagnostics*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    // Resolves <root>/<fileName>, creating the root directory (best-effort, mirroring the stores' own
    // CreateDirectory-on-save behavior) and running the one-time legacy migration for the file.
    private static string? Resolve(string fileName)
    {
        string? root = ConfigRoot;
        if (root is null)
        {
            return null; // WASM: no filesystem
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch
        {
            // Best-effort: a store's own write path surfaces or swallows a genuine I/O failure.
        }

        string target = Path.Combine(root, fileName);
        MigrateLegacyFile(fileName, target);
        return target;
    }

    // One-time, best-effort migration of a pre-unification file. Only macOS is actually affected:
    // Environment.SpecialFolder.ApplicationData maps to $XDG_CONFIG_HOME (default ~/.config) on Unix,
    // INCLUDING macOS, whereas the unified root is ~/Library/Application Support. So on macOS the legacy
    // file sits at ~/.config/DemoViewer.NET/<file> while the new root is elsewhere; move it across once.
    // On Windows and Linux the legacy and new roots are the SAME directory (legacyPath == target), so this
    // no-ops.
    private static void MigrateLegacyFile(string fileName, string target)
    {
        // Suppressed when the root is overridden (test seam) so a test run never touches the real
        // ~/.config. (Browser already returned null in Resolve before reaching here.)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConfigDirEnvVar)))
        {
            return;
        }

        lock (_migrationLock)
        {
            if (!_migrationAttempted.Add(target))
            {
                return; // already handled this file this process
            }
        }

        try
        {
            string legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConfigDirName, fileName);

            // Same directory (Windows/Linux) → nothing to migrate; never clobber a file already at the new
            // location; nothing to do if the source is absent. Together these make the move idempotent.
            if (string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(target), StringComparison.Ordinal)
                || !File.Exists(legacyPath)
                || File.Exists(target))
            {
                return;
            }

            File.Move(legacyPath, target);
        }
        catch
        {
            // Best-effort: a missing source, a locked file, or a cross-volume move must never throw. The
            // store simply starts fresh at the new location.
        }
    }
}

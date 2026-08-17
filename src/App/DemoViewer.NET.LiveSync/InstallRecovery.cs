#region

using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Services;
using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     Crash recovery for the CS2 install. A DV crash with a
///     live session skips <c>ShutdownRequested</c>, so CSVG's stop-time restore never runs and the
///     install stays modified: <c>gameinfo.gi</c> keeps the injected <c>Game csgo/csvg</c> search
///     path (the plugin loads on every NORMAL CS2 launch) and the plugin files stay under
///     <c>game/csgo/csvg/</c>.
///     <para>
///         Detection is CONTENT-based, not backup-index-based: CSVG deliberately keeps backups
///         after a clean stop (redundancy; pruned by age), so "backups exist" is normal — the
///         crash signature is the marker still present in <c>gameinfo.gi</c> and/or the plugin
///         directory still existing. Restore un-patches <c>gameinfo.gi</c> from CSVG's own backup
///         and removes the plugin files. User-settings backups are deliberately NOT restored
///         here: they snapshot the crashed session's start, and blindly restoring them could
///         clobber settings the user changed since — CSVG's next session start self-heals their
///         hygiene, and <c>csvg restore</c> is the manual full-restore fallback.
///     </para>
///     <para>
///         Both operations run against a short-lived CSVG service container (no Kestrel, no
///         session) so install discovery, backup layout, and uninstall logic stay CSVG's own —
///         nothing is reimplemented DV-side. Mock mode has no real files: both no-op.
///     </para>
/// </summary>
internal static class InstallRecovery
{
    /// <summary>The exact search-path line CSVG's <c>ModifyGameInfoFile</c> injects.</summary>
    private const string GameInfoMarker = "Game\tcsgo/csvg";

    /// <summary>
    ///     Probes the real CS2 install for leftover CSVG modifications. Null when nothing is
    ///     determinable — mock mode, no CS2 install found, or unreadable files — which callers
    ///     must treat as "nothing to offer", never as an error.
    /// </summary>
    public static LeftoverState? Detect(LiveSyncSettings settings)
    {
        if (IsMock(settings))
        {
            return null;
        }

        try
        {
            using ServiceProvider services = BuildCsvgServices(settings);
            Cs2DirectoryUtil paths = services.GetRequiredService<Cs2DirectoryUtil>();
            string gameInfoPath = paths.Cs2GameInfoFilePath;
            if (!File.Exists(gameInfoPath))
            {
                return null;
            }

            bool patched = File.ReadAllText(gameInfoPath).Contains(GameInfoMarker, StringComparison.Ordinal);
            bool pluginPresent = Directory.Exists(paths.CsvgPluginDirectory);
            return new LeftoverState(patched, pluginPresent);
        }
        catch
        {
            // Install discovery failing (no Steam/CS2 on this machine) means there is nothing
            // to recover — detection must never surface an error of its own.
            return null;
        }
    }

    /// <summary>
    ///     Restores the install: un-patches <c>gameinfo.gi</c> from CSVG's backup and removes the
    ///     plugin files. Also the permanent-disable uninstall path. Throws on hard failures (the
    ///     caller surfaces them with the <c>csvg restore</c> fallback copy).
    /// </summary>
    public static void Restore(LiveSyncSettings settings, Action<string>? log = null)
    {
        if (IsMock(settings))
        {
            return;
        }

        using ServiceProvider services = BuildCsvgServices(settings);
        Cs2DirectoryUtil paths = services.GetRequiredService<Cs2DirectoryUtil>();
        string gameInfoPath = paths.Cs2GameInfoFilePath;

        if (File.Exists(gameInfoPath)
            && File.ReadAllText(gameInfoPath).Contains(GameInfoMarker, StringComparison.Ordinal))
        {
            log?.Invoke($"Restoring patched gameinfo.gi from CSVG backup: {gameInfoPath}");
            services.GetRequiredService<IBackupManager>().RestorePath(gameInfoPath);
        }

        log?.Invoke("Removing CSVG plugin files from the CS2 install.");
        services.GetRequiredService<IPluginInstaller>().UninstallPluginFiles();

        LeftoverState? after = Detect(settings);
        if (after?.Any == true)
        {
            throw new InvalidOperationException(
                "The CS2 install still carries CSVG modifications after restore — "
                + "run `csvg restore` (or `csvg doctor`) from the CSVG CLI to repair it manually.");
        }

        log?.Invoke("CS2 install restored.");
    }

    private static bool IsMock(LiveSyncSettings settings) =>
        settings.MockMode || !string.IsNullOrWhiteSpace(settings.ExternalMockServerPath);

    private static ServiceProvider BuildCsvgServices(LiveSyncSettings settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CsvgWebHost.ProjectSettings(settings))
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCs2VideoGeneratorCore(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>What a crashed session left behind. <see cref="Any" /> gates the offer-restore UI.</summary>
    internal sealed record LeftoverState(bool GameInfoPatched, bool PluginFilesPresent)
    {
        public bool Any => GameInfoPatched || PluginFilesPresent;
    }
}

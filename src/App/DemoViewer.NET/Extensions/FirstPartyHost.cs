#region

using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     What the Strat Book reads of the app beyond the SDK: where the user's own work lived before extensions had
///     folders of their own, one member per store, so that data stays where users have it. Nothing here is a root
///     folder. What the Strat Book can rebuild lives in its own folders through the SDK. Every member is null in
///     the browser build, which has no filesystem.
/// </summary>
public sealed class FirstPartyHost
{
    private readonly string? _palettes = AppPaths.PalettesDirectory;
    private readonly string? _legacyLineups = FileIn(AppPaths.DemoCacheDir, "grenade-lineups.json.gz");
    private readonly string? _legacyMiningState = FileIn(AppPaths.ConfigRoot, "strat-mining.json");

    /// <summary>The strats folder: one folder per book and the books' index.</summary>
    public string? StratsDirectory { get; } = AppPaths.StratsDir;

    /// <summary>The tags folder: the tag index and one tag file per demo.</summary>
    public string? TagsDirectory { get; } = AppPaths.TagsDir;

    /// <summary>The user's teams file, <c>teams.json</c>.</summary>
    public string? TeamsFile { get; } = ConfigFile("teams.json");

    /// <summary>The Watched Situations file, <c>watched-situations.json</c>.</summary>
    public string? WatchedSituationsFile { get; } = ConfigFile("watched-situations.json");

    /// <summary>The Opponent Dossier's veto history, <c>veto-history.json</c>.</summary>
    public string? VetoHistoryFile { get; } = ConfigFile("veto-history.json");

    /// <summary>The Opponent Dossier's notes, <c>dossier-notes.json</c>.</summary>
    public string? DossierNotesFile { get; } = ConfigFile("dossier-notes.json");

    /// <summary>The Suggested Tags folder: the tuning profile and the learned site regions.</summary>
    public string? SuggestedTagsDirectory { get; } = AppPaths.SuggestedTagsDirectory;

    /// <summary>The rendered lineup clips folder.</summary>
    public string? LineupClipsDirectory { get; } = ConfigFile("lineup-clips");

    /// <summary>
    ///     The app's zones overlay folder, which the core 2D Playback tab reads too. Read only: the zone loader
    ///     takes a folder and reads each map's overlay file from it per call, so an edit shows without a restart.
    /// </summary>
    public string? ZonesDirectory { get; } = AppPaths.ZonesDirectory;

    /// <summary>The tag palettes folder, created on first use where it can be.</summary>
    public string? EnsurePalettesDirectory()
    {
        if (_palettes is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_palettes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The palette scan checks the folder exists and offers the built-in palette alone.
        }

        return _palettes;
    }

    /// <summary>
    ///     The grenade lineups file where an older build kept it in the shared cache, read once to copy into the
    ///     extension's own cache folder. Null when there is none.
    /// </summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    public byte[]? ReadLegacyGrenadeLineups() => ReadIfPresent(_legacyLineups);

    /// <summary>
    ///     Strat Mining's dismissed and promoted patterns where an older build kept them, read once to copy into
    ///     the extension's own config folder. Null when there is none.
    /// </summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    public byte[]? ReadLegacyStratMiningState() => ReadIfPresent(_legacyMiningState);

    private static string? ConfigFile(string name) => FileIn(AppPaths.ConfigRoot, name);

    private static string? FileIn(string? folder, string name) => folder is null ? null : Path.Combine(folder, name);

    private static byte[]? ReadIfPresent(string? path) => path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
}

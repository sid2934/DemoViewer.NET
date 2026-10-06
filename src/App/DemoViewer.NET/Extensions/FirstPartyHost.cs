#region

using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     What a first-party extension reads of the app beyond the SDK. The folders are where the Strat Book kept
///     the user's own work (strats, tags, teams, palettes, the Suggested Tags profile, lineup clips) and its
///     shared caches before extensions had folders of their own; that data stays where users have it. The zones
///     overlay is the app's own, read only.
/// </summary>
public sealed class FirstPartyHost
{
    private readonly string? _palettes = AppPaths.PalettesDirectory;

    /// <summary>The config root, or null in the browser build.</summary>
    public string? ConfigRoot { get; } = AppPaths.ConfigRoot;

    /// <summary>The cache root under the config root, or null in the browser build.</summary>
    public string? CacheRoot { get; } = AppPaths.DemoCacheDir;

    /// <summary>The strats folder, or null.</summary>
    public string? StratsDirectory { get; } = AppPaths.StratsDir;

    /// <summary>The tags folder, or null.</summary>
    public string? TagsDirectory { get; } = AppPaths.TagsDir;

    /// <summary>The Suggested Tags folder (profile and site regions), or null.</summary>
    public string? SuggestedTagsDirectory { get; } = AppPaths.SuggestedTagsDirectory;

    /// <summary>The user's zones overlay folder, or null.</summary>
    public string? ZonesDirectory { get; } = AppPaths.ZonesDirectory;

    /// <summary>The tag palettes folder, created on first use where it can be, or null.</summary>
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
}

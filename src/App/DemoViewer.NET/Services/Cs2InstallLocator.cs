#region

using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     The outcome of a CS2 demos-folder lookup: the found folder (or null) plus the Steam library
///     directories that were actually searched — so the UI can either offer the folder or explain, with the
///     concrete locations it checked, why it couldn't be auto-detected.
/// </summary>
/// <param name="DemosDirectory">The CS2 downloaded-demos ("replays") folder, or null when not found.</param>
/// <param name="SearchedDirectories">
///     The existing Steam library roots examined for a CS2 install, in the order searched. Empty when no
///     Steam installation was found at all (nothing to search).
/// </param>
public sealed record Cs2DemosLookup(string? DemosDirectory, IReadOnlyList<string> SearchedDirectories);

/// <summary>
///     Best-effort, cross-platform locator for the CS2 install and its downloaded-demos ("replays") folder —
///     where the in-game client saves GOTV / competitive match demos (<c>…/game/csgo/replays</c>, verified
///     against the CS2 <c>sv_replaysdir</c> convar default). Used by the first-run wizard to offer the user's
///     real demo folder with one click.
///     <para>
///         Pure filesystem work (no registry, no VRF, no native deps) so it is safe in the cross-platform App
///         project. It finds Steam's root from the well-known per-OS install locations, reads
///         <c>steamapps/libraryfolders.vdf</c> to discover every Steam library (games can live on other
///         drives), and looks for the CS2 <c>common</c> folder under each. Custom Steam install roots on
///         Windows (installed outside Program Files) are the one gap — detection is best-effort by design.
///     </para>
/// </summary>
public static partial class Cs2InstallLocator
{
    // The CS2 install directory name under a library's steamapps/common.
    private const string Cs2CommonFolderName = "Counter-Strike Global Offensive";

    /// <summary>
    ///     Looks up the CS2 downloaded-demos ("replays") folder and reports what was searched. The demos
    ///     folder is returned only when it exists; the searched list always reflects the real Steam libraries
    ///     examined (empty when no Steam install was found). Always empty / null on WASM (no filesystem).
    /// </summary>
    public static Cs2DemosLookup FindDemos()
    {
        if (OperatingSystem.IsBrowser())
        {
            return new Cs2DemosLookup(null, []);
        }

        return FindDemos(DefaultSteamRoots(), Directory.Exists, SafeReadAllText);
    }

    /// <summary>
    ///     Testable core: given candidate Steam roots and injected filesystem probes, returns the first
    ///     existing CS2 replays directory found across all Steam libraries (or null), plus the existing
    ///     library roots searched. Deterministic — candidates are tried in order.
    /// </summary>
    internal static Cs2DemosLookup FindDemos(
        IEnumerable<string> steamRoots, Func<string, bool> dirExists, Func<string, string?> readText)
    {
        List<string> searched = [];
        HashSet<string> seenLibraries = new(StringComparer.OrdinalIgnoreCase);

        foreach (string steamRoot in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(steamRoot) || !dirExists(steamRoot))
            {
                continue;
            }

            foreach (string library in LibraryFolders(steamRoot, readText))
            {
                if (!seenLibraries.Add(library) || !dirExists(library))
                {
                    continue; // duplicate across roots, or a stale libraryfolders.vdf entry.
                }

                searched.Add(library);

                string install = Path.Combine(library, "steamapps", "common", Cs2CommonFolderName);
                if (!dirExists(install))
                {
                    continue;
                }

                string replays = Path.Combine(install, "game", "csgo", "replays");
                if (dirExists(replays))
                {
                    return new Cs2DemosLookup(replays, searched);
                }
            }
        }

        return new Cs2DemosLookup(null, searched);
    }

    // Every Steam library root reachable from a Steam install root: the root itself (its own steamapps/common)
    // plus every "path" entry in steamapps/libraryfolders.vdf (games can be installed on other drives). The vdf
    // lists library ROOTS (e.g. "D:\\SteamLibrary"), each of which has its own steamapps/common beneath it.
    private static IEnumerable<string> LibraryFolders(string steamRoot, Func<string, string?> readText)
    {
        yield return steamRoot;

        // Modern location is steamapps/libraryfolders.vdf; a very old layout used config/libraryfolders.vdf.
        string[] vdfCandidates =
        [
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf")
        ];

        foreach (string vdf in vdfCandidates)
        {
            string? content = readText(vdf);
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            foreach (Match m in LibraryPathRegex().Matches(content))
            {
                // VDF strings escape backslashes ("D:\\SteamLibrary"); unescape to a real path.
                string path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }
    }

    // Well-known Steam install roots per OS, in priority order. No registry read (keeps the App project free of
    // a Windows-only dependency); the default Program Files locations cover the overwhelming majority.
    private static IEnumerable<string> DefaultSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";

            string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Steam");
            }

            yield break;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam");

            yield break;
        }

        // Linux — native, plus the common symlink roots and the Flatpak sandbox.
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }

    private static string? SafeReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null; // unreadable / locked vdf — best-effort, treat as absent.
        }
    }

    [GeneratedRegex("\"path\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}

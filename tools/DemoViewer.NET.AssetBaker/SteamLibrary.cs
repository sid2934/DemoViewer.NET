#region

using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Finds the local Counter-Strike 2 install so the icon bake can read <c>pak01_dir.vpk</c> straight
///     out of it.
///     <para>
///         <b>Why this exists at all.</b> The map bake reads a hand-staged <c>cs2-assets/</c> cache: radar
///         textures and overview txt files have to be pulled out of per-map vpks anyway, so staging them
///         once is cheaper than re-opening the archives every run. Icons need none of that — they all live
///         in one archive, <c>game/csgo/pak01_dir.vpk</c>, so the bake reads it in place and the cache
///         never enters the picture. A developer with CS2 installed can run <c>--icons</c> with no setup.
///     </para>
/// </summary>
public static partial class SteamLibrary
{
    private const string Cs2AppId = "730";
    private const string Cs2Dir = "Counter-Strike Global Offensive";

    /// <summary>
    ///     Locates <c>pak01_dir.vpk</c>, or returns null when CS2 cannot be found. Pass an explicit path to
    ///     skip discovery entirely — a CI runner or a non-Steam copy has no library folders to walk.
    /// </summary>
    /// <param name="explicitPath">A caller-supplied vpk or CS2 root, or null to auto-discover.</param>
    public static string? FindCs2Pak(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // Accept either the vpk itself or the install root, so --cs2 takes whichever the caller has.
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }

            string nested = Path.Combine(explicitPath, "game", "csgo", "pak01_dir.vpk");
            return File.Exists(nested) ? nested : null;
        }

        foreach (string root in SteamRoots())
        {
            foreach (string library in LibraryFolders(root).Prepend(root))
            {
                string pak = Path.Combine(
                    library, "steamapps", "common", Cs2Dir, "game", "csgo", "pak01_dir.vpk");
                if (File.Exists(pak))
                {
                    return pak;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Locates the per-map archive directory (<c>game/csgo/maps</c>), or returns null when CS2 cannot
    ///     be found. This is what the collision-only bake reads: the triangle soups live in the per-map
    ///     vpks, so that mode needs no staged <c>cs2-assets/</c> cache either.
    /// </summary>
    /// <param name="explicitPath">
    ///     A caller-supplied vpk, CS2 root, or the maps directory itself, or null to auto-discover. The
    ///     maps directory is accepted directly so a copied-out subset with no <c>pak01_dir.vpk</c> beside
    ///     it still works.
    /// </param>
    public static string? FindCs2MapsDir(string? explicitPath = null)
    {
        string? pak = FindCs2Pak(explicitPath);
        if (pak is null)
        {
            return !string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath)
                ? explicitPath
                : null;
        }

        string maps = Path.Combine(Path.GetDirectoryName(pak)!, "maps");
        return Directory.Exists(maps) ? maps : null;
    }

    // The platform's default Steam locations. Only ever a starting point: libraryfolders.vdf below is
    // what actually says where games live, and a second drive is the common case, not the exception.
    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string p in new[]
                     {
                         @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam"
                     })
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Steam");
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
        }
    }

    // libraryfolders.vdf is Valve's KeyValues, not JSON. We need exactly one thing from it — the "path"
    // of each library that lists app 730 — so a targeted regex beats taking a KeyValues parser as a
    // dependency for a dev tool. Libraries that do not list 730 are skipped rather than probed.
    private static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }

        string text;
        try { text = File.ReadAllText(vdf); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (Match block in LibraryBlock().Matches(text))
        {
            if (!block.Value.Contains($"\"{Cs2AppId}\"", StringComparison.Ordinal))
            {
                continue;
            }

            Match path = LibraryPath().Match(block.Value);
            if (path.Success)
            {
                yield return path.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
            }
        }
    }

    // One numbered library entry, from its "path" through the end of its "apps" list.
    [GeneratedRegex("\"path\"[^\"]*\"[^\"]*\".*?\"apps\".*?\\}", RegexOptions.Singleline)]
    private static partial Regex LibraryBlock();

    [GeneratedRegex("\"path\"[^\"]*\"([^\"]*)\"")]
    private static partial Regex LibraryPath();
}

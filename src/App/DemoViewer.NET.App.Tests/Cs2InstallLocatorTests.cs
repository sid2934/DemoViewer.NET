#region

using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure-logic coverage of <see cref="Cs2InstallLocator" />'s testable core over a synthetic filesystem
///     (injected dir-exists + vdf-read), so it runs anywhere with no real Steam install. Covers: the primary
///     Steam library, a secondary library discovered via <c>libraryfolders.vdf</c> (with VDF backslash
///     unescaping), the no-install case, the CS2-present-but-no-replays case, and the searched-directory
///     reporting used by the not-found notice.
/// </summary>
public class Cs2InstallLocatorTests
{
    private const string SteamRoot = "/steam";

    private static string CommonInstall(string libraryRoot) =>
        Path.Combine(libraryRoot, "steamapps", "common", "Counter-Strike Global Offensive");

    private static string Replays(string libraryRoot) =>
        Path.Combine(CommonInstall(libraryRoot), "game", "csgo", "replays");

    private static Cs2DemosLookup Find(HashSet<string> dirs, Dictionary<string, string>? files = null) =>
        Cs2InstallLocator.FindDemos(
            [SteamRoot],
            dirs.Contains,
            path => (files ?? []).GetValueOrDefault(path));

    [Test]
    public async Task FindsReplays_UnderPrimarySteamLibrary()
    {
        string replays = Replays(SteamRoot);
        HashSet<string> dirs = new(StringComparer.Ordinal)
        {
            SteamRoot,
            CommonInstall(SteamRoot),
            replays
        };

        await Assert.That(Find(dirs).DemosDirectory).IsEqualTo(replays);
    }

    [Test]
    public async Task FindsReplays_OnSecondaryLibrary_FromLibraryFoldersVdf_WithBackslashUnescape()
    {
        // The vdf lists a second library on another drive with escaped backslashes, Windows-style.
        const string secondLibrary = @"D:\SteamLibrary";
        string replays = Replays(secondLibrary);

        HashSet<string> dirs = new(StringComparer.Ordinal)
        {
            SteamRoot, // the root exists, but CS2 is NOT under it
            secondLibrary,
            CommonInstall(secondLibrary),
            replays
        };
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] =
                "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}"
        };

        await Assert.That(Find(dirs, files).DemosDirectory).IsEqualTo(replays);
    }

    [Test]
    public async Task ReturnsNull_WhenNoCs2Install_ButReportsSearchedSteamLibrary()
    {
        HashSet<string> dirs = new(StringComparer.Ordinal)
        {
            SteamRoot
        };

        Cs2DemosLookup result = Find(dirs);
        await Assert.That(result.DemosDirectory).IsNull();
        await Assert.That(result.SearchedDirectories).Contains(SteamRoot)
            .Because("the notice must list the Steam library we checked");
    }

    [Test]
    public async Task ReturnsNull_WhenCs2Installed_ButReplaysFolderMissing()
    {
        // CS2 is installed but the user has never downloaded a demo, so replays/ does not exist yet.
        HashSet<string> dirs = new(StringComparer.Ordinal)
        {
            SteamRoot,
            CommonInstall(SteamRoot)
        };

        await Assert.That(Find(dirs).DemosDirectory).IsNull();
    }

    [Test]
    public async Task NoSteamInstall_ReportsNothingSearched()
    {
        // No candidate Steam root exists at all → nothing to search.
        Cs2DemosLookup result = Cs2InstallLocator.FindDemos(
            [SteamRoot], _ => false, _ => null);

        await Assert.That(result.DemosDirectory).IsNull();
        await Assert.That(result.SearchedDirectories).IsEmpty();
    }
}

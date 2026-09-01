#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pins the name of the per-user config directory.
///     <para>
///         The name lives in <see cref="RuleSetLocator" />, which ships in CS2DemoKit.Analysis and
///         defaults to the library's own name. <see cref="AppPaths" /> claims it back for this
///         application at assembly load. If that ever stops happening, a package upgrade renaming
///         the property or the module initializer being dropped in a refactor, every install's
///         settings, session state, recents, bookmarks, library cache and user rules resolve to a
///         directory that has never held any of them, and the app comes up looking factory-fresh.
///         That failure is silent by nature, so it gets a test rather than a comment.
///     </para>
/// </summary>
public class AppConfigDirectoryNameTests
{
    /// <summary>The app's name wins over the library default.</summary>
    [Test]
    public async Task ConfigDirectoryName_IsClaimedByTheApp()
    {
        // Call it directly rather than relying on the module initializer: a const like
        // ConfigDirEnvVar is inlined by the compiler, so touching one never loads the assembly and
        // never fires the initializer. The app's own startup calls this method for the same reason.
        AppPaths.ClaimConfigDirectoryName();

        await Assert.That(RuleSetLocator.AppConfigDirName).IsEqualTo("DemoViewer.NET");
    }

    /// <summary>
    ///     The resolved root ends in that name on every platform. Reads through
    ///     <see cref="RuleSetLocator.GetConfigRoot" /> rather than <see cref="AppPaths.ConfigRoot" />
    ///     because the suite points the latter at a temp directory (see <c>SessionIsolation</c>).
    /// </summary>
    [Test]
    public async Task ConfigRoot_EndsWithTheAppDirectoryName()
    {
        AppPaths.ClaimConfigDirectoryName();

        string root = RuleSetLocator.GetConfigRoot();

        await Assert.That(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)))
            .IsEqualTo("DemoViewer.NET");
    }
}

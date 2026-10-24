#region

using DemoViewer.NET.Playback2D.Pipeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Locates the committed fixture corpus. The repo copy wins; the sibling <c>fixtures</c> directory
///     the csproj copies next to the test binary is the fallback, which keeps the suite runnable from a
///     run that cannot see a checkout.
/// </summary>
internal static class FixtureCorpus
{
    /// <summary>The corpus root, i.e. the directory holding <c>scenes/</c> and <c>goldens/</c>.</summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>Every committed <c>*.scene.json</c>, sorted, so data-driven cases are deterministic.</summary>
    public static IReadOnlyList<string> ScenePaths()
    {
        string scenes = Path.Combine(Root, "scenes");
        if (!Directory.Exists(scenes))
        {
            return [];
        }

        string[] paths = Directory.GetFiles(scenes, "*.scene.json", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }

    /// <summary>Loads one named fixture, e.g. <c>synthetic-tenplayers</c>.</summary>
    /// <param name="name">The fixture's base name, without the <c>.scene.json</c> suffix.</param>
    public static SceneFixture Load(string name) =>
        SceneFixture.Load(Path.Combine(Root, "scenes", name + ".scene.json"));

    // The REPO copy wins over the copied-beside one. Golden regeneration writes through this path, and a
    // golden written into artifacts/bin is a golden nobody can commit.
    private static string ResolveRoot()
    {
        if (TestTiers.FindRepoRoot() is { } repo)
        {
            string candidate = Path.Combine(repo, "tests", "fixtures", "playback2d");
            if (Directory.Exists(Path.Combine(candidate, "scenes")))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "fixtures");
    }
}

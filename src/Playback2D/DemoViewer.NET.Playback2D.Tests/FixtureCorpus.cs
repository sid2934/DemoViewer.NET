#region

using DemoViewer.NET.Playback2D.Pipeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Locates the committed fixture corpus. The csproj copies <c>tests/fixtures/playback2d/**/*.json</c>
///     next to the test binary, so the normal path is a sibling directory; the repo-walk fallback keeps
///     the suite runnable from an IDE that has not re-run the copy step.
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

    private static string ResolveRoot()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "fixtures");
        if (Directory.Exists(Path.Combine(beside, "scenes")))
        {
            return beside;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "playback2d");
            if (Directory.Exists(Path.Combine(candidate, "scenes")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return beside;
    }
}

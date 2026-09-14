#region

using System.Text.RegularExpressions;
using DemoViewer.NET.GameIcons;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Every icon key written into the app's own source must exist in the bake.
///     <para>
///         The fallback pattern is what keeps a missing key from breaking a screen — but a key that was
///         never right in the first place should not reach a user at all, and a runtime warning nobody
///         reads is not a gate. This turns a typo, or an icon a CS2 update removed, into a red build the
///         moment the bake changes.
///     </para>
///     <para>
///         Only <b>literal</b> keys can be checked: most keys are composed at runtime from demo data
///         (<c>"equipment/" + weapon</c>) and no static check can cover those. That is exactly the case
///         the fallback exists for.
///     </para>
/// </summary>
public class IconKeyReferenceTests
{
    private const char Quote = '"';

    /// <summary>
    ///     Keys that are absent on purpose, so the missing-state can be rendered and photographed. Named
    ///     rather than excluded by project, so the surrounding real keys in the same file stay guarded.
    /// </summary>
    private const string PlaceholderMarker = "missing_on_purpose";

    // The namespaces are listed explicitly so an arbitrary slashed string is not mistaken for a key.
    private const string Namespaces = "(?:equipment|modifier|ui|rank|premier|map)/[a-z0-9_/]+";

    // Key="ui/bomb_c4" in XAML.
    private static readonly Regex XamlKey =
        new("Key=" + Quote + "(" + Namespaces + ")" + Quote, RegexOptions.Compiled);

    // "modifier/headshot" as a C# string literal.
    private static readonly Regex CsharpKey =
        new(Quote + "(" + Namespaces + ")" + Quote, RegexOptions.Compiled);

    [Test]
    public async Task EveryLiteralIconKeyInSource_ExistsInTheBake()
    {
        string root = RepoRoot();
        List<string> bad = [];
        int checkedKeys = 0;

        foreach (string file in Sources(root))
        {
            string text = File.ReadAllText(file);
            bool xaml = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);

            foreach (Match m in (xaml ? XamlKey : CsharpKey).Matches(text))
            {
                string key = m.Groups[1].Value;
                checkedKeys++;

                if (key.Contains(PlaceholderMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                // Blank is a legitimate answer, not a miss: CS2 ships empty artwork on purpose.
                if (IconCatalogue.Get(key) is null && !IconCatalogue.IsBlank(key))
                {
                    bad.Add($"{Path.GetRelativePath(root, file)}: {key}");
                }
            }
        }

        Console.WriteLine($"[icon-keys] {checkedKeys} literal keys checked across the app source");

        // A guard that matches nothing passes vacuously and would hide a broken pattern.
        await Assert.That(checkedKeys).IsGreaterThan(5);
        await Assert.That(bad).IsEmpty();
    }

    private static IEnumerable<string> Sources(string root)
    {
        string src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            yield break;
        }

        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

        // Test projects are out of scope: they are not shipping code and legitimately synthesise keys.
        // UiCapture stays IN scope — it renders the real galleries, and those keys are worth guarding —
        // with only its deliberate placeholders exempted by name (see PlaceholderMarker).
        string tests = $".Tests{Path.DirectorySeparatorChar}";

        foreach (string f in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains(obj, StringComparison.Ordinal) || f.Contains(bin, StringComparison.Ordinal)
                                                          || f.Contains(tests, StringComparison.Ordinal))
            {
                continue;
            }

            if (f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                yield return f;
            }
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found from " + AppContext.BaseDirectory);
    }
}

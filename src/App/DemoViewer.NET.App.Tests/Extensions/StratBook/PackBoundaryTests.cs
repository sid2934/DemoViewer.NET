#region

using System.Text.RegularExpressions;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The pack has boundaries: core-to-pack edges are listed in design §4.1, and every one is scheduled for removal
///     in a specific item. This test scans for accidental new edges (undeclared dependencies on pack-owned namespaces)
///     and documents the known ones with their removal schedule, so a new edge registers as a test failure with a
///     clear diff.
/// </summary>
public class PackBoundaryTests
{
    /// <summary>Known edges between core and the pack, with the item that removes each.</summary>
    private static readonly (string FilePath, string Namespace, string RemovalItem)[] AllowedEdges =
    [
        // Composition root; allowed under option (a); moves to the heads at item 29.
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Extensions.StratBook", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Modules.Situations", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Modules.StratBook", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Modules.SuggestedTags", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Modules.UtilityBook", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Services.Provenance", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Services.RoundFacts", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Services.RoundIndex", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Services.Strats", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Services.Tags", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.Services.Teams", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.ViewModels.StratBook", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.ViewModels.SuggestedTags", "composition root"),
        ("src/App/DemoViewer.NET/App.axaml.cs", "DemoViewer.NET.ViewModels.Teams", "composition root"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.RoundTagger", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.RoundTagger.Palette", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.RoundTagger.Review", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.RoundTagger.Timeline", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.Situations", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.StratBook", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Modules.SuggestedTags", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Services.RoundFacts", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Services.Strats", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Services.Tags", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.Services.Teams", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs", "DemoViewer.NET.ViewModels.StratBook", "item 15"),
        ("src/App/DemoViewer.NET/Modules/Playback2D/Timeline/RoundTrack.cs", "DemoViewer.NET.Services.RoundFacts", "item 2"),
        ("src/App/DemoViewer.NET/Services/Review/ReviewQueue.cs", "DemoViewer.NET.Services.Tags", "item 25"),
        ("src/App/DemoViewer.NET/Services/Zones/AssetZonePlaceResolverSource.cs", "DemoViewer.NET.Services.RoundIndex", "item 25"),
        ("src/App/DemoViewer.NET/Services/Zones/AssetZonePlaceResolverSource.cs", "DemoViewer.NET.Services.Strats", "item 25"),
        ("src/App/DemoViewer.NET/ViewModels/Settings/SuggestedTagsTuningViewModel.cs", "DemoViewer.NET.Modules.SuggestedTags", "item 14"),
        ("src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs", "DemoViewer.NET.Modules.SuggestedTags", "item 14"),
        ("src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs", "DemoViewer.NET.Services.Provenance", "item 22"),
        ("src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs", "DemoViewer.NET.Services.Teams", "item 22"),
        ("src/App/DemoViewer.NET/ViewModels/Settings/SettingsViewModel.cs", "DemoViewer.NET.Modules.SuggestedTags", "item 14"),
        ("src/App/DemoViewer.NET/ViewModels/Settings/SettingsViewModel.cs", "DemoViewer.NET.Extensions.StratBook", "item 14"),
        ("src/App/DemoViewer.NET/ViewModels/Library/LibraryTabViewModel.cs", "DemoViewer.NET.Services.Provenance", "item 22"),
        ("src/App/DemoViewer.NET/ViewModels/Library/LibraryTabViewModel.cs", "DemoViewer.NET.Services.Teams", "item 22"),
        ("src/App/DemoViewer.NET/ViewModels/Library/LibraryTabViewModel.cs", "DemoViewer.NET.ViewModels.Teams", "item 22"),
        ("src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml", "DemoViewer.NET.Modules.RoundTagger", "item 17"),
        ("src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs", "DemoViewer.NET.Modules.SuggestedTags", "item 21"),
        ("src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs", "DemoViewer.NET.Modules.UtilityBook", "item 21"),
        ("src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs", "DemoViewer.NET.Services.RoundFacts", "item 21"),
        ("src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs", "DemoViewer.NET.Services.RoundIndex", "item 21"),
    ];

    [Test]
    public async Task CoreNamespaces_DoNotReferencePack_Except_AllowedEdges()
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");

        // Build the set of pack-owned namespaces: those where EVERY file declaring the namespace lives under Extensions/StratBook.
        HashSet<string> packOwnedNamespaces = FindPackOwnedNamespaces(repoRoot);

        // Scan every .cs and .axaml file under src/App/DemoViewer.NET that is NOT under Extensions/,
        // looking for using/xmlns/fully-qualified references to pack-owned namespaces.
        string appRoot = Path.Combine(repoRoot, "src", "App", "DemoViewer.NET");
        HashSet<string> allowedSet = new(AllowedEdges.Select(e => $"{e.FilePath}|{e.Namespace}"), StringComparer.Ordinal);

        List<(string FilePath, string Namespace)> violations = new();
        foreach (string file in Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories))
        {
            // Skip build output and extension files.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}Extensions{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content = File.ReadAllText(file);
            string relPath = Path.GetRelativePath(repoRoot, file);

            foreach (string ns in packOwnedNamespaces)
            {
                // Check for using statements, xmlns, and fully-qualified references.
                if (ContainsPackReference(content, ns))
                {
                    string key = $"{relPath}|{ns}";
                    if (!allowedSet.Contains(key))
                    {
                        violations.Add((relPath, ns));
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            string message = "Core namespaces reference pack-owned namespaces:\n\n";
            foreach (var (filePath, ns) in violations.OrderBy(v => v.FilePath).ThenBy(v => v.Namespace))
            {
                message += $"  {filePath} → {ns}\n";
            }

            message += "\nKnown allowed edges:\n";
            foreach (var (filePath, ns, item) in AllowedEdges.OrderBy(e => e.FilePath).ThenBy(e => e.Namespace))
            {
                message += $"  {filePath} → {ns} (removed by {item})\n";
            }

            await Assert.That(violations.Count).IsEqualTo(0).Because(message);
        }
    }

    /// <summary>
    ///     Scans all namespaces declared under Extensions/StratBook and returns those where EVERY file declaring
    ///     the namespace lives under Extensions/StratBook (i.e., the namespace is pack-owned, not shared).
    /// </summary>
    private static HashSet<string> FindPackOwnedNamespaces(string repoRoot)
    {
        string packRoot = Path.Combine(repoRoot, "src", "App", "DemoViewer.NET", "Extensions", "StratBook");

        // Collect all namespaces declared in the pack, and track which files declare each.
        Dictionary<string, HashSet<string>> namespaceToFiles = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(packRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string content = File.ReadAllText(file);

            // Extract namespace declarations. Match "namespace Foo.Bar;" or "namespace Foo.Bar {".
            foreach (Match match in Regex.Matches(content, @"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", RegexOptions.Multiline))
            {
                string ns = match.Groups[1].Value;
                if (!namespaceToFiles.TryGetValue(ns, out var files))
                {
                    files = new HashSet<string>(StringComparer.Ordinal);
                    namespaceToFiles[ns] = files;
                }

                files.Add(file);
            }
        }

        // Now find files that declare the SAME namespace but live outside the pack.
        string coreRoot = Path.Combine(repoRoot, "src", "App", "DemoViewer.NET");
        HashSet<string> coreNamespaces = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}Extensions{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string content = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(content, @"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", RegexOptions.Multiline))
            {
                coreNamespaces.Add(match.Groups[1].Value);
            }
        }

        // A namespace is pack-owned iff it appears in the pack AND never appears in the core (outside Extensions/).
        HashSet<string> packOwned = new(StringComparer.Ordinal);
        foreach (string ns in namespaceToFiles.Keys)
        {
            if (!coreNamespaces.Contains(ns))
            {
                packOwned.Add(ns);
            }
        }

        return packOwned;
    }

    /// <summary>
    ///     Returns true if the content references the namespace via a using statement, xmlns attribute, or
    ///     fully-qualified type name (e.g. Namespace.Type).
    /// </summary>
    private static bool ContainsPackReference(string content, string ns)
    {
        // 1. Using statement: "using Namespace;" or "using Namespace.Sub;"
        if (Regex.IsMatch(content, @"\busing\s+" + Regex.Escape(ns) + @"\b", RegexOptions.Multiline))
        {
            return true;
        }

        // 2. xmlns attribute in XAML: xmlns="clr-namespace:Namespace;..."
        if (content.Contains(@$"clr-namespace:{ns}", StringComparison.Ordinal))
        {
            return true;
        }

        // 3. Fully-qualified type reference: "Namespace.Type" (word boundary on both sides).
        // Use a heuristic: the namespace followed by a dot and at least one uppercase letter (typical start of a type name).
        if (Regex.IsMatch(content, Regex.Escape(ns) + @"\.[A-Z][A-Za-z0-9_]*", RegexOptions.None))
        {
            return true;
        }

        return false;
    }
}

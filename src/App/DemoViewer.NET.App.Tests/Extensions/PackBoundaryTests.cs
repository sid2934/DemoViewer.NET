#region

using System.Text.RegularExpressions;
using System.Xml.Linq;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The pack has boundaries. The compiler enforces the hard one: the app project does not
///     reference the extension project, so a core type cannot name a pack type. Two scans stay beside it: the
///     csproj check that keeps the reference out, and the text scan over core sources
///     for pack-owned namespaces, which also catches what the compiler does not (doc comments, XAML
///     namespaces) and documents the edges that remain with the item that removes each.
/// </summary>
public class PackBoundaryTests
{
    private const string AppProject = "src/App/DemoViewer.NET";
    private const string PackProject = "src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook";
    private const string Playback2DSceneProject = "src/Playback2D/DemoViewer.NET.Playback2D.Scene";
    private const string Playback2DCoreProject = "src/Playback2D/DemoViewer.NET.Playback2D.Core";
    private const string Playback2DPipelineProject = "src/Playback2D/DemoViewer.NET.Playback2D.Pipeline";

    /// <summary>Known mentions of a pack-owned namespace in core text, with the item that removes each.</summary>
    private static readonly (string FilePath, string Namespace, string RemovalItem)[] AllowedEdges =
    [
    ];

    [Test]
    public async Task AppProject_HasNoProjectReferenceToAnExtension()
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");

        XDocument csproj = XDocument.Load(Path.Combine(repoRoot, AppProject, "DemoViewer.NET.csproj"));
        List<string> extensionReferences = csproj.Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include") ?? "")
            .Where(include => include.Replace('\\', '/').Contains("/Extensions/", StringComparison.Ordinal))
            .ToList();

        await Assert.That(extensionReferences).IsEmpty()
            .Because("the app assembly must not reference an extension; the heads compose both");
    }

    /// <summary>
    ///     Half of the boundary: the strat-only types moved out of Playback2D.Core and
    ///     Pipeline (the keyframe track, the strat frame source, the token tool, the guides layer) must not
    ///     pull the extension back in as a dependency of the two assemblies they left.
    /// </summary>
    [Test]
    [Arguments(Playback2DSceneProject, "DemoViewer.NET.Playback2D.Scene.csproj")]
    [Arguments(Playback2DCoreProject, "DemoViewer.NET.Playback2D.Core.csproj")]
    [Arguments(Playback2DPipelineProject, "DemoViewer.NET.Playback2D.Pipeline.csproj")]
    public async Task Playback2DProject_HasNoProjectReferenceToAnExtension(string projectDir, string csprojName)
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");

        XDocument csproj = XDocument.Load(Path.Combine(repoRoot, projectDir, csprojName));
        List<string> extensionReferences = csproj.Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include") ?? "")
            .Where(include => include.Replace('\\', '/').Contains("/Extensions/", StringComparison.Ordinal))
            .ToList();

        await Assert.That(extensionReferences).IsEmpty()
            .Because($"{csprojName} must not reference an extension; the extension references it, not the other way round");
    }

    /// <summary>
    ///     The pack builds on what the app and the scene publish: neither the app nor a Playback2D assembly
    ///     grants it their internals. Its test project keeps its grants.
    /// </summary>
    [Test]
    public async Task NoAppOrPlayback2DAssembly_GrantsThePackItsInternals()
    {
        System.Reflection.Assembly[] granting =
        [
            typeof(App).Assembly, typeof(Playback2D.Core.Scene2DFrame).Assembly,
            typeof(Playback2D.Core.Layers.RadarLayer).Assembly, typeof(Playback2D.Pipeline.Assets.LoadedMapAsset).Assembly
        ];

        string[] grants =
        [
            .. granting.SelectMany(a => a.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false)
                    .Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
                    .Where(g => g.AssemblyName == "DemoViewer.NET.Extensions.StratBook")
                    .Select(_ => a.GetName().Name!))
        ];

        await Assert.That(grants).IsEmpty();
    }

    [Test]
    public async Task CoreNamespaces_DoNotReferencePack_Except_AllowedEdges()
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");

        // Pack-owned namespaces: declared in the extension project and never in the app project. A namespace
        // both declare (Services.Zones, Services.RoundFacts, Services.RoundIndex) is shared and
        // left to the compiler.
        HashSet<string> packOwnedNamespaces = FindPackOwnedNamespaces(repoRoot);

        string appRoot = Path.Combine(repoRoot, AppProject);
        HashSet<string> allowedSet = new(AllowedEdges.Select(e => $"{e.FilePath}|{e.Namespace}"), StringComparer.Ordinal);

        List<(string FilePath, string Namespace)> violations = new();
        foreach (string file in SourceFiles(appRoot))
        {
            string content = File.ReadAllText(file);
            string relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            foreach (string ns in packOwnedNamespaces)
            {
                if (ContainsPackReference(content, ns) && !allowedSet.Contains($"{relPath}|{ns}"))
                {
                    violations.Add((relPath, ns));
                }
            }
        }

        if (violations.Count > 0)
        {
            string message = "Core sources mention pack-owned namespaces:\n\n";
            foreach (var (filePath, ns) in violations.OrderBy(v => v.FilePath).ThenBy(v => v.Namespace))
            {
                message += $"  {filePath} -> {ns}\n";
            }

            message += "\nKnown allowed edges:\n";
            foreach (var (filePath, ns, item) in AllowedEdges.OrderBy(e => e.FilePath).ThenBy(e => e.Namespace))
            {
                message += $"  {filePath} -> {ns} (removed by {item})\n";
            }

            await Assert.That(violations.Count).IsEqualTo(0).Because(message);
        }
    }

    // Every .cs and .axaml under a project root, build output excluded.
    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                               || file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)));

    private static HashSet<string> FindPackOwnedNamespaces(string repoRoot)
    {
        HashSet<string> packNamespaces = DeclaredNamespaces(Path.Combine(repoRoot, PackProject));
        HashSet<string> coreNamespaces = DeclaredNamespaces(Path.Combine(repoRoot, AppProject));
        packNamespaces.ExceptWith(coreNamespaces);
        return packNamespaces;
    }

    // "namespace Foo.Bar;" or "namespace Foo.Bar {" in every .cs under the root.
    private static HashSet<string> DeclaredNamespaces(string root)
    {
        HashSet<string> namespaces = new(StringComparer.Ordinal);
        foreach (string file in SourceFiles(root).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", RegexOptions.Multiline))
            {
                namespaces.Add(match.Groups[1].Value);
            }
        }

        return namespaces;
    }

    /// <summary>
    ///     Returns true if the content references the namespace via a using statement, xmlns attribute, or
    ///     fully-qualified type name (e.g. Namespace.Type).
    /// </summary>
    private static bool ContainsPackReference(string content, string ns)
    {
        // 0. InternalsVisibleTo carries an ASSEMBLY NAME string, not C# syntax: "DemoViewer.NET.Extensions.
        // StratBook.Tests" is a different, longer assembly name, but textually it is the pack namespace plus
        // ".Tests", which check 3 below would otherwise read as a qualified type reference. Stripped first so
        // an IVT grant to a sibling of the pack can never register as a core-to-pack code edge.
        content = Regex.Replace(content, @"InternalsVisibleTo\(""[^""]*""\)", "", RegexOptions.Multiline);

        // 1. Using statement: exactly "using Namespace;". A using of a child namespace is the child's edge,
        // not this one's, so each allow-list row names the namespace a file really imports.
        if (Regex.IsMatch(content, @"\busing\s+" + Regex.Escape(ns) + @"\s*;", RegexOptions.Multiline))
        {
            return true;
        }

        // 2. xmlns attribute in XAML: xmlns="clr-namespace:Namespace;..." or "using:Namespace".
        if (content.Contains(@$"clr-namespace:{ns}", StringComparison.Ordinal)
            || content.Contains(@$"using:{ns}""", StringComparison.Ordinal))
        {
            return true;
        }

        // 3. Fully-qualified type reference outside the using directives: "Namespace.Type", the namespace
        // followed by a dot and an uppercase letter (the typical start of a type name).
        string body = Regex.Replace(content, @"^\s*using\s+[^;]+;\s*$", "", RegexOptions.Multiline);
        return Regex.IsMatch(body, Regex.Escape(ns) + @"\.[A-Z][A-Za-z0-9_]*", RegexOptions.None);
    }
}

#region

using System.Reflection;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The layering rules that make Core a runtime instead of a UI helper (design §4, §11). These are
///     asserted against assembly metadata rather than csproj text, because a transitive edge added three
///     projects away is exactly the kind of regression a reference-graph reading of the csproj misses.
/// </summary>
public class ArchitectureTests
{
    // Everything Core is allowed to reference directly. SkiaSharp plus the BCL, and nothing else.
    private static readonly string[] _coreAllowedPrefixes =
    [
        "SkiaSharp", "System", "netstandard", "mscorlib", "Microsoft.CSharp"
    ];

    [Test]
    public async Task Core_ReferencesOnlySkiaSharpAndBcl()
    {
        List<string> disallowed = [];
        foreach (AssemblyName reference in typeof(Scene2DFrame).Assembly.GetReferencedAssemblies())
        {
            string name = reference.Name ?? "";
            if (!_coreAllowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                disallowed.Add(name);
            }
        }

        await Assert.That(disallowed).IsEmpty();
    }

    [Test]
    public async Task Core_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(typeof(Scene2DFrame).Assembly);

    [Test]
    public async Task Pipeline_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(typeof(SceneFrameBuilder).Assembly);

    /// <summary>
    ///     Decision D1's guard. Pipeline consumes <c>IPlaybackSnapshot</c> / <c>IPlayerState</c> /
    ///     <c>IReadOnlyEntityView</c> from this assembly, so an Avalonia reference creeping back into it
    ///     would drag Avalonia into every headless consumer: export, the CLI, and CI.
    /// </summary>
    [Test]
    public async Task ModulesAbstractions_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(typeof(IPlayerState).Assembly);

    [Test]
    public async Task Core_DoesNotReferencePipeline()
    {
        string pipeline = typeof(SceneFrameBuilder).Assembly.GetName().Name!;
        bool referenced = typeof(Scene2DFrame).Assembly.GetReferencedAssemblies()
            .Any(a => string.Equals(a.Name, pipeline, StringComparison.Ordinal));

        await Assert.That(referenced).IsFalse();
    }

    private static async Task AssertNoAvaloniaIn(SysAssembly root)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        List<string> offenders = [];
        Walk(root, visited, offenders);

        await Assert.That(offenders).IsEmpty();
    }

    private static void Walk(SysAssembly assembly, HashSet<string> visited, List<string> offenders)
    {
        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            string name = reference.Name ?? "";
            if (!visited.Add(name))
            {
                continue;
            }

            if (name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{assembly.GetName().Name} -> {name}");
                continue;
            }

            SysAssembly loaded;
            try
            {
                loaded = SysAssembly.Load(reference);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // A reference this test host cannot resolve cannot be Avalonia's parent either: the name
                // check above already ran, and an unresolvable assembly contributes no further edges.
                continue;
            }

            Walk(loaded, visited, offenders);
        }
    }
}

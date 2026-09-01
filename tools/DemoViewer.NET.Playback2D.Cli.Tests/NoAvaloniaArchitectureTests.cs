#region

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The phase's hard constraint: <c>dv2d</c> loads <b>zero</b> Avalonia assemblies (design §4, §11).
///     <para>
///         Three prongs, because one is not enough. A deps.json scan proves what was <i>referenced</i>;
///         it cannot prove the render happened. A loaded-assembly dump from a real subprocess proves what
///         was <i>loaded</i>, including that SkiaSharp actually did the drawing rather than the command
///         short-circuiting. Scanning this test assembly's own graph closes the last hole: an Avalonia
///         reference added here would make the first two vacuous.
///     </para>
/// </summary>
[NotInParallel]
public class NoAvaloniaArchitectureTests
{
    [Test]
    public async Task CliDepsJson_NamesNoAvaloniaAssembly()
    {
        await AssertDepsJsonIsAvaloniaFree(Path.Combine(Dv2d.CliOutputDirectory, "dv2d.deps.json"));
    }

    [Test]
    public async Task TestAssemblyDepsJson_NamesNoAvaloniaAssembly()
    {
        await AssertDepsJsonIsAvaloniaFree(Path.Combine(AppContext.BaseDirectory,
            "DemoViewer.NET.Playback2D.Cli.Tests.deps.json"));
    }

    [Test]
    public async Task CoreAndPipeline_ReferenceClosures_ContainNoAvalonia()
    {
        await AssertClosureIsAvaloniaFree(typeof(Scene2DFrame).Assembly);
        await AssertClosureIsAvaloniaFree(typeof(SceneFrameBuilder).Assembly);
    }

    [Test]
    [Category("Integration")]
    public async Task RealRenderSubprocess_LoadsSkiaSharp_AndNoAvalonia()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        CliRun run = Dv2d.Subprocess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "diag.png"), "--json", "--diag-assemblies");

        await Assert.That(run.ExitCode).IsEqualTo(0);

        IReadOnlyList<string> loaded = LoadedAssemblies(run.StdErr);
        await Assert.That(loaded).IsNotEmpty();
        await Assert.That(loaded.Any(n => n.StartsWith("SkiaSharp", StringComparison.Ordinal))).IsTrue();

        List<string> offenders =
            [.. loaded.Where(n => n.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))];
        await Assert.That(offenders).IsEmpty();
    }

    private static IReadOnlyList<string> LoadedAssemblies(string stderr)
    {
        foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                continue;
            }

            JsonNode? node = JsonNode.Parse(trimmed);
            if (node?["event"]?.GetValue<string>() != "loaded_assemblies")
            {
                continue;
            }

            return [.. ((JsonArray)node["assemblies"]!).Select(a => a!.GetValue<string>())];
        }

        throw new JsonException("the subprocess emitted no loaded_assemblies event:\n" + stderr);
    }

    /// <summary>
    ///     Asserts that no Avalonia-named package in the deps graph contributes a <b>managed assembly</b>.
    ///     <para>
    ///         The rule is "zero Avalonia assemblies", not "no package whose id starts with Avalonia": as
    ///         of C2 this tool references <c>Avalonia.Angle.Windows.Natives</c> for <c>av_libglesv2.dll</c>,
    ///         which ships only <c>runtimeTargets</c> of <c>assetType: native</c> and therefore cannot be
    ///         loaded as an assembly, referenced at compile time, or drag Avalonia's graph in. Classify
    ///         structurally rather than by a by-name allowlist: the day somebody references
    ///         <c>Avalonia.Skia</c>, its <c>compile</c>/<c>runtime</c> entries put it straight back on
    ///         the offender list.
    ///     </para>
    /// </summary>
    /// <param name="path">The deps.json to scan.</param>
    private static async Task AssertDepsJsonIsAvaloniaFree(string path)
    {
        await Assert.That(File.Exists(path)).IsTrue();

        JsonNode deps = JsonNode.Parse(File.ReadAllText(path))!;

        // "targets" is what would actually be loaded, and it is the only half that says WHAT a package
        // contributes. Classify there first, then use the verdict to read "libraries" (restore's flat
        // list, which carries no asset information at all).
        HashSet<string> nativeOnly = new(StringComparer.OrdinalIgnoreCase);
        List<string> offenders = [];

        foreach (KeyValuePair<string, JsonNode?> target in AsObject(deps["targets"]))
        {
            foreach (KeyValuePair<string, JsonNode?> package in AsObject(target.Value))
            {
                if (!package.Key.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ContributesManagedAssemblies(package.Value))
                {
                    offenders.Add($"targets/{target.Key}/{package.Key}");
                }
                else
                {
                    nativeOnly.Add(package.Key);
                }
            }
        }

        foreach (KeyValuePair<string, JsonNode?> library in AsObject(deps["libraries"]))
        {
            if (library.Key.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) &&
                !nativeOnly.Contains(library.Key))
            {
                offenders.Add($"libraries/{library.Key}");
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>Whether a deps.json package entry contributes anything loadable as managed code.</summary>
    /// <param name="package">The package's entry under a target.</param>
    private static bool ContributesManagedAssemblies(JsonNode? package)
    {
        JsonObject entry = AsObject(package);

        // "runtime" = assemblies copied to the output; "compile" = reference assemblies. Either one
        // means managed code. "dependencies" is not evidence on its own: a native package can depend
        // on another native package.
        if (entry.ContainsKey("runtime") || entry.ContainsKey("compile"))
        {
            return true;
        }

        // RID-specific assets: managed ones carry assetType "runtime", natives carry "native".
        return AsObject(entry["runtimeTargets"]).Any(static asset =>
            !string.Equals(AsObject(asset.Value)["assetType"]?.GetValue<string>(), "native",
                StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject AsObject(JsonNode? node) => node as JsonObject ?? [];

    private static async Task AssertClosureIsAvaloniaFree(SysAssembly root)
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
            catch (Exception e) when (e is FileNotFoundException or FileLoadException
                                          or BadImageFormatException)
            {
                continue;
            }

            Walk(loaded, visited, offenders);
        }
    }
}

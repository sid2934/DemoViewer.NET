#region

using System.Reflection;
using SysAssembly = System.Reflection.Assembly;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The phase's hard constraint: <c>dv2d</c> loads <b>zero</b> Avalonia assemblies (design §4, §11).
///     <para>
///         Three prongs, because one is not enough. A deps.json scan proves what was <i>referenced</i>;
///         it cannot prove the render happened. A loaded-assembly dump from a real subprocess proves what
///         was <i>loaded</i> — including that SkiaSharp actually did the drawing rather than the command
///         short-circuiting. And scanning this test assembly's own graph keeps the assertion honest: an
///         Avalonia reference added here would make the first two vacuous in the eyes of a reader.
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

    private static async Task AssertDepsJsonIsAvaloniaFree(string path)
    {
        await Assert.That(File.Exists(path)).IsTrue();

        JsonNode deps = JsonNode.Parse(File.ReadAllText(path))!;
        List<string> offenders = [];

        // Both halves matter: "libraries" is what restore resolved, "targets" is what would actually be
        // loaded. A package can appear in one and not the other.
        foreach (string section in new[] { "libraries", "targets" })
        {
            Collect(deps[section], offenders);
        }

        await Assert.That(offenders).IsEmpty();
    }

    private static void Collect(JsonNode? node, List<string> offenders)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> pair in o)
                {
                    if (pair.Key.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add(pair.Key);
                    }

                    Collect(pair.Value, offenders);
                }

                break;
            case JsonArray a:
                foreach (JsonNode? item in a)
                {
                    Collect(item, offenders);
                }

                break;
        }
    }

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

#region

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d render</c>: one frame to one PNG, with no app and no window. The design's exit criterion
///     for this phase is the fixture path: a designer edits a layer, runs this, and looks, in well under
///     a second.
/// </summary>
internal static class RenderCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    public static ExitCode Run(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        long started = Stopwatch.GetTimestamp();

        using SceneProvider source = SceneProvider.Build(args);

        // Read before the plan is built: the plan refuses `--layers annotations` with nothing to draw,
        // and that refusal has to be able to see whether a sidecar was supplied.
        AnnotationSession? ink = args.String("ink") is { Length: > 0 } inkPath
            ? FixtureInk.Load(inkPath) ?? throw new CliUsageException(
                $"--ink {inkPath} holds no annotation this build can draw (missing, empty, or written " +
                "by a newer schema).")
            : null;

        using SceneRenderPlan plan = SceneRenderPlan.Build(args, source.DefaultSize, source.MapName,
            annotations: ink);

        string outPath = args.String("out") ?? "dv2d-render.png";
        string? cameraSpec = args.String("camera");
        bool diagAssemblies = args.Flag("diag-assemblies");
        args.ThrowIfUnconsumed();

        if (plan.Assets is { Source: AssetsRootSource.Flag or AssetsRootSource.Env, Path: null })
        {
            throw new DirectoryNotFoundException(
                $"the asset root given by --assets/{AssetsRootResolver.EnvironmentVariable} does not exist: " +
                string.Join(", ", plan.Assets.Probed));
        }

        Scene2DFrame frame = plan.WithRadarArt(source.FrameAt(0));
        SceneTime time = source.TimeAt(0);
        plan.Renderer.Camera = CameraSpec.Resolve(cameraSpec, frame, plan.Size, source.Camera);

        byte[] png = plan.Renderer.RenderPng(frame, in time, plan.Size);
        WriteFile(outPath, png);

        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (ConsoleOut.IsJson)
        {
            JsonObject payload = new()
            {
                ["schema_version"] = 1,
                ["command"] = "render",
                ["ok"] = true,
                ["out"] = outPath,
                ["width"] = plan.Size.Width,
                ["height"] = plan.Size.Height,
                ["backend"] = plan.Backend.Backend.ToString(),
                ["backend_requested"] = plan.Backend.Requested,
                ["assets_root"] = plan.Assets.Path,
                ["assets_source"] = plan.Assets.SourceToken,
                ["source"] = new JsonObject
                {
                    ["kind"] = source.Kind,
                    ["name"] = source.Name
                },
                ["map"] = source.MapName,
                ["map_version"] = plan.MapAssets?.Bundle.MapVersion ?? source.MapVersion,
                ["tick"] = time.Tick,
                ["frame_index"] = time.FrameIndex,
                ["layers"] = ToArray(plan.LayerIds),
                ["png_sha256"] = Sha256(png),
                ["png_bytes"] = png.Length,
                ["parse_ms"] = Round(source.ParseMs),
                ["elapsed_ms"] = Round(elapsedMs)
            };
            ConsoleOut.Json(payload);
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"wrote {outPath}  {plan.Size.Width}x{plan.Size.Height}  {plan.Backend.Backend}  " +
                $"tick={time.Tick} frame={time.FrameIndex}  layers=[{string.Join(",", plan.LayerIds)}]"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"elapsed {elapsedMs:F1} ms (parse {source.ParseMs:F1} ms)  " +
                $"assets={plan.Assets.Path ?? "-"} ({plan.Assets.SourceToken})"));
        }

        if (plan.Backend.Reason is { } reason)
        {
            ConsoleOut.Warn(reason);
        }

        if (diagAssemblies)
        {
            DumpLoadedAssemblies();
        }

        return ExitCode.Success;
    }

    /// <summary>
    ///     Writes the loaded-assembly list to stderr. The no-Avalonia architecture test reads it from a
    ///     subprocess: a deps.json scan proves what was *referenced*, this proves what was *loaded*.
    ///     Documented rather than hidden, because it is also the first thing to ask for in support
    ///     triage.
    /// </summary>
    public static void DumpLoadedAssemblies()
    {
        JsonArray names = [];
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            names.Add(assembly.GetName().Name ?? "");
        }

        ConsoleOut.JsonEvent(new JsonObject
        {
            ["schema_version"] = 1,
            ["event"] = "loaded_assemblies",
            ["assemblies"] = names
        });
    }

    /// <summary>Rounds a millisecond figure to one decimal so the JSON is stable and readable.</summary>
    /// <param name="value">The value to round.</param>
    public static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>Lower-case hex SHA-256. The determinism assertions compare these.</summary>
    /// <param name="bytes">The payload to hash.</param>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Copies a string list into a JSON array.</summary>
    /// <param name="values">The values.</param>
    public static JsonArray ToArray(IEnumerable<string> values)
    {
        JsonArray array = [];
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>Writes bytes to a path, creating the directory.</summary>
    /// <param name="path">The destination file.</param>
    /// <param name="bytes">The payload.</param>
    public static void WriteFile(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, bytes);
    }
}

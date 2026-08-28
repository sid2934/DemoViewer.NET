#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d fixture capture | list | verify</c>: authoring and checking the corpus.
///     <para>
///         <c>capture</c> is the one command that needs a demo. It replays a private tracker to the
///         requested tick, serializes the built scene, and registers it in <c>manifest.json</c>. After
///         that the fixture is demo-free, so the whole corpus runs in CI (decision 10).
///     </para>
/// </summary>
internal static class FixtureCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    public static ExitCode Run(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return (args.SubVerb ?? throw new CliUsageException("fixture takes capture | list | verify.")) switch
        {
            "capture" => Capture(args),
            "list" => List(args),
            "verify" => Verify(args),
            var other => throw new CliUsageException($"fixture takes capture | list | verify, got '{other}'.")
        };
    }

    private static ExitCode Capture(CliArgs args)
    {
        string name = args.Require("name");
        if (name.Any(static c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new CliUsageException(
                $"--name must be a file-safe identifier (letters, digits, '-', '_'), got '{name}'.");
        }

        string corpusDir = CorpusLocator.Directory(args);
        using SceneProvider source = SceneProvider.Build(args);

        SKSizeI size = args.Size("size", new SKSizeI(640, 360));
        string? cameraSpec = args.String("camera");
        string? annotationsPath = args.String("annotations");
        IReadOnlyList<string>? layers = args.List("layers");
        AssetsRoot assets = AssetsRootResolver.Resolve(args);
        args.ThrowIfUnconsumed();

        Scene2DFrame frame = source.FrameAt(0);
        SceneTime time = source.TimeAt(0);
        ViewportTransform camera = CameraSpec.Resolve(cameraSpec, frame, size, source.Camera);

        JsonElement? annotations = null;
        if (annotationsPath is not null)
        {
            if (!File.Exists(annotationsPath))
            {
                throw new FileNotFoundException($"annotations not found: {annotationsPath}", annotationsPath);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(annotationsPath));
            annotations = document.RootElement.Clone();
        }

        string? mapVersion = MapAssetPipeline.TryReadMapVersion(assets.Path, source.MapName);

        SceneFixture fixture = new()
        {
            Frame = frame,
            Time = time,
            Camera = camera,
            Size = size,
            MapName = source.MapName,
            MapVersion = mapVersion,
            Annotations = annotations,
            SourceDemoId = source.SourceDemoId,
            Notes = string.Create(CultureInfo.InvariantCulture,
                $"captured by dv2d fixture capture at tick {time.Tick} (demo frame {time.FrameIndex})")
        };

        string relative = $"scenes/{name}.scene.json";
        string scenePath = Path.Combine(corpusDir, "scenes", name + ".scene.json");
        fixture.Save(scenePath);

        GoldenCorpusEntry entry = new(name, scenePath, size, source.MapName, mapVersion, layers,
            GoldenBudget.Default, false)
        {
            SceneRelativePath = relative,
            CorpusDirectory = corpusDir,
            Notes = fixture.Notes
        };
        GoldenCorpus.Upsert(corpusDir, entry);

        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(new JsonObject
            {
                ["schema_version"] = 1,
                ["command"] = "fixture",
                ["action"] = "capture",
                ["ok"] = true,
                ["name"] = name,
                ["scene"] = scenePath,
                ["corpus"] = corpusDir,
                ["map"] = source.MapName,
                ["map_version"] = mapVersion,
                ["tick"] = time.Tick,
                ["frame_index"] = time.FrameIndex,
                ["markers"] = frame.Markers.Count,
                ["size"] = new JsonObject
                {
                    ["width"] = size.Width,
                    ["height"] = size.Height
                }
            });
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"wrote {scenePath} ({frame.Markers.Count} markers, tick {time.Tick}) and registered it " +
                $"in {Path.Combine(corpusDir, GoldenCorpus.ManifestFileName)}"));
            ConsoleOut.Info("next: dv2d golden update --name " + name);
        }

        return ExitCode.Success;
    }

    private static ExitCode List(CliArgs args)
    {
        GoldenCorpus corpus = CorpusLocator.Load(args);
        args.ThrowIfUnconsumed();

        JsonArray entries = [];
        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            entries.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["scene"] = entry.SceneRelativePath,
                ["exists"] = File.Exists(entry.ScenePath),
                ["size"] = new JsonObject
                {
                    ["width"] = entry.Size.Width,
                    ["height"] = entry.Size.Height
                },
                ["map"] = entry.MapName,
                ["map_version"] = entry.MapVersion,
                ["pending"] = entry.Pending,
                ["tolerance"] = entry.Tolerance == GoldenMode.ByteExact ? "byte-exact" : "perceptual",
                ["notes"] = entry.Notes
            });

            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"{entry.Name,-28} {entry.Size.Width}x{entry.Size.Height,-6} " +
                $"{entry.MapName ?? "-",-12} {(entry.Pending ? "pending" : "")}"));
        }

        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(new JsonObject
            {
                ["schema_version"] = 1,
                ["command"] = "fixture",
                ["action"] = "list",
                ["ok"] = true,
                ["corpus"] = corpus.Directory,
                ["entries"] = entries
            });
        }

        return ExitCode.Success;
    }

    private static ExitCode Verify(CliArgs args)
    {
        GoldenCorpus corpus = CorpusLocator.Load(args);
        args.ThrowIfUnconsumed();

        int ok = 0;
        int skipped = 0;
        JsonArray results = [];

        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            if (!File.Exists(entry.ScenePath))
            {
                // A pending entry is allowed to have no scene yet; a non-pending one is a broken manifest.
                if (entry.Pending)
                {
                    skipped++;
                    results.Add(Row(entry.Name, "skipped", "pending: no scene file yet"));
                    continue;
                }

                results.Add(Row(entry.Name, "missing", $"no scene at {entry.ScenePath}"));
                continue;
            }

            try
            {
                // Round-trip rather than load: a fixture that reads but does not write back identically
                // is one whose next `capture` would silently drop data.
                SceneFixture loaded = SceneFixture.Load(entry.ScenePath);
                using MemoryStream first = new();
                SceneFixtureSerializer.Write(loaded, first);
                first.Position = 0;
                SceneFixture again = SceneFixtureSerializer.Read(first);
                using MemoryStream second = new();
                SceneFixtureSerializer.Write(again, second);

                if (!first.ToArray().AsSpan().SequenceEqual(second.ToArray()))
                {
                    results.Add(Row(entry.Name, "unstable", "the fixture does not round-trip byte-identically"));
                    continue;
                }

                ok++;
                results.Add(Row(entry.Name, "ok", null));
            }
            catch (JsonException e)
            {
                results.Add(Row(entry.Name, "malformed", e.Message));
            }
        }

        int bad = results.Count - ok - skipped;
        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(new JsonObject
            {
                ["schema_version"] = 1,
                ["command"] = "fixture",
                ["action"] = "verify",
                ["ok"] = bad == 0,
                ["corpus"] = corpus.Directory,
                ["counts"] = new JsonObject
                {
                    ["total"] = results.Count,
                    ["ok"] = ok,
                    ["skipped"] = skipped,
                    ["failed"] = bad
                },
                ["results"] = results
            });
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"verify: {results.Count} entries — ok {ok}, skipped {skipped}, failed {bad}"));
        }

        return bad == 0 ? ExitCode.Success : ExitCode.GateFailure;
    }

    private static JsonObject Row(string name, string status, string? reason)
    {
        JsonObject o = new()
        {
            ["name"] = name,
            ["status"] = status
        };
        if (reason is not null)
        {
            o["reason"] = reason;
        }

        return o;
    }
}

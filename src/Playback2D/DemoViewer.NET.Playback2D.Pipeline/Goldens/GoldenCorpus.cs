#region

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Goldens;

/// <summary>The measured budget one corpus entry is gated against (design §6).</summary>
/// <param name="RenderP99Ms">99th-percentile render time, in milliseconds.</param>
/// <param name="AdvanceP99Ms">99th-percentile advance time, in milliseconds.</param>
/// <param name="BytesPerFrame">Steady-state allocation per frame. Zero, and never scaled.</param>
public readonly record struct GoldenBudget(double RenderP99Ms, double AdvanceP99Ms, long BytesPerFrame)
{
    /// <summary>Design §6's numbers, used when the manifest states no per-entry budget.</summary>
    public static readonly GoldenBudget Default = new(8.0, 2.0, 0);

    /// <summary>
    ///     This budget with the two <b>time</b> limits multiplied. The allocation limit is deliberately
    ///     not scaled: a shared CI runner is slower, not leakier, and 0 bytes is 0 bytes everywhere.
    /// </summary>
    /// <param name="scale">The multiplier, e.g. 2.0 on the ubuntu CI lane.</param>
    public GoldenBudget Scaled(double scale) =>
        new(RenderP99Ms * scale, AdvanceP99Ms * scale, BytesPerFrame);
}

/// <summary>One entry in <c>tests/fixtures/playback2d/manifest.json</c>.</summary>
/// <param name="Name">The corpus name, e.g. <c>duel-mirage-b</c>.</param>
/// <param name="ScenePath">Absolute path to the <c>.scene.json</c>.</param>
/// <param name="Size">The render / golden size.</param>
/// <param name="MapName">The map the scene was captured on, or null for a synthetic scene.</param>
/// <param name="MapVersion">The bundle's <c>mapVersion</c> CRC when the entry was authored.</param>
/// <param name="Layers">The layer ids to register, or null for every known layer.</param>
/// <param name="Budget">The frame budget for <c>dv2d bench --gate</c>.</param>
/// <param name="Pending">
///     True when this entry's inputs have not all landed yet (a B2 annotation document, a B3 level
///     pick). A pending entry is <b>skipped</b> by <c>golden verify</c> and <c>fixture verify</c>, never
///     failed, which is what lets a later phase register its fixture before it can render it.
/// </param>
public sealed record GoldenCorpusEntry(
    string Name,
    string ScenePath,
    SKSizeI Size,
    string? MapName,
    string? MapVersion,
    IReadOnlyList<string>? Layers,
    GoldenBudget Budget,
    bool Pending)
{
    /// <summary>How this entry's golden is compared. Defaults to perceptual (see the corpus README).</summary>
    public GoldenMode Tolerance { get; init; } = GoldenMode.Perceptual;

    /// <summary>What this entry covers, for a reviewer reading the manifest.</summary>
    public string? Notes { get; init; }

    /// <summary>The corpus-relative scene path, as written in the manifest.</summary>
    public string SceneRelativePath { get; init; } = "";

    /// <summary>The corpus root this entry was loaded from.</summary>
    public string CorpusDirectory { get; init; } = "";

    /// <summary>Absolute path to this entry's golden for a backend: <c>goldens/{cpu|gpu}/name@WxH.png</c>.</summary>
    /// <param name="backend">The backend whose lane the golden belongs to.</param>
    public string GoldenPath(RenderBackend backend) => Path.Combine(
        CorpusDirectory, "goldens",
        backend == RenderBackend.CpuRaster ? "cpu" : "gpu",
        string.Create(CultureInfo.InvariantCulture, $"{Name}@{Size.Width}x{Size.Height}.png"));
}

/// <summary>
///     The <c>tests/fixtures/playback2d</c> corpus: <c>manifest.json</c> plus the scene and golden paths
///     it names. One index, read by <c>dv2d golden</c>, <c>dv2d bench</c>, <c>dv2d fixture</c> and by the
///     direct-execution test suites, so "which fixtures exist" has exactly one answer.
/// </summary>
public sealed class GoldenCorpus
{
    /// <summary>The manifest schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The manifest's file name inside a corpus directory.</summary>
    public const string ManifestFileName = "manifest.json";

    // The manifest is read and reviewed by humans in a PR diff, so the writer must not turn '+', an
    // apostrophe or an em dash into \u escapes. Relaxed escaping is safe here: this file is never
    // embedded in HTML or a script tag.
    private static readonly JsonSerializerOptions _manifestOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private GoldenCorpus(string directory, int schemaVersion, IReadOnlyList<GoldenCorpusEntry> entries)
    {
        Directory = directory;
        SchemaVersion = schemaVersion;
        Entries = entries;
    }

    /// <summary>The corpus root: the directory holding <c>manifest.json</c>, <c>scenes/</c>, <c>goldens/</c>.</summary>
    public string Directory { get; }

    /// <summary>The manifest's declared schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Every entry, in manifest order.</summary>
    public IReadOnlyList<GoldenCorpusEntry> Entries { get; }

    /// <summary>Reads the manifest. Entries are returned in file order, which is the render order.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <exception cref="FileNotFoundException">The directory holds no manifest.</exception>
    /// <exception cref="JsonException">The manifest is malformed.</exception>
    public static GoldenCorpus Load(string corpusDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(corpusDirectory);

        string root = Path.GetFullPath(corpusDirectory);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"No {ManifestFileName} in {root}.", manifestPath);
        }

        JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath))
                            ?? throw new JsonException($"{manifestPath} is empty.");

        int schemaVersion = manifest["schema_version"]?.GetValue<int>() ?? CurrentSchemaVersion;
        GoldenBudget defaultBudget = ReadBudget(manifest["default_budget"], GoldenBudget.Default);

        List<GoldenCorpusEntry> entries = [];
        if (manifest["entries"] is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (node is null)
                {
                    continue;
                }

                entries.Add(ReadEntry(node, root, defaultBudget));
            }
        }

        return new GoldenCorpus(root, schemaVersion, entries);
    }

    /// <summary>
    ///     Walks up from the process base directory (then the working directory) for the corpus beside a
    ///     <c>DemoViewer.NET.slnx</c>. Null when the tool is running outside a checkout.
    /// </summary>
    public static string? FindDefaultCorpusDirectory()
    {
        foreach (string start in new[]
                 {
                     AppContext.BaseDirectory, System.IO.Directory.GetCurrentDirectory()
                 })
        {
            DirectoryInfo? dir = new(start);
            for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
                {
                    continue;
                }

                string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "playback2d");
                if (File.Exists(Path.Combine(candidate, ManifestFileName)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The entry with this name, or null.</summary>
    /// <param name="name">The corpus name.</param>
    public GoldenCorpusEntry? Find(string name)
    {
        foreach (GoldenCorpusEntry entry in Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    ///     Adds or replaces an entry and rewrites <c>manifest.json</c>, preserving every member this build
    ///     does not know. Used by <c>dv2d fixture capture</c>; a hand edit is equally valid.
    /// </summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entry">The entry to write. Its <c>SceneRelativePath</c> must be set.</param>
    public static void Upsert(string corpusDirectory, GoldenCorpusEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(corpusDirectory);
        ArgumentNullException.ThrowIfNull(entry);

        string root = Path.GetFullPath(corpusDirectory);
        string manifestPath = Path.Combine(root, ManifestFileName);

        JsonObject manifest = File.Exists(manifestPath)
            ? JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? []
            : [];

        manifest["schema_version"] ??= CurrentSchemaVersion;
        if (manifest["entries"] is not JsonArray entries)
        {
            entries = [];
            manifest["entries"] = entries;
        }

        JsonObject written = WriteEntry(entry);
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i]?["name"]?.GetValue<string>(), entry.Name, StringComparison.Ordinal))
            {
                entries[i] = written;
                Save(manifestPath, manifest);
                return;
            }
        }

        entries.Add(written);
        Save(manifestPath, manifest);
    }

    private static void Save(string manifestPath, JsonObject manifest) =>
        File.WriteAllText(manifestPath, manifest.ToJsonString(_manifestOptions) + Environment.NewLine);

    private static GoldenCorpusEntry ReadEntry(JsonNode node, string root, GoldenBudget defaultBudget)
    {
        string name = node["name"]?.GetValue<string>()
                      ?? throw new JsonException("A manifest entry has no 'name'.");
        string relative = node["scene"]?.GetValue<string>() ?? $"scenes/{name}.scene.json";

        SKSizeI size = new(
            node["size"]?["width"]?.GetValue<int>() ?? 640,
            node["size"]?["height"]?.GetValue<int>() ?? 360);

        List<string>? layers = null;
        if (node["layers"] is JsonArray layerArray)
        {
            layers = [];
            foreach (JsonNode? layer in layerArray)
            {
                if (layer?.GetValue<string>() is { Length: > 0 } id)
                {
                    layers.Add(id);
                }
            }
        }

        GoldenMode tolerance =
            string.Equals(node["tolerance"]?.GetValue<string>(), "byte-exact", StringComparison.OrdinalIgnoreCase)
                ? GoldenMode.ByteExact
                : GoldenMode.Perceptual;

        return new GoldenCorpusEntry(
            name,
            Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))),
            size,
            node["map"]?.GetValue<string>(),
            node["map_version"]?.GetValue<string>(),
            layers,
            ReadBudget(node["budget"], defaultBudget),
            node["pending"]?.GetValue<bool>() ?? false)
        {
            Tolerance = tolerance,
            Notes = node["notes"]?.GetValue<string>(),
            SceneRelativePath = relative,
            CorpusDirectory = root
        };
    }

    private static JsonObject WriteEntry(GoldenCorpusEntry entry)
    {
        JsonObject o = new()
        {
            ["name"] = entry.Name,
            ["scene"] = entry.SceneRelativePath.Length > 0
                ? entry.SceneRelativePath
                : $"scenes/{entry.Name}.scene.json",
            ["size"] = new JsonObject
            {
                ["width"] = entry.Size.Width,
                ["height"] = entry.Size.Height
            },
            ["map"] = entry.MapName,
            ["map_version"] = entry.MapVersion,
            ["tolerance"] = entry.Tolerance == GoldenMode.ByteExact ? "byte-exact" : "perceptual",
            ["budget"] = new JsonObject
            {
                ["render_p99_ms"] = entry.Budget.RenderP99Ms,
                ["advance_p99_ms"] = entry.Budget.AdvanceP99Ms,
                ["bytes_per_frame"] = entry.Budget.BytesPerFrame
            },
            ["pending"] = entry.Pending
        };

        if (entry.Layers is { Count: > 0 })
        {
            JsonArray layers = [];
            foreach (string id in entry.Layers)
            {
                layers.Add(id);
            }

            o["layers"] = layers;
        }

        if (!string.IsNullOrEmpty(entry.Notes))
        {
            o["notes"] = entry.Notes;
        }

        return o;
    }

    private static GoldenBudget ReadBudget(JsonNode? node, GoldenBudget fallback) => node is null
        ? fallback
        : new GoldenBudget(
            node["render_p99_ms"]?.GetValue<double>() ?? fallback.RenderP99Ms,
            node["advance_p99_ms"]?.GetValue<double>() ?? fallback.AdvanceP99Ms,
            node["bytes_per_frame"]?.GetValue<long>() ?? fallback.BytesPerFrame);
}

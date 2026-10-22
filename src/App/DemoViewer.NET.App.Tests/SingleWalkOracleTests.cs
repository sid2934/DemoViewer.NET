#region

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The byte oracle for the single-walk work: every output a benchmark demo's visits write, as one
///     SHA256 line per output, against <c>tests/fixtures/single-walk/&lt;demo&gt;.digest.txt</c>. Three
///     visits per demo: the sweep import, a forced scan after it, and an open with every
///     sweep off in a second config. With no fixture the test fails, unless <c>SINGLE_WALK_RECORD=1</c>
///     asks it to write one. <c>SINGLE_WALK_DUMP_DIR</c>
///     keeps the canonical text each line hashes.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public partial class SingleWalkOracleTests
{
    private static readonly JsonSerializerOptions Fields = new() { IncludeFields = true };

    private const string DumpDirEnvVar = "SINGLE_WALK_DUMP_DIR";

    private const string RecordEnvVar = "SINGLE_WALK_RECORD";

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheVisitsWriteTheRecordedOutputs(int which)
    {
        string demo = BenchmarkDemo(which);
        string name = Path.GetFileNameWithoutExtension(demo);
        string root = Path.Combine(Path.GetTempPath(), "dvsinglewalk_" + Guid.NewGuid().ToString("N"));
        try
        {
            SortedDictionary<string, string> dump = new(StringComparer.Ordinal);

            string sweepDir = Path.Combine(root, "sweep");
            await SingleWalkSession.WithAppAsync(sweepDir, SingleWalkSession.Settings(Path.GetDirectoryName(demo)!, true),
                async provider =>
                {
                    SingleWalkSession.Visit visit = await SingleWalkSession.RunAsync(provider, SingleWalkSession.Sweep, demo);
                    Console.WriteLine($"{name} sweep: reads {visit.Reads}, sites {Sites(visit)}");
                    SortedDictionary<string, string> swept = Canonical(sweepDir, demo);
                    foreach ((string key, string value) in swept)
                    {
                        dump["sweep/" + key] = value;
                    }

                    // The forced scan, the Compute full stats press, on a row the sweep already stamped current.
                    provider.GetRequiredService<DemoCacheStore>().UpdateExisting(demo, r => r.AnalysisState = DemoAnalysisState.Pending);
                    provider.GetRequiredService<HighlightScanService>().RequestScan(demo);
                    await SingleWalkSession.SettleAsync(provider.GetRequiredService<DemoProcessingQueue>());
                    foreach ((string key, string value) in Canonical(sweepDir, demo))
                    {
                        if (!swept.TryGetValue(key, out string? before) || before != value)
                        {
                            dump["forced/" + key] = value;
                        }
                    }

                    return 0;
                });

            string openDir = Path.Combine(root, "open");
            await SingleWalkSession.WithAppAsync(openDir, SingleWalkSession.Settings(Path.GetDirectoryName(demo)!, false),
                async provider =>
                {
                    SingleWalkSession.Visit visit = await SingleWalkSession.RunAsync(provider, SingleWalkSession.Open, demo);
                    Console.WriteLine($"{name} open: reads {visit.Reads}, sites {Sites(visit)}");
                    foreach ((string key, string value) in Canonical(openDir, demo))
                    {
                        dump["open/" + key] = value;
                    }

                    return 0;
                });

            string digest = string.Concat(dump.Select(kv => $"{kv.Key} {Sha(kv.Value)}\n"));
            if (Environment.GetEnvironmentVariable(DumpDirEnvVar) is { Length: > 0 } dumpRoot)
            {
                WriteDump(Path.Combine(dumpRoot, name), dump, digest);
            }

            string fixture = Path.Combine(FixtureDir(), name + ".digest.txt");
            if (!File.Exists(fixture))
            {
                // A missing fixture never passes on its own: a renamed demo or a fresh clone would record whatever runs.
                if (Environment.GetEnvironmentVariable(RecordEnvVar) != "1")
                {
                    Assert.Fail($"{name}: no fixture at {fixture}; set {RecordEnvVar}=1 to record one");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
                File.WriteAllText(fixture, digest);
                Console.WriteLine($"wrote {fixture}");
                return;
            }

            Dictionary<string, string> expected = Lines(File.ReadAllText(fixture));
            Dictionary<string, string> actual = Lines(digest);
            string[] moved = [.. expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal)
                .Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))];
            await Assert.That(moved).IsEmpty().Because($"{name}: outputs moved from {fixture}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static string Sites(SingleWalkSession.Visit visit) =>
        string.Join(",", visit.Sites.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}"));

    private static string BenchmarkDemo(int which)
    {
        string? folder = DemoTestHelper.CorpusDir("benchmarks");
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under demos/benchmarks");
    }

    private static string FixtureDir() =>
        Path.Combine(DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("no repo root"), "tests", "fixtures", "single-walk");

    private static Dictionary<string, string> Lines(string digest) =>
        digest.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "", StringComparer.Ordinal);

    private static void WriteDump(string dir, SortedDictionary<string, string> dump, string digest)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "digest.txt"), digest);
        foreach ((string key, string value) in dump)
        {
            File.WriteAllText(Path.Combine(dir, Regex.Replace(key, "[^A-Za-z0-9._-]", "_") + ".txt"), value);
        }
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ── Canonical form ──────────────────────────────────────────────────────────────────────────────

    // Written by the session itself, not by a pass: settings, logs, seeded defaults.
    private static readonly string[] Skipped = ["settings.json", "logs/", "crash.log", "themes/", "palettes/", "launch-state.json", "SessionState.json", "extension-settings/"];

    /// <summary>
    ///     Every file the session wrote under <paramref name="configDir" />, one entry per file, or per
    ///     top-level property of a JSON object. Gzip is inflated, JSON keys are sorted, wall-clock fields
    ///     and machine paths are masked, a content key in a file name reads <c>{key}</c>, and the demo's
    ///     path key (<see cref="DemoCacheStore.StableKey" />, a hash of its absolute path) reads <c>{demokey}</c>.
    /// </summary>
    internal static SortedDictionary<string, string> Canonical(string configDir, string demo)
    {
        SortedDictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(configDir, file).Replace('\\', '/');
            if (Skipped.Any(s => s.EndsWith('/') ? rel.StartsWith(s, StringComparison.Ordinal) : rel == s))
            {
                continue;
            }

            string key = ContentKey().Replace(rel, "{key}");
            byte[] bytes = File.ReadAllBytes(file);
            byte[] stored = bytes;
            if (bytes is [0x1f, 0x8b, ..])
            {
                bytes = Inflate(bytes);
            }

            // An extension demo-data file is a JSON header line, then the payload.
            int newline = Array.IndexOf(bytes, (byte)'\n');
            if (newline > 0 && TryParse(bytes[..newline]) is JsonObject header && (header.ContainsKey("facet") || header.ContainsKey("Facet")))
            {
                Add(result, key + "@header", header, demo, configDir);
                bytes = bytes[(newline + 1)..];
                stored = bytes;
                if (bytes is [0x1f, 0x8b, ..])
                {
                    bytes = Inflate(bytes);
                }
            }

            // The grenade sidecar is binary; its decoded rows and header are compared, Engine masked.
            // TryDecode reads the gzip stream as written, not the inflated bytes.
            JsonNode? json = bytes is [(byte)'D', (byte)'V', (byte)'G', (byte)'L', ..]
                             && GrenadeThrowLog.TryDecode(stored) is { } grenades
                ? JsonSerializer.SerializeToNode(grenades, Fields)
                : TryParse(bytes);
            Add(result, key, json, demo, configDir, bytes);
        }

        return result;
    }

    private static byte[] Inflate(byte[] bytes)
    {
        using MemoryStream inflated = new();
        using (GZipStream gz = new(new MemoryStream(bytes), CompressionMode.Decompress))
        {
            gz.CopyTo(inflated);
        }

        return inflated.ToArray();
    }

    private static JsonNode? TryParse(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Not JSON: the raw bytes' hash. A JSON object: one entry per top-level property.
    private static void Add(SortedDictionary<string, string> result, string key, JsonNode? json, string demo, string configDir,
        byte[]? raw = null)
    {
        if (json is null)
        {
            result[key] = raw is null
                ? "null"
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.Latin1.GetBytes(Mask(Encoding.Latin1.GetString(raw), demo, configDir))));
            return;
        }

        JsonNode? canonical = Normalize(json, demo, configDir, null);
        if (canonical is JsonObject obj)
        {
            foreach ((string prop, JsonNode? value) in obj)
            {
                result[$"{key}#{prop}"] = value?.ToJsonString() ?? "null";
            }
        }
        else
        {
            result[key] = canonical?.ToJsonString() ?? "null";
        }
    }

    // Wall-clock stamps, the demo file's mtime (differs per copy of the corpus), and the engine and
    // extension builds, which move with any local build.
    private static bool Masked(string key) =>
        key.EndsWith("AtTicks", StringComparison.OrdinalIgnoreCase) || key.EndsWith("UtcTicks", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ModifiedTicks", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("AtUtc", StringComparison.OrdinalIgnoreCase) || key.Equals("Engine", StringComparison.OrdinalIgnoreCase) || key.Equals("ExtensionVersion", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Normalize(JsonNode? node, string demo, string configDir, string? key)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = [];
                foreach ((string name, string k, JsonNode? v) in obj.Select(kv => (Mask(kv.Key, demo, configDir), kv.Key, kv.Value))
                             .OrderBy(e => e.Item1, StringComparer.Ordinal))
                {
                    sorted[name] = Masked(k) ? JsonValue.Create("<masked>") : Normalize(v, demo, configDir, k);
                }

                return sorted;
            case JsonArray array:
                JsonArray copy = [];
                foreach (JsonNode? item in array)
                {
                    copy.Add(Normalize(item, demo, configDir, key));
                }

                return copy;
            case JsonValue value when value.TryGetValue(out string? s):
                return JsonValue.Create(Mask(s, demo, configDir));
            default:
                return node?.DeepClone();
        }
    }

    private static string Mask(string text, string demo, string configDir) => text
        .Replace(demo, "{demo}", StringComparison.OrdinalIgnoreCase)
        .Replace(Path.GetDirectoryName(demo)!, "{demos}", StringComparison.OrdinalIgnoreCase)
        .Replace(configDir, "{config}", StringComparison.OrdinalIgnoreCase)
        .Replace(DemoCacheStore.StableKey(demo), "{demokey}", StringComparison.Ordinal);

    [GeneratedRegex("[0-9a-f]{16,}")]
    private static partial Regex ContentKey();
}

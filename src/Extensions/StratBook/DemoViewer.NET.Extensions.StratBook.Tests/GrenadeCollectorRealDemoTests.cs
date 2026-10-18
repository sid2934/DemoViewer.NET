#region

using System.Text.Json;
using System.Text.Json.Nodes;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="GrenadeCollector" /> on the rules run against the two-pass walk it replaced, on the benchmark
///     demos: the same rows and trajectories off the held parse, and the same again off a streaming read. The
///     header is the one thing allowed to move: the input decoder is the run's command lane, whose coverage is
///     measured per frame rather than per command.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class GrenadeCollectorRealDemoTests
{
    private static readonly JsonSerializerOptions Json = new(GrenadeSidecar.JsonOptions) { WriteIndented = false };

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheCollectedRows_EqualTheWalkedRows_OnTheHeldParseAndOnAStreamingRead(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        int tickRate = parsed.TickRate > 0 ? parsed.TickRate : 64;

        GrenadeWalk walked = GrenadeWalker.Walk(parsed);
        CollectorSet set = CollectorSet.Of([new RegisteredCollector(GrenadeCollector.Contribution(() => 4), null)]);
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null))
        {
            CollectorsFor = _ => set
        };
        GrenadeCollected retained = rules.Collected(parsed, path).Get<GrenadeCollected>(GrenadeCollector.CollectorId)!;
        GrenadeWalk collected = GrenadeWalker.Assemble(retained, parsed.Players.Values, tickRate, parsed.ServerStartTick);
        rules.Forget(parsed);

        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules | ForwardNeeds.UserCommands, rules.Docs, null, null, set);
        GrenadeCollected streamed = forward.Collected.Get<GrenadeCollected>(GrenadeCollector.CollectorId)!;
        GrenadeWalk read = GrenadeWalker.Assemble(streamed, forward.Demo.Players.Values,
            forward.Demo.TickRate > 0 ? forward.Demo.TickRate : 64, forward.Demo.ServerStartTick);

        // A fallback crouch read is one the flags did not cover: the collector has no fallback, so it would differ.
        int fallbacks = walked.Rows.Count(r => r.ReleaseOnGround is null && r.ReleaseCrouched is not null);
        Console.WriteLine($"{Path.GetFileName(path)}: {walked.Rows.Count} walked (coverage {walked.InputCoverage:0.0000}), "
                          + $"{collected.Rows.Count} collected (coverage {collected.InputCoverage:0.0000}), {read.Rows.Count} streamed; "
                          + $"{fallbacks} fallback crouch reads; {Diff(walked, collected) ?? "retained rows identical"}; "
                          + $"{Diff(collected, read) ?? "streamed rows identical"}");

        using (Assert.Multiple())
        {
            await Assert.That(walked.Rows.Count).IsGreaterThan(0);
            await Assert.That(fallbacks).IsEqualTo(0).Because("every release read had m_fFlags, so the bit decided the crouch");
            await Assert.That(Diff(walked, collected)).IsNull().Because("the collector's rows off the held parse equal the walk's");
            await Assert.That(Diff(collected, read)).IsNull().Because("the streaming read's rules run carries the same frames");
            await Assert.That(collected.InputDecoder).IsEqualTo(GrenadeCollector.InputDecoderName);
            await Assert.That(collected.TrajectoryStride).IsEqualTo(walked.TrajectoryStride);
        }
    }

    // The first row that differs, with the fields that do; null when every row and trajectory is the same.
    private static string? Diff(GrenadeWalk a, GrenadeWalk b)
    {
        if (a.Rows.Count != b.Rows.Count)
        {
            return $"{a.Rows.Count} rows against {b.Rows.Count}";
        }

        for (int i = 0; i < a.Rows.Count; i++)
        {
            JsonObject left = (JsonObject)JsonSerializer.SerializeToNode(a.Rows[i], Json)!;
            JsonObject right = (JsonObject)JsonSerializer.SerializeToNode(b.Rows[i], Json)!;
            left["trajectory"] = JsonSerializer.SerializeToNode(a.Rows[i].Trajectory, Json);
            right["trajectory"] = JsonSerializer.SerializeToNode(b.Rows[i].Trajectory, Json);
            List<string> fields = [];
            foreach ((string key, JsonNode? value) in left)
            {
                string l = value?.ToJsonString() ?? "null";
                string r = right[key]?.ToJsonString() ?? "null";
                if (l != r)
                {
                    fields.Add($"{key}: {l} against {r}");
                }
            }

            if (fields.Count > 0)
            {
                return $"row {i} ({a.Rows[i].Id}) differs: {string.Join("; ", fields)}";
            }
        }

        return null;
    }

    private static string BenchmarkDemo(int which)
    {
        // The benchmarks are the oracle set; a folder DEMO_PATH stands in only where they are absent.
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is not null && Directory.Exists(Path.Combine(root, "demos", "benchmarks"))
            ? Path.Combine(root, "demos", "benchmarks")
            : Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar) is { } env && Directory.Exists(env)
                ? env
                : throw new SkipTestException("no benchmark demos");
        List<string> demos = [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under {folder}");
    }
}

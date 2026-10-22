#region

using System.Text.Json;
using System.Text.Json.Nodes;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.RulesetsV2.Model;
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
///     The grenade rows off the three rules tables on the five benchmark demos and the seven pro demos: the rows
///     folded from a run over the held parse equal the rows folded from a streaming read, every grenade has its
///     flight and a thrower, and the inputs cover the throws.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class GrenadeRowsRealDemoTests
{
    private static readonly JsonSerializerOptions Json = new(GrenadeSidecar.JsonOptions) { WriteIndented = false };

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    public async Task TheRows_AreTheSameOffTheHeldParseAndOffAStreamingRead(int which)
    {
        string path = CorpusDemo(which);
        string name = Path.GetFileName(path);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        int tickRate = parsed.TickRate > 0 ? parsed.TickRate : 64;
        IReadOnlyList<RulesetDoc> docs = Docs();
        HashSet<string> outputs = new(GrenadeTables.Tables, StringComparer.Ordinal);
        CollectorSet set = CollectorSet.Of([new RegisteredCollector(GrenadeTrajectoryCollector.Contribution(() => 4), null)]);

        GrenadeWalkInputs held = GrenadeIndexEvaluator.Run(parsed, 4);
        GrenadeWalk rows = GrenadeWalker.Assemble(held, ClipRounds.Derive(parsed), parsed.Players.Values, tickRate, parsed.ServerStartTick);

        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, docs, null, outputs, set);
        await Assert.That(forward.Run!.Build.ExcludedRulesets).IsEmpty()
            .Because(string.Join("; ", forward.Run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics)));
        GrenadeWalkInputs streamedInputs = new(GrenadeTables.Read(forward.Run, forward.Demo.ServerStartTick),
            forward.Collected.Get<GrenadeTrajectories>(GrenadeTrajectoryCollector.CollectorId)!);
        GrenadeWalk streamed = GrenadeWalker.Assemble(streamedInputs, forward.Rounds, forward.Demo.Players.Values,
            forward.Demo.TickRate > 0 ? forward.Demo.TickRate : 64, forward.Demo.ServerStartTick);

        List<string> diffs = Diff(rows, streamed);
        Console.WriteLine($"{name}: {rows.Rows.Count} rows (coverage {rows.InputCoverage:0.0000}), {streamed.Rows.Count} streamed; "
                          + $"{held.Tables.Projectiles.Count(p => p.ThrowerSlot < 0)} landings of unresolved throwers, {held.Tables.Releases.Count} releases; "
                          + $"thrower {Distribution(rows.Rows, r => r.ThrowerSource)}; release {Distribution(rows.Rows, r => r.ReleaseSource)}; "
                          + $"jump {Distribution(rows.Rows, r => r.JumpThrowSource)}; detonation {Distribution(rows.Rows, r => r.DetonationSource)}");
        if (diffs.Count > 0)
        {
            Console.WriteLine($"{name}: " + string.Join("\n  ", diffs.Take(20)));
        }

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rows.Count).IsGreaterThan(0);
            await Assert.That(diffs).IsEmpty().Because("the streaming read's run records the same tables and flights");
            await Assert.That(rows.Rows.Count(r => r.Trajectory.Count == 0)).IsEqualTo(0).Because("the observer kept every flight");
            await Assert.That(rows.Rows.Count(r => r.ThrowerSource == ThrowerSource.None)).IsEqualTo(0);
            await Assert.That(rows.InputCoverage).IsGreaterThanOrEqualTo(0.99).Because("the lane carries nearly every thrower's commands");
            await Assert.That(rows.InputDecoder).IsEqualTo(GrenadeWalker.InputDecoderName);
            await Assert.That(rows.TrajectoryStride).IsEqualTo(4);
        }
    }

    private static IReadOnlyList<RulesetDoc> Docs()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null);
        return [.. shipped.Rulesets, .. GrenadeTables.LoadRulesets()];
    }

    private static string Distribution<T>(IEnumerable<GrenadeRow> rows, Func<GrenadeRow, T> key) where T : notnull =>
        string.Join(", ", rows.GroupBy(key).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

    // Every (row, field) that differs, as "id.field: left against right".
    private static List<string> Diff(GrenadeWalk a, GrenadeWalk b)
    {
        List<string> diffs = [];
        if (a.Rows.Count != b.Rows.Count)
        {
            diffs.Add($"rows: {a.Rows.Count} against {b.Rows.Count}");
            return diffs;
        }

        for (int i = 0; i < a.Rows.Count; i++)
        {
            if (a.Rows[i].Id != b.Rows[i].Id)
            {
                diffs.Add($"order at {i}: {a.Rows[i].Id} against {b.Rows[i].Id}");
                continue;
            }

            JsonObject left = (JsonObject)JsonSerializer.SerializeToNode(a.Rows[i], Json)!;
            JsonObject right = (JsonObject)JsonSerializer.SerializeToNode(b.Rows[i], Json)!;
            left["trajectory"] = JsonSerializer.SerializeToNode(a.Rows[i].Trajectory, Json);
            right["trajectory"] = JsonSerializer.SerializeToNode(b.Rows[i].Trajectory, Json);
            foreach ((string key, JsonNode? value) in left)
            {
                string l = value?.ToJsonString() ?? "null";
                string r = right[key]?.ToJsonString() ?? "null";
                if (l != r)
                {
                    diffs.Add($"{a.Rows[i].Id}.{key}: {l} against {r}");
                }
            }
        }

        return diffs;
    }

    // The five benchmark demos, then the seven pro demos. CS2DEMOKIT_CORPUS_DIR predates
    // DEMO_CORPUS and still wins, so an existing configuration keeps working.
    internal static string CorpusDemo(int which)
    {
        string? root = Environment.GetEnvironmentVariable("CS2DEMOKIT_CORPUS_DIR");
        if (root is null || !Directory.Exists(root))
        {
            root = DemoTestHelper.CorpusDir();
        }

        if (root is null || !Directory.Exists(Path.Combine(root, "benchmarks")))
        {
            throw new SkipTestException(
                $"no corpus: set CS2DEMOKIT_CORPUS_DIR or {DemoTestHelper.CorpusRootEnvVar} " +
                "to a folder holding benchmarks/ and pro-demos/");
        }

        List<string> demos = [.. Directory.EnumerateFiles(Path.Combine(root, "benchmarks"), "*.dem").Order(StringComparer.Ordinal)];
        string pro = Path.Combine(root, "pro-demos");
        if (Directory.Exists(pro))
        {
            demos.AddRange(Directory.EnumerateFiles(pro, "*.dem").Order(StringComparer.Ordinal));
        }

        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under {root}");
    }
}

#region

using System.Text.Json;
using System.Text.Json.Nodes;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Graphs;
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
///     The grenade rows folded from the three rules tables against the rows <see cref="GrenadeCollector" /> put on
///     the same run, on the five benchmark demos and the seven pro demos, and the same again off a streaming read.
///     Every field equals the collector's but the release reads: a read on a wire event sees the state the wire
///     had delivered before that frame's entity update, where the collector read the frame's folded state, so for
///     every release the table's pawn must equal the collector's read of the frame before, and the speed the
///     collector's speed over the two reads eight ticks apart ending there. The header may move: the input decoder
///     is the lane behind the tables, and its coverage is per throw rather than per frame.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class GrenadeTableParityTests
{
    private static readonly JsonSerializerOptions Json = new(GrenadeSidecar.JsonOptions) { WriteIndented = false };

    // The fields the one-frame shift of the release read reaches; everything else must be identical.
    private static readonly HashSet<string> ReleaseFields = new(StringComparer.Ordinal)
    {
        "releasePosition", "releaseEyePitch", "releaseEyeYaw", "releaseOnGround", "releaseCrouched", "throwStrength",
        "throwStrengthClass", "speedAtRelease", "movement"
    };

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
    public async Task TheTableRows_EqualTheCollectorsRows_OnTheHeldParseAndOnAStreamingRead(int which)
    {
        string path = CorpusDemo(which);
        string name = Path.GetFileName(path);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        int tickRate = parsed.TickRate > 0 ? parsed.TickRate : 64;
        IReadOnlyList<RulesetDoc> docs = Docs();
        HashSet<string> outputs = new(GrenadeTables.Tables, StringComparer.Ordinal);
        CollectorSet set = CollectorSet.Of([
            new RegisteredCollector(GrenadeCollector.Contribution(() => 4), null),
            new RegisteredCollector(GrenadeTrajectoryCollector.Contribution(() => 4), null)
        ]);

        BuildResult build = ForwardDemoPass.Build(parsed, docs, outputs, set);
        await Assert.That(build.ExcludedRulesets).IsEmpty().Because(string.Join("; ", build.ExcludedRulesets.SelectMany(e => e.Diagnostics)));
        (AnalysisOptions options, CollectorObserver? observer) = set.Declare(new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, options);
        GrenadeCollected collected = observer!.Results.Get<GrenadeCollected>(GrenadeCollector.CollectorId)!;
        GrenadeTrajectories flights = observer.Results.Get<GrenadeTrajectories>(GrenadeTrajectoryCollector.CollectorId)!;
        GrenadeWalkInputs inputs = new(GrenadeTables.Read(run, parsed.ServerStartTick), flights);
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);

        GrenadeWalk old = GrenadeWalker.Assemble(collected, parsed.Players.Values, tickRate, parsed.ServerStartTick);
        GrenadeWalk tables = GrenadeWalker.Assemble(inputs, rounds, parsed.Players.Values, tickRate, parsed.ServerStartTick);

        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        CollectorSet streamedSet = CollectorSet.Of([new RegisteredCollector(GrenadeTrajectoryCollector.Contribution(() => 4), null)]);
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, docs, null, outputs, streamedSet);
        GrenadeWalkInputs streamedInputs = new(GrenadeTables.Read(forward.Run!, forward.Demo.ServerStartTick),
            forward.Collected.Get<GrenadeTrajectories>(GrenadeTrajectoryCollector.CollectorId)!);
        GrenadeWalk streamed = GrenadeWalker.Assemble(streamedInputs, forward.Rounds, forward.Demo.Players.Values,
            forward.Demo.TickRate > 0 ? forward.Demo.TickRate : 64, forward.Demo.ServerStartTick);

        Console.WriteLine($"{name}: {old.Rows.Count} collector rows (coverage {old.InputCoverage:0.0000}), "
                          + $"{tables.Rows.Count} table rows (coverage {tables.InputCoverage:0.0000}), {streamed.Rows.Count} streamed rows; "
                          + $"{inputs.Tables.Projectiles.Count} projectiles ({inputs.Tables.Projectiles.Count(p => p.ThrowerSlot < 0)} landings), "
                          + $"{inputs.Tables.Releases.Count} releases");
        Console.WriteLine($"{name}: thrower {Distribution(tables.Rows, r => r.ThrowerSource)}; release {Distribution(tables.Rows, r => r.ReleaseSource)}; "
                          + $"jump {Distribution(tables.Rows, r => r.JumpThrowSource)}; detonation {Distribution(tables.Rows, r => r.DetonationSource)}");
        Console.WriteLine($"{name}: created after the spawn {inputs.Tables.Projectiles.Count(p => p.CreatedTick != p.SpawnTick)}, "
                          + $"last tick differs from the collector's {LastTickDiffs(collected, inputs)}");
        List<string> diffs = Diff(old, tables);
        List<string> unexplained = [.. diffs.Where(d => !ReleaseFields.Contains(Field(d)))];
        (int checkedReleases, int unreadable, List<string> shifted) = ReleaseShift(collected, tables, tickRate);
        Console.WriteLine($"{name}: held parse: {Report(diffs)}");
        Console.WriteLine($"{name}: release reads checked against the frame before {checkedReleases}, with no read there {unreadable}, "
                          + $"not that read {shifted.Count}" + (shifted.Count > 0 ? ": " + string.Join(" | ", shifted.Take(5)) : ""));
        Console.WriteLine($"{name}: streamed: {Report(Diff(tables, streamed))}");

        using (Assert.Multiple())
        {
            await Assert.That(old.Rows.Count).IsGreaterThan(0);
            await Assert.That(unexplained).IsEmpty().Because("only the release reads move, by the one frame the wire-event read sees");
            await Assert.That(shifted).IsEmpty().Because("the table's release read is the collector's read of the frame before");
            await Assert.That(unreadable).IsLessThanOrEqualTo(checkedReleases / 50).Because("the collector's ring reaches the frame before nearly every release");
            await Assert.That(Diff(tables, streamed)).IsEmpty().Because("the streaming read's run records the same tables");
            await Assert.That(tables.InputDecoder).IsEqualTo(GrenadeWalker.InputDecoderName);
            await Assert.That(tables.TrajectoryStride).IsEqualTo(old.TrajectoryStride);
        }
    }

    // For every table row with a weapon_fire release: the row's release fields recomputed from the collector's
    // pawn read on the frame before the release frame, and from the read eight ticks before that for the speed.
    private static (int Checked, int Unreadable, List<string> Shifted) ReleaseShift(GrenadeCollected collected, GrenadeWalk tables, int tickRate)
    {
        Dictionary<string, ThrowPlan> plans = collected.Plans.ToDictionary(p => p.Track.Id, StringComparer.Ordinal);
        int checkedReleases = 0, unreadable = 0;
        List<string> shifted = [];
        foreach (GrenadeRow row in tables.Rows)
        {
            if (row.ReleaseSource != ReleaseSource.WeaponFire || row.ThrowerSlot < 0 || !plans.TryGetValue(row.Id, out ThrowPlan? plan)
                || plan.ReleaseFrame is not { } frame)
            {
                continue;
            }

            if (!collected.Reads.Pawns.TryGetValue((frame - 1, row.ThrowerSlot), out PawnRead before))
            {
                unreadable++;
                continue;
            }

            checkedReleases++;
            PawnRead? earlier = collected.Reads.Pawns
                .Where(kv => kv.Key.Slot == row.ThrowerSlot && kv.Value.Tick <= before.Tick - GrenadeRules.SpeedWindowTicks)
                .Select(kv => (PawnRead?)kv.Value)
                .MaxBy(r => r!.Value.Tick);
            float? speed = GrenadeRules.HorizontalSpeed(earlier?.Position, before.Tick - GrenadeRules.SpeedWindowTicks, before.Position, before.Tick, tickRate);
            GrenadeRow expected = new()
            {
                ReleasePosition = before.Position is { } p ? WorldPoint.From(p) : null,
                ReleaseEyePitch = before.Pitch,
                ReleaseEyeYaw = before.Yaw,
                ReleaseOnGround = before.OnGround,
                ReleaseCrouched = before.Ducked,
                ThrowStrength = before.ThrowStrength,
                ThrowStrengthClass = GrenadeRules.ClassifyStrength(before.ThrowStrength, before.HeldGrenade),
                SpeedAtRelease = earlier is null ? row.SpeedAtRelease : speed,
                Movement = earlier is null ? row.Movement : GrenadeRules.ClassifyMovement(speed)
            };
            JsonObject want = (JsonObject)JsonSerializer.SerializeToNode(expected, Json)!;
            JsonObject got = (JsonObject)JsonSerializer.SerializeToNode(row, Json)!;
            foreach (string field in ReleaseFields)
            {
                string w = want[field]?.ToJsonString() ?? "null", g = got[field]?.ToJsonString() ?? "null";
                if (w != g)
                {
                    shifted.Add($"{row.Id}.{field}: expected {w}, read {g}");
                }
            }
        }

        return (checkedReleases, unreadable, shifted);
    }

    private static string Field(string diff) =>
        diff.Contains('.') && diff.Contains(':') && diff.IndexOf('.') < diff.IndexOf(':') ? diff[(diff.IndexOf('.') + 1)..diff.IndexOf(':')] : diff;

    private static IReadOnlyList<RulesetDoc> Docs()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null);
        return [.. shipped.Rulesets, .. GrenadeTables.LoadRulesets()];
    }

    private static string Distribution<T>(IEnumerable<GrenadeRow> rows, Func<GrenadeRow, T> key) where T : notnull =>
        string.Join(", ", rows.GroupBy(key).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

    private static int LastTickDiffs(GrenadeCollected collected, GrenadeWalkInputs inputs)
    {
        Dictionary<string, ProjectileFacts> facts = inputs.Tables.Projectiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        return collected.Plans.Count(p => facts.TryGetValue(p.Track.Id, out ProjectileFacts? f) && f.LastTick != p.Track.LastTick);
    }

    // Every (row, field) that differs, as "id.field: left against right".
    private static List<string> Diff(GrenadeWalk a, GrenadeWalk b)
    {
        List<string> diffs = [];
        if (a.Rows.Count != b.Rows.Count)
        {
            diffs.Add($"rows: {a.Rows.Count} against {b.Rows.Count}");
            Dictionary<string, GrenadeRow> right = b.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (GrenadeRow row in a.Rows)
            {
                if (right.TryGetValue(row.Id, out GrenadeRow? other))
                {
                    diffs.AddRange(Diff(row, other));
                }
                else
                {
                    diffs.Add($"{row.Id}: missing on the right");
                }
            }

            foreach (GrenadeRow row in b.Rows.Where(r => a.Rows.All(l => l.Id != r.Id)))
            {
                diffs.Add($"{row.Id}: missing on the left");
            }

            return diffs;
        }

        for (int i = 0; i < a.Rows.Count; i++)
        {
            if (a.Rows[i].Id != b.Rows[i].Id)
            {
                diffs.Add($"order at {i}: {a.Rows[i].Id} against {b.Rows[i].Id}");
            }

            diffs.AddRange(Diff(a.Rows[i], b.Rows[i]));
        }

        return diffs;
    }

    private static IEnumerable<string> Diff(GrenadeRow a, GrenadeRow b)
    {
        JsonObject left = (JsonObject)JsonSerializer.SerializeToNode(a, Json)!;
        JsonObject right = (JsonObject)JsonSerializer.SerializeToNode(b, Json)!;
        left["trajectory"] = JsonSerializer.SerializeToNode(a.Trajectory, Json);
        right["trajectory"] = JsonSerializer.SerializeToNode(b.Trajectory, Json);
        foreach ((string key, JsonNode? value) in left)
        {
            string l = value?.ToJsonString() ?? "null";
            string r = right[key]?.ToJsonString() ?? "null";
            if (l != r)
            {
                yield return $"{a.Id}.{key}: {l} against {r}";
            }
        }
    }

    // The differences by field with counts, then the first three of each field.
    private static string Report(List<string> diffs)
    {
        if (diffs.Count == 0)
        {
            return "identical";
        }

        return string.Join("\n  ", diffs.GroupBy(Field).Select(g => $"{g.Key} x{g.Count()}: " + string.Join(" | ", g.Take(3))));
    }

    // The five benchmark demos, then the seven pro demos, under CS2DEMOKIT_CORPUS_DIR or the repo's demos folder.
    private static string CorpusDemo(int which)
    {
        string? root = Environment.GetEnvironmentVariable("CS2DEMOKIT_CORPUS_DIR");
        if (root is null || !Directory.Exists(root))
        {
            root = DemoTestHelper.FindRepoRoot() is { } repo ? Path.Combine(repo, "demos") : null;
        }

        if (root is null || !Directory.Exists(Path.Combine(root, "benchmarks")))
        {
            throw new SkipTestException("no corpus: set CS2DEMOKIT_CORPUS_DIR to a folder holding benchmarks/ and pro-demos/");
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

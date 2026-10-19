#region

using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The roundindex table against the sampler walk, on the benchmark demos. Folds: A, <see cref="PositionSampler.Walk" />
///     at the shipped stride of 4 frames; B, the walk at stride 1 kept to the first
///     frame at or after each due tick, the frame a cadence fires on; C, the <see cref="RoundIndexTable" /> rows.
///     A against B is the stride's shift, printed, not asserted. C equals B: a cadence step reads the frame it fires on.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundIndexTableParityTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheTableIndex_IsTheWalkIndexOfTheCadenceFrames(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        RulesetDoc doc = LoadRuleset();

        AnalysisRun run = DemoAnalysis.Run(parsed, [doc], new AnalysisOptions { CaptureSnapshots = false });
        IReadOnlyList<MetricTable> tables = run.ProjectConfiguredOutputs();
        MetricTable table = tables.SingleOrDefault(t => t.Name == RoundIndexTable.TableName)
                            ?? throw new InvalidOperationException(
                                $"no table; tables [{string.Join(", ", tables.Select(t => t.Name))}], excluded " +
                                $"[{string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics))}], diagnostics [{string.Join("; ", run.Build.RulesetDiagnostics)}]");
        List<PositionSample> c = RoundIndexTable.Samples(table.Rows.Select(r => (r.Dimensions, r.Values)));
        // What the pass reads: the rows as IPassContext.Table hands them over.
        List<PositionSample> passRows = RoundIndexTable.Samples(FactsCodec.FromMetric(table,
            new FactKey(RoundIndexTable.RulesetId, RoundIndexTable.TableName), "", 0, "player_event").Rows);

        DemoCacheStore store = new(null);
        new RoundFactsEvaluator(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity()).Evaluate(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no round facts rows");
        RoundIndexDemoValues values = RoundIndexDemoValues.For(parsed);
        List<PositionSample> b = CadenceFrames(parsed, rows, values);

        RoundIndexBuild foldA = Fold(values, rows, PositionSampler.Walk(parsed, 4));
        RoundIndexBuild foldB = Fold(values, rows, b);
        RoundIndexBuild foldC = Fold(values, rows, c);
        RoundIndexBuild foldPass = Fold(values, rows, passRows);

        // The walk visits pawns through a live-pawn scan and the table every player, so only B's keys compare.
        Dictionary<(int, int), int> sideAt = c.GroupBy(s => (s.Tick, s.PlayerSlot)).ToDictionary(g => g.Key, g => g.Last().Team);
        int missing = b.Count(s => !sideAt.ContainsKey((s.Tick, s.PlayerSlot)));
        int sideDiffers = b.Count(s => sideAt.TryGetValue((s.Tick, s.PlayerSlot), out int side) && side != s.Team);
        Console.WriteLine($"B samples with no table row {missing}, whose side differs {sideDiffers}");

        int serverVsFrame = parsed.Frames.Count(f => f.GameTick is { } g && g != f.ServerTick);
        Console.WriteLine($"demo {Path.GetFileName(path)}: frames {parsed.Frames.Count}, frames whose GameTick != ServerTick {serverVsFrame}");
        Console.WriteLine($"rows: table {table.Rows.Count}, table samples {c.Count}, null-position rows {table.Rows.Count - c.Count}, B samples {b.Count}");
        Report("A vs B (stride shift)", foldA, foldB);
        Report("B vs C (table path)", foldB, foldC);

        using (Assert.Multiple())
        {
            await Assert.That(missing).IsEqualTo(0).Because("every pawn the walk samples has a row at that step");
            await Assert.That(sideDiffers).IsEqualTo(0).Because("player.team is the pawn's side at the step");
            await Assert.That(foldC.Index.Serialize()).IsEqualTo(foldB.Index.Serialize());
            await Assert.That(Positions(foldC)).IsEqualTo(Positions(foldB));
            await Assert.That(foldPass.Index.Serialize()).IsEqualTo(foldB.Index.Serialize()).Because("the pass's rows fold like the table's");
            await Assert.That(Positions(foldPass)).IsEqualTo(Positions(foldB));
        }
    }

    internal static RulesetDoc LoadRuleset() => RoundIndexTable.LoadRuleset();

    private static RoundIndexBuild Fold(RoundIndexDemoValues values, RoundFactsRows rows, IEnumerable<PositionSample> samples) =>
        RoundIndexBuilder.BuildWithPositions(values, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance, samples);

    private static string Positions(RoundIndexBuild build) => Encoding.UTF8.GetString(build.Positions.SerializeUtf8());

    // The cadence's frames: from each live round's freeze end, the first frame whose frame clock reaches each
    // due tick, stamped with that clock. The fold clips to the round windows, so stepping past an end is harmless.
    private static List<PositionSample> CadenceFrames(ParsedDemo parsed, RoundFactsRows rows, RoundIndexDemoValues values)
    {
        int period = RoundIndexOptions.Default.CadenceTicks(values.TickRate);
        List<int> anchors = [.. rows.Rounds.Where(r => r.IsLive).Select(r => r.FreezeEndTick).Order()];
        Dictionary<int, int> picked = [];
        int next = 0;
        long due = long.MaxValue;
        for (int i = 0; i < parsed.Frames.Count; i++)
        {
            int clock = parsed.Frames[i].GameTick ?? parsed.Frames[i].ServerTick;
            while (next < anchors.Count && clock >= anchors[next])
            {
                due = anchors[next];
                next++;
            }

            if (clock < due)
            {
                continue;
            }

            picked[i] = clock;
            while (clock >= due)
            {
                due += period;
            }
        }

        return [.. PositionSampler.Walk(parsed).Where(s => picked.ContainsKey(s.FrameIndex)).Select(s => s with { Tick = picked[s.FrameIndex] })];
    }

    private static void Report(string label, RoundIndexBuild expected, RoundIndexBuild actual)
    {
        string ei = expected.Index.Serialize(), ai = actual.Index.Serialize();
        string ep = Positions(expected), ap = Positions(actual);
        int roundDiffs = expected.Index.Rounds.Zip(actual.Index.Rounds).Count(p => string.Join(";", p.First.Runs) != string.Join(";", p.Second.Runs));
        int stepDiffs = expected.Positions.Rounds.Zip(actual.Positions.Rounds)
            .Sum(p => p.First.Pos.Zip(p.Second.Pos).Count(s => string.Join(",", s.First) != string.Join(",", s.Second))
                      + Math.Abs(p.First.Pos.Count - p.Second.Pos.Count));
        int steps = expected.Positions.Rounds.Sum(r => r.Pos.Count);
        Console.WriteLine($"{label}: index {(ei == ai ? "equal" : "differs")}, positions {(ep == ap ? "equal" : "differs")}, " +
                          $"rounds whose runs differ {roundDiffs}/{expected.Index.Rounds.Count}, steps whose tuples differ {stepDiffs}/{steps}");
        PrintFirstDiff(label + " index", ei, ai);
        PrintFirstDiff(label + " positions", ep, ap);
    }

    private static void PrintFirstDiff(string label, string expected, string actual)
    {
        int at = 0;
        while (at < Math.Min(expected.Length, actual.Length) && expected[at] == actual[at])
        {
            at++;
        }

        if (at < Math.Max(expected.Length, actual.Length))
        {
            int from = Math.Max(0, at - 150);
            Console.WriteLine($"{label} first diff at {at} of {expected.Length}/{actual.Length}:\n  expected " +
                              $"{expected.Substring(from, Math.Min(300, expected.Length - from))}\n  actual   " +
                              $"{actual.Substring(from, Math.Min(300, actual.Length - from))}");
        }
    }

    private static string BenchmarkDemo(int which)
    {
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is not null && Directory.Exists(Path.Combine(root, "demos", "benchmarks"))
            ? Path.Combine(root, "demos", "benchmarks")
            : Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar) is { } env && Directory.Exists(env)
                ? env
                : throw new SkipTestException("no benchmark demos");
        string[] demos = [.. Directory.EnumerateFiles(folder, "*.dem").Order(StringComparer.Ordinal)];
        return which < demos.Length ? demos[which] : throw new SkipTestException($"fewer than {which + 1} benchmark demos");
    }
}

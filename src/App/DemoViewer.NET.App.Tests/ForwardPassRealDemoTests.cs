#region

using DemoViewer.NET.Extensions.StratBook;
using System.Text.Json;
using System.Text.Json.Nodes;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Library tier 2, bare highlights and round facts off one forward pass must write what the retained
///     parse writes, and the merged build must reproduce the two separate builds it replaced. Only the two
///     fingerprints move. Read in place from the <c>DEMO_PATH</c> folder (its three smallest demos).
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ForwardPassRealDemoTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, IncludeFields = true };

    private static RuleConfigLoadResult ShippedRules() =>
        YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null);

    // The two separate builds before the merge: highlights alone, round_facts alone.
    private static IReadOnlyList<RulesetDoc> HighlightsOnly(IReadOnlyList<RulesetDoc> rules) =>
        [.. rules.Where(r => r.Id != RoundFactsFingerprint.RulesetId)];

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ForwardPass_WritesWhatTheRetainedParseWrites_AndMatchesTheSeparateBuilds(int which) =>
        await Compare(BackgroundPlanRealDemoTests.SmallestDemo(which), true);

    /// <summary>
    ///     The same comparison over demos named in <c>FORWARD_PASS_DEMOS</c> (separated by <c>;</c>), read in
    ///     place: large pro demos, overtime, dialects without <c>begin_new_match</c>, demos with no round facts rows.
    /// </summary>
    [Test]
    public async Task ForwardPass_ListedDemos()
    {
        string[] paths = (Environment.GetEnvironmentVariable("FORWARD_PASS_DEMOS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0)
        {
            throw new TUnit.Core.Exceptions.SkipTestException("FORWARD_PASS_DEMOS names no demos");
        }

        foreach (string path in paths)
        {
            await Compare(path, false);
        }
    }

    private static async Task Compare(string path, bool requireRows)
    {
        MergedRulesBuild merged = new(ShippedRules);

        (Dictionary<string, string> retained, Dictionary<string, string> old) = Retained(path, merged);
        Dictionary<string, string> forward = Forward(path, merged);
        string rulesOnly = RulesOnlyRoundFacts(path, merged);

        using (Assert.Multiple())
        {
            await Assert.That(forward.Keys).IsEquivalentTo(retained.Keys);
            foreach ((string key, string value) in retained)
            {
                await Assert.That(forward[key]).IsEqualTo(value).Because($"{key}, forward vs retained, {Path.GetFileName(path)}");
            }

            await Assert.That(rulesOnly).IsEqualTo(forward["round facts"]).Because("a Round Facts backlog visit reads rules only");
            await Assert.That(forward["firings"]).IsEqualTo(old["firings"]).Because("merged build vs highlights alone");
            await Assert.That(forward["round facts"]).IsEqualTo(old["round facts"]).Because("merged build vs round_facts alone");
            // The engine's fingerprint hashes highlight definitions only, so adding round_facts leaves it as it was.
            await Assert.That(forward["highlight fingerprint"]).IsEqualTo(old["highlight fingerprint"]);
            if (forward["round facts"] != None)
            {
                await Assert.That(forward["round facts fingerprint"]).IsNotEqualTo(old["round facts fingerprint"]);
            }

            foreach (string key in requireRows ? new[] { "firings", "round facts", "record", "library" } : ["firings", "record", "library"])
            {
                await Assert.That(forward[key].Length).IsGreaterThan(200).Because($"{key}: {forward[key]}");
            }
        }

        Console.WriteLine($"{Path.GetFileName(path)}: " + string.Join(", ", forward.Select(kv => $"{kv.Key} {kv.Value.Length}"))
                          + $", rows {(forward["round facts"] == None ? "none" : "yes")}");
    }

    [Test]
    public async Task ForwardPass_Cancelled_ThrowsAndReportedProgressFirst()
    {
        string path = BackgroundPlanRealDemoTests.SmallestDemo(0);
        MergedRulesBuild merged = new(ShippedRules);
        using CancellationTokenSource cancel = new();
        double seen = 0;
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(cancel.Token));

        Assert.Throws<OperationCanceledException>(() => ForwardDemoPass.Run(reader, ForwardNeeds.FinalState | ForwardNeeds.Rules, merged.Docs,
            p =>
            {
                seen = p;
                if (p > 0.25)
                {
                    cancel.Cancel();
                }
            }, cancel.Token));
        await Assert.That(seen).IsGreaterThan(0.25).And.IsLessThan(0.5);
    }

    // The retained parse the queue runs, through the evaluators' retained entry points, plus the two
    // separate builds that ran before the merge.
    private static (Dictionary<string, string> Written, Dictionary<string, string> Old) Retained(string path, MergedRulesBuild merged)
    {
        ParsedDemo parsed = BackgroundPlanRealDemoTests.ParseMapped(path, DemoProcessingQueue.WithoutUserCommands);
        Dictionary<string, string> written = Write(path, merged, (library, entry, highlights, facts) =>
        {
            library.IndexTier2Core(entry, parsed);
            highlights.Evaluate(path, parsed);
            facts.Evaluate(path, parsed);
        });
        written["firings"] = JsonSerializer.Serialize(merged.BareRun(parsed).Highlights, Json);

        IReadOnlyList<RulesetDoc> rules = ShippedRules().Rulesets;
        BuildResult highlightsOnly = DemoAnalysis.Build(parsed, HighlightsOnly(rules));
        RulesetDoc doc = merged.EnabledDoc(RoundFactsFingerprint.RulesetId)!;
        BuildResult factsOnly = DemoAnalysis.Build(parsed, [doc]);
        AnalysisRun factsRun = DemoAnalysis.Evaluate(parsed, factsOnly, new AnalysisOptions { CaptureSnapshots = false });
        MetricTable? table = factsRun.ProjectConfiguredOutputs(parsed)
            .FirstOrDefault(t => t.Name == EngineRoundFactsRowSource.TableName);
        RoundFactsTable factsTable =
            EngineRoundFactsRowSource.FromTable(table, EngineRoundFactsRowSource.ParametersOf(doc), ClipRounds.Derive(parsed));
        RoundFactsRows rows = RoundFactsProjection.Project(ClipRounds.Derive(parsed), factsTable);
        rows.Clock = RoundFactsClock.For(parsed);

        Dictionary<string, string> old = new()
        {
            ["firings"] = JsonSerializer.Serialize(
                DemoAnalysis.Evaluate(parsed, highlightsOnly, new AnalysisOptions { CaptureSnapshots = false }).Highlights, Json),
            ["round facts"] = factsTable.Rows.Count == 0 ? None : WithoutSha(rows),
            ["highlight fingerprint"] = HighlightConfigFingerprint.Compute(
                HighlightsOnly(rules), parsed.TickRate, RulesHighlightHarvester.GotvProfileId).Fingerprint,
            ["round facts fingerprint"] = RoundFactsFingerprint.Combine(RoundFactsRecords.Schema,
                HighlightConfigFingerprint.Compute([doc], parsed.TickRate, RulesHighlightHarvester.GotvProfileId).Fingerprint)
        };
        return (written, old);
    }

    private static Dictionary<string, string> Forward(string path, MergedRulesBuild merged)
    {
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult pass = ForwardDemoPass.Run(reader, ForwardNeeds.FinalState | ForwardNeeds.Rules, merged.Docs);
        Dictionary<string, string> written = Write(path, merged, (library, entry, highlights, facts) =>
        {
            library.IndexTier2Core(entry, pass);
            highlights.EvaluateForward(path, pass);
            facts.EvaluateForward(path, pass);
        });
        written["firings"] = JsonSerializer.Serialize(pass.Run!.Highlights, Json);
        return written;
    }

    // What a round-facts-only visit runs: no final-state tracker, the build's own plan plus the freeze ends.
    private static string RulesOnlyRoundFacts(string path, MergedRulesBuild merged)
    {
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult pass = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, merged.Docs);
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });
        RulesRoundFactsRulesetIdentity identity = new(merged);
        new RoundFactsEvaluator(cache, new EngineRoundFactsRowSource(identity), identity).EvaluateForward(path, pass);
        return WithoutSha(cache.TryLoadRecord(path)?.RoundFacts);
    }

    private static Dictionary<string, string> Write(string path, MergedRulesBuild merged,
        Action<DemoLibraryService, DemoEntry, HighlightScanService, RoundFactsEvaluator> run)
    {
        FileInfo file = new(path);
        DemoEntry entry = new()
        {
            FilePath = path,
            FileName = file.Name,
            Directory = file.DirectoryName ?? "",
            FileSizeBytes = file.Length,
            Modified = file.LastWriteTime
        };
        string libraryJson = Path.Combine(Path.GetTempPath(), $"dv-forward-{Guid.NewGuid():N}.json");
        DemoCacheStore cache = new(null);
        RulesRoundFactsRulesetIdentity identity = new(merged);
        try
        {
            using DemoLibraryService library = new(a => a(), libraryJson, demoCache: cache);
            using HighlightScanService highlights = new(cache, new RulesHighlightHarvester(merged), () => [path], () => true);
            RoundFactsEvaluator facts = new(cache, new EngineRoundFactsRowSource(identity), identity);
            run(library, entry, highlights, facts);

            DemoCacheRecord record = cache.TryLoadRecord(path)!;
            JsonObject recordJson = JsonSerializer.SerializeToNode(record, Json)!.AsObject();
            StripStamps(recordJson);
            return new Dictionary<string, string>
            {
                ["record"] = recordJson.ToJsonString(),
                ["round facts"] = WithoutSha(record.RoundFacts),
                ["highlight fingerprint"] = record.ConfigFingerprint ?? "",
                ["round facts fingerprint"] = record.RoundFactsFingerprint() ?? "",
                ["library"] = JsonSerializer.Serialize(new
                {
                    entry.State,
                    entry.Players,
                    entry.DurationSeconds,
                    entry.MapName,
                    entry.CtScore,
                    entry.TScore,
                    entry.CtClan,
                    entry.TClan,
                    Rounds = record.Rounds.Count,
                    Roster = record.Players
                }, Json)
            };
        }
        finally
        {
            File.Delete(libraryJson);
        }
    }

    private const string None = "none";

    private static string WithoutSha(RoundFactsRows? rows)
    {
        if (rows is null)
        {
            return None;
        }

        JsonObject json = JsonSerializer.SerializeToNode(rows, Json)!.AsObject();
        json.Remove("DemoSha256");
        return json.ToJsonString();
    }

    // Write times differ between the two runs by construction.
    private static void StripStamps(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).Where(k => k.EndsWith("AtTicks", StringComparison.Ordinal)).ToList())
                {
                    obj.Remove(key);
                }

                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    StripStamps(kv.Value);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    StripStamps(item);
                }

                break;
        }
    }
}

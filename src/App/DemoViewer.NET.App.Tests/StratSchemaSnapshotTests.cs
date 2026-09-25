#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>Shared strat fixtures: a temp root per test, the schema sample, and the scripted history the log golden is written from.</summary>
internal static class StratTestData
{
    public static readonly Guid TeamId = Guid.Parse("3f2a9c1e-5b7d-4e2f-8a61-0c9d4b3e2f10");

    public static readonly StratOwner Team = StratOwner.Team(TeamId);

    public static readonly Guid SampleId = Guid.Parse("6f1c0d2e-3a4b-4c5d-8e9f-a0b1c2d3e4f5");

    public static readonly Guid HistoryId = Guid.Parse("0b7e4a21-9c3d-4f58-b6a2-e1d0c9b8a7f6");

    public static readonly DateTime Created = new(2026, 9, 23, 14, 2, 11, DateTimeKind.Utc);

    public static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-strats-{Guid.NewGuid():N}");

    public static void DeleteQuietly(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp dir left behind is harmless.
        }
    }

    /// <summary>A clock that advances one minute per read, so every commit has its own stamp.</summary>
    public static Func<DateTime> SteppingClock(DateTime start)
    {
        DateTime next = start;
        return () =>
        {
            DateTime now = next;
            next = next.AddMinutes(1);
            return now;
        };
    }

    public static Guid StepId(int n) => Guid.Parse($"9a0b0000-0000-4000-8000-{n:D12}");

    public static StratStep Step(int n, double atSeconds, string actor, string verb, string? from = null, string? to = null) => new()
    {
        Id = StepId(n),
        AtSeconds = atSeconds,
        Actor = actor,
        Verb = verb,
        From = from is null ? null : new PlaceRef { Place = from },
        To = to is null ? null : new PlaceRef { Place = to }
    };

    /// <summary>A valid strat with no unknown fields: the base most store tests edit.</summary>
    public static StratDocument Minimal(Guid? id = null, StratOwner? owner = null, string map = "de_mirage")
    {
        StratDocument document = StratDocument.Create(id ?? Guid.NewGuid(), owner ?? Team, map, "T", "execute", "A exec", Created);
        document.TargetSite = "A";
        document.Steps.Add(Step(1, 90, "B", "throw", "TRamp", "BombsiteA"));
        document.Steps.Add(Step(2, 82, "C", "throw", "TRamp", "BombsiteA"));
        return document;
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    ///     Every field of schema v1 at least once, correction 17's four additions (opponent position slots, the
    ///     landing point, the canvas block, positions and strokes on three steps), and an unknown field at root,
    ///     step, branch and position level so the golden pins that they survive.
    /// </summary>
    public static StratDocument SchemaSample()
    {
        StratDocument document = StratDocument.Create(SampleId, Team, "de_mirage", "T", "execute", "A exec, double smoke", Created);
        document.TargetSite = "A";
        document.Economy = "full";
        document.Tempo = "slow";
        document.Trigger = new StratTrigger { Text = "on call at 1:15", Kind = "time", AtSeconds = 75 };
        document.Status = nameof(StratStatus.Active);
        document.Revision = 4;
        document.ModifiedUtc = Created.AddMinutes(98);
        document.Origin = new StratOrigin
        {
            DemoSha256 = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12",
            Round = 7,
            FileName = "match730_003842233788306292960_0260929275_408.dem"
        };
        document.Tags = ["default-break", "vs-aggressive-ct"];
        document.Notes = "Stairs smoke first, CT smoke second, then jungle molly.";
        document.Canvas = new StratCanvas();
        document.Slots[0].Role = "entry";
        document.Slots[1].Role = "support";
        document.Slots[2].Role = "lurk";
        document.Slots[2].SteamId = "76561198000000003";
        document.Slots[3].Role = "awp";
        document.Slots[4].Role = "igl";
        document.Extra = new Dictionary<string, JsonElement> { ["futureRoot"] = Element("""{ "kept": true }""") };

        StratStep smoke = Step(1, 90, "B", "throw", "TRamp", "BombsiteA");
        smoke.Utility = new UtilityRef { Kind = "smoke", Landing = new UtilityLanding { Place = "Stairs", X = -1024.5, Y = -1616, LevelMinZ = -256 } };
        smoke.Note = "throw on the 1:30 call, not before";
        smoke.Positions =
        [
            new StepPosition { Slot = "A", X = 400, Y = -1800, LevelMinZ = -256, YawDegrees = 180 },
            new StepPosition { Slot = "B", X = 420, Y = -1750, LevelMinZ = -256, Extra = new Dictionary<string, JsonElement> { ["futurePosition"] = Element("3") } },
            new StepPosition { Slot = "O1", X = -1300, Y = -2100, LevelMinZ = -256 }
        ];
        smoke.Strokes = [JsonNode.Parse("""{ "kind": "arrow", "space": { "kind": "world", "levelMinZ": -256 }, "points": [[420, -1750], [-900, -1600]], "color": "#FFC107", "width": 3 }""")!.AsObject()];
        smoke.Interpolation = "linear";
        smoke.Extra = new Dictionary<string, JsonElement> { ["futureStep"] = Element("\"kept\"") };

        StratStep molly = Step(2, 76, "C", "throw", "TRamp", "BombsiteA");
        molly.Utility = new UtilityRef { Kind = "molotov", LineupId = Guid.Parse("e4f5a6b7-c8d9-4e0f-9a1b-2c3d4e5f6a7b"), Landing = new UtilityLanding { Place = "Jungle" } };
        molly.Positions = [new StepPosition { Slot = "C", X = 380, Y = -1700, LevelMinZ = -256 }];
        molly.HoldSeconds = 2.5;
        molly.Interpolation = "hold";

        StratStep peek = Step(3, 65, "A", "peek", "TRamp", "Connector");
        peek.Positions = [new StepPosition { Slot = "A", X = -600, Y = -1400, LevelMinZ = -256 }];
        peek.Strokes = [JsonNode.Parse("""{ "kind": "text", "space": { "kind": "world", "levelMinZ": -256 }, "points": [[-600, -1400]], "text": "swing wide" }""")!.AsObject()];

        StratStep all = Step(4, 60, "all", "move", "TRamp", "BombsiteA");

        document.Steps = [smoke, molly, peek, all];
        document.Branches =
        [
            new StratBranch
            {
                Id = Guid.Parse("c2d3e4f5-a6b7-4c8d-9e0f-1a2b3c4d5e6f"),
                AfterStepId = molly.Id,
                Condition = new BranchCondition { Text = "contact at Connector before 1:05" },
                Target = new BranchTarget { StratId = SampleId, StepId = all.Id },
                Note = "",
                Extra = new Dictionary<string, JsonElement> { ["futureBranch"] = Element("false") }
            }
        ];
        return document;
    }

    /// <summary>
    ///     The scripted history the log golden pins: five commits through a real store, one per kind of edit,
    ///     with revision 2 a drag of forty replaces merged by the commit buffer into one op.
    /// </summary>
    public static void RunHistoryScript(StratStore store)
    {
        StratDocument document = Minimal(HistoryId);
        document.Economy = "full";
        document.Steps[0].Note = "stairs first";
        document.Steps[1].Utility = new UtilityRef { Kind = "molotov", Landing = new UtilityLanding { Place = "Jungle" } };
        Commit(store, document, [], "created");

        StratCommitBuffer drag = new();
        for (int i = 1; i <= 40; i++)
        {
            double from = 82 - (i - 1) * 0.15;
            double to = i == 40 ? 76 : 82 - i * 0.15;
            drag.Record(PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(from), JsonValue.Create(to)), Created.AddSeconds(i));
        }

        document = Commit(store, document, drag.Drain(), "molotov moved from 1:22 to 1:16");

        StratStep peek = Step(3, 65, "A", "peek", "TRamp", "Connector");
        document = Commit(store, document,
        [
            PatchOp.AddOp("/steps/2", JsonSerializer.SerializeToNode(peek, StratJsonContext.Default.StratStep)),
            PatchOp.ReplaceOp("/name", JsonValue.Create(document.Name), JsonValue.Create("A exec, con peek"))
        ], "step added: A peeks Connector at 1:05");

        StratBranch branch = new()
        {
            Id = Guid.Parse("c2d3e4f5-0000-4000-8000-000000000001"),
            AfterStepId = StepId(1),
            Condition = new BranchCondition { Text = "CT smoke fades early" },
            Target = new BranchTarget { StratId = HistoryId, StepId = StepId(3) }
        };
        document = Commit(store, document,
        [
            PatchOp.AddOp("/branches/0", JsonSerializer.SerializeToNode(branch, StratJsonContext.Default.StratBranch)),
            PatchOp.ReplaceOp("/status", JsonValue.Create(document.Status), JsonValue.Create(nameof(StratStatus.Active)))
        ], "branch added; status Theory → Active");

        Commit(store, document,
        [
            PatchOp.RemoveOp("/steps/0/note", JsonValue.Create(document.Steps[0].Note)),
            PatchOp.ReplaceOp("/economy", JsonValue.Create(document.Economy), JsonValue.Create("force")),
            PatchOp.ReplaceOp("/targetSite", JsonValue.Create(document.TargetSite), null)
        ], "note removed; economy full → force; target site cleared");
    }

    public const int HistoryRevisions = 5;

    private static StratDocument Commit(StratStore store, StratDocument document, IReadOnlyList<PatchOp> ops, string summary)
    {
        StratDocument next = ops.Count == 0 ? document : StratHistory.Apply(document, ops);
        StratSaveResult result = store.Save(next, ops, summary);
        if (!result.Saved)
        {
            throw new InvalidOperationException("history script commit failed: " + result.Reason);
        }

        return next;
    }
}

/// <summary>
///     Schema v1 of <c>.dvstrat.json</c> and its <c>.history.jsonl</c>, pinned by committed samples under
///     tests/fixtures/strats/. A team shares these files by copying a folder, so a renamed field is a
///     compatibility break the store's own round trips would happily agree with themselves about. Regenerate
///     deliberately with <c>PB2D_GOLDEN_UPDATE=1</c> and read the diff before committing it.
/// </summary>
[NotInParallel]
public class StratSchemaSnapshotTests
{
    private const string SampleName = "schema-v1.sample.dvstrat.json";
    private const string HistoryName = "schema-v1.sample.history.jsonl";

    private static bool Updating => Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1";

    private static string SnapshotName(int revision) => $"schema-v1.sample.history.r{revision}.dvstrat.json";

    private static string FixtureDir()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        return repo is null
            ? throw new SkipTestException("repo root not found from the test output directory")
            : Path.Combine(repo, "tests", "fixtures", "strats");
    }

    [Test]
    public async Task V1Schema_MatchesCheckedInSample()
    {
        string path = Path.Combine(FixtureDir(), SampleName);
        if (Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StratStore.Serialize(StratTestData.SchemaSample()) + "\n");
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException($"missing {path}; regenerate with PB2D_GOLDEN_UPDATE=1");
        }

        string original = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        string root = StratTestData.TempRoot();
        try
        {
            // Through the real store, not the serializer alone: the bytes a user's file goes through are the store's.
            string file = Path.Combine(StratStore.FolderFor(root, StratTestData.Team, "de_mirage"), StratTestData.SampleId + StratStore.StratExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, original);

            StratStore store = new(root);
            StratLoadResult loaded = store.Load(StratTestData.SampleId);
            StratDocument document = loaded.Document!;

            using (Assert.Multiple())
            {
                await Assert.That(document.Revision).IsEqualTo(4);
                await Assert.That(document.Steps.Count).IsEqualTo(4);
                await Assert.That(document.Steps.Count(s => s.Positions.Count > 0)).IsEqualTo(3);
                await Assert.That(document.Steps[0].Positions[2].Slot).IsEqualTo("O1");
                await Assert.That(document.Steps[0].Utility!.Landing!.LevelMinZ).IsEqualTo(-256);
                await Assert.That(document.Canvas!.FadeOutTicks).IsEqualTo(16);
                await Assert.That(document.Extra!.ContainsKey("futureRoot")).IsTrue();
                await Assert.That(document.Steps[0].Extra!.ContainsKey("futureStep")).IsTrue();
                await Assert.That(document.Steps[0].Positions[1].Extra!.ContainsKey("futurePosition")).IsTrue();
                await Assert.That(document.Branches[0].Extra!.ContainsKey("futureBranch")).IsTrue();
                await Assert.That(loaded.Issues.Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
                await Assert.That(loaded.Issues.Any(i => i.Message.Contains("futureRoot", StringComparison.Ordinal))).IsTrue()
                    .Because("unknown fields are listed as info on load");
            }

            await Assert.That(StratStore.Serialize(document) + "\n").IsEqualTo(original)
                .Because("the v1 strat is a published format; a round trip must be field-identical, unknown fields included");
            await Assert.That(StratStore.Serialize(StratTestData.SchemaSample()) + "\n").IsEqualTo(original)
                .Because("this build still writes the committed shape");

            // A commit with nothing in it writes nothing, so a stored file is never churned by a no-op save.
            await Assert.That(store.Save(document, [], null).Saved).IsTrue();
            await Assert.That(File.ReadAllText(file)).IsEqualTo(original).Because("the file is not touched at all");

            // A real commit keeps every unknown field.
            PatchOp op = PatchOp.ReplaceOp("/tempo", JsonValue.Create("slow"), JsonValue.Create("mid"));
            StratDocument edited = StratHistory.Apply(document, [op]);
            StratSaveResult saved = store.Save(edited, [op], "tempo slow → mid");
            string written = File.ReadAllText(file);
            using (Assert.Multiple())
            {
                await Assert.That(saved.Saved).IsTrue();
                await Assert.That(saved.Revision).IsEqualTo(5);
                await Assert.That(written).Contains("\"futureRoot\"");
                await Assert.That(written).Contains("\"futureStep\"");
                await Assert.That(written).Contains("\"futurePosition\"");
                await Assert.That(written).Contains("\"futureBranch\"");
                await Assert.That(written).Contains("\"tempo\": \"mid\"");
            }
        }
        finally
        {
            StratTestData.DeleteQuietly(root);
        }
    }

    [Test]
    public async Task V1History_MatchesCheckedInSample()
    {
        string dir = FixtureDir();
        string historyPath = Path.Combine(dir, HistoryName);
        string scratch = StratTestData.TempRoot();
        try
        {
            // Run the script through a real store with a pinned clock; the log it writes is the golden.
            StratStore writer = new(scratch, utcNow: StratTestData.SteppingClock(StratTestData.Created));
            StratTestData.RunHistoryScript(writer);
            string folder = StratStore.FolderFor(scratch, StratTestData.Team, "de_mirage");
            string writtenLog = File.ReadAllText(Path.Combine(folder, StratTestData.HistoryId + StratStore.HistoryExtension));

            if (Updating)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(historyPath, writtenLog);
                for (int n = 1; n <= StratTestData.HistoryRevisions; n++)
                {
                    File.WriteAllText(Path.Combine(dir, SnapshotName(n)), StratStore.Serialize(writer.Materialize(StratTestData.HistoryId, n)!) + "\n");
                }
            }

            if (!File.Exists(historyPath))
            {
                throw new SkipTestException($"missing {historyPath}; regenerate with PB2D_GOLDEN_UPDATE=1");
            }

            string committedLog = File.ReadAllText(historyPath).Replace("\r\n", "\n", StringComparison.Ordinal);
            await Assert.That(writtenLog).IsEqualTo(committedLog).Because("this build still writes the committed log, one line per commit");

            string[] lines = committedLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Length).IsEqualTo(StratTestData.HistoryRevisions);
            await Assert.That(lines[1]).Contains("\"from\":82,\"value\":76")
                .Because("a forty-replace drag inside one commit is one op carrying the first from and the last value");

            // Materialize from the committed log alone, in a store that has never seen the script.
            string reader = StratTestData.TempRoot();
            try
            {
                string readerFolder = StratStore.FolderFor(reader, StratTestData.Team, "de_mirage");
                Directory.CreateDirectory(readerFolder);
                File.WriteAllText(Path.Combine(readerFolder, StratTestData.HistoryId + StratStore.HistoryExtension), committedLog);
                StratStore store = new(reader);
                await Assert.That(store.TryLoad(StratTestData.HistoryId)?.Revision).IsEqualTo(StratTestData.HistoryRevisions)
                    .Because("a log with no strat file is a first commit that crashed; the store recovers the file");

                string[] snapshots = new string[StratTestData.HistoryRevisions + 1];
                for (int n = 1; n <= StratTestData.HistoryRevisions; n++)
                {
                    string snapshotPath = Path.Combine(dir, SnapshotName(n));
                    snapshots[n] = File.ReadAllText(snapshotPath).Replace("\r\n", "\n", StringComparison.Ordinal);
                    await Assert.That(StratStore.Serialize(store.Materialize(StratTestData.HistoryId, n)!) + "\n").IsEqualTo(snapshots[n])
                        .Because($"Materialize({n}) must equal the committed snapshot");
                }

                await Assert.That(StratStore.Serialize(store.TryLoad(StratTestData.HistoryId)!) + "\n").IsEqualTo(snapshots[^1]);

                // Every entry's inverse, applied newest first, walks the latest state back to revision 1.
                IReadOnlyList<HistoryEntry> log = store.History(StratTestData.HistoryId);
                JsonNode? node = StratHistory.ToNode(store.Materialize(StratTestData.HistoryId, StratTestData.HistoryRevisions)!);
                for (int i = log.Count - 1; i >= 1; i--)
                {
                    node = StratHistory.ApplyAll(node, log[i].Inverse().Ops);
                    StratDocument back = node!.Deserialize(StratJsonContext.Default.StratDocument)!;
                    back.Revision = log[i - 1].Revision;
                    back.ModifiedUtc = log[i - 1].AtUtc;
                    await Assert.That(StratStore.Serialize(back) + "\n").IsEqualTo(snapshots[i])
                        .Because($"inverting revision {i + 1} returns to revision {i}");
                }
            }
            finally
            {
                StratTestData.DeleteQuietly(reader);
            }
        }
        finally
        {
            StratTestData.DeleteQuietly(scratch);
        }
    }

    [Test]
    public async Task NullFieldsAreAbsent_AndPrimaryIsWrittenOnlyWhenTrue()
    {
        string json = StratStore.Serialize(StratTestData.Minimal(StratTestData.SampleId));
        CalloutTable table = new()
        {
            Map = "de_mirage",
            Aliases = [new CalloutAlias { Alias = "palace", Place = "PalaceInterior", Primary = true }, new CalloutAlias { Alias = "con", Place = "Connector" }]
        };
        string callouts = JsonSerializer.Serialize(table, StratJsonContext.Default.CalloutTable);

        using (Assert.Multiple())
        {
            await Assert.That(json).DoesNotContain("\"origin\"");
            await Assert.That(json).DoesNotContain("\"canvas\"");
            await Assert.That(json).DoesNotContain("\"steamId\"");
            await Assert.That(json).DoesNotContain("\"teamId\": null");
            await Assert.That(json).Contains("\"clock\": {\n    \"kind\": \"round\",\n    \"roundSeconds\": 115");
            await Assert.That(json).Contains("\"positions\": []");
            await Assert.That(callouts.Split("\"primary\"").Length - 1).IsEqualTo(1);
        }
    }
}

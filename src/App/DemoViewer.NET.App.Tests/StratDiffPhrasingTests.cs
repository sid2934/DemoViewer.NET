#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The history's words and the history pane's data (strat-model.md §3.8, §9 step 5): each op phrased against
///     the document as it stood before it, place names through the owner's callouts, the committed sample log
///     phrased entry by entry, a commit without a summary given one by the store, and the pane's split either
///     side of an entry.
/// </summary>
public class StratDiffPhrasingTests
{
    private static JsonNode Node(StratDocument document) => StratHistory.ToNode(document);

    private static StratDocument WithMolotov()
    {
        StratDocument document = Minimal(HistoryId);
        document.Steps[1].Utility = new UtilityRef { Kind = "molotov", Landing = new UtilityLanding { Place = "Jungle" } };
        return document;
    }

    private static List<HistoryEntry> SampleLog()
    {
        string? repo = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found from the test output directory");
        string path = Path.Combine(repo, "tests", "fixtures", "strats", "schema-v1.sample.history.jsonl");
        return
        [
            .. File.ReadAllLines(path).Where(l => l.Length > 0)
                .Select(l => JsonSerializer.Deserialize(l, StratJsonContext.Default.HistoryEntry)!)
        ];
    }

    [Test]
    public async Task ATimeDrag_IsNamedByTheStepsUtility_OnTheRoundClock()
    {
        string summary = StratDiffPhrasing.Summary(Node(WithMolotov()),
            [PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76))]);

        await Assert.That(summary).IsEqualTo("molotov moved from 1:22 to 1:16");
    }

    [Test]
    public async Task TheSampleLog_IsPhrasedEntryByEntry_AgainstEachEntrysBeforeState()
    {
        IReadOnlyList<StratHistoryRow> rows = StratHistoryPane.Rows(SampleLog());
        Dictionary<int, string> changes = rows.ToDictionary(r => r.Revision, r => string.Join("; ", r.Changes));

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(r => r.Revision)).IsEquivalentTo([5, 4, 3, 2, 1]);
            await Assert.That(rows[^1].Revision).IsEqualTo(1).Because("newest first");
            await Assert.That(changes[1]).IsEqualTo("created");
            await Assert.That(changes[2]).IsEqualTo("molotov moved from 1:22 to 1:16");
            await Assert.That(changes[3]).IsEqualTo("step added: A peeks Connector at 1:05; renamed to A exec, con peek");
            await Assert.That(changes[4]).IsEqualTo("branch added; status Theory → Active");
            await Assert.That(changes[5]).IsEqualTo("B's throw: note removed; economy full → force; target site cleared");
            await Assert.That(rows.Single(r => r.Revision == 2).Summary).IsEqualTo("molotov moved from 1:22 to 1:16")
                .Because("a stored summary is shown as written");
            await Assert.That(rows.All(r => r.Before is null && r.After is null)).IsTrue().Because("no record was given");
        }
    }

    [Test]
    public async Task Places_PrintThroughTheOwnersPrimaryAlias_ElseSplitIntoWords()
    {
        CalloutTable table = new() { Map = "de_mirage", Aliases = [new CalloutAlias { Alias = "con", Place = "Connector", Primary = true }] };
        CalloutResolver callouts = new(CanonicalPlaces.Embedded("de_mirage"), table);
        StratStep peek = Step(3, 65, "A", "peek", "TRamp", "Connector");
        PatchOp add = PatchOp.AddOp("/steps/2", JsonSerializer.SerializeToNode(peek, StratJsonContext.Default.StratStep));
        PatchOp moveTo = PatchOp.ReplaceOp("/steps/0/to/place", JsonValue.Create("BombsiteA"), JsonValue.Create("Connector"));

        using (Assert.Multiple())
        {
            await Assert.That(StratDiffPhrasing.Summary(Node(Minimal()), [add], callouts)).IsEqualTo("step added: A peeks con at 1:05");
            await Assert.That(StratDiffPhrasing.Summary(Node(Minimal()), [moveTo], callouts)).IsEqualTo("B's throw: to Bombsite A → con");
            await Assert.That(StratDiffPhrasing.Summary(Node(Minimal()), [moveTo])).IsEqualTo("B's throw: to Bombsite A → Connector");
        }
    }

    [Test]
    public async Task StepsBranchesSlotsAndTags_EachHaveTheirOwnWords()
    {
        StratDocument document = WithMolotov();
        document.Tags = ["default-break"];
        JsonNode removed = JsonSerializer.SerializeToNode(document.Steps[1], StratJsonContext.Default.StratStep)!;
        IReadOnlyList<string> lines = StratDiffPhrasing.Describe(Node(document),
        [
            PatchOp.RemoveOp("/steps/1", removed),
            PatchOp.ReplaceOp("/slots/2/steamId", null, JsonValue.Create("76561198000000001")),
            PatchOp.ReplaceOp("/slots/2/role", null, JsonValue.Create("lurk")),
            PatchOp.AddOp("/tags/1", JsonValue.Create("vs-aggressive-ct")),
            PatchOp.RemoveOp("/tags/0", JsonValue.Create("default-break")),
            PatchOp.ReplaceOp("/notes", null, JsonValue.Create("stairs smoke first")),
            PatchOp.ReplaceOp("/steps/0/positions", new JsonArray(), new JsonArray(new JsonObject { ["slot"] = "A" })),
            PatchOp.ReplaceOp("/steps/0/positions", new JsonArray(), new JsonArray(new JsonObject { ["slot"] = "B" }))
        ]);

        await Assert.That(string.Join(" | ", lines)).IsEqualTo(string.Join(" | ",
            "step removed: C throws molotov to Jungle at 1:22",
            "slot C pinned",
            "slot C role set to lurk",
            "tag added: vs-aggressive-ct",
            "tag removed: default-break",
            "notes edited",
            "B's throw: positions changed"));
    }

    [Test]
    public async Task ARemovedStepIsNamedFromItsFromValue_WhenTheBeforeStateIsUnknown()
    {
        JsonNode removed = JsonSerializer.SerializeToNode(Step(4, 40, "all", "rotate", null, "BombsiteB"), StratJsonContext.Default.StratStep)!;
        string summary = StratDiffPhrasing.Summary(null, [PatchOp.RemoveOp("/steps/3", removed)]);
        await Assert.That(summary).IsEqualTo("step removed: all rotate to Bombsite B at 0:40");
    }

    [Test]
    public async Task SummaryAfter_RecoversTheBeforeState_ByInvertingTheOps()
    {
        StratDocument before = WithMolotov();
        PatchOp drag = PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76));
        PatchOp remove = PatchOp.RemoveOp("/steps/0", JsonSerializer.SerializeToNode(before.Steps[0], StratJsonContext.Default.StratStep));
        StratDocument after = StratHistory.Apply(before, [drag, remove]);

        await Assert.That(StratDiffPhrasing.SummaryAfter(after, [drag, remove]))
            .IsEqualTo("molotov moved from 1:22 to 1:16; step removed: B throws to Bombsite A at 1:30");
    }

    [Test]
    public async Task ACommitWithoutASummary_IsPhrasedByTheStore()
    {
        StratStore store = new(null);
        StratDocument document = WithMolotov();
        await Assert.That(store.Save(document, [], null).Saved).IsTrue();

        PatchOp drag = PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76));
        StratDocument edited = StratHistory.Apply(document, [drag]);
        await Assert.That(store.Save(edited, [drag], null).Saved).IsTrue();

        IReadOnlyList<HistoryEntry> log = store.History(document.Id);
        using (Assert.Multiple())
        {
            await Assert.That(log[0].Summary).IsEqualTo("created");
            await Assert.That(log[1].Summary).IsEqualTo("molotov moved from 1:22 to 1:16");
        }
    }

    [Test]
    public async Task ACommitWithoutASummary_ThroughTheSession_IsPhrased()
    {
        StratStore store = new(null);
        StratDocument document = WithMolotov();
        store.Save(document, [], "created");
        using StratSession session = new(store, null, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1),
            IdleCommitDelay = TimeSpan.FromHours(1)
        };
        session.Open(document.Id);
        session.Apply(PatchOp.ReplaceOp("/economy", null, JsonValue.Create("full")));
        session.Commit();

        await Assert.That(store.History(document.Id)[^1].Summary).IsEqualTo("economy set to full");
    }

    [Test]
    public async Task TheHistoryPane_SplitsTheRecordEitherSideOfEachEntry()
    {
        StratRun Tagged(int revision, RunOutcome outcome) =>
            new(new TagInstanceRef(new string('a', 64), Guid.NewGuid(), "A execute", 0, 1, 1),
                new string('a', 64), 1, revision, null, outcome, []);

        StratRecord record = StratEvidence.Record(HistoryId, 5,
            [Tagged(1, RunOutcome.Won), Tagged(1, RunOutcome.Lost), Tagged(2, RunOutcome.Won), Tagged(4, RunOutcome.Won), Tagged(0, RunOutcome.Lost)]);
        StratHistoryRow two = StratHistoryPane.Rows(SampleLog(), record: record).Single(r => r.Revision == 2);

        using (Assert.Multiple())
        {
            await Assert.That(two.Before).IsEqualTo(new RecordSplit(2, 1, 1, 0, 0));
            await Assert.That(two.After).IsEqualTo(new RecordSplit(2, 2, 0, 0, 0))
                .Because("the run tagged without a revision sits on neither side");
        }
    }
}

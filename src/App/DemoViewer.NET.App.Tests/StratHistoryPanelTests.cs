#region

using System.Globalization;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Strat Version History (plan.md §3, strat-model.md §3.8): the log as a pane, newest entry first, each
///     phrased against the document as it stood just before it, with the record split either side of the
///     entry's revision. The item's own done line: "every save is a diff and the record splits".
/// </summary>
public class StratHistoryPanelTests
{
    private static readonly string ShaA = new('a', 64);

    private static string S => HistoryId.ToString("D");

    private static TagInstance Run(int round, int revision, string winner) =>
        Extend(Instance("A execute", round * 1000, round * 1000 + 600, ("strat", S),
            ("strat.rev", revision.ToString(CultureInfo.InvariantCulture))), round, winner);

    private static TagInstance Extend(TagInstance instance, int round, string winner)
    {
        instance.Round = round;
        instance.Facts = [new TagLabel(StratEvidence.WinnerFact, winner)];
        return instance;
    }

    [Test]
    public async Task Configure_ListsEveryEntry_NewestFirst_EachAsADiff()
    {
        using Fixture fixture = new();
        StratDocument created = Minimal(HistoryId);
        fixture.Store.Save(created, [], null);
        StratDocument edited = StratHistory.Apply(created,
            [PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76))]);
        fixture.Store.Save(edited, [PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76))], null);

        fixture.Panel.Configure(edited);
        await fixture.Panel.Pending;

        using (Assert.Multiple())
        {
            await Assert.That(fixture.Panel.HasStrat).IsTrue();
            await Assert.That(fixture.Panel.Entries.Select(e => e.Revision)).IsEquivalentTo([2, 1]);
            await Assert.That(fixture.Panel.Entries[0].Revision).IsEqualTo(2).Because("newest first");
            await Assert.That(fixture.Panel.Entries[0].Summary).IsEqualTo("C's throw moved from 1:22 to 1:16");
            await Assert.That(fixture.Panel.Entries[0].ShowChangeList).IsFalse().Because("one op needs no itemized list beside its summary");
            await Assert.That(fixture.Panel.Entries[1].Summary).IsEqualTo("created");
        }
    }

    [Test]
    public async Task ACommitWithSeveralOps_ShowsTheItemizedChangeList()
    {
        using Fixture fixture = new();
        StratDocument created = Minimal(HistoryId);
        fixture.Store.Save(created, [], null);
        PatchOp renamed = PatchOp.ReplaceOp("/name", JsonValue.Create("A exec"), JsonValue.Create("A exec, con peek"));
        PatchOp tag = PatchOp.AddOp("/tags/0", JsonValue.Create("default-break"));
        StratDocument edited = StratHistory.Apply(created, [renamed, tag]);
        fixture.Store.Save(edited, [renamed, tag], null);

        fixture.Panel.Configure(edited);
        await fixture.Panel.Pending;

        StratHistoryEntryRow latest = fixture.Panel.Entries[0];
        using (Assert.Multiple())
        {
            await Assert.That(latest.ShowChangeList).IsTrue();
            await Assert.That(latest.Changes).IsEquivalentTo(["renamed to A exec, con peek", "tag added: default-break"]);
            await Assert.That(latest.Summary).IsEqualTo(string.Join("; ", latest.Changes)).Because("no explicit summary was given");
        }
    }

    [Test]
    public async Task TheRecordSplitsEitherSideOfEachEntrysRevision()
    {
        using Fixture fixture = new();
        StratDocument created = Minimal(HistoryId);
        fixture.Store.Save(created, [], null);
        StratDocument edited = StratHistory.Apply(created,
            [PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76))]);
        fixture.Store.Save(edited, [PatchOp.ReplaceOp("/steps/1/atSeconds", JsonValue.Create(82), JsonValue.Create(76))], null);
        fixture.Tags.Save(Document(ShaA, Run(1, 1, "T"), Run(2, 2, "CT")));

        fixture.Panel.Configure(edited);
        await fixture.Panel.Pending;

        StratHistoryEntryRow revision2 = fixture.Panel.Entries.Single(e => e.Revision == 2);
        using (Assert.Multiple())
        {
            await Assert.That(revision2.HasSplit).IsTrue();
            await Assert.That(revision2.BeforeText).IsEqualTo("1 run · 1 won · 100%");
            await Assert.That(revision2.AfterText).IsEqualTo("1 run · 1 lost · 0%");
        }
    }

    [Test]
    public async Task TaggingARound_UpdatesTheSplit_WithoutReconfiguring()
    {
        using Fixture fixture = new();
        StratDocument created = Minimal(HistoryId);
        fixture.Store.Save(created, [], null);
        fixture.Tags.Save(Document(ShaA, Run(1, 1, "T")));

        fixture.Panel.Configure(created);
        await fixture.Panel.Pending;
        await Assert.That(fixture.Panel.Entries.Single().BeforeText).IsEqualTo("no runs");
        await Assert.That(fixture.Panel.Entries.Single().AfterText).IsEqualTo("1 run · 1 won · 100%");

        TagDocument document = fixture.Tags.LoadDocuments(_ => true).Single();
        document.Instances.Add(Run(2, 1, "CT"));
        fixture.Tags.Save(document);

        await fixture.Panel.Pending;
        await Assert.That(fixture.Panel.Entries.Single().AfterText).IsEqualTo("2 runs · 1 won · 1 lost · 50%")
            .Because("a tag written elsewhere reaches the open pane");
    }

    [Test]
    public async Task ClosingTheStrat_ClearsThePane()
    {
        using Fixture fixture = new();
        StratDocument created = Minimal(HistoryId);
        fixture.Store.Save(created, [], null);
        fixture.Panel.Configure(created);
        await fixture.Panel.Pending;
        await Assert.That(fixture.Panel.HasEntries).IsTrue();

        fixture.Panel.Configure(null);

        using (Assert.Multiple())
        {
            await Assert.That(fixture.Panel.HasStrat).IsFalse();
            await Assert.That(fixture.Panel.HasEntries).IsFalse();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Store = new StratStore(null);
            Tags = new TagStore(null);
            CalloutResolverSource callouts = new(Store);
            Panel = new StratHistoryPanelViewModel(Store, new StratEvidenceService(Tags), Tags, callouts, debounce: TimeSpan.Zero);
        }

        public StratStore Store { get; }

        public TagStore Tags { get; }

        public StratHistoryPanelViewModel Panel { get; }

        public void Dispose() => Panel.Dispose();
    }
}

#region

using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Record Panel (plan.md §3, strat-model.md §3.6): run / won / lost / aborted, split by Demo
///     Provenance Labels, the small-sample caution, the failure breakdown, and a click on every number that
///     sends its runs to the Review Queue and shows the Review tab. The one behaviour the plan names as
///     "done" for this item: tagging a round updates the panel without a restart.
/// </summary>
public class StratRecordPanelTests
{
    private static readonly Guid StratId = Guid.Parse("0b7e4a21-9c3d-4f58-b6a2-e1d0c9b8a7f6");
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);

    private static string S => StratId.ToString("D");

    private static TagInstance Run(int round, (string Group, string Value)[] labels, string? winner = null) =>
        Extend(Instance("A execute", round * 1000, round * 1000 + 600, labels), round, winner);

    private static TagInstance Extend(TagInstance instance, int round, string? winner)
    {
        instance.Round = round;
        if (winner is not null)
        {
            instance.Facts = [new TagLabel(StratEvidence.WinnerFact, winner)];
        }

        return instance;
    }

    private static StratDocument Strat(Guid? id = null, string side = StratVocabulary.SideT, int revision = 1)
    {
        StratDocument document = StratTestData.Minimal(id ?? StratId);
        document.Side = side;
        document.Revision = revision;
        return document;
    }

    [Test]
    public async Task Configure_ComputesTheRecord_ByProvenanceAndTheFailureBreakdown()
    {
        using Fixture fixture = Fixture.Create(
            Document(ShaA,
                Run(1, [("strat", S), ("strat.rev", "1")], winner: "T"),
                Run(2, [("strat", S), ("strat.rev", "1"), ("strat.failure", "utility-late")], winner: "CT")),
            Document(ShaB,
                Run(1, [("strat", S), ("strat.rev", "1"), ("strat.result", "aborted")], winner: "T")));
        fixture.Provenance[ShaA] = DemoProvenanceLabel.Scrim;

        fixture.Panel.Configure(Strat());
        await fixture.Panel.Pending;

        using (Assert.Multiple())
        {
            await Assert.That(fixture.Panel.HasStrat).IsTrue();
            await Assert.That(fixture.Panel.Total.Run.Count).IsEqualTo(3);
            await Assert.That(fixture.Panel.Total.Won.Count).IsEqualTo(1);
            await Assert.That(fixture.Panel.Total.Lost.Count).IsEqualTo(1);
            await Assert.That(fixture.Panel.Total.Aborted.Count).IsEqualTo(1);
            await Assert.That(fixture.Panel.WinRateText).IsEqualTo("50%");
            await Assert.That(fixture.Panel.ByProvenance.Select(r => r.Display))
                .IsEquivalentTo([DemoProvenanceLabel.Scrim, StratEvidence.Unlabeled]);
            await Assert.That(fixture.Panel.Failures.Single().Failure).IsEqualTo("utility-late");
            await Assert.That(fixture.Panel.Failures.Single().Count).IsEqualTo(1);
            await Assert.That(fixture.Panel.Caution).IsEqualTo("3 runs: a small sample, read with caution");
        }
    }

    [Test]
    public async Task TaggingARound_UpdatesThePanel_WithoutReconfiguring()
    {
        using Fixture fixture = Fixture.Create(Document(ShaA, Run(1, [("strat", S), ("strat.rev", "1")], winner: "T")));

        fixture.Panel.Configure(Strat());
        await fixture.Panel.Pending;
        await Assert.That(fixture.Panel.Total.Run.Count).IsEqualTo(1);

        TagDocument document = fixture.Tags.LoadDocuments(_ => true).Single();
        document.Instances.Add(Run(2, [("strat", S), ("strat.rev", "1")], winner: "CT"));
        fixture.Tags.Save(document);

        await fixture.Panel.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(fixture.Panel.Total.Run.Count).IsEqualTo(2).Because("a tag written elsewhere reaches the open panel");
            await Assert.That(fixture.Panel.Total.Lost.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ChangingTheStratsSide_Rebuilds_AndFlipsWonAndLost()
    {
        using Fixture fixture = Fixture.Create(Document(ShaA, Run(1, [("strat", S), ("strat.rev", "1")], winner: "T")));

        fixture.Panel.Configure(Strat(side: StratVocabulary.SideT));
        await fixture.Panel.Pending;
        await Assert.That(fixture.Panel.Total.Won.Count).IsEqualTo(1);

        fixture.Panel.Configure(Strat(side: StratVocabulary.SideCt));
        await fixture.Panel.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(fixture.Panel.Total.Won.Count).IsEqualTo(0);
            await Assert.That(fixture.Panel.Total.Lost.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ClosingTheStrat_ClearsThePanel()
    {
        using Fixture fixture = Fixture.Create(Document(ShaA, Run(1, [("strat", S), ("strat.rev", "1")], winner: "T")));
        fixture.Panel.Configure(Strat());
        await fixture.Panel.Pending;
        await Assert.That(fixture.Panel.HasStrat).IsTrue();

        fixture.Panel.Configure(null);

        using (Assert.Multiple())
        {
            await Assert.That(fixture.Panel.HasStrat).IsFalse();
            await Assert.That(fixture.Panel.Total.Run.Count).IsEqualTo(0);
            await Assert.That(fixture.Panel.ByProvenance).IsEmpty();
            await Assert.That(fixture.Panel.Failures).IsEmpty();
        }
    }

    [Test]
    public async Task ANumber_SendsExactlyItsRuns_ToTheReviewQueue_UnderOneTitleCard_AndShowsReview()
    {
        using Fixture fixture = Fixture.Create(
            Document(ShaA,
                Run(1, [("strat", S), ("strat.rev", "1")], winner: "T"),
                Run(2, [("strat", S), ("strat.rev", "1")], winner: "T"),
                Run(3, [("strat", S), ("strat.rev", "1")], winner: "CT")));
        fixture.Cache.Upsert(RecordFor(ShaA, "/d/a.dem"));

        fixture.Panel.Configure(Strat());
        await fixture.Panel.Pending;

        StratRecordCountCell won = fixture.Panel.Total.Won;
        won.OpenCommand.Execute(null);

        IReadOnlyList<ReviewEntry> entries = fixture.Queue.Entries;
        using (Assert.Multiple())
        {
            await Assert.That(won.Count).IsEqualTo(2);
            await Assert.That(entries.Count).IsEqualTo(3).Because("one title card, then the two won clips");
            await Assert.That(entries[0].Kind).IsEqualTo(ReviewEntryKind.Section);
            await Assert.That(entries[0].Title).IsEqualTo("Strat record · total · All runs");
            await Assert.That(entries.Skip(1).All(e => e.DemoPath == "/d/a.dem" && e.Source == ReviewSources.Tag)).IsTrue();
            await Assert.That(fixture.SelectedTabs).IsEquivalentTo([ReviewQueueModule.TabId]);
            await Assert.That(fixture.Panel.StatusLine).IsEqualTo("2 clips sent to Review");
        }

        won.OpenCommand.Execute(null);
        await Assert.That(fixture.Queue.Entries.Count).IsEqualTo(3).Because("a second click adds nothing");
    }

    [Test]
    public async Task ARunWhoseDemoIsNotInTheLibrary_IsNotQueued_AndTheLineSaysSo()
    {
        using Fixture fixture = Fixture.Create(Document(ShaA, Run(1, [("strat", S), ("strat.rev", "1")], winner: "T")));
        fixture.Panel.Configure(Strat());
        await fixture.Panel.Pending;

        fixture.Panel.Total.Won.OpenCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(fixture.Queue.Entries).IsEmpty();
            await Assert.That(fixture.Panel.StatusLine).IsEqualTo("nothing queued: the demo is not in the library");
        }
    }

    [Test]
    public async Task WithNoReviewQueue_TheStatusLineSaysSo()
    {
        TagStore tags = new(null);
        tags.Save(Document(ShaA, Run(1, [("strat", S), ("strat.rev", "1")], winner: "T")));
        StratRecordPanelViewModel panel = new(new StratEvidenceService(tags), tags, debounce: TimeSpan.Zero);
        panel.Configure(Strat());
        await panel.Pending;

        panel.Total.Won.OpenCommand.Execute(null);
        await Assert.That(panel.StatusLine).IsEqualTo("no Review Queue on this host");
        panel.Dispose();
    }

    private static DemoCacheRecord RecordFor(string sha, string path) => new()
    {
        Path = path,
        Size = 1000,
        ModifiedTicks = 2000,
        Sha256 = sha,
        Map = "de_mirage",
        TickRate = 64
    };

    private sealed class Fixture : IDisposable
    {
        public readonly Dictionary<string, string?> Provenance = new(StringComparer.Ordinal);

        private Fixture()
        {
            Tags = new TagStore(null);
            Cache = new DemoCacheStore(null);
            Queue = new ReviewQueue(null);
            StratEvidenceService evidence = new(Tags, new FixedProvenance(Provenance));
            Panel = new StratRecordPanelViewModel(evidence, Tags, Queue, Cache.TryGetIndexBySha256,
                tab =>
                {
                    SelectedTabs.Add(tab);
                    return true;
                },
                debounce: TimeSpan.Zero);
        }

        public TagStore Tags { get; }

        public DemoCacheStore Cache { get; }

        public ReviewQueue Queue { get; }

        public StratRecordPanelViewModel Panel { get; }

        public List<string> SelectedTabs { get; } = [];

        public static Fixture Create(params TagDocument[] documents)
        {
            Fixture fixture = new();
            foreach (TagDocument document in documents)
            {
                fixture.Tags.Save(document);
            }

            return fixture;
        }

        public void Dispose() => Panel.Dispose();
    }

    private sealed class FixedProvenance(IReadOnlyDictionary<string, string?> labels) : IDemoProvenanceSource
    {
        public string? LabelFor(string sha256) => labels.GetValueOrDefault(sha256);

        public IReadOnlyDictionary<string, string?> LabelsFor(IEnumerable<string> sha256s) =>
            sha256s.Distinct(StringComparer.Ordinal).ToDictionary(s => s, LabelFor, StringComparer.Ordinal);

        public DemoProvenance? Resolve(string demoPath) => null;

        public IReadOnlyDictionary<string, DemoProvenance> ResolveAll(IEnumerable<string> demoPaths) =>
            new Dictionary<string, DemoProvenance>();

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }
}

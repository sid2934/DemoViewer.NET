#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The evidence rule and the record pane's data (strat-model.md §3.6, decisions 3, 4 and 7) over a fixture of
///     three tag documents: run, won, lost, aborted and unknown counts; the <c>winner</c> fact beating the human
///     <c>outcome</c> label; the provenance and revision splits; the failure breakdown counting a repeated value
///     once; and the caution flag at seven runs and not at eight. The <c>winner</c> fact comes from Round Facts
///     rows; the facts here are synthetic for speed and isolation, and the real-demo variant
///     is <see cref="StratEvidenceRealDemoTests" />.
/// </summary>
public class StratEvidenceTests
{
    private static readonly Guid StratId = Guid.Parse("0b7e4a21-9c3d-4f58-b6a2-e1d0c9b8a7f6");
    private static readonly Guid OtherId = Guid.Parse("e4f50000-0000-4000-8000-000000000009");

    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly string ShaC = new('c', 64);

    private static readonly string[] ProvenanceOrder = [DemoProvenanceLabel.Scrim, DemoProvenanceLabel.Matchmaking, StratEvidence.Unlabeled];
    private static readonly string[] RevisionOrder = ["1", "2", "3"];
    private static readonly string[] FailureOrder = ["utility-late", "entry-lost", "early-contact"];

    private static readonly Dictionary<string, string?> Labels = new(StringComparer.Ordinal)
    {
        [ShaA] = DemoProvenanceLabel.Scrim,
        [ShaB] = DemoProvenanceLabel.Matchmaking,
        [ShaC] = null
    };

    private static string S => StratId.ToString("D");

    private static TagInstance Run(int round, (string Group, string Value)[] labels, string? winner = null, string source = TagSources.Human)
    {
        TagInstance instance = Instance("A execute", round * 1000, round * 1000 + 600, labels);
        instance.Round = round;
        instance.Source = source;
        if (winner is not null)
        {
            instance.Facts = [new TagLabel("round", round.ToString(System.Globalization.CultureInfo.InvariantCulture)), new TagLabel(StratEvidence.WinnerFact, winner)];
        }

        return instance;
    }

    /// <summary>
    ///     A (scrim): won on the fact though a person said lost; lost with a repeated failure; aborted in a round
    ///     the strat's side won; a round of another strat. B (matchmaking): an upper-case id won on the label; no
    ///     result at all; an import; an accepted proposal whose fact is <c>none</c>, so the label decides. C
    ///     (unlabeled): a round that ran two strats, each with its own revision, won with a failure noted.
    /// </summary>
    private static List<TagDocument> Fixture() =>
    [
        Document(ShaA,
            Run(1, [("strat", S), ("strat.rev", "1"), ("outcome", "lost")], winner: "T"),
            Run(2, [("strat", S), ("strat.rev", "1"), ("strat.failure", "utility-late"), ("strat.failure", "utility-late"), ("strat.failure", "entry-lost")], winner: "CT"),
            Run(3, [("strat", S), ("strat.rev", "2"), ("strat.result", "aborted"), ("strat.failure", "early-contact")], winner: "T"),
            Run(4, [("strat", OtherId.ToString("D")), ("strat.rev", "7")], winner: "T")),
        Document(ShaB,
            Run(1, [("strat", S.ToUpperInvariant()), ("strat.rev", "2"), ("outcome", "won")]),
            Run(2, [("strat", S), ("strat.rev", "2"), ("strat.failure", "utility-late")]),
            Run(3, [("strat", S), ("strat.rev", "2"), ("outcome", "won")], winner: "T", source: TagSources.Import),
            Run(4, [("strat", S), ("strat.rev", "2"), ("outcome", "lost")], winner: "none", source: TagSources.Suggested)),
        Document(ShaC,
            Run(5, [("strat", OtherId.ToString("D")), ("strat.rev", "5"), ("strat", S), ("strat.rev", "3"), ("strat.failure", "other")], winner: "T"))
    ];

    private static StratRecord Build(List<TagDocument>? docs = null) =>
        StratEvidence.Build(StratId, 4, StratVocabulary.SideT, docs ?? Fixture(), hashes => hashes.ToDictionary(h => h, h => Labels.GetValueOrDefault(h)));

    [Test]
    public async Task Record_CountsRunsWonLostAbortedAndUnknown_AndSkipsImportsAndOtherStrats()
    {
        StratRecord record = Build();

        using (Assert.Multiple())
        {
            await Assert.That(record.Runs.Count).IsEqualTo(7).Because("the import and the other strat's round are not runs");
            await Assert.That(record.Total).IsEqualTo(new RecordSplit(7, 3, 2, 1, 1));
            await Assert.That(record.Revision).IsEqualTo(4);
            await Assert.That(record.Total.WinRate).IsEqualTo(0.6);
            await Assert.That(record.Runs.All(r => r.Ref.Sha256 == r.Sha256)).IsTrue();
        }
    }

    [Test]
    public async Task TheWinnerFact_BeatsTheOutcomeLabel_AndTheLabelDecides_WhenTheFactIsAbsentOrNone()
    {
        StratRecord record = Build();
        StratRun onFact = record.Runs.Single(r => r.Sha256 == ShaA && r.Round == 1);
        StratRun onLabel = record.Runs.Single(r => r.Sha256 == ShaB && r.Round == 1);
        StratRun onNone = record.Runs.Single(r => r.Sha256 == ShaB && r.Round == 4);
        StratRun neither = record.Runs.Single(r => r.Sha256 == ShaB && r.Round == 2);

        using (Assert.Multiple())
        {
            await Assert.That(onFact.Outcome).IsEqualTo(RunOutcome.Won).Because("winner T on a T strat, whatever the label says");
            await Assert.That(onLabel.Outcome).IsEqualTo(RunOutcome.Won);
            await Assert.That(onNone.Outcome).IsEqualTo(RunOutcome.Lost).Because("a winner of none is no winner");
            await Assert.That(neither.Outcome).IsEqualTo(RunOutcome.Unknown);
        }
    }

    [Test]
    public async Task TheWinnerFact_IsReadAgainstTheStratsOwnSide()
    {
        TagInstance round = Run(1, [("strat", S)], winner: "T");
        using (Assert.Multiple())
        {
            await Assert.That(StratEvidence.OutcomeOf(round, StratVocabulary.SideT)).IsEqualTo(RunOutcome.Won);
            await Assert.That(StratEvidence.OutcomeOf(round, StratVocabulary.SideCt)).IsEqualTo(RunOutcome.Lost);
            await Assert.That(StratEvidence.OutcomeOf(round, "")).IsEqualTo(RunOutcome.Unknown)
                .Because("with no side the fact cannot decide, and there is no label to fall back to");
        }
    }

    [Test]
    public async Task Aborted_BeatsARoundTheSideWon()
    {
        StratRun aborted = Build().Runs.Single(r => r.Sha256 == ShaA && r.Round == 3);
        await Assert.That(aborted.Outcome).IsEqualTo(RunOutcome.Aborted);
    }

    [Test]
    public async Task ByProvenance_KeysOnTheDemosLabel_WithUnlabeledForNone()
    {
        StratRecord record = Build();

        using (Assert.Multiple())
        {
            await Assert.That(record.ByProvenance.Count).IsEqualTo(3);
            await Assert.That(record.ByProvenance[DemoProvenanceLabel.Scrim]).IsEqualTo(new RecordSplit(3, 1, 1, 1, 0));
            await Assert.That(record.ByProvenance[DemoProvenanceLabel.Matchmaking]).IsEqualTo(new RecordSplit(3, 1, 1, 0, 1));
            await Assert.That(record.ByProvenance[StratEvidence.Unlabeled]).IsEqualTo(new RecordSplit(1, 1, 0, 0, 0));
        }
    }

    [Test]
    public async Task ByRevision_KeysOnStratRev_PairedWithItsOwnStratLabel()
    {
        StratRecord record = Build();

        using (Assert.Multiple())
        {
            await Assert.That(record.ByRevision.Keys.Order()).IsEquivalentTo([1, 2, 3]);
            await Assert.That(record.ByRevision[1]).IsEqualTo(new RecordSplit(2, 1, 1, 0, 0));
            await Assert.That(record.ByRevision[2]).IsEqualTo(new RecordSplit(4, 1, 1, 1, 1));
            await Assert.That(record.ByRevision[3]).IsEqualTo(new RecordSplit(1, 1, 0, 0, 0))
                .Because("the round that ran two strats carries 5 for the other one and 3 for this one");
            await Assert.That(record.SplitAround(2)).IsEqualTo((new RecordSplit(2, 1, 1, 0, 0), new RecordSplit(5, 2, 1, 1, 1)));
        }
    }

    [Test]
    public async Task ARunWithoutStratRev_IsRevisionZero_AndSitsOnNeitherSideOfAnEntry()
    {
        StratRecord record = Build([Document(ShaA, Run(1, [("strat", S), ("outcome", "won")]))]);

        using (Assert.Multiple())
        {
            await Assert.That(record.Runs.Single().Revision).IsEqualTo(0);
            await Assert.That(record.ByRevision[0].Run).IsEqualTo(1);
            await Assert.That(record.SplitAround(1)).IsEqualTo((RecordSplit.Empty, RecordSplit.Empty));
        }
    }

    [Test]
    public async Task FailureBreakdown_CountsARepeatedValueOnce_AndReadsOnlyRunsThatDidNotWin()
    {
        StratRecord record = Build();
        StratRun lost = record.Runs.Single(r => r.Sha256 == ShaA && r.Round == 2);

        using (Assert.Multiple())
        {
            await Assert.That(lost.Failures).IsEquivalentTo(["utility-late", "entry-lost"]);
            await Assert.That(record.FailureBreakdown.Count).IsEqualTo(3);
            await Assert.That(record.FailureBreakdown["utility-late"]).IsEqualTo(2).Because("once for the lost run, once for the unknown one");
            await Assert.That(record.FailureBreakdown["entry-lost"]).IsEqualTo(1);
            await Assert.That(record.FailureBreakdown["early-contact"]).IsEqualTo(1);
            await Assert.That(record.FailureBreakdown.ContainsKey("other")).IsFalse().Because("that run was won");
        }
    }

    [Test]
    public async Task SmallSample_AtSeven_AndNotAtEight()
    {
        List<TagDocument> docs = Fixture();
        await Assert.That(Build(docs).SmallSample).IsTrue();

        docs[2].Instances.Add(Run(6, [("strat", S), ("strat.rev", "4")], winner: "CT"));
        StratRecord eight = Build(docs);
        using (Assert.Multiple())
        {
            await Assert.That(eight.Runs.Count).IsEqualTo(StratEvidence.SmallSampleBelow);
            await Assert.That(eight.SmallSample).IsFalse();
        }
    }

    [Test]
    public async Task TheFailureVocabulary_IsTheSevenShippedValues()
    {
        await Assert.That(StratEvidence.Failures)
            .IsEquivalentTo(["utility-late", "entry-lost", "early-contact", "rotation-early", "info-lost", "economy", "other"]);
    }

    [Test]
    public async Task Pane_OrdersRows_AndBacksEveryNumberWithItsRuns()
    {
        StratRecordPane pane = StratRecordPane.From(Build());

        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", pane.ByProvenance.Select(r => r.Key))).IsEqualTo(string.Join(",", ProvenanceOrder));
            await Assert.That(string.Join(",", pane.ByRevision.Select(r => r.Key))).IsEqualTo(string.Join(",", RevisionOrder));
            await Assert.That(pane.ByRevision[0].Display).IsEqualTo("revision 1");
            await Assert.That(string.Join(",", pane.Failures.Select(f => f.Failure))).IsEqualTo(string.Join(",", FailureOrder));
            await Assert.That(pane.Failures[0].Runs.Count).IsEqualTo(2);
            await Assert.That(pane.Total.RunsWith(RunOutcome.Won).Count).IsEqualTo(3);
            await Assert.That(pane.ByProvenance[0].RunsWith(RunOutcome.Aborted).Single().Ref.Round).IsEqualTo(3);
            await Assert.That(pane.Caution).IsEqualTo("7 runs: a small sample, read with caution");
            await Assert.That(pane.UnknownNote).IsEqualTo("1 run has no result yet");
            await Assert.That(StratRecordPane.WinRateText(pane.Total.Split)).IsEqualTo("60%");
            await Assert.That(StratRecordPane.WinRateText(RecordSplit.Empty)).IsEqualTo("no result");
        }
    }

    [Test]
    public async Task Service_LoadsOnlyDocumentsListingTheStrat_AndLabelsThemThroughProvenance()
    {
        TagStore tags = new(null);
        foreach (TagDocument document in Fixture())
        {
            tags.Save(document);
        }

        tags.Save(Document(new string('d', 64), Run(1, [("outcome", "won")], winner: "T")));

        StratDocument strat = StratTestData.Minimal(StratId);
        strat.Revision = 4;
        StratEvidenceService service = new(tags, new FixedProvenance(Labels));
        StratRecord record = await service.ComputeAsync(strat);

        using (Assert.Multiple())
        {
            await Assert.That(record.Total).IsEqualTo(Build().Total);
            await Assert.That(record.ByProvenance.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(Build().ByProvenance.Keys.Order(StringComparer.Ordinal));
            await Assert.That(new StratEvidenceService(tags).Compute(strat).ByProvenance.Keys).IsEquivalentTo([StratEvidence.Unlabeled])
                .Because("without Demo Provenance Labels every run is unlabeled");
        }
    }

    [Test]
    public async Task WonAndLost_FromRefreshedRoundFactsRows_BeforeTheHumanLabel()
    {
        // Synthetic rows through the production refresher: round 1 CT wins, round 2 T wins.
        RoundFacts one = RoundIndexTestData.Round(1, 1_000, 8_000);
        one.WinnerSide = 3;
        RoundFacts two = RoundIndexTestData.Round(2, 10_000, 18_000);
        two.WinnerSide = 2;
        two.EndReason = RoundEndReason.TargetBombed;

        TagInstance first = Instance("A execute", 1_500, 3_000, ("strat", S), ("strat.rev", "1"), ("outcome", "won"));
        TagInstance second = Instance("A execute", 11_000, 13_000, ("strat", S), ("strat.rev", "1"));
        TagDocument document = Document(ShaA, first, second);
        TagFactsRefresher.Refresh(document, RoundIndexTestData.Facts(one, two), DemoCacheRecord.RoundFactsSchema, Created);

        StratRecord record = StratEvidence.Build(StratId, 1, StratVocabulary.SideT, [document]);
        using (Assert.Multiple())
        {
            await Assert.That(record.Runs[0].Round).IsEqualTo(1);
            await Assert.That(record.Runs[0].Outcome).IsEqualTo(RunOutcome.Lost).Because("CT won round 1; the human said won");
            await Assert.That(record.Runs[1].Outcome).IsEqualTo(RunOutcome.Won);
            await Assert.That(record.Total).IsEqualTo(new RecordSplit(2, 1, 1, 0, 0));
        }
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

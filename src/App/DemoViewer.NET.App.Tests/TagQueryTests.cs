#region

using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tag query layer (tag-store.md §3.7) over a fixture of three documents: pivot cell counts, a
///     multi-valued group counted once per value, the <c>Any</c> namespace merging human and fact labels,
///     the default source rule, the <c>CreatedAfterUtc</c> cursor and <c>At</c>'s inclusive bounds.
/// </summary>
public class TagQueryTests
{
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly string ShaC = new('c', 64);

    private static readonly string[] ThreeCodes = ["A execute", "B execute", "Default"];
    private static readonly string[] Outcomes = ["lost", "won"];
    private static readonly string[] RoundKeys = ["1", "2", "3", "5", "10"];
    private static readonly int[] WonOnASite = [1000, 7000];
    private static readonly int[] CreatedLater = [7000, 3000];
    private static readonly string[] AtTwoHundred = ["A", "B"];

    /// <summary>
    ///     Three demos. A: two executes on A and a Default, one of them labelled with both sites. B: a B
    ///     execute lost, an accepted proposal, and an imported instance the default slice must not see.
    ///     C: an A execute whose site is only a fact, and an instance with no round.
    /// </summary>
    private static List<TagDocument> Fixture()
    {
        TagDocument a = Document(ShaA,
            Tagged(Instance("A execute", 1000, 1600, ("outcome", "won"), ("site", "A")), round: 1),
            Tagged(Instance("A execute", 5000, 5600, ("outcome", "lost"), ("site", "A"), ("site", "B"), ("site", "A")), round: 3),
            Tagged(Instance("Default", 9000, 9900, ("outcome", "won")), round: 5));

        TagDocument b = Document(ShaB,
            Tagged(Instance("B execute", 2000, 2600, ("outcome", "lost"), ("site", "B")), round: 2),
            Tagged(Instance("B execute", 7000, 7600, ("outcome", "won"), ("site", "B")), round: 10, source: TagSources.Suggested,
                created: Created.AddDays(2)),
            Tagged(Instance("B execute", 8000, 8600, ("outcome", "won"), ("site", "B")), round: 11, source: TagSources.Import));

        TagInstance factSite = Tagged(Instance("A execute", 3000, 3600, ("outcome", "won")), round: 2, created: Created.AddDays(1));
        factSite.Facts = [new TagLabel("plantSite", "A"), new TagLabel("winner", "T")];
        TagDocument c = Document(ShaC,
            factSite,
            Tagged(Instance("Default", 100, 200, ("outcome", "won")), round: null));

        return [a, b, c];
    }

    private static TagInstance Tagged(TagInstance instance, int? round, string source = TagSources.Human, DateTime? created = null)
    {
        instance.Round = round;
        instance.Source = source;
        instance.CreatedUtc = created ?? Created;
        return instance;
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    [Test]
    public async Task Pivot_CodeByOutcome_CountsEveryAdmittedInstance_InOrderedKeys()
    {
        TagPivot pivot = TagQuery.Pivot(Fixture(), TagSlice.Everything, new PivotAxis.Code(),
            new PivotAxis.Label(LabelNamespace.Human, "outcome"));

        using (Assert.Multiple())
        {
            await Assert.That(pivot.RowKeys).IsEquivalentTo(ThreeCodes);
            await Assert.That(pivot.ColumnKeys).IsEquivalentTo(Outcomes);
            await Assert.That(pivot.Cells[("A execute", "won")].Count).IsEqualTo(2);
            await Assert.That(pivot.Cells[("A execute", "lost")].Count).IsEqualTo(1);
            await Assert.That(pivot.Cells[("B execute", "won")].Count).IsEqualTo(1)
                .Because("the accepted proposal counts and the imported instance does not, by default");
            await Assert.That(pivot.Cells[("B execute", "lost")].Count).IsEqualTo(1);
            await Assert.That(pivot.Cells[("Default", "won")].Count).IsEqualTo(2);
            await Assert.That(pivot.Cells.ContainsKey(("Default", "lost"))).IsFalse()
                .Because("the pivot is sparse: an empty cell is absent, not an empty list");
        }
    }

    [Test]
    public async Task Pivot_SwappingAxes_TransposesTheCells()
    {
        List<TagDocument> docs = Fixture();
        PivotAxis code = new PivotAxis.Code();
        PivotAxis outcome = new PivotAxis.Label(LabelNamespace.Human, "outcome");

        TagPivot forward = TagQuery.Pivot(docs, TagSlice.Everything, code, outcome);
        TagPivot swapped = TagQuery.Pivot(docs, TagSlice.Everything, outcome, code);

        await Assert.That(swapped.RowKeys).IsEquivalentTo(forward.ColumnKeys);
        await Assert.That(swapped.ColumnKeys).IsEquivalentTo(forward.RowKeys);
        foreach (((string row, string column), IReadOnlyList<TagInstanceRef> refs) in forward.Cells)
        {
            await Assert.That(swapped.Cells[(column, row)]).IsEquivalentTo(refs);
        }
    }

    [Test]
    public async Task Pivot_MultiValuedGroup_CountsOncePerDistinctValue()
    {
        TagPivot pivot = TagQuery.Pivot(Fixture(), TagSlice.Everything, new PivotAxis.Demo(),
            new PivotAxis.Label(LabelNamespace.Human, "site"));

        using (Assert.Multiple())
        {
            // Demo A: the round-1 execute is site A; the round-3 one says A, B, A and is one A and one B.
            await Assert.That(pivot.Cells[(ShaA, "A")].Count).IsEqualTo(2);
            await Assert.That(pivot.Cells[(ShaA, "B")].Count).IsEqualTo(1);
            await Assert.That(pivot.Cells[(ShaA, "B")].Single().Round).IsEqualTo(3);
            await Assert.That(pivot.RowKeys).IsEquivalentTo(new[] { ShaA, ShaB })
                .Because("demo C has no human site label, so it has no key on the column axis and no row");
        }
    }

    [Test]
    public async Task Pivot_AnyNamespace_MergesHumanLabelsAndFacts_AndCountsASharedValueOnce()
    {
        List<TagDocument> docs = Fixture();
        docs[2].Instances[0].Labels.Add(new TagLabel("winner", "T"));

        TagPivot human = TagQuery.Pivot(docs, TagSlice.Everything, new PivotAxis.Code(),
            new PivotAxis.Label(LabelNamespace.Human, "plantSite"));
        TagPivot fact = TagQuery.Pivot(docs, TagSlice.Everything, new PivotAxis.Code(),
            new PivotAxis.Label(LabelNamespace.Fact, "plantSite"));
        TagPivot any = TagQuery.Pivot(docs, TagSlice.Everything, new PivotAxis.Code(),
            new PivotAxis.Label(LabelNamespace.Any, "winner"));

        using (Assert.Multiple())
        {
            await Assert.That(human.Cells.Count).IsEqualTo(0);
            await Assert.That(fact.Cells[("A execute", "A")].Single().Sha256).IsEqualTo(ShaC);
            await Assert.That(any.Cells[("A execute", "T")].Count).IsEqualTo(1)
                .Because("the same value in both namespaces is one value under Any");
        }
    }

    [Test]
    public async Task Pivot_RoundAxis_OrdersNumerically_AndLeavesOutAnInstanceWithNoRound()
    {
        TagPivot pivot = TagQuery.Pivot(Fixture(), TagSlice.Everything, new PivotAxis.Round(), new PivotAxis.Code());

        using (Assert.Multiple())
        {
            await Assert.That(pivot.RowKeys).IsEquivalentTo(RoundKeys);
            await Assert.That(pivot.RowKeys[^1]).IsEqualTo("10").Because("round keys sort as numbers, not text");
            await Assert.That(pivot.Cells.Values.Sum(c => c.Count)).IsEqualTo(6)
                .Because("seven admitted instances, one with no round, which is in no cell");
            await Assert.That(TagQuery.Find(Fixture(), TagSlice.Everything).Count).IsEqualTo(7);
        }
    }

    [Test]
    public async Task Find_WherePredicates_AreAnAnd_OfOneOfValues()
    {
        TagSlice slice = TagSlice.Everything with
        {
            Where =
            [
                new LabelPredicate(LabelNamespace.Human, "outcome", Set("won")),
                new LabelPredicate(LabelNamespace.Human, "site", Set("A", "B"))
            ]
        };

        IReadOnlyList<TagInstanceRef> found = TagQuery.Find(Fixture(), slice);

        using (Assert.Multiple())
        {
            await Assert.That(found.Select(r => r.FromTick)).IsEquivalentTo(WonOnASite);
            await Assert.That(TagQuery.Find(Fixture(), TagSlice.Everything with
            {
                Where = [new LabelPredicate(LabelNamespace.Human, "outcome", Set())]
            }).Count).IsEqualTo(0).Because("an empty one-of matches nothing");
        }
    }

    [Test]
    public async Task Find_FactPredicate_ReadsOnlyTheParserNamespace()
    {
        LabelPredicate plantedA = new(LabelNamespace.Fact, "plantSite", Set("A"));
        IReadOnlyList<TagInstanceRef> found = TagQuery.Find(Fixture(), TagSlice.Everything with { Where = [plantedA] });

        await Assert.That(found.Single().Sha256).IsEqualTo(ShaC);
        await Assert.That(TagQuery.Find(Fixture(), TagSlice.Everything with
        {
            Where = [new LabelPredicate(LabelNamespace.Fact, "site", Set("A"))]
        }).Count).IsEqualTo(0).Because("site is a human label, and a Fact predicate does not read labels");
    }

    [Test]
    public async Task Find_DemosCodesAndRounds_Narrow()
    {
        List<TagDocument> docs = Fixture();

        using (Assert.Multiple())
        {
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything with { Demos = Set(ShaB, ShaC) }).Count).IsEqualTo(4);
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything with { Codes = Set("Default") }).Count).IsEqualTo(2);
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything with { Rounds = new HashSet<int> { 2 } })
                .Select(r => r.Sha256)).IsEquivalentTo(new[] { ShaB, ShaC });
        }
    }

    [Test]
    public async Task Find_Source_DefaultExcludesImport_AndANamedSourceIsExact()
    {
        List<TagDocument> docs = Fixture();

        using (Assert.Multiple())
        {
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything).Any(r => r.Round == 11)).IsFalse();
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything with { Source = TagSource.Import }).Single().Round)
                .IsEqualTo(11);
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything with { Source = TagSource.Suggested }).Single().Round)
                .IsEqualTo(10);
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything with { Source = TagSource.Human }).Count).IsEqualTo(6);
        }
    }

    [Test]
    public async Task Find_CreatedAfterUtc_IsAStrictCursor()
    {
        List<TagDocument> docs = Fixture();

        IReadOnlyList<TagInstanceRef> sinceStart = TagQuery.Find(docs, TagSlice.Everything with { CreatedAfterUtc = Created });
        IReadOnlyList<TagInstanceRef> sinceDayOne =
            TagQuery.Find(docs, TagSlice.Everything with { CreatedAfterUtc = Created.AddDays(1) });

        using (Assert.Multiple())
        {
            await Assert.That(sinceStart.Select(r => r.FromTick)).IsEquivalentTo(CreatedLater);
            await Assert.That(sinceDayOne.Single().FromTick).IsEqualTo(7000)
                .Because("an instance created at the cursor is already seen");
        }
    }

    [Test]
    public async Task Find_Refs_CarryTheInstanceIdentity_InDocumentThenInstanceOrder()
    {
        List<TagDocument> docs = Fixture();
        IReadOnlyList<TagInstanceRef> found = TagQuery.Find(docs, TagSlice.Everything);

        TagInstance first = docs[0].Instances[0];
        using (Assert.Multiple())
        {
            await Assert.That(found[0]).IsEqualTo(new TagInstanceRef(ShaA, first.Id, "A execute", 1000, 1600, 1));
            await Assert.That(found.Select(r => r.Sha256).ToArray())
                .IsEquivalentTo(new[] { ShaA, ShaA, ShaA, ShaB, ShaB, ShaC, ShaC });
            await Assert.That(found[^1].Round).IsNull();
        }
    }

    [Test]
    public async Task At_ReturnsInstancesContainingTheTick_BoundsInclusive_WhateverTheSource()
    {
        TagDocument doc = Document(ShaB,
            Instance("A", 100, 200),
            Tagged(Instance("B", 200, 300), round: 1, source: TagSources.Import),
            Instance("C", 301, 400));

        using (Assert.Multiple())
        {
            await Assert.That(TagQuery.At(doc, 99).Count).IsEqualTo(0);
            await Assert.That(TagQuery.At(doc, 100).Single().Code).IsEqualTo("A");
            await Assert.That(TagQuery.At(doc, 200).Select(r => r.Code)).IsEquivalentTo(AtTwoHundred);
            await Assert.That(TagQuery.At(doc, 300).Single().Code).IsEqualTo("B");
            await Assert.That(TagQuery.At(doc, 400).Single().Code).IsEqualTo("C");
            await Assert.That(TagQuery.At(doc, 401).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Pivot_OverTheStoresCrossDemoReader_IsTheMatrixCall()
    {
        // The Matrix's call shape, in memory: the index row filter skips a document before it is opened.
        TagStore store = new(null);
        foreach (TagDocument document in Fixture())
        {
            store.Save(document);
        }

        TagSlice slice = TagSlice.Everything with { Demos = Set(ShaA, ShaC) };
        TagPivot pivot = TagQuery.Pivot(store.LoadDocuments(e => slice.Demos!.Contains(e.Sha256)), slice,
            new PivotAxis.Code(), new PivotAxis.Demo());

        using (Assert.Multiple())
        {
            await Assert.That(pivot.ColumnKeys).IsEquivalentTo(new[] { ShaA, ShaC });
            await Assert.That(pivot.Cells[("A execute", ShaA)].Count).IsEqualTo(2);
            await Assert.That(pivot.Cells[("A execute", ShaC)].Count).IsEqualTo(1);
        }
    }
}

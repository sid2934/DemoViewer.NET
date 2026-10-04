#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round index stamp on the record: <c>NeedsRoundIndex</c> on missing, stale-fingerprint and
///     Failed stamps, the index mirror agreeing with the record, stamping leaving the other tiers and the
///     other stamps alone, and a sidecar written before the stamps existed reading as "never written".
/// </summary>
public class DemoCacheRoundIndexStampTests
{
    private const string Current = "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1";

    [Test]
    public async Task NeedsRoundIndex_OnMissing_Stale_Current_AndFailed()
    {
        DemoCacheRecord record = ParsedRecord("/d/a.dem");
        await Assert.That(record.NeedsRoundIndex(Current)).IsTrue().Because("never written");

        record.StampRoundIndex("ri1;cadence=2;token=1;rf=1;src=pawn;pos=1");
        await Assert.That(record.NeedsRoundIndex(Current)).IsTrue().Because("another cadence means another row");

        record.StampRoundIndex(Current);
        await Assert.That(record.NeedsRoundIndex(Current)).IsFalse();
        await Assert.That(record.IsRoundIndexCurrent(Current)).IsTrue();

        record.MarkFailed(RoundIndexEvaluator.EvaluatorId);
        await Assert.That(record.NeedsRoundIndex(Current)).IsFalse().Because("Failed is excluded: retry is an explicit user action");
        await Assert.That(record.IsRoundIndexCurrent(Current)).IsFalse();
        await Assert.That(record.RoundIndexFingerprint()).IsEqualTo(Current).Because("a failure keeps what the stamp carried");

        record.ClearFailed(RoundIndexEvaluator.EvaluatorId);
        await Assert.That(record.RoundIndexState()).IsEqualTo(DemoAnalysisState.Pending);
        await Assert.That(record.NeedsRoundIndex(Current)).IsTrue();
    }

    [Test]
    public async Task TheIndexMirror_AgreesWithTheRecord()
    {
        DemoCacheRecord record = ParsedRecord("/d/a.dem");
        record.StampRoundIndex(Current, rowCount: 1570);

        DemoCacheIndexEntry entry = record.ToIndexEntry();

        using (Assert.Multiple())
        {
            await Assert.That(entry.RoundIndexSchema()).IsEqualTo(StratBookCache.RoundIndexSchema);
            await Assert.That(entry.RoundIndexComputedAtTicks()).IsEqualTo(record.RoundIndexComputedAtTicks());
            await Assert.That(entry.RoundIndexState()).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(entry.RoundIndexFingerprint()).IsEqualTo(Current);
            await Assert.That(entry.RoundIndexRowCount()).IsEqualTo(1570);
            await Assert.That(entry.NeedsRoundIndex(Current)).IsEqualTo(record.NeedsRoundIndex(Current));
            await Assert.That(entry.NeedsRoundIndex("other")).IsEqualTo(record.NeedsRoundIndex("other"));
            await Assert.That(entry.NeedsRoundIndex("other")).IsTrue();
        }

        record.MarkFailed(RoundIndexEvaluator.EvaluatorId);
        entry = record.ToIndexEntry();
        await Assert.That(entry.NeedsRoundIndex(Current)).IsFalse();
    }

    [Test]
    public async Task Stamping_LeavesTheOtherTiersAndStampsAlone()
    {
        DemoCacheStore store = new(null);
        store.Upsert(ParsedRecord("/d/a.dem", facts: Facts(Round(1, 1000, 2000))));

        store.UpdateExisting("/d/a.dem", r => r.StampRoundIndex(Current));

        DemoCacheRecord record = store.TryLoadRecord("/d/a.dem")!;
        using (Assert.Multiple())
        {
            await Assert.That(record.RoundIndexSchema()).IsEqualTo(StratBookCache.RoundIndexSchema);
            await Assert.That(record.Parse.IsPresent).IsTrue().Because("the Library's tier is untouched");
            await Assert.That(record.Analysis.IsPresent).IsFalse().Because("the index never claims the highlight scan's stamp");
            await Assert.That(record.RoundFacts()).IsNotNull().Because("the rows the index read stay where they were");
            await Assert.That(record.RoundFactsFingerprint()).IsEqualTo("rf-A");
            await Assert.That(record.PackStamps.Count).IsEqualTo(2);
            await Assert.That(store.TryGetIndex("/d/a.dem")!.RoundIndexState()).IsEqualTo(DemoAnalysisState.Indexed);
        }
    }

    [Test]
    public async Task ASidecarWrittenBeforeTheStampsExisted_ReadsAsNeverWritten()
    {
        const string legacy = """
                              {
                                "Path": "/d/old.dem",
                                "Size": 10,
                                "ModifiedTicks": 20,
                                "Parse": { "Schema": 1, "ComputedAtTicks": 5 },
                                "RoundFactsFingerprint": "rf-A"
                              }
                              """;

        DemoCacheRecord record = JsonSerializer.Deserialize<DemoCacheRecord>(legacy)!;
        DemoCacheIndexEntry entry = JsonSerializer.Deserialize<DemoCacheIndexEntry>("""{ "Path": "/d/old.dem" }""")!;

        using (Assert.Multiple())
        {
            await Assert.That(record.RoundIndexStamp()).IsNull();
            await Assert.That(record.RoundIndexState()).IsEqualTo(DemoAnalysisState.Pending);
            await Assert.That(record.RoundIndexFingerprint()).IsNull();
            await Assert.That(record.RoundIndexRowCount()).IsEqualTo(0);
            await Assert.That(record.NeedsRoundIndex(Current)).IsTrue();
            await Assert.That(record.RoundFactsFingerprint()).IsEqualTo("rf-A").Because("the flat field folds into a stamp");
            await Assert.That(record.NeedsRoundFacts("rf-A")).IsTrue().Because("a fingerprint with no rows behind it is not current");
            await Assert.That(entry.RoundIndexSchema()).IsEqualTo(0);
            await Assert.That(entry.NeedsRoundIndex(Current)).IsTrue();
        }
    }
}

#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round index stamp on the record: <c>NeedsRoundIndex</c> on missing, stale-fingerprint and
///     Failed rows, the index mirror agreeing with the record, stamping leaving the other tiers alone,
///     and a sidecar written before the fields existed deserializing to "never written".
/// </summary>
public class DemoCacheRoundIndexStampTests
{
    private const string Current = "ri1;cadence=1;token=1;rf=1;src=pawn";

    [Test]
    public async Task NeedsRoundIndex_OnMissing_Stale_Current_AndFailed()
    {
        DemoCacheRecord record = ParsedRecord("/d/a.dem");
        await Assert.That(record.NeedsRoundIndex(Current)).IsTrue().Because("never written");

        DemoCacheStore.StampRoundIndex(record);
        record.RoundIndexState = RoundIndexState.Indexed;
        record.RoundIndexFingerprint = "ri1;cadence=2;token=1;rf=1;src=pawn";
        await Assert.That(record.NeedsRoundIndex(Current)).IsTrue().Because("another cadence means another row");

        record.RoundIndexFingerprint = Current;
        await Assert.That(record.NeedsRoundIndex(Current)).IsFalse();
        await Assert.That(record.IsRoundIndexCurrent(Current)).IsTrue();

        record.RoundIndexState = RoundIndexState.Failed;
        await Assert.That(record.NeedsRoundIndex(Current)).IsFalse().Because("Failed is excluded: retry is an explicit user action");
        await Assert.That(record.IsRoundIndexCurrent(Current)).IsFalse();
    }

    [Test]
    public async Task TheIndexMirror_AgreesWithTheRecord()
    {
        DemoCacheRecord record = ParsedRecord("/d/a.dem");
        DemoCacheStore.StampRoundIndex(record);
        record.RoundIndexState = RoundIndexState.Indexed;
        record.RoundIndexFingerprint = Current;
        record.RoundIndexRowCount = 1570;

        DemoCacheIndexEntry entry = record.ToIndexEntry();

        using (Assert.Multiple())
        {
            await Assert.That(entry.RoundIndexSchema).IsEqualTo(DemoCacheRecord.RoundIndexSchema);
            await Assert.That(entry.RoundIndexComputedAtTicks).IsEqualTo(record.RoundIndex.ComputedAtTicks);
            await Assert.That(entry.RoundIndexState).IsEqualTo(RoundIndexState.Indexed);
            await Assert.That(entry.RoundIndexFingerprint).IsEqualTo(Current);
            await Assert.That(entry.RoundIndexRowCount).IsEqualTo(1570);
            await Assert.That(entry.NeedsRoundIndex(Current)).IsEqualTo(record.NeedsRoundIndex(Current));
            await Assert.That(entry.NeedsRoundIndex("other")).IsEqualTo(record.NeedsRoundIndex("other"));
            await Assert.That(entry.NeedsRoundIndex("other")).IsTrue();
        }

        record.RoundIndexState = RoundIndexState.Failed;
        entry = record.ToIndexEntry();
        await Assert.That(entry.NeedsRoundIndex(Current)).IsFalse();
    }

    [Test]
    public async Task Stamping_LeavesTheOtherTiersAlone()
    {
        DemoCacheStore store = new(null);
        store.Upsert(ParsedRecord("/d/a.dem", facts: Facts(Round(1, 1000, 2000))));

        store.UpdateExisting("/d/a.dem", r =>
        {
            DemoCacheStore.StampRoundIndex(r);
            r.RoundIndexState = RoundIndexState.Indexed;
            r.RoundIndexFingerprint = Current;
        });

        DemoCacheRecord record = store.TryLoadRecord("/d/a.dem")!;
        using (Assert.Multiple())
        {
            await Assert.That(record.RoundIndex.IsPresent).IsTrue();
            await Assert.That(record.Parse.IsPresent).IsTrue().Because("the Library's tier is untouched");
            await Assert.That(record.Analysis.IsPresent).IsFalse().Because("the index never claims the highlight scan's stamp");
            await Assert.That(record.RoundFacts).IsNotNull().Because("the rows the index read stay where they were");
            await Assert.That(record.RoundFactsFingerprint).IsEqualTo("rf-A");
            await Assert.That(store.TryGetIndex("/d/a.dem")!.RoundIndexState).IsEqualTo(RoundIndexState.Indexed);
        }
    }

    [Test]
    public async Task ASidecarWrittenBeforeTheFieldsExisted_ReadsAsNeverWritten()
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
            await Assert.That(record.RoundIndex.IsPresent).IsFalse();
            await Assert.That(record.RoundIndexState).IsEqualTo(RoundIndexState.Pending);
            await Assert.That(record.RoundIndexFingerprint).IsNull();
            await Assert.That(record.RoundIndexRowCount).IsEqualTo(0);
            await Assert.That(record.NeedsRoundIndex(Current)).IsTrue();
            await Assert.That(entry.RoundIndexSchema).IsEqualTo(0);
            await Assert.That(entry.NeedsRoundIndex(Current)).IsTrue();
        }
    }
}

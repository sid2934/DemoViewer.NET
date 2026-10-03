#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Analysis-tier home of round facts: its own fingerprint and schema decide staleness, the index
///     mirrors both so the backlog derives without a sidecar read, a sidecar written before the field
///     existed still loads, and the rows round-trip through the store with their clock header.
/// </summary>
public class RoundFactsCacheTests
{
    private const string Fingerprint = "rf-A";

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-roundfacts-{Guid.NewGuid():N}");

    private static RoundFactsRows Rows(int schema = DemoCacheRecord.RoundFactsSchema) => new()
    {
        Schema = schema,
        Clock = new RoundFactsClock
        {
            TickRate = 64,
            FrameCount = 1000,
            FirstTick = 1,
            LastTick = 1000
        },
        Rounds =
        [
            new RoundFacts
            {
                Number = 1,
                IsLive = true,
                FreezeEndTick = 100,
                EndTick = 700,
                EndSource = RoundEndSource.WinStatus,
                EndReason = RoundEndReason.BombDefused,
                WinnerSide = 3,
                Ct = new SideFacts
                {
                    Side = 3,
                    Slots = [1, 2, 3, 4, 5],
                    PlayersAtFreezeEnd = 5,
                    EquipmentFreezeEnd = 1000,
                    BuyType = BuyType.Pistol
                },
                T = new SideFacts
                {
                    Side = 2,
                    Slots = [6, 7, 8, 9, 10],
                    PlayersAtFreezeEnd = 5,
                    EquipmentFreezeEnd = 1000,
                    BuyType = BuyType.Pistol
                },
                Kills =
                [
                    new KillStep
                    {
                        Tick = 300,
                        VictimSide = 2,
                        VictimSlot = 6,
                        AttackerSlot = 1,
                        CtAlive = 5,
                        TAlive = 4
                    }
                ],
                Extra = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["my_column"] = 5L
                },
                Sources = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RoundFactsColumns.Money] = RoundFactsSourceKind.Unavailable
                }
            }
        ]
    };

    private static DemoCacheRecord Analysed(string path) => new()
    {
        Path = path,
        Size = 1000,
        ModifiedTicks = 2000,
        Map = "de_nuke",
        Parse = new TierStamp
        {
            Schema = DemoCacheRecord.ParseSchema,
            ComputedAtTicks = 1
        },
        Analysis = new TierStamp
        {
            Schema = DemoCacheRecord.AnalysisSchema,
            ComputedAtTicks = 1
        },
        AnalysisState = DemoAnalysisState.Indexed,
        ConfigFingerprint = "highlights-fp"
    };

    [Test]
    public async Task ARecordWithAnalysisAndNoRows_NeedsRoundFacts()
    {
        DemoCacheRecord record = Analysed("/d/a.dem");

        using (Assert.Multiple())
        {
            await Assert.That(record.NeedsRoundFacts(Fingerprint)).IsTrue()
                .Because("the highlight scan being current says nothing about the round facts");
            await Assert.That(record.NeedsRoundFacts(null)).IsFalse()
                .Because("no ruleset to run is nothing to do, never everything stale");
            await Assert.That(record.IsRoundFactsCurrent(Fingerprint)).IsFalse();
            await Assert.That(record.ToIndexEntry().NeedsRoundFacts(Fingerprint)).IsTrue();
            await Assert.That(record.ToIndexEntry().RoundFactsSchema).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AChangedFingerprint_ReportsItAgain_AndTheSameOneDoesNot()
    {
        DemoCacheRecord record = Analysed("/d/a.dem");
        record.RoundFacts = Rows();
        record.RoundFactsFingerprint = Fingerprint;

        using (Assert.Multiple())
        {
            await Assert.That(record.IsRoundFactsCurrent(Fingerprint)).IsTrue();
            await Assert.That(record.NeedsRoundFacts(Fingerprint)).IsFalse();
            await Assert.That(record.NeedsRoundFacts("rf-B")).IsTrue()
                .Because("a threshold edit changes the ruleset identity");
            await Assert.That(record.NeedsRoundFacts(null)).IsFalse();
            await Assert.That(record.NeedsAnalysis("highlights-fp")).IsFalse()
                .Because("the round facts fingerprint never touches the highlight scan's staleness");
        }
    }

    [Test]
    public async Task RowsAtAnOlderSchema_AreStaleUnderTheSameFingerprint()
    {
        DemoCacheRecord record = Analysed("/d/a.dem");
        record.RoundFacts = Rows(0);
        record.RoundFactsFingerprint = Fingerprint;

        await Assert.That(record.NeedsRoundFacts(Fingerprint)).IsTrue();
    }

    [Test]
    public async Task AnOldSidecarWithoutTheField_Deserializes_WithNoRows()
    {
        const string oldSidecar = """
            {
              "Path": "/d/old.dem",
              "Size": 10,
              "ModifiedTicks": 20,
              "Parse": { "Schema": 1, "ComputedAtTicks": 5 },
              "Rounds": [ { "Number": 1, "StartTickFrameClock": 100 } ],
              "Analysis": { "Schema": 1, "ComputedAtTicks": 6 },
              "AnalysisState": 1,
              "ConfigFingerprint": "fp"
            }
            """;

        DemoCacheRecord? record = JsonSerializer.Deserialize<DemoCacheRecord>(oldSidecar);

        using (Assert.Multiple())
        {
            await Assert.That(record).IsNotNull();
            await Assert.That(record!.RoundFacts).IsNull();
            await Assert.That(record.RoundFactsFingerprint).IsNull();
            await Assert.That(record.Rounds.Count).IsEqualTo(1).Because("the tier-2 rounds are untouched");
            await Assert.That(record.NeedsRoundFacts(Fingerprint)).IsTrue();
        }
    }

    [Test]
    public async Task TheRows_RoundTripThroughTheStore_AndTheIndexAgreesWithTheSidecar()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            DemoCacheRecord record = Analysed("/d/a.dem");
            record.RoundFacts = Rows();
            record.RoundFactsFingerprint = Fingerprint;
            store.Upsert(record);
            store.SaveIndex();

            DemoCacheStore reopened = new(root);
            DemoCacheIndexEntry? entry = reopened.TryGetIndex("/d/a.dem");
            DemoCacheRecord? cached = reopened.TryLoadRecord("/d/a.dem");

            using (Assert.Multiple())
            {
                await Assert.That(entry).IsNotNull();
                await Assert.That(entry!.RoundFactsSchema).IsEqualTo(DemoCacheRecord.RoundFactsSchema);
                await Assert.That(entry.RoundFactsFingerprint).IsEqualTo(Fingerprint);
                await Assert.That(entry.NeedsRoundFacts(Fingerprint)).IsFalse();
                await Assert.That(entry.NeedsRoundFacts("rf-B")).IsTrue();

                await Assert.That(cached).IsNotNull();
                await Assert.That(cached!.NeedsRoundFacts(Fingerprint)).IsEqualTo(entry.NeedsRoundFacts(Fingerprint));
                await Assert.That(cached.NeedsRoundFacts("rf-B")).IsEqualTo(entry.NeedsRoundFacts("rf-B"));

                RoundFactsRows rows = cached.RoundFacts!;
                await Assert.That(rows.ClockIdentity().TickRate).IsEqualTo(64);
                await Assert.That(rows.ClockIdentity().LastTick).IsEqualTo(1000);
                RoundFacts round = rows.Rounds[0];
                await Assert.That(round.EndReason).IsEqualTo(RoundEndReason.BombDefused);
                await Assert.That(round.Ct.Slots).IsEquivalentTo([1, 2, 3, 4, 5]);
                await Assert.That(round.Ct.BuyType).IsEqualTo(BuyType.Pistol);
                await Assert.That(round.Ct.Thresholds).IsEqualTo(BuyThresholds.Default);
                await Assert.That(round.Kills[0].AttackerSlot).IsEqualTo(1);
                await Assert.That(round.Sources[RoundFactsColumns.Money]).IsEqualTo(RoundFactsSourceKind.Unavailable);
                // Extra comes back as JSON; the label projection reads it through the same coercion.
                await Assert.That(RoundFactsSource.Labels(round).Single(l => l.Key == "my_column").Value).IsEqualTo("5");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task InMemoryStore_HoldsTheRowsForTheSession()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = Analysed("/d/a.dem");
        record.RoundFacts = Rows();
        record.RoundFactsFingerprint = Fingerprint;
        store.Upsert(record);

        using (Assert.Multiple())
        {
            await Assert.That(store.TryLoadRecord("/d/a.dem")?.RoundFacts?.Rounds.Count).IsEqualTo(1);
            await Assert.That(store.TryGetIndex("/d/a.dem")?.RoundFactsFingerprint).IsEqualTo(Fingerprint);
        }
    }
}

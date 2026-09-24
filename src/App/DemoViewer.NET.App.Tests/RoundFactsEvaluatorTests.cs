#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The evaluator's staleness and write logic over a fake ruleset identity and a fake row source:
///     no ruleset means nothing runs, the parked engine source writes nothing, rows write once under
///     the fingerprint they were produced under, a changed identity (a threshold edit) re-runs, and an
///     identity that did not change (a malformed override the loader dropped) leaves the rows alone.
/// </summary>
public class RoundFactsEvaluatorTests
{
    private const string Demo = "/d/match.dem";

    private static ParsedDemo TwoRoundDemo() => SyntheticParsedDemo.Create(
        Frames(1, 500, 1000, 5000, 9000),
        [
            TestGameEvents.RoundFreezeEnd(frameNumber: 2, serverTick: 3000, gameTick: 1000),
            TestGameEvents.RoundOfficiallyEnded(frameNumber: 3, serverTick: 7000, gameTick: 4448),
            TestGameEvents.RoundFreezeEnd(frameNumber: 3, serverTick: 7000, gameTick: 5000)
        ],
        tickCount: 9000,
        serverStartTick: 2000);

    private static DemoFrame[] Frames(params int[] ticks)
    {
        DemoFrame[] frames = new DemoFrame[ticks.Length];
        for (int i = 0; i < ticks.Length; i++)
        {
            frames[i] = new DemoFrame
            {
                CommandKind = EDemoCommands.DemPacket,
                FrameNumber = i,
                ServerTick = ticks[i],
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            };
        }

        return frames;
    }

    private static Dictionary<string, object?> Row(int round, int side, int freezeEnd, int winner = 3) => new(StringComparer.Ordinal)
    {
        [RoundFactsColumns.RoundNumber] = round,
        [RoundFactsColumns.Side] = side,
        [RoundFactsColumns.FreezeEndTick] = freezeEnd,
        [RoundFactsColumns.Slots] = side == 3 ? (int[])[1, 2, 3, 4, 5] : (int[])[6, 7, 8, 9, 10],
        [RoundFactsColumns.Players] = 5,
        [RoundFactsColumns.Equipment] = 4000,
        [RoundFactsColumns.WinnerSide] = winner,
        [RoundFactsColumns.EndTick] = freezeEnd + 3000,
        [RoundFactsColumns.MatchRound] = round - 1
    };

    private static RoundFactsTable TwoRoundTable() => new(
        [Row(1, 3, 1000), Row(1, 2, 1000), Row(2, 3, 5000, 2), Row(2, 2, 5000, 2)],
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, object?>(StringComparer.Ordinal),
        []);

    private static DemoCacheStore StoreWithParsedDemo()
    {
        DemoCacheStore store = new(null);
        store.Upsert(new DemoCacheRecord
        {
            Path = Demo,
            Size = 10,
            ModifiedTicks = 20,
            Sha256 = "abc",
            Map = "de_nuke",
            Parse = new TierStamp
            {
                Schema = DemoCacheRecord.ParseSchema,
                ComputedAtTicks = 1
            }
        });
        return store;
    }

    [Test]
    public async Task WithoutARuleset_NothingRuns_AndNothingIsWanted()
    {
        DemoCacheStore store = StoreWithParsedDemo();
        CountingSource source = new(TwoRoundTable());
        RoundFactsEvaluator evaluator = new(store, source, new FakeIdentity(null));
        int updates = 0;
        evaluator.Updated += _ => updates++;

        evaluator.OnParsedOpportunistically(Demo, TwoRoundDemo());
        evaluator.Evaluate(Demo, TwoRoundDemo());

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
            await Assert.That(source.Calls).IsEqualTo(0).Because("no ruleset to run means the engine seam is never asked");
            await Assert.That(store.TryLoadRecord(Demo)!.RoundFacts).IsNull();
            await Assert.That(updates).IsEqualTo(0);
        }
    }

    [Test]
    public async Task TheParkedEngineSource_ReportsEverythingUnavailable_AndWritesNoRows()
    {
        DemoCacheStore store = StoreWithParsedDemo();
        RoundFactsEvaluator evaluator = new(store, new EngineRoundFactsRowSource(), new FakeIdentity("rf-A"));
        int updates = 0;
        evaluator.Updated += _ => updates++;

        RoundFactsTable table = new EngineRoundFactsRowSource().Rows(TwoRoundDemo());
        evaluator.Evaluate(Demo, TwoRoundDemo());
        DemoCacheRecord record = store.TryLoadRecord(Demo)!;

        using (Assert.Multiple())
        {
            await Assert.That(table.Rows).IsEmpty();
            await Assert.That(table.UnavailableColumns).IsEquivalentTo(RoundFactsColumns.Known);
            await Assert.That(table.Diagnostics.Single()).Contains("CS2DemoKit #54");
            await Assert.That(record.RoundFacts).IsNull().Because("no rows means nothing is written, so nothing changes for users");
            await Assert.That(record.RoundFactsFingerprint).IsNull();
            await Assert.That(updates).IsEqualTo(0);
            await Assert.That(evaluator.Wants(Demo)).IsTrue()
                .Because("the demo still wants rows; it is the engine that cannot supply them yet");
        }
    }

    [Test]
    public async Task RowsAreWrittenOnce_UnderTheFingerprint_WithTheClockHeader()
    {
        DemoCacheStore store = StoreWithParsedDemo();
        CountingSource source = new(TwoRoundTable());
        RoundFactsEvaluator evaluator = new(store, source, new FakeIdentity("rf-A"));
        List<string> updates = [];
        evaluator.Updated += updates.Add;

        await Assert.That(evaluator.Wants(Demo)).IsTrue();
        await Assert.That(evaluator.PendingPaths()).Contains(Demo);

        evaluator.OnParsedOpportunistically(Demo, TwoRoundDemo());
        evaluator.Evaluate(Demo, TwoRoundDemo());
        DemoCacheRecord record = store.TryLoadRecord(Demo)!;
        DemoCacheIndexEntry entry = store.TryGetIndex(Demo)!;

        using (Assert.Multiple())
        {
            await Assert.That(source.Calls).IsEqualTo(1).Because("the second pass sees current rows and stops at the fingerprint compare");
            await Assert.That(updates).IsEquivalentTo([Demo]);
            await Assert.That(record.RoundFacts).IsNotNull();
            await Assert.That(record.RoundFacts!.Rounds.Count).IsEqualTo(2);
            await Assert.That(record.RoundFacts.Rounds[0].FreezeEndTick).IsEqualTo(1000)
                .Because("ClipRounds.Derive is the round authority, on the frame clock");
            await Assert.That(record.RoundFacts.Rounds[1].WinnerSide).IsEqualTo(2);
            await Assert.That(record.RoundFacts.DemoSha256).IsEqualTo("abc");
            await Assert.That(record.RoundFacts.Schema).IsEqualTo(DemoCacheRecord.RoundFactsSchema);
            await Assert.That(record.RoundFactsFingerprint).IsEqualTo("rf-A");
            await Assert.That(record.Parse.IsPresent).IsTrue().Because("the Library's tier is left as it was");
            await Assert.That(record.Analysis.IsPresent).IsFalse().Because("round facts never claim the highlight scan's stamp");
            await Assert.That(entry.RoundFactsFingerprint).IsEqualTo("rf-A");
            await Assert.That(entry.RoundFactsSchema).IsEqualTo(DemoCacheRecord.RoundFactsSchema);
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();

            RoundFactsClock clock = record.RoundFacts.Clock!;
            await Assert.That(clock.Kind).IsEqualTo("dv-frame-clock");
            await Assert.That(clock.TickRate).IsEqualTo(64);
            await Assert.That(clock.FrameCount).IsEqualTo(5);
            await Assert.That(clock.FirstTick).IsEqualTo(1).Because("the first frame's server tick, the frame clock");
            await Assert.That(clock.LastTick).IsEqualTo(9000);
        }
    }

    [Test]
    public async Task AChangedIdentity_ReRunsOnlyThisEvaluator_AndAnUnchangedOneDoesNot()
    {
        DemoCacheStore store = StoreWithParsedDemo();
        CountingSource source = new(TwoRoundTable());
        FakeIdentity identity = new("rf-A");
        RoundFactsEvaluator evaluator = new(store, source, identity);
        evaluator.Evaluate(Demo, TwoRoundDemo());

        // A malformed override: the loader drops the user file, the shipped ruleset stays, the identity is
        // the same, and the cached rows stay in place.
        evaluator.Evaluate(Demo, TwoRoundDemo());
        await Assert.That(source.Calls).IsEqualTo(1);
        await Assert.That(evaluator.Wants(Demo)).IsFalse();

        // A threshold edit: the identity moves, the demo is wanted again, and the rows are rewritten under
        // the new fingerprint.
        identity.Value = "rf-B";
        await Assert.That(evaluator.Wants(Demo)).IsTrue();
        await Assert.That(evaluator.PendingPaths()).Contains(Demo);
        evaluator.Evaluate(Demo, TwoRoundDemo());

        using (Assert.Multiple())
        {
            await Assert.That(source.Calls).IsEqualTo(2);
            await Assert.That(store.TryLoadRecord(Demo)!.RoundFactsFingerprint).IsEqualTo("rf-B");
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
        }
    }

    [Test]
    public async Task AnUnparsedDemo_IsNotWanted_ButAnOpportunisticParseStillWrites()
    {
        DemoCacheStore store = new(null);
        CountingSource source = new(TwoRoundTable());
        RoundFactsEvaluator evaluator = new(store, source, new FakeIdentity("rf-A"));

        await Assert.That(evaluator.Wants(Demo)).IsFalse().Because("the Library parses first; its fan-out reaches this");

        evaluator.OnParsedOpportunistically(Demo, TwoRoundDemo());

        using (Assert.Multiple())
        {
            await Assert.That(source.Calls).IsEqualTo(1);
            await Assert.That(store.TryLoadRecord(Demo)?.RoundFacts?.Rounds.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ASourceThatThrows_IsIsolated()
    {
        DemoCacheStore store = StoreWithParsedDemo();
        RoundFactsEvaluator evaluator = new(store, new ThrowingSource(), new FakeIdentity("rf-A"));

        evaluator.Evaluate(Demo, TwoRoundDemo());

        using (Assert.Multiple())
        {
            await Assert.That(store.TryLoadRecord(Demo)!.RoundFacts).IsNull();
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
        }
    }

    [Test]
    public async Task TheFingerprint_FoldsTheSchemaIntoTheRulesetIdentity()
    {
        string a = RoundFactsFingerprint.Combine(1, "engine-hash");

        using (Assert.Multiple())
        {
            await Assert.That(a).IsEqualTo(RoundFactsFingerprint.Combine(1, "engine-hash"));
            await Assert.That(a).IsNotEqualTo(RoundFactsFingerprint.Combine(2, "engine-hash"))
                .Because("a payload shape change re-runs the evaluator");
            await Assert.That(a).IsNotEqualTo(RoundFactsFingerprint.Combine(1, "other-hash"));
            await Assert.That(a.Length).IsEqualTo(32);
        }
    }

    private sealed class FakeIdentity(string? value) : IRoundFactsRulesetIdentity
    {
        public string? Value { get; set; } = value;

        public string? Fingerprint(int tickRate) => Value;
    }

    private sealed class CountingSource(RoundFactsTable table) : IRoundFactsRowSource
    {
        public int Calls { get; private set; }

        public RoundFactsTable Rows(ParsedDemo parsed)
        {
            Calls++;
            return table;
        }
    }

    private sealed class ThrowingSource : IRoundFactsRowSource
    {
        public RoundFactsTable Rows(ParsedDemo parsed) => throw new InvalidOperationException("engine down");
    }
}

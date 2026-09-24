#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The builder over synthetic samples and synthetic Round Facts rows: rows only inside live
///     windows, step arithmetic at 64 and 128 ticks, a death at the sampled tick removing the slot
///     from that row on, runs tiling the steps, two frames on one tick folding into one row,
///     spectators dropped, the place and transition sums, the clock header, and the zones source.
/// </summary>
public class RoundIndexBuilderTests
{
    private static RoundIndexDocument Build(ParsedDemo demo, RoundFactsRows facts, IEnumerable<PositionSample> samples,
        IPlaceSource? source = null) =>
        RoundIndexBuilder.Build(demo, facts, RoundIndexOptions.Default, source ?? PawnPlaceSource.Instance, samples);

    [Test]
    public async Task RowsAreSampledOnlyInsideLiveWindows_FromFreezeEndToEnd()
    {
        // Round 1 live from 1000 to 1200 (freeze end, end); round 2 not live; a warmup sample before any round.
        RoundFactsRows facts = Facts(Round(1, 1000, 1200), Round(2, 5000, 6000, live: false));
        List<PositionSample> samples =
        [
            .. Everyone(500, "CTSpawn", "TSpawn"),
            .. Everyone(1000, "CTSpawn", "TSpawn"),
            .. Everyone(1064, "Outside", "Ramp"),
            .. Everyone(1128, "Outside", "Ramp"),
            .. Everyone(1200, "BombsiteA", "BombsiteA"),
            .. Everyone(5000, "CTSpawn", "TSpawn")
        ];

        RoundIndexDocument document = Build(Demo(), facts, samples);

        using (Assert.Multiple())
        {
            await Assert.That(document.Rounds.Count).IsEqualTo(1).Because("a round that is not live is skipped");
            await Assert.That(document.CadenceTicks).IsEqualTo(64);
            await Assert.That(document.RowCount).IsEqualTo(3).Because("1000, 1064 and 1128; the end tick itself is not sampled");
            await Assert.That(document.Rounds[0].Runs.Count).IsEqualTo(2);
            await Assert.That(document.Rounds[0].Runs[0]).IsEqualTo(new RoundIndexRun(0, 0, "CTSpawn:5", "TSpawn:5"));
            await Assert.That(document.Rounds[0].Runs[1]).IsEqualTo(new RoundIndexRun(1, 2, "Outside:5", "Ramp:5"));
            await Assert.That(document.Map).IsEqualTo("de_nuke");
            await Assert.That(document.Fingerprint).IsEqualTo("ri1;cadence=1;token=1;rf=1;src=pawn");
        }
    }

    [Test]
    public async Task StepArithmetic_FollowsTheTickRate()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1400));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "A", "B"),
            // At 64 the first frame at or past the due tick 1064 opens step 1, and 1100 is that frame; at
            // 128 the next due tick is 1128, so 1100 falls between two due ticks and is skipped.
            .. Everyone(1100, "A", "B"),
            .. Everyone(1128, "C", "D"),
            .. Everyone(1256, "E", "F")
        ];

        RoundIndexDocument at64 = Build(Demo(), facts, samples);
        RoundIndexDocument at128 = Build(Demo(tickRate: 128), facts, samples);

        using (Assert.Multiple())
        {
            await Assert.That(at64.CadenceTicks).IsEqualTo(64);
            await Assert.That(at64.ExpandRows(at64.Rounds[0]).Select(r => r.Tick)).IsEquivalentTo([1000, 1064, 1128, 1256])
                .Because("a row's tick is its nominal step tick, never the frame it was read from");
            await Assert.That(at64.ExpandRows(at64.Rounds[0]).Select(r => r.Step)).IsEquivalentTo([0, 1, 2, 4]);
            await Assert.That(at64.ExpandRows(at64.Rounds[0]).Select(r => r.Ct)).IsEquivalentTo(["A:5", "A:5", "C:5", "E:5"]);

            await Assert.That(at128.CadenceTicks).IsEqualTo(128);
            await Assert.That(at128.ExpandRows(at128.Rounds[0]).Select(r => r.Tick)).IsEquivalentTo([1000, 1128, 1256]);
            await Assert.That(at128.ExpandRows(at128.Rounds[0]).Select(r => r.Step)).IsEquivalentTo([0, 1, 2]);
            await Assert.That(at128.Fingerprint).IsEqualTo(at64.Fingerprint)
                .Because("the cadence is in seconds, so the rate is not part of what a row means");
        }
    }

    [Test]
    public async Task ADeathAtTheSampledTick_RemovesTheSlotFromThatRowOn()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1300, kills: [Kill(1064, 6), Kill(1100, 1)]));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "Outside", "Ramp"),
            .. Everyone(1064, "Outside", "Ramp"),
            .. Everyone(1128, "Outside", "Ramp"),
            .. Everyone(1192, "Outside", "Ramp")
        ];

        RoundIndexDocument document = Build(Demo(), facts, samples);
        List<RoundIndexRow> rows = [.. document.ExpandRows(document.Rounds[0])];

        using (Assert.Multiple())
        {
            await Assert.That(rows[0].T).IsEqualTo("Ramp:5");
            await Assert.That(rows[1].T).IsEqualTo("Ramp:4").Because("slot 6 died at the sampled tick and is dead from it on");
            await Assert.That(rows[1].Ct).IsEqualTo("Outside:5").Because("slot 1 dies at 1100, after this row");
            await Assert.That(rows[2].Ct).IsEqualTo("Outside:4");
            await Assert.That(rows[3].Ct).IsEqualTo("Outside:4");
            await Assert.That(rows[3].T).IsEqualTo("Ramp:4");
        }
    }

    [Test]
    public async Task TwoFramesOnOneTick_ProduceOneRow_AndTheLaterFrameWinsBySlot()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1200));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "Outside", "Ramp", frame: 10),
            Sample(1000, 1, "Hell", frame: 11), // the same tick, a later frame: slot 1 moved
            .. Everyone(1064, "Outside", "Ramp", frame: 14)
        ];

        RoundIndexDocument document = Build(Demo(), facts, samples);
        List<RoundIndexRow> rows = [.. document.ExpandRows(document.Rounds[0])];

        using (Assert.Multiple())
        {
            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows[0].Ct).IsEqualTo("Hell:1|Outside:4");
            await Assert.That(rows[1].Ct).IsEqualTo("Outside:5");
        }
    }

    [Test]
    public async Task SpectatorsAndUnseatedSlots_AreDropped_AndANullPlaceIsAQuestionMark()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1200));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "Outside", "Ramp"),
            Sample(1000, 12, "Outside"), // a spectator: on neither side
            Sample(1000, 3, null) // slot 3 again: overwrites its place with null
        ];

        RoundIndexDocument document = Build(Demo(), facts, samples);
        RoundIndexRow row = document.ExpandRows(document.Rounds[0]).Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.Ct).IsEqualTo("?:1|Outside:4");
            await Assert.That(row.T).IsEqualTo("Ramp:5");
            await Assert.That(document.Places.ContainsKey("?")).IsFalse().Because("a null place has no centroid");
            await Assert.That(document.Places["Outside"].Count).IsEqualTo(4).Because("the spectator's sample is not counted");
        }
    }

    [Test]
    public async Task PlacesAndTransitions_AreSummed()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1300), Round(2, 2000, 2200));
        List<PositionSample> samples = [];
        // Round 1: everyone in Outside at z=10, then the CTs cross to Hell (z=100), then back.
        samples.AddRange(CtSlots.Select(s => Sample(1000, s, "Outside", 100, 200, 10)));
        samples.AddRange(TSlots.Select(s => Sample(1000, s, "Ramp", 500, 600, 10)));
        samples.AddRange(CtSlots.Select(s => Sample(1064, s, "Hell", 300, 400, 100)));
        samples.AddRange(TSlots.Select(s => Sample(1064, s, "Ramp", 520, 620, 10)));
        samples.AddRange(CtSlots.Select(s => Sample(1128, s, "Outside", 100, 200, 10)));
        samples.AddRange(TSlots.Select(s => Sample(1128, s, "Ramp", 500, 600, 10)));
        // Round 2: a chain does not cross a round boundary; slot 1 starts in Hell and stays.
        samples.AddRange(CtSlots.Select(s => Sample(2000, s, "Hell", 300, 400, 100)));
        samples.AddRange(TSlots.Select(s => Sample(2000, s, "Ramp", 500, 600, 10)));

        RoundIndexDocument document = Build(Demo(), facts, samples);

        using (Assert.Multiple())
        {
            await Assert.That(document.Places["Outside"].Count).IsEqualTo(10);
            await Assert.That(document.Places["Outside"].Buckets.Count).IsEqualTo(1);
            await Assert.That(document.Places["Outside"].Buckets[0].ZBucket).IsEqualTo(0).Because("z=10 quantizes to the 0 bucket");
            await Assert.That(document.Places["Outside"].Buckets[0].SumX).IsEqualTo(1000);
            await Assert.That(document.Places["Hell"].Buckets[0].ZBucket).IsEqualTo(128).Because("z=100 quantizes to the 128 bucket");
            await Assert.That(document.Places["Ramp"].Count).IsEqualTo(20);

            PlaceTransition hellOutside = document.Transitions.Single(t => t.A == "Hell" && t.B == "Outside");
            await Assert.That(hellOutside.Count).IsEqualTo(10).Because("five players crossed twice within round 1; round 2 starts a new chain");
            await Assert.That(document.Transitions.Count).IsEqualTo(1).Because("the T side never moved");
        }
    }

    [Test]
    public async Task TheClockHeader_IsTheFrameClock_AndTheDemoBlockIsLeftForTheCaller()
    {
        RoundIndexDocument document = Build(Demo(lastTick: 9000), Facts(Round(1, 1000, 1100)), Everyone(1000, "A", "B"));

        using (Assert.Multiple())
        {
            await Assert.That(document.Clock.Kind).IsEqualTo("dv-frame-clock");
            await Assert.That(document.Clock.TickRate).IsEqualTo(64);
            await Assert.That(document.Clock.FrameCount).IsEqualTo(2);
            await Assert.That(document.Clock.FirstTick).IsEqualTo(1);
            await Assert.That(document.Clock.LastTick).IsEqualTo(9000);
            await Assert.That(document.Demo.StableKey).IsEqualTo("");
            await Assert.That(document.SchemaVersion).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AnEndlessLastRound_RunsToTheNextFreezeEnd_ThenToTheLastFrame()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, null), Round(2, 1200, null));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "A", "B"),
            .. Everyone(1128, "A", "B"),
            .. Everyone(1200, "C", "D"),
            .. Everyone(1264, "C", "D")
        ];

        RoundIndexDocument document = Build(Demo(lastTick: 1300), facts, samples);

        using (Assert.Multiple())
        {
            await Assert.That(document.Rounds[0].EndTick).IsEqualTo(1200).Because("the next round's freeze end bounds a round without an end");
            await Assert.That(document.Rounds[1].EndTick).IsEqualTo(1301).Because("the last round runs one past the last frame");
            await Assert.That(document.Rounds[0].Runs.Count).IsEqualTo(2)
                .Because("step 1 was not sampled, and a run never spans a step the walk did not see");
            await Assert.That(document.Rounds[0].Runs[0]).IsEqualTo(new RoundIndexRun(0, 0, "A:5", "B:5"));
            await Assert.That(document.Rounds[0].Runs[1]).IsEqualTo(new RoundIndexRun(2, 2, "A:5", "B:5"));
            await Assert.That(document.Rounds[1].Runs.Single()).IsEqualTo(new RoundIndexRun(0, 1, "C:5", "D:5"));
        }
    }

    [Test]
    public async Task TheZonesSource_MintsTheNameFromTheResolver_AndCarriesTheVersion()
    {
        FakeZoneResolver resolver = new("zv-1", world => world.X > 0 ? "E-box" : null);
        ZonePlaceSource source = new(resolver);
        RoundFactsRows facts = Facts(Round(1, 1000, 1100));
        List<PositionSample> samples =
        [
            .. CtSlots.Select(s => Sample(1000, s, "Outside", x: 10)),
            .. TSlots.Select(s => Sample(1000, s, "Ramp", x: -10))
        ];

        RoundIndexDocument document = Build(Demo(), facts, samples, source);
        RoundIndexRow row = document.ExpandRows(document.Rounds[0]).Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.Ct).IsEqualTo("E-box:5").Because("the resolver's name, not the pawn's");
            await Assert.That(row.T).IsEqualTo("?:5").Because("a point the resolver does not place keeps the man-count");
            await Assert.That(document.Fingerprint).IsEqualTo("ri1;cadence=1;token=1;rf=1;src=zones;zv=zv-1");
        }
    }

    internal sealed class FakeZoneResolver(string version, Func<System.Numerics.Vector3, string?> resolve,
        IReadOnlyDictionary<string, string[]>? adjacency = null) : IZonePlaceResolver
    {
        public string ZonesVersion { get; } = version;

        public string? Resolve(System.Numerics.Vector3 world) => resolve(world);

        // A floor click is answered as the world point on the floor key, which is what a resolver
        // over areas of that floor would see.
        public string? ResolveOnFloor(double x, double y, double floorKey) =>
            resolve(new System.Numerics.Vector3((float)x, (float)y, (float)floorKey));

        public IReadOnlySet<string> Adjacent(string place) =>
            adjacency is not null && adjacency.TryGetValue(place, out string[]? neighbours)
                ? new HashSet<string>(neighbours, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
    }
}

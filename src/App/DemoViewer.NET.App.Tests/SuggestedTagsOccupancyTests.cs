#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The input layer (suggested-tags.md §9 step 1) over synthetic samples and synthetic Round Facts
///     rows: windows, sides and alive from the rows (integrator correction 11), the first sample per
///     (slot, second), the empty place the wire delivers, the bomb site from the fact, the index source
///     agreeing with the walk, the detonation events, the cloud, and the resolver's precedence.
/// </summary>
public class SuggestedTagsOccupancyTests
{
    [Test]
    public async Task TheWalk_FoldsFirstSamplePerSecond_OnlyInsideLiveWindows()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1256), Round(2, 5000, 6000, live: false));
        List<PositionSample> samples =
        [
            .. Everyone(500, "CTSpawn", "TSpawn"), // warmup, before any window
            .. Everyone(1000, "CTSpawn", "TSpawn"),
            .. Everyone(1032, "Outside", "Ramp"), // same second: the first sample stands
            .. Everyone(1064, "Outside", "Ramp"),
            .. Everyone(1256, "BombsiteA", "BombsiteA"), // the end tick is outside
            .. Everyone(5000, "CTSpawn", "TSpawn")
        ];

        OccupancyBuild build = RoundOccupancyBuilder.FromWalk(Demo(), facts, samples: samples);

        RoundOccupancy round = build.Rounds.Single();
        using (Assert.Multiple())
        {
            await Assert.That(round.Round).IsEqualTo(1);
            await Assert.That(round.StartTick).IsEqualTo(1000);
            await Assert.That(round.EndTick).IsEqualTo(1256);
            await Assert.That(round.Seconds).IsEqualTo(4);
            await Assert.That(round.HasSlots).IsTrue();
            await Assert.That(round.CountsAt(3, 0)["CTSpawn"]).IsEqualTo(5);
            await Assert.That(round.CountsAt(2, 0)["TSpawn"]).IsEqualTo(5).Because("1032 is the same second as 1000");
            await Assert.That(round.CountsAt(2, 1)["Ramp"]).IsEqualTo(5);
            await Assert.That(round.AliveCount(2, 3)).IsEqualTo(0).Because("nothing was sampled in second 3");
            await Assert.That(round.PlaceOf(6, 1)).IsEqualTo("Ramp");
            await Assert.That(round.SideOf(6)).IsEqualTo(2);
            await Assert.That(round.Slots(3)).IsEquivalentTo(CtSlots);
        }
    }

    [Test]
    public async Task AliveComesFromTheKills_AndSidesFromTheSlots()
    {
        // Round 2 swaps the sides, as the halftime does; slot 6 dies at 1100 in round 1 and keeps
        // producing samples, as a GOTV pawn does.
        RoundFactsRows facts = Facts(
            Round(1, 1000, 1300, kills: Kill(1100, 6)),
            Round(2, 2000, 2300, ctSlots: TSlots, tSlots: CtSlots));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "CTSpawn", "TSpawn"),
            .. Everyone(1064, "CTSpawn", "TSpawn"),
            .. Everyone(1128, "CTSpawn", "TSpawn"),
            .. Everyone(2000, "CTSpawn", "TSpawn")
        ];

        OccupancyBuild build = RoundOccupancyBuilder.FromWalk(Demo(), facts, samples: samples);

        RoundOccupancy first = build.Rounds[0];
        RoundOccupancy second = build.Rounds[1];
        using (Assert.Multiple())
        {
            await Assert.That(first.AliveCount(2, 1)).IsEqualTo(5).Because("1064 is before the kill");
            await Assert.That(first.AliveCount(2, 2)).IsEqualTo(4).Because("dead from the kill tick on");
            await Assert.That(first.IsAlive(6, 2)).IsFalse();
            await Assert.That(first.PlaceOf(6, 2)).IsNull();
            await Assert.That(second.SideOf(6)).IsEqualTo(3).Because("the second round's row seats slot 6 on CT");
            await Assert.That(second.CountsAt(3, 0)["TSpawn"]).IsEqualTo(5).Because("the old T slots stand where they stood, now on CT");
            await Assert.That(second.AliveCount(2, 0)).IsEqualTo(5).Because("slot 6's death was last round's");
        }
    }

    [Test]
    public async Task TheEmptyPlace_IsAliveButUnplaced()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1128));
        List<PositionSample> samples =
        [
            Sample(1000, 1, ""),
            Sample(1000, 2, null),
            Sample(1000, 3, "CTSpawn")
        ];

        RoundOccupancy round = RoundOccupancyBuilder.FromWalk(Demo(), facts, samples: samples).Rounds.Single();

        using (Assert.Multiple())
        {
            await Assert.That(round.AliveCount(3, 0)).IsEqualTo(3);
            await Assert.That(round.CountsAt(3, 0).Count).IsEqualTo(1);
            await Assert.That(round.PlaceOf(1, 0)).IsNull();
            await Assert.That(round.IsAlive(1, 0)).IsTrue();
        }
    }

    [Test]
    public async Task TheBomb_ComesFromTheFact_AsAPlaceName()
    {
        RoundFacts planted = Round(1, 1000, 9000);
        planted.PlantTick = 3000;
        planted.PlantSite = BombSite.B;
        planted.DefuseTick = 4000;
        RoundFacts unknownSite = Round(2, 10000, 19000);
        unknownSite.PlantTick = 12000;

        IReadOnlyList<RoundOccupancy> rounds =
            RoundOccupancyBuilder.FromWalk(Demo(lastTick: 30000), Facts(planted, unknownSite), samples: []).Rounds;

        using (Assert.Multiple())
        {
            await Assert.That(rounds[0].Bomb).IsEqualTo(new RoundBomb(3000, SiteRegions.SiteB, 4000, null));
            await Assert.That(rounds[1].Bomb).IsEqualTo(new RoundBomb(12000, null, null, null));
        }
    }

    [Test]
    public async Task TheIndexSource_AgreesWithTheWalk_PerSidePerSecond()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1400, kills: Kill(1100, 7)));
        List<PositionSample> samples =
        [
            .. Everyone(1000, "CTSpawn", "TSpawn"),
            .. Everyone(1064, "Outside", "Ramp"),
            .. Everyone(1128, "Outside", ""),
            .. Everyone(1192, "Heaven", "BombsiteA"),
            .. Everyone(1256, "Heaven", "BombsiteA"),
            .. Everyone(1320, "Heaven", "BombsiteA")
        ];

        RoundOccupancy walked = RoundOccupancyBuilder.FromWalk(Demo(), facts, samples: samples).Rounds.Single();
        RoundIndexDocument index = RoundIndexBuilder.Build(Demo(), facts, RoundIndexOptions.Default, PawnPlaceSource.Instance, samples);
        RoundOccupancy indexed = RoundOccupancyBuilder.FromIndex(index, facts).Single();

        using (Assert.Multiple())
        {
            await Assert.That(indexed.HasSlots).IsFalse();
            await Assert.That(indexed.Seconds).IsEqualTo(walked.Seconds);
            await Assert.That(indexed.Slots(2)).IsEquivalentTo(walked.Slots(2));
            for (int s = 0; s < walked.Seconds; s++)
            {
                foreach (int side in (int[])[2, 3])
                {
                    await Assert.That(indexed.AliveCount(side, s)).IsEqualTo(walked.AliveCount(side, s)).Because($"side {side} second {s}");
                    await Assert.That(indexed.CountsAt(side, s)).IsEquivalentTo(walked.CountsAt(side, s)).Because($"side {side} second {s}");
                }
            }
        }
    }

    [Test]
    public async Task TheCloud_KeepsOneInTwentyFive_AndOnlyPlacedAliveSamples()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1000 + 64 * 60));
        List<PositionSample> samples = [];
        for (int s = 0; s < 60; s++)
        {
            samples.Add(Sample(1000 + s * 64, 1, "Outside", x: s));
            samples.Add(Sample(1000 + s * 64, 2, ""));
        }

        DetonationCloud cloud = RoundOccupancyBuilder.FromWalk(Demo(), facts, samples: samples).Cloud;

        await Assert.That(cloud.Count).IsEqualTo(3).Because("sixty placed samples, kept at the 1st, 26th and 51st");
    }

    [Test]
    public async Task DetonationEvents_AreReadInTickOrder_WithTheInfernoAndDecoyUnattributed()
    {
        ParsedDemo demo = SyntheticParsedDemo.Create(allGameEvents:
        [
            Fire("inferno_startburn", 300, new InfernoStartburnEvent { EntityId = 1, X = 1, Y = 2, Z = 3 }),
            Fire("smokegrenade_detonate", 100, new SmokeGrenadeDetonateEvent { EntityId = 2, UserId = 6, UserIdPawn = 0, X = 4, Y = 5, Z = 6 }),
            Fire("flashbang_detonate", 200, new FlashbangDetonateEvent { EntityId = 3, UserId = 7, UserIdPawn = 0, X = 7, Y = 8, Z = 9 }),
            Fire("hegrenade_detonate", 250, new HegrenadeDetonateEvent { EntityId = 4, UserId = 8, UserIdPawn = 0, X = 0, Y = 0, Z = 0 }),
            Fire("decoy_started", 400, new DecoyStartedEvent { EntityId = 5, UserId = 123456, X = 0, Y = 0, Z = 0 })
        ]);

        List<PlacedEvent> events = DetonationEvents.From(demo);

        using (Assert.Multiple())
        {
            await Assert.That(events.Select(e => e.Kind)).IsEquivalentTo(
                [DetonationEvents.Smoke, DetonationEvents.Flash, DetonationEvents.He, DetonationEvents.Inferno, DetonationEvents.Decoy]);
            await Assert.That(events[0].ThrowerSlot).IsEqualTo(6);
            await Assert.That(events[0].Position).IsEqualTo(new Vector3(4, 5, 6));
            await Assert.That(events[3].ThrowerSlot).IsEqualTo(-1).Because("inferno_startburn has no thrower");
            await Assert.That(events[4].ThrowerSlot).IsEqualTo(-1).Because("decoy_started names a pawn handle, not a slot");
            await Assert.That(events.All(e => e.Side == 0 && e.Place is null)).IsTrue();
        }
    }

    [Test]
    public async Task ThrowerSides_AreTheRoundsNotTheDemos()
    {
        RoundOccupancy round = SuggestedTagsTestData.Round();
        PlacedEvent smoke = new(SuggestedTagsTestData.At(5), DetonationEvents.Smoke, 6, 3, Vector3.Zero, "ALong");
        List<PlacedEvent> flashes =
        [
            smoke,
            smoke with { Tick = SuggestedTagsTestData.At(5.5) },
            smoke with { Tick = SuggestedTagsTestData.At(6) }
        ];

        IReadOnlyList<TagProposal> proposals = SuggestedTagsTestData.Detect(round, flashes);

        await Assert.That(proposals.Single().Side).IsEqualTo(2).Because("slot 6 is on T this round whatever the event carried");
    }

    [Test]
    public async Task TheResolver_PrefersZones_ThenTheIndex_ThenTheCloud_Within400Units()
    {
        DetonationCloud cloud = new();
        cloud.Add(new Vector3(0, 0, 0), "CloudPlace");
        cloud.Add(new Vector3(1000, 0, 0), "FarCloud");
        List<PlaceSummary> places =
        [
            new("IndexPlace", 10, [new PlaceZBucket(0, 10, 50, 0)]),
            new("UpperFloor", 10, [new PlaceZBucket(512, 10, 0, 0)])
        ];
        FakeZones zones = new(p => p.X < -100 ? "ZonePlace" : null);

        using (Assert.Multiple())
        {
            await Assert.That(new DetonationPlaceResolver(zones, places, cloud).Resolve(new Vector3(-200, 0, 0))).IsEqualTo("ZonePlace");
            await Assert.That(new DetonationPlaceResolver(zones, places, cloud).Resolve(new Vector3(0, 0, 0))).IsEqualTo("IndexPlace")
                .Because("no zone there, and the index centroid on the band is 50 units away");
            await Assert.That(new DetonationPlaceResolver(null, null, cloud).Resolve(new Vector3(10, 0, 0))).IsEqualTo("CloudPlace");
            await Assert.That(new DetonationPlaceResolver(null, places, cloud).Resolve(new Vector3(0, 0, 900))).IsNull()
                .Because("the upper floor's centroid is off the band and the cloud's points are 900 units down");
            await Assert.That(new DetonationPlaceResolver(null, null, cloud).Resolve(new Vector3(500, 0, 0))).IsNull()
                .Because("both cloud points are more than 400 units away");
            await Assert.That(new DetonationPlaceResolver(null, null, null).Resolve(Vector3.Zero)).IsNull();
        }
    }

    private static GameEvent Fire(string name, int tick, object payload) => new(name, -1, 0, tick, tick, payload);

    private sealed class FakeZones(Func<Vector3, string?> resolve) : IZonePlaceResolver
    {
        public string ZonesVersion => "test";

        public string? Resolve(Vector3 world) => resolve(world);

        public string? ResolveOnFloor(double x, double y, double floorKey) => null;

        public IReadOnlySet<string> Adjacent(string place) => new HashSet<string>();
    }
}

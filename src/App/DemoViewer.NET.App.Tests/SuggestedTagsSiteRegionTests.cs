#region

using DemoViewer.NET.Modules.SuggestedTags;
using static DemoViewer.NET.AppTests.SuggestedTagsTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Site regions (suggested-tags.md §9 step 2): the learned table with its spawn filter and its
///     thresholds, the site alone under four plants, composition in precedence order with the profile's
///     edits last, the per-map file and its store, and the profile's own round trip.
/// </summary>
public class SuggestedTagsSiteRegionTests
{
    // A round planted at A at 30 whose Ts stood in the given places from 18 to 29, crowding TRamp
    // with two players in the first ten seconds.
    private static RoundOccupancy PlantedAt(string site, params string[] approach)
    {
        Dictionary<int, string?[]> t = [];
        int[] slots = [6, 7, 8, 9, 10];
        for (int i = 0; i < slots.Length; i++)
        {
            string lane = approach.Length == 0 ? "TSpawn" : approach[i % approach.Length];
            t[slots[i]] = Path(60, "TSpawn", (0, 8, i < 2 ? "TRamp" : "TSpawn"), (18, 29, lane), (30, 59, site));
        }

        return Round(60, t, bomb: Plant(30, site));
    }

    // A round with no plant, spawning like the others.
    private static RoundOccupancy NoPlant() => Round(60, new Dictionary<int, string?[]>
    {
        [6] = Path(60, "TSpawn", (0, 8, "TRamp")),
        [7] = Path(60, "TSpawn", (0, 8, "TRamp"))
    });

    [Test]
    public async Task TheLearner_KeepsApproachPlaces_BeforeFortyPercentOfPlants_AndDropsSpawnPlaces()
    {
        // Five A plants: Long before all five, Short before two (40 percent), Cat before one (20 percent).
        // TRamp is crowded at spawn in every round and appears before every plant too.
        List<RoundOccupancy> rounds =
        [
            PlantedAt(SiteRegions.SiteA, "ALong", "TRamp"),
            PlantedAt(SiteRegions.SiteA, "ALong", "Short"),
            PlantedAt(SiteRegions.SiteA, "ALong", "Short"),
            PlantedAt(SiteRegions.SiteA, "ALong", "Cat"),
            PlantedAt(SiteRegions.SiteA, "ALong"),
            PlantedAt(SiteRegions.SiteB, "BTunnel"),
            NoPlant()
        ];

        SiteRegionTable table = SiteRegionLearner.Learn(Map, [rounds]);

        using (Assert.Multiple())
        {
            await Assert.That(table.DemoCount).IsEqualTo(1);
            await Assert.That(table.RoundCount).IsEqualTo(7);
            await Assert.That(table.Plants[SiteRegions.SiteA]).IsEqualTo(5);
            await Assert.That(table.Regions[SiteRegions.SiteA]).IsEquivalentTo([SiteRegions.SiteA, "ALong", "Short"]);
            await Assert.That(table.Regions[SiteRegions.SiteB]).IsEquivalentTo([SiteRegions.SiteB])
                .Because("one plant is under the four a site needs");
            await Assert.That(table.SpawnAdjacent).IsEquivalentTo(["TRamp", "TSpawn"]);
        }
    }

    [Test]
    public async Task WithoutTheSpawnFilter_TheSpawnPlaceWouldHaveBeenLearned()
    {
        // TRamp crowded at spawn in only two of seven rounds (under 30 percent) is an approach, not spawn.
        List<RoundOccupancy> rounds =
        [
            PlantedAt(SiteRegions.SiteA, "TRamp"),
            PlantedAt(SiteRegions.SiteA, "TRamp"),
            .. Enumerable.Range(0, 3).Select(_ => Round(60, new Dictionary<int, string?[]>
            {
                [6] = Path(60, "TSpawn", (18, 29, "TRamp"), (30, 59, SiteRegions.SiteA))
            }, bomb: Plant(30, SiteRegions.SiteA))),
            Round(60),
            Round(60)
        ];

        SiteRegionTable table = SiteRegionLearner.Learn(Map, [rounds]);

        using (Assert.Multiple())
        {
            await Assert.That(table.SpawnAdjacent).IsEquivalentTo(["TSpawn"]);
            await Assert.That(table.Regions[SiteRegions.SiteA]).Contains("TRamp");
        }
    }

    [Test]
    public async Task Compose_ShippedBeatsLearned_AndTheProfileEditsLast()
    {
        SiteRegionTable shipped = new()
        {
            Map = Map,
            Regions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [SiteRegions.SiteA] = [SiteRegions.SiteA, "Shipped"]
            }
        };
        DetectorProfile edited = DetectorProfile.Default.WithOverride(Map, SiteRegions.SiteA,
            new SiteRegionOverride(["Added"], ["ALong"]));

        SiteRegions learned = SiteRegions.Compose(Map, null, Table, DetectorProfile.Default);
        SiteRegions fromShipped = SiteRegions.Compose(Map, shipped, Table, DetectorProfile.Default);
        SiteRegions withEdit = SiteRegions.Compose(Map, null, Table, edited);
        SiteRegions nothing = SiteRegions.Compose(Map, null, null, DetectorProfile.Default);

        using (Assert.Multiple())
        {
            await Assert.That(learned.Source).IsEqualTo("learned:1");
            await Assert.That(learned.RegionOf(SiteRegions.SiteA)).IsEquivalentTo([SiteRegions.SiteA, "ALong", "ASmall"]);
            await Assert.That(learned.SpawnAdjacent).IsEquivalentTo(["TSpawn", "TRamp"]);
            await Assert.That(fromShipped.Source).IsEqualTo("shipped");
            await Assert.That(fromShipped.RegionOf(SiteRegions.SiteA)).IsEquivalentTo([SiteRegions.SiteA, "Shipped"]);
            await Assert.That(fromShipped.RegionOf(SiteRegions.SiteB)).IsEquivalentTo([SiteRegions.SiteB])
                .Because("a site the table leaves out is the site alone");
            await Assert.That(withEdit.Source).IsEqualTo("learned:1+profile");
            await Assert.That(withEdit.RegionOf(SiteRegions.SiteA)).IsEquivalentTo([SiteRegions.SiteA, "ASmall", "Added"]);
            await Assert.That(nothing.Source).IsEqualTo("site-only");
            await Assert.That(nothing.SitesContaining(SiteRegions.SiteB)).IsEquivalentTo([SiteRegions.SiteB]);
        }
    }

    [Test]
    public async Task TheShippedProfile_TakesMiddleOutOfInfernosA()
    {
        SiteRegionTable inferno = new()
        {
            Map = "de_inferno",
            Regions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [SiteRegions.SiteA] = [SiteRegions.SiteA, "Middle", "TopofMid", "Balcony"]
            }
        };

        SiteRegions regions = SiteRegions.Compose("de_inferno", null, inferno, DetectorProfile.Default);

        await Assert.That(regions.RegionOf(SiteRegions.SiteA)).IsEquivalentTo([SiteRegions.SiteA, "TopofMid", "Balcony"]);
    }

    [Test]
    public async Task TheTable_RoundTripsByteForByte()
    {
        string json = Table.ToJson();
        SiteRegionTable? back = SiteRegionTable.TryParse(json);

        using (Assert.Multiple())
        {
            await Assert.That(back).IsNotNull();
            await Assert.That(back!.ToJson()).IsEqualTo(json);
            await Assert.That(back.Regions[SiteRegions.SiteA]).IsEquivalentTo([SiteRegions.SiteA, "ALong", "ASmall"]);
            await Assert.That(SiteRegionTable.TryParse("{\"schemaVersion\": 2}")).IsNull().Because("a newer file is not read");
            await Assert.That(SiteRegionTable.TryParse("not json")).IsNull();
        }
    }

    [Test]
    public async Task TheStore_KeepsTablesInMemory_WithNoDirectory()
    {
        SiteRegionStore store = new(null);

        await Assert.That(store.TryLoad(Map)).IsNull();
        store.Save(Table);
        await Assert.That(store.TryLoad(Map)!.ToJson()).IsEqualTo(Table.ToJson());
    }

    [Test]
    public async Task TheStore_WritesOneFilePerMap_BesideTheProfile()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dv-st-" + Guid.NewGuid().ToString("N"));
        try
        {
            SiteRegionStore store = new(directory);
            store.Save(Table);
            store.Save(Table);

            string file = System.IO.Path.Combine(directory, "site-regions.de_test.json");
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(file)).IsTrue();
                await Assert.That(Directory.GetFiles(directory).Length).IsEqualTo(1).Because("the atomic write leaves no temp file");
                await Assert.That(new SiteRegionStore(directory).TryLoad(Map)!.ToJson()).IsEqualTo(Table.ToJson());
                Assert.Throws<ArgumentException>(() => SiteRegionStore.FileNameFor("../x"));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Test]
    public async Task TheShippedProfile_ReadsAsTheDesignWritesIt_AndRoundTrips()
    {
        const string design = """
            { "schemaVersion": 1, "id": "team-default",
              "order": ["execute", "default", "fake", "opener", "retake"],
              "execute": { "N": 4, "T": 4, "touch": 10, "minSecond": 3, "lead": 6, "lag": 8, "rushSecond": 25, "slowSecond": 60 },
              "default": { "defaultSecond": 40, "spreadSecond": 30, "spreadPlaces": 3, "lag": 10 },
              "fake":    { "fakeWindow": 25, "fakePresence": 2, "fakeUtility": 2, "lead": 4 },
              "opener":  { "K": 3, "W": 2, "lead": 5, "lag": 6 },
              "retake":  { "retakeGap": 6, "minGroup": 2, "lead": 5, "lag": 10 },
              "siteRegionOverrides": { "de_inferno": { "BombsiteA": { "remove": ["Middle"] } } } }
            """;

        DetectorProfile parsed = DetectorProfile.Parse(design);
        DetectorProfile back = DetectorProfile.Parse(DetectorProfile.Default.ToJson());

        using (Assert.Multiple())
        {
            await Assert.That(parsed.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson())
                .Because("the §3.7 example is the shipped profile, window mode off by default");
            await Assert.That(back.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson());
            await Assert.That(parsed.Order).IsEquivalentTo(["execute", "default", "fake", "opener", "retake"]);
            await Assert.That(parsed.Get("execute", "window")).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AProfileNamingOneValue_IsAWholeProfile_AndKeepsWhatItDoesNotKnow()
    {
        DetectorProfile profile = DetectorProfile.Parse("""{ "execute": { "N": 3, "future": 7 }, "newDetector": { "x": 1 } }""");

        using (Assert.Multiple())
        {
            await Assert.That(profile.Get("execute", "N")).IsEqualTo(3);
            await Assert.That(profile.Get("execute", "T")).IsEqualTo(4).Because("missing values are the shipped defaults");
            await Assert.That(profile.Order).IsEquivalentTo(DetectorProfile.Default.Order);
            await Assert.That(profile.ToJson()).Contains("\"future\": 7");
            await Assert.That(profile.ToJson()).Contains("\"newDetector\"");
            Assert.Throws<FormatException>(() => DetectorProfile.Parse("""{ "schemaVersion": 2 }"""));
            Assert.Throws<KeyNotFoundException>(() => profile.Get("execute", "nothing"));
        }
    }
}

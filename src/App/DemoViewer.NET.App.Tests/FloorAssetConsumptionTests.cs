#region

using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Levels;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     First increment of the app-side map-asset consumption: the viewport's
///     <see cref="FloorSplitter" /> adopts nav-derived floor bands from a baked bundle, overriding its
///     Z-histogram heuristic; the <see cref="MapAssetLoader" /> loads those bundles VRF-free and degrades
///     gracefully when absent. The real-bundle integration checks skip when the AssetBaker hasn't run.
/// </summary>
public class FloorAssetConsumptionTests
{
    private static readonly List<FloorSlice> _nukeLikeFloors = new()
    {
        new FloorSlice(-100_000, -528),
        new FloorSlice(-528, 100_000)
    };

    [Test]
    public async Task AuthoritativeFloors_OverrideHistogram_AndClassifyByZ()
    {
        FloorSplitter s = new();
        for (int i = 0; i < 100; i++)
        {
            s.Observe(0); // histogram alone would yield ONE floor
        }

        s.SetAuthoritativeFloors(_nukeLikeFloors);

        await Assert.That(s.HasAuthoritativeFloors).IsTrue();
        await Assert.That(s.Slices.Count).IsEqualTo(2); // bundle bands win over the 1-floor histogram
        await Assert.That(s.SliceIndexFor(-600)).IsEqualTo(0); // lower floor
        await Assert.That(s.SliceIndexFor(-100)).IsEqualTo(1); // upper floor
    }

    [Test]
    public async Task Reset_ClearsAuthoritativeFloors_FallsBackToHistogram()
    {
        FloorSplitter s = new();
        s.SetAuthoritativeFloors(_nukeLikeFloors);
        await Assert.That(s.Slices.Count).IsEqualTo(2);

        s.Reset();
        await Assert.That(s.HasAuthoritativeFloors).IsFalse();

        for (int i = 0; i < 100; i++)
        {
            s.Observe(0);
        }

        await Assert.That(s.Slices.Count).IsEqualTo(1); // histogram single-floor fallback restored
    }

    [Test]
    public async Task SetAuthoritativeFloors_Null_ClearsOverride()
    {
        FloorSplitter s = new();
        s.SetAuthoritativeFloors(_nukeLikeFloors);
        await Assert.That(s.HasAuthoritativeFloors).IsTrue();

        s.SetAuthoritativeFloors(null);
        await Assert.That(s.HasAuthoritativeFloors).IsFalse();
    }

    [Test]
    public async Task Loader_SyntheticBundle_ParsesFloorsAndTransform()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mapasset_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string Json = """
                                {
                                  "schemaVersion": 1,
                                  "mapName": "de_test",
                                  "mapVersion": "abcd1234",
                                  "bakerVersion": "test",
                                  "transform": { "posX": -100, "posY": 100, "scale": 5, "rotate": 0, "zoom": 1, "imageSize": 1024 },
                                  "bounds": { "minX": -100, "minY": -100, "maxX": 100, "maxY": 100 },
                                  "floors": [ { "minZ": -100000, "maxZ": -500 }, { "minZ": -500, "maxZ": 100000 } ],
                                  "radarLayers": [ { "minZ": -100000, "maxZ": 100000, "image": "de_test.png" } ],
                                  "radarImages": []
                                }
                                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "bundle.json"), Json);

            LoadedMapAsset? loaded = MapAssetLoader.TryLoadFromDirectory(dir);

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Bundle.MapName).IsEqualTo("de_test");
            await Assert.That(loaded.Floors.Count).IsEqualTo(2);
            await Assert.That(loaded.Floors[0].MaxZ).IsEqualTo(-500);
            await Assert.That(loaded.Bundle.Transform.Scale).IsEqualTo(5);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Loader_MissingOrNull_ReturnsNull()
    {
        await Assert.That(MapAssetLoader.TryLoad("does_not_exist_map_xyz")).IsNull();
        await Assert.That(MapAssetLoader.TryLoad(null)).IsNull();
        await Assert.That(MapAssetLoader.TryLoad("  ")).IsNull();
    }

    [Test]
    public async Task Integration_NukeBundle_TwoFloors_BoundaryInValley()
    {
        LoadedMapAsset? nuke = MapAssetLoader.TryLoad("de_nuke");
        if (nuke is null)
        {
            throw new SkipTestException("de_nuke bundle not baked (run tools/DemoViewer.NET.AssetBaker)");
        }

        await Assert.That(nuke.Bundle.MapName).IsEqualTo("de_nuke");
        await Assert.That(nuke.Floors.Count).IsEqualTo(2);
        // The lower/upper boundary sits in the validated player-Z valley (ZFloorValidationProbe ≈ −528).
        await Assert.That(nuke.Floors[0].MaxZ).IsGreaterThan(-600);
        await Assert.That(nuke.Floors[0].MaxZ).IsLessThan(-460);
    }

    [Test]
    public async Task Integration_Dust2Bundle_SingleFloor()
    {
        LoadedMapAsset? dust2 = MapAssetLoader.TryLoad("de_dust2");
        if (dust2 is null)
        {
            throw new SkipTestException("de_dust2 bundle not baked");
        }

        await Assert.That(dust2.Floors.Count).IsEqualTo(1);
    }
}

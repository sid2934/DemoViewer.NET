#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Zones;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The resolver source the composition root registers: a map with a baked <c>zones.json</c> resolves
///     through Zone Baking's <see cref="PlaceResolver" /> and the Tolerance Slider names the zone graph,
///     a map without one answers null so the empirical graph applies, and the user overlay is applied
///     and re-read when it is saved. Over the committed de_nuke bake, which is an asset, not a demo;
///     skipped on a checkout without it.
/// </summary>
public class ZonePlaceResolverSourceTests
{
    private const string Map = "de_nuke";

    [Test]
    public async Task MapWithZones_ResolvesThroughPlaceResolver_AndTheSliderNamesTheZoneGraph()
    {
        string bundleDir = RequireBundle();
        using TempDir overlays = new();
        AssetZonePlaceResolverSource source = new(MapAssetBundleReader.FindBundleDirectory, () => overlays.Path);
        PlaceResolver direct = ZoneAssetPipeline.TryLoad(bundleDir, null)!;

        IZonePlaceResolver? zones = source.TryGet(Map);
        await Assert.That(zones).IsNotNull();
        await Assert.That(ReferenceEquals(source.TryGet(Map), zones)).IsTrue().Because("one load per map");
        await Assert.That(ReferenceEquals(source.TryGet("DE_NUKE"), zones)).IsTrue();
        await Assert.That(zones!.ZonesVersion).IsEqualTo(direct.Zones.EffectiveVersion);

        // Every seeded area's first corner answers what the pipeline's own resolver answers, both ways
        // in: a world point and a click on the area's floor.
        ZoneArea[] seeded = [.. direct.Zones.Areas.Where(a => a.IsSeed).Take(40)];
        await Assert.That(seeded.Length).IsGreaterThan(0);
        foreach (ZoneArea area in seeded)
        {
            Vector3 world = new((float)area.Xy[0], (float)area.Xy[1], (float)area.Z);
            await Assert.That(zones.Resolve(world)).IsEqualTo(direct.Resolve(world).Name);
            await Assert.That(zones.ResolveOnFloor(world.X, world.Y, area.FloorKey))
                .IsEqualTo(direct.ResolveOnFloor(world.X, world.Y, area.FloorKey).Name);
        }

        // The graph comes over by name, and an unknown place has no neighbours.
        ZonePlace linked = direct.Zones.Places.First(p => direct.Adjacent(p.Id).Count > 0);
        string[] expected = [.. direct.Adjacent(linked.Id).Select(id => direct.Zones.Places[id].Name)];
        await Assert.That(zones.Adjacent(linked.Name).ToArray()).IsEquivalentTo(expected);
        await Assert.That(zones.Adjacent("NoSuchPlace").Count).IsEqualTo(0);

        using ToleranceSliderTests.Harness slider = new(source);
        using (Assert.Multiple())
        {
            await Assert.That(slider.Vm.Map).IsEqualTo(Map);
            await Assert.That(slider.Vm.AdjacencySource).IsEqualTo("zones");
            await Assert.That(slider.Vm.AdjacencyLine).IsEqualTo("adjacency: zones");
            await Assert.That(slider.Vm.ToleranceTip).Contains($"zones:{zones.ZonesVersion}");
        }
    }

    [Test]
    public async Task MapWithoutZones_AnswersNull_AndTheSliderFallsBackToTheEmpiricalGraph()
    {
        RequireBundle();
        using TempDir noZones = new();
        File.WriteAllText(Path.Combine(noZones.Path, "bundle.json"), "{}");
        AssetZonePlaceResolverSource source = new(
            map => string.Equals(map, Map, StringComparison.OrdinalIgnoreCase)
                ? MapAssetBundleReader.FindBundleDirectory(map)
                : noZones.Path,
            () => null);

        using (Assert.Multiple())
        {
            await Assert.That(source.TryGet("de_dust2")).IsNull().Because("the bundle has no zones.json");
            await Assert.That(source.TryGet("")).IsNull();
            await Assert.That(source.TryGet(Map)).IsNotNull().Because("no overlay directory is the baked set");
        }

        // The browser host: no bundle directory and no config root for any map.
        AssetZonePlaceResolverSource browser = new(_ => null, () => null);
        await Assert.That(browser.TryGet(Map)).IsNull();

        using ToleranceSliderTests.Harness slider = new(source);
        await Assert.That(slider.Vm.AdjacencySource).IsEqualTo("zones");
        slider.Vm.Map = "de_dust2";
        await Assert.That(slider.Vm.AdjacencySource).IsEqualTo("empirical");
        await Assert.That(slider.Vm.ToleranceTip).Contains("index:");

        using ToleranceSliderTests.Harness offline = new(browser);
        await Assert.That(offline.Vm.AdjacencySource).IsEqualTo("empirical");
    }

    [Test]
    public async Task Overlay_IsApplied_AndASavedEditIsPickedUpOnTheNextQuery()
    {
        string bundleDir = RequireBundle();
        using TempDir overlays = new();
        AssetZonePlaceResolverSource source = new(MapAssetBundleReader.FindBundleDirectory, () => overlays.Path);
        string baked = ZoneAssetPipeline.TryReadBaked(bundleDir)!.ZonesVersion;

        IZonePlaceResolver? before = source.TryGet(Map);
        await Assert.That(before).IsNotNull();
        await Assert.That(before!.ZonesVersion).IsEqualTo(baked);

        string file = Path.Combine(overlays.Path, Map + ZoneAssetPipeline.OverlaySuffix);
        File.WriteAllText(file, """
                                {
                                  "schemaVersion": 1,
                                  "mapName": "de_nuke",
                                  "zones": [
                                    { "name": "Sandwich", "floor": -512,
                                      "polygon": [ -1600, -1900, -1200, -1900, -1200, -1500, -1600, -1500 ] }
                                  ]
                                }
                                """);

        IZonePlaceResolver? after = source.TryGet(Map);
        await Assert.That(after).IsNotNull();
        await Assert.That(ReferenceEquals(after, before)).IsFalse().Because("the overlay was saved after the first load");
        ZonePlaceResolverAdapter adapter = (ZonePlaceResolverAdapter)after!;
        using (Assert.Multiple())
        {
            await Assert.That(adapter.Resolver.Zones.HasOverlay).IsTrue();
            await Assert.That(adapter.Resolver.PlaceId("Sandwich")).IsNotNull();
            await Assert.That(after!.ZonesVersion).IsNotEqualTo(baked);
            await Assert.That(after.ZonesVersion).IsEqualTo(adapter.Resolver.Zones.EffectiveVersion);
            await Assert.That(after.ResolveOnFloor(-1400, -1700, -512)).IsEqualTo("Sandwich");
        }

        await Assert.That(ReferenceEquals(source.TryGet(Map), after)).IsTrue().Because("an unchanged overlay is not re-read");

        using ToleranceSliderTests.Harness slider = new(source);
        await Assert.That(slider.Vm.ToleranceTip).Contains($"zones:{after.ZonesVersion}");
    }

    private static string RequireBundle()
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(Map);
        if (dir is null || ZoneAssetPipeline.TryReadBaked(dir) is null)
        {
            throw new SkipTestException($"no {Map} bundle with {ZoneAssetPipeline.FileName} in this checkout");
        }

        return dir;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dv-zone-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // a leftover temp directory is not a test failure
            }
        }
    }
}

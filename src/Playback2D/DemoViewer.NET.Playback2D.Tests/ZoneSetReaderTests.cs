#region

using System.IO.Compression;
using System.Numerics;
using System.Text;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The <c>zones.json</c> reader and the file probe: a synthetic file round-trips, the three ways a
///     map can have no zones all answer null, and every floor key is the quantized band minimum.
/// </summary>
public class ZoneSetReaderTests
{
    [Test]
    public async Task SyntheticFile_RoundTrips()
    {
        ZoneSet expected = ZoneFixtures.Build();
        ZoneSet actual = ZoneSetReader.Read(Encoding.UTF8.GetBytes(ZoneFixtures.Json()));

        await Assert.That(actual.MapName).IsEqualTo("de_synthetic");
        await Assert.That(actual.ZonesVersion).IsEqualTo(ZoneFixtures.ZonesVersion);
        await Assert.That(actual.EffectiveVersion).IsEqualTo(ZoneFixtures.ZonesVersion);
        await Assert.That(actual.BundleMapVersion).IsEqualTo("075a27b3");
        await Assert.That(actual.BakerVersion).IsEqualTo("0.4+test");
        await Assert.That(actual.HasOverlay).IsFalse();
        await Assert.That(actual.Floors.Count).IsEqualTo(2);
        await Assert.That(actual.Places.Select(p => p.Name).ToArray()).IsEquivalentTo(ZoneFixtures.PlaceNames);
        await Assert.That(actual.Places.All(p => p.Origin == PlaceOrigin.Baked)).IsTrue();
        await Assert.That(actual.Volumes.Count).IsEqualTo(expected.Volumes.Count);
        await Assert.That(actual.Areas.Count).IsEqualTo(expected.Areas.Count);
        await Assert.That(actual.AreaLinks.Count).IsEqualTo(expected.AreaLinks.Count);
        await Assert.That(actual.Adjacency.Count).IsEqualTo(expected.Adjacency.Count);
        await Assert.That(actual.BombRadius).IsEqualTo(650);

        ZoneVolume hut = actual.Volumes[0];
        await Assert.That(hut.Kind).IsEqualTo(ZoneVolumeKind.Place);
        await Assert.That(hut.PlaceId).IsEqualTo(1);
        await Assert.That(hut.Contains(new Vector3(250, 50, -400))).IsTrue();
        await Assert.That(hut.Contains(new Vector3(150, 50, -400))).IsFalse();
        await Assert.That(actual.Volumes[1].Site).IsEqualTo(Bombsite.A);

        ZoneArea island = actual.Areas.Single(a => a.Id == 6);
        await Assert.That(island.PlaceId).IsEqualTo(-1);
        await Assert.That(actual.Areas.Single(a => a.Id == 5).FloorKey).IsEqualTo(ZoneFixtures.Lower);
    }

    [Test]
    public async Task MissingFile_Gzip_AndMalformed_AnswerAsDesigned()
    {
        using TempDir dir = new();

        // No file at all: the answer a map baked before zones shipped gets.
        await Assert.That(ZoneAssetPipeline.TryLoad(dir.Path, null)).IsNull();
        await Assert.That(ZoneAssetPipeline.TryLoad(null, null)).IsNull();
        await Assert.That(ZoneAssetPipeline.TryLoad(Path.Combine(dir.Path, "nope"), null)).IsNull();

        // The gzipped spelling alone: read through the same reader (decision D3).
        string gz = Path.Combine(dir.Path, ZoneAssetPipeline.GzipFileName);
        await using (FileStream file = File.Create(gz))
        await using (GZipStream deflate = new(file, CompressionLevel.Fastest))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(ZoneFixtures.Json());
            await deflate.WriteAsync(bytes);
        }

        PlaceResolver? fromGz = ZoneAssetPipeline.TryLoad(dir.Path, null);
        await Assert.That(fromGz).IsNotNull();
        await Assert.That(fromGz!.Zones.Places.Count).IsEqualTo(4);

        // The plain file wins over the gzipped one when both exist, and a malformed plain file is
        // "no zones" rather than a fall-through to the other spelling: one file, one answer.
        string plain = Path.Combine(dir.Path, ZoneAssetPipeline.FileName);
        await File.WriteAllTextAsync(plain, "{ \"schemaVersion\": 1, \"mapName\": ");
        await Assert.That(ZoneAssetPipeline.TryLoad(dir.Path, null)).IsNull();

        await File.WriteAllTextAsync(plain, "{ \"schemaVersion\": 1, \"mapName\": \"x\", \"zonesVersion\": \"1\", " +
                                            "\"places\": [ { \"id\": 0, \"name\": \"A\" } ], " +
                                            "\"areas\": [ { \"id\": 1, \"place\": 7, \"floor\": 0, \"z\": 0, \"xy\": [] } ] }");
        await Assert.That(ZoneAssetPipeline.TryLoad(dir.Path, null)).IsNull();

        await File.WriteAllTextAsync(plain, ZoneFixtures.Json());
        await Assert.That(ZoneAssetPipeline.TryLoad(dir.Path, null)).IsNotNull();
    }

    [Test]
    public async Task FloorKey_IsTheQuantizedBandMinimum_OnEveryBand()
    {
        ZoneSet synthetic = ZoneSetReader.Read(Encoding.UTF8.GetBytes(ZoneFixtures.Json()));
        foreach (ZoneFloor floor in synthetic.Floors)
        {
            await Assert.That(floor.Key).IsEqualTo(MapSpace.QuantizeZ(floor.MinZ));
        }

        // And on every committed bake this checkout carries: the reader recomputes what the baker
        // wrote and the two must agree, or an annotation anchor and a zone floor would drift apart.
        int checkedMaps = 0;
        foreach (string map in new[] { "de_nuke", "de_mirage", "de_dust2", "de_inferno", "de_vertigo" })
        {
            using LoadedMapAsset? asset = MapAssetPipeline.TryLoad(map);
            if (asset is null || ZoneAssetPipeline.TryReadBaked(asset.BakedDir) is not { } baked)
            {
                continue;
            }

            checkedMaps++;
            foreach (ZoneFloor floor in baked.Floors)
            {
                await Assert.That(floor.Key).IsEqualTo(MapSpace.QuantizeZ(floor.MinZ));
            }

            foreach (ZoneArea area in baked.Areas)
            {
                ZoneFloor band = baked.Floors.First(f => f.Contains(area.Z));
                await Assert.That(area.FloorKey).IsEqualTo(band.Key);
            }

            Console.WriteLine($"[zones] {map}: {baked.Places.Count} places, {baked.Areas.Count} areas, " +
                              $"{baked.Floors.Count} floors, version {baked.ZonesVersion}");
        }

        Console.WriteLine($"[zones] committed bakes checked: {checkedMaps}");
    }

    /// <summary>
    ///     The committed nuke bake resolves its own bombsite A volume: a point at the centre of the box
    ///     the file states is inside by the volume test, both as a place and as a bombsite. Reads a
    ///     committed asset, never a demo; skipped on a checkout without the assets tree.
    /// </summary>
    [Test]
    public async Task CommittedNukeBake_ResolvesBombsiteA_ByVolume()
    {
        using LoadedMapAsset? asset = MapAssetPipeline.TryLoad("de_nuke");
        if (asset is null || ZoneAssetPipeline.TryLoad(asset.BakedDir, null) is not { } resolver)
        {
            throw new SkipTestException("no de_nuke bundle with zones.json in this checkout");
        }

        ZoneVolume site = resolver.Zones.Volumes.First(v => v.Kind == ZoneVolumeKind.Bombsite && v.Site == Bombsite.A);
        Vector3 centre = (site.Min + site.Max) / 2;

        PlaceHit hit = resolver.Resolve(centre);
        Console.WriteLine($"[zones] nuke A centre {centre} -> {hit.Name} ({hit.Kind})");
        await Assert.That(hit.Kind).IsEqualTo(PlaceHitKind.Volume);
        await Assert.That(hit.Name).IsEqualTo("BombsiteA");
        await Assert.That(resolver.BombsiteAt(centre)).IsEqualTo(Bombsite.A);
        await Assert.That(resolver.IsInside(centre, Bombsite.B)).IsFalse();
        await Assert.That(resolver.PlaceId("BombsiteA")).IsEqualTo(hit.PlaceId);
        await Assert.That(resolver.Adjacent(hit.PlaceId).Count).IsGreaterThan(0);
    }

    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dv2d-zones-" + Guid.NewGuid().ToString("N"));
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

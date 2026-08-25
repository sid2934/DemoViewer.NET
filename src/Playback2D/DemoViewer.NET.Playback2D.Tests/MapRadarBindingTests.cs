#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The three radar-binding rules, against a decision table taken from the pre-v2
///     <c>ResolveRadarImage</c> (viewport lines 1096-1115). The binder is evaluated once per level-set
///     rebuild rather than once per band per frame, so the rules had to be restated as a batch — which
///     is exactly the kind of restatement that quietly changes behaviour if nothing pins it.
/// </summary>
public class MapRadarBindingTests
{
    [Test]
    public async Task NoRadarLayers_EveryLevelGetsThePrimaryImage()
    {
        MapRadarBinder binder = new(Asset([], ["radar.png"]));
        List<SKImage?> images = [];
        List<string?> names = [];

        RadarBindingQuality quality = binder.Bind(Bands(3), images, names);

        await Assert.That(string.Join(",", names)).IsEqualTo("radar.png,radar.png,radar.png");
        await Assert.That(quality).IsEqualTo(RadarBindingQuality.Degraded);
    }

    [Test]
    public async Task NoRadarLayersAndNoImages_BindsNothing()
    {
        MapRadarBinder binder = new(Asset([], []));
        List<SKImage?> images = [];
        List<string?> names = [];

        RadarBindingQuality quality = binder.Bind(Bands(2), images, names);

        await Assert.That(quality).IsEqualTo(RadarBindingQuality.None);
        await Assert.That(names.TrueForAll(n => n is null)).IsTrue();
        await Assert.That(names.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LayerCountEqualsBandCount_IndexMatchesByAscendingZ()
    {
        // Deliberately supplied out of Z order: the pre-v2 code sorted before indexing, and a binder
        // that trusted file order would put the upper floor's picture under the lower floor.
        MapRadarBinder binder = new(Asset(
            [new RadarLayerDto(100, 400, "upper.png"), new RadarLayerDto(-400, 100, "lower.png")],
            ["upper.png", "lower.png"]));

        List<SKImage?> images = [];
        List<string?> names = [];
        RadarBindingQuality quality = binder.Bind(Bands(2), images, names);

        await Assert.That(string.Join(",", names)).IsEqualTo("lower.png,upper.png");
        await Assert.That(quality).IsEqualTo(RadarBindingQuality.Exact);
    }

    [Test]
    public async Task CountsDisagree_EveryLevelGetsTheHighestLayer_AndIsFlaggedDegraded()
    {
        MapRadarBinder binder = new(Asset(
            [new RadarLayerDto(-400, 100, "lower.png"), new RadarLayerDto(100, 400, "upper.png")],
            ["lower.png", "upper.png"]));

        List<SKImage?> images = [];
        List<string?> names = [];
        RadarBindingQuality quality = binder.Bind(Bands(3), images, names);

        await Assert.That(string.Join(",", names)).IsEqualTo("upper.png,upper.png,upper.png");
        await Assert.That(quality).IsEqualTo(RadarBindingQuality.Degraded);
    }

    [Test]
    public async Task NullAsset_BindsNothing()
    {
        MapRadarBinder binder = new(null);
        List<SKImage?> images = [];
        List<string?> names = [];

        await Assert.That(binder.Bind(Bands(2), images, names)).IsEqualTo(RadarBindingQuality.None);
        await Assert.That(images).IsEmpty();
    }

    /// <summary>
    ///     The reason the binder exists: the pre-v2 version ran <c>OrderBy</c> + <c>ToList</c> +
    ///     <c>First</c> per band per frame. Binding must be cheap enough to leave no trace, because
    ///     B3's level hysteresis will call it more often than B1 does.
    /// </summary>
    [Test]
    public async Task Bind_AllocatesNothingOnceTheDestinationListsHaveGrown()
    {
        MapRadarBinder binder = new(Asset(
            [new RadarLayerDto(-400, 100, "lower.png"), new RadarLayerDto(100, 400, "upper.png")],
            ["lower.png", "upper.png"]));

        List<SKImage?> images = [];
        List<string?> names = [];
        FloorSlice[] bands = Bands(2);
        for (int i = 0; i < 8; i++)
        {
            binder.Bind(bands, images, names);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            binder.Bind(bands, images, names);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 binds: {delta} bytes");
        await Assert.That(delta).IsEqualTo(0);
    }

    /// <summary>
    ///     The pre-v2 <c>LoadedMapAsset.Floors</c> projected and materialised a fresh list on every
    ///     property read, and the viewport read it once per push (plan §4 T15 item 7).
    /// </summary>
    [Test]
    public async Task LoadedMapAsset_Floors_IsCachedNotRebuiltPerRead()
    {
        LoadedMapAsset asset = Asset([], [],
            [new FloorBandDto(-400, -300), new FloorBandDto(100, 400)]);

        IReadOnlyList<FloorSlice> first = asset.Floors;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            _ = asset.Floors;
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 Floors reads: {delta} bytes");
        await Assert.That(ReferenceEquals(first, asset.Floors)).IsTrue();
        await Assert.That(delta).IsEqualTo(0);
        await Assert.That(first.Count).IsEqualTo(2);
    }

    private static FloorSlice[] Bands(int count)
    {
        FloorSlice[] bands = new FloorSlice[count];
        for (int i = 0; i < count; i++)
        {
            bands[i] = new FloorSlice(-400 + i * 256, -400 + i * 256 + 192);
        }

        return bands;
    }

    private static LoadedMapAsset Asset(IReadOnlyList<RadarLayerDto> layers, IReadOnlyList<string> images,
        IReadOnlyList<FloorBandDto>? floors = null) =>
        new()
        {
            Bundle = new MapAssetBundle(1, "de_test", "1", "1",
                new RadarTransform(0, 0, 1, 0, 1, 1024),
                new WorldBoundsDto(-1000, -1000, 1000, 1000),
                floors ?? [],
                layers,
                images,
                null!),
            // Fixtures carry no pixels: the binder's job is choosing NAMES, and every name here is
            // absent from the (empty) decoded map, which is itself the "bundle present, image
            // undecodable" case the pre-v2 code degraded through.
            RadarImages = new Dictionary<string, SKImage>(StringComparer.Ordinal),
            BakedDir = "."
        };
}

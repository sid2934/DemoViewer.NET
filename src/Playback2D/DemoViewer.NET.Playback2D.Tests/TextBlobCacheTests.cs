#region

using DemoViewer.NET.Playback2D.Core;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The embedded-typeface contract (integrator correction 6) and the LRU that stops the pre-v2
///     per-marker-per-frame text allocation.
/// </summary>
public class TextBlobCacheTests
{
    [Test]
    public async Task Typeface_ComesFromTheEmbeddedResource_NotTheHost()
    {
        await Assert.That(typeof(TextBlobCache).Assembly
                .GetManifestResourceNames())
            .Contains(TextBlobCache.TypefaceResourceName);

        using TextBlobCache cache = new();
        await Assert.That(cache.Typeface.FamilyName).IsEqualTo("Inter");

        // The whole point: it is NOT whatever the host would have picked.
        await Assert.That(cache.Typeface.FamilyName).IsNotEqualTo(SKTypeface.Default.FamilyName);
    }

    [Test]
    public async Task Get_SameTextAndSize_ReturnsTheSameBlobInstance()
    {
        using TextBlobCache cache = new();
        ShapedText? first = cache.Get("AB", 10f);
        ShapedText? second = cache.Get("AB", 10f);

        await Assert.That(first).IsNotNull();
        await Assert.That(ReferenceEquals(first!.Value.Blob, second!.Value.Blob)).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Get_DifferentSize_IsADifferentEntry()
    {
        using TextBlobCache cache = new();
        cache.Get("AB", 10f);
        cache.Get("AB", 11f);
        await Assert.That(cache.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Get_EmptyOrNull_ReturnsNull()
    {
        using TextBlobCache cache = new();
        await Assert.That(cache.Get("", 10f)).IsNull();
        await Assert.That(cache.Get(null, 10f)).IsNull();
    }

    [Test]
    public async Task Get_PastCapacity_EvictsLeastRecentlyUsed()
    {
        using TextBlobCache cache = new(4);
        for (int i = 0; i < 12; i++)
        {
            cache.Get("s" + i, 10f);
        }

        await Assert.That(cache.Count).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task Get_SteadyState_AllocatesNothing()
    {
        using TextBlobCache cache = new();
        for (int i = 0; i < 64; i++)
        {
            cache.Get("AB", 10f);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            cache.Get("AB", 10f);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] text cache hit: {delta} bytes over 512 gets");
        await Assert.That(delta).IsEqualTo(0);
    }

    [Test]
    public async Task OriginForTopLeft_PlacesInkTopLeftAtThePoint()
    {
        using TextBlobCache cache = new();
        ShapedText shaped = cache.Get("floor 0", 11f)!.Value;
        (float x, float y) = shaped.OriginForTopLeft(8f, 6f);

        // Skia positions a blob by its BASELINE; the pre-v2 control positioned by the ink top-left.
        await Assert.That(x + shaped.Bounds.Left).IsEqualTo(8f).Within(0.001f);
        await Assert.That(y + shaped.Bounds.Top).IsEqualTo(6f).Within(0.001f);
    }
}

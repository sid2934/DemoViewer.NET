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
    [Category("Budget")]
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

    /// <summary>
    ///     <b>The measurement the port originally got wrong.</b> <c>SKTextBlob.Bounds</c> is computed
    ///     conservatively from the font's global glyph box, not from the run: its <c>Left</c> is the same
    ///     large negative number for every string at a size, its <c>Top</c>/<c>Bottom</c> are exactly the
    ///     font metrics' <c>Top</c>/<c>Bottom</c>, and its width is several times the real ink. Treating
    ///     it as tight ink is what put marker initials ~6 px left of their discs.
    /// </summary>
    [Test]
    public async Task Bounds_AreTightInk_NotTheBlobsConservativeBox()
    {
        using TextBlobCache cache = new();
        ShapedText narrow = cache.Get("7", 10f)!.Value;
        ShapedText wide = cache.Get("WW", 10f)!.Value;

        Console.WriteLine($"[text] \"7\"  ink={narrow.Bounds} advance={narrow.Advance:F3} " +
                          $"blob={narrow.Blob.Bounds}");
        Console.WriteLine($"[text] \"WW\" ink={wide.Bounds} advance={wide.Advance:F3} " +
                          $"blob={wide.Blob.Bounds}");

        // The blob's own box is the same for both strings on the left and top edges — that is the
        // tell that it never looked at the glyphs. The tight ink is not.
        await Assert.That(narrow.Blob.Bounds.Left).IsEqualTo(wide.Blob.Bounds.Left).Within(0.001f);
        await Assert.That(narrow.Bounds.Left).IsGreaterThan(narrow.Blob.Bounds.Left);

        // Tight ink never runs wider than the advance by more than a side bearing, whereas the blob
        // box is multiples of it.
        await Assert.That(narrow.Bounds.Width).IsLessThan(narrow.Advance + 2f);
        await Assert.That(narrow.Blob.Bounds.Width).IsGreaterThan(narrow.Advance * 2f);

        // And a narrower string really does measure narrower, which the blob box also gets right but
        // only because the run's advance leaks into its right edge.
        await Assert.That(narrow.Bounds.Width).IsLessThan(wide.Bounds.Width);
    }

    /// <summary>
    ///     A string past the miss path's stack buffer measures the same way. A kill-feed row with two
    ///     long names and every modifier glyph really can run past 64 characters, and the heap
    ///     fallback is the one branch nothing else exercises.
    /// </summary>
    [Test]
    public async Task Get_StringPastTheStackBuffer_MeasuresTheSameWay()
    {
        using TextBlobCache cache = new();
        const string longRow =
            "a_very_long_player_name +another_long_assister  awp HS WB NS  →  yet_another_victim";
        await Assert.That(longRow.Length).IsGreaterThan(64);

        ShapedText shaped = cache.Get(longRow, 14f)!.Value;
        ShapedText half = cache.Get(longRow[..40], 14f)!.Value;

        Console.WriteLine($"[text] {longRow.Length} chars: advance={shaped.Advance:F3} " +
                          $"ink width={shaped.Bounds.Width:F3}");

        await Assert.That(shaped.Advance).IsGreaterThan(half.Advance);
        await Assert.That(shaped.Bounds.Width).IsLessThan(shaped.Advance + 4f);
        await Assert.That(shaped.Height).IsEqualTo(half.Height).Within(0.001f);
    }

    /// <summary>
    ///     <c>Width</c> is the advance and <c>Height</c> is one line box: the two numbers a caller
    ///     sizing a panel or stacking rows actually means. Line height is a property of the font at a
    ///     size, so it must not vary with the string.
    /// </summary>
    [Test]
    public async Task WidthIsTheAdvance_AndHeightIsTheLineBox()
    {
        using TextBlobCache cache = new();
        ShapedText plain = cache.Get("Round 1", 14f)!.Value;
        ShapedText descender = cache.Get("Round pg", 14f)!.Value;

        await Assert.That(plain.Width).IsEqualTo(plain.Advance).Within(0.001f);
        await Assert.That(plain.Height).IsEqualTo(plain.Descent - plain.Ascent).Within(0.001f);

        // A descender changes the ink and must not change the line height a row layout is built on.
        Console.WriteLine($"[text] line box plain={plain.Height:F3} descender={descender.Height:F3} " +
                          $"ink height plain={plain.Bounds.Height:F3} descender={descender.Bounds.Height:F3}");
        await Assert.That(descender.Height).IsEqualTo(plain.Height).Within(0.001f);
        await Assert.That(descender.Bounds.Height).IsGreaterThan(plain.Bounds.Height);
    }

    /// <summary>
    ///     <c>OriginForTopLeft</c> places the text's <b>line box</b> top-left, not its ink top-left —
    ///     the same thing the pre-v2 <c>DrawingContext.DrawText(text, point)</c> placed. Its three HUD
    ///     callers are laying out rows and panels, and a row whose inset moved with whichever glyph
    ///     happened to start it would not be a layout.
    /// </summary>
    [Test]
    public async Task OriginForTopLeft_PlacesTheLineBoxTopLeftAtThePoint()
    {
        using TextBlobCache cache = new();
        ShapedText shaped = cache.Get("floor 0", 11f)!.Value;
        (float x, float y) = shaped.OriginForTopLeft(8f, 6f);

        // Skia positions a blob by its BASELINE; the line box's top sits Ascent above it (Ascent < 0).
        await Assert.That(x).IsEqualTo(8f).Within(0.001f);
        await Assert.That(y + shaped.Ascent).IsEqualTo(6f).Within(0.001f);

        // The ink lands inside that box rather than on its corner — a left side bearing is real.
        await Assert.That(x + shaped.Bounds.Left).IsGreaterThanOrEqualTo(8f);
        await Assert.That(y + shaped.Bounds.Top).IsGreaterThanOrEqualTo(6f);

        // Two strings at one size share a baseline. That is the whole point of measuring vertically
        // from the font rather than from each string's own ink.
        ShapedText other = cache.Get("floor 12  z[-352..-128]", 11f)!.Value;
        await Assert.That(other.OriginForTopLeft(8f, 6f).Y).IsEqualTo(y).Within(0.001f);
    }

    /// <summary>
    ///     <c>OriginForCentre</c> centres the <b>advance</b> horizontally and the <b>font's line box</b>
    ///     vertically — exactly what the pre-v2 control's
    ///     <c>Point(cx - text.Width / 2, cy - text.Height / 2)</c> did, since Avalonia's
    ///     <c>FormattedText.Width</c> is an advance and its <c>Height</c> is a line height.
    /// </summary>
    [Test]
    [Arguments("AA")]
    [Arguments("WW")]
    [Arguments("7")]
    [Arguments("10")]
    public async Task OriginForCentre_PutsTheInkOnThePoint(string label)
    {
        using TextBlobCache cache = new();
        ShapedText shaped = cache.Get(label, 10f)!.Value;
        (float x, float y) = shaped.OriginForCentre(100f, 100f);

        float inkCentreX = x + shaped.Bounds.MidX;
        float inkCentreY = y + shaped.Bounds.MidY;
        Console.WriteLine($"[text] \"{label}\" centred on (100,100): ink centre " +
                          $"({inkCentreX:F3},{inkCentreY:F3})");

        // Well inside a pixel on both axes, against a 9 px marker disc. The old blob-bounds maths put
        // this 4.2-6.2 px to the left depending on the string.
        await Assert.That(inkCentreX).IsEqualTo(100f).Within(1f);
        await Assert.That(inkCentreY).IsEqualTo(100f).Within(1f);

        // Vertically every label shares one baseline, so a scene's initials sit on a line rather than
        // each drifting to centre its own ink.
        await Assert.That(y).IsEqualTo(100f - (shaped.Ascent + shaped.Descent) / 2f).Within(0.001f);
    }
}

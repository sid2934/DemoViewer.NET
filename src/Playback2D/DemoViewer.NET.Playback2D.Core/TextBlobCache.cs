#region

using System.Reflection;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Shaped, measured, reusable text. The pre-v2 control built one Avalonia <c>FormattedText</c> per
///     marker per frame and one per floor band — ten-plus shaping passes and ten-plus allocations every
///     frame, for strings that change only when the roster does (plan §4 T15 items 1).
///     <para>
///         <b>The typeface is embedded, never resolved from the host</b> (integrator correction 6).
///         <c>SKTypeface.Default</c> is the platform UI font, so the same scene rasterises differently on
///         a developer's Windows box and on the ubuntu golden lane and every text-bearing golden becomes
///         machine-specific. One <c>Inter-Regular.ttf</c> ships inside this assembly instead — see
///         <c>THIRD-PARTY-NOTICES.md</c> §d and <c>docs/playback2d-v2/plans/B1-skia-api-notes.md</c> §3.
///     </para>
///     <para>
///         <b>Not thread-safe by design.</b> <see cref="Get" /> both reads and MUTATES — a miss shapes,
///         measures, files and evicts — and every layer calls it from <c>Render</c>, never from
///         <c>Advance</c>: the strings a layer draws are composed in <c>Render</c>, from the snapshot
///         <c>Advance</c> captured. So the discipline that makes this safe is the host's render gate,
///         which serialises <c>Render</c>, and nothing else. (The doc here used to say entries were built
///         during <c>Advance</c>; they never were, and stating the wrong invariant is worse than stating
///         none, because the next reader adds a call on the strength of it — D6, wave 3.) A cache that
///         locked would be lying about where mutation happens.
///     </para>
/// </summary>
public sealed class TextBlobCache : IDisposable
{
    /// <summary>The manifest name of the embedded face. Public so a test can assert it is present.</summary>
    public const string TypefaceResourceName = "DemoViewer.NET.Playback2D.Core.Assets.Inter-Regular.ttf";

    /// <summary>Default LRU capacity — far above the ~12 live strings a scene actually draws.</summary>
    public const int DefaultCapacity = 512;

    // Glyph ids for a miss are measured out of a stack buffer. Every string this cache is ever asked
    // for is a marker's initials, a floor caption, a scoreboard line or a kill-feed row; 64 covers all
    // of them with room to spare, and anything longer takes one array on the MISS path rather than
    // risking a variable-length stackalloc on a caller-supplied length.
    private const int MaxStackGlyphs = 64;

    // Insertion-ordered map doubling as the LRU: a hit re-inserts at the tail. A LinkedList<T> would
    // save the re-insert but costs one node allocation per entry, which is the thing being removed.
    private readonly Dictionary<Key, Entry> _entries;
    private readonly Dictionary<float, SKFont> _fonts = new(2);
    private readonly List<Key> _order;

    // The manifest name to load, and the face to borrow if it is not there. Fields rather than the
    // constants they normally hold so the missing-resource branch is testable WITHOUT the test having to
    // dispose SKTypeface.Default to find out whether this class would have: a suite that proved the bug
    // by destroying the process-wide singleton would report it as every other text test failing.
    private readonly SKTypeface _fallbackTypeface;
    private readonly string _typefaceResourceName;

    private bool _disposed;
    private SKTypeface? _typeface;

    // Whether _typeface is ours to dispose. False on the missing-resource fallback — see Dispose.
    private bool _ownsTypeface;

    /// <summary>Creates a cache with a bounded LRU.</summary>
    /// <param name="capacity">Maximum live blobs; the least recently used is evicted past it.</param>
    public TextBlobCache(int capacity = DefaultCapacity)
        : this(capacity, TypefaceResourceName, SKTypeface.Default)
    {
    }

    /// <summary>Creates a cache with a substituted typeface source. Test seam; see the fields it sets.</summary>
    /// <param name="capacity">Maximum live blobs.</param>
    /// <param name="typefaceResourceName">Manifest resource to load the face from.</param>
    /// <param name="fallbackTypeface">The face to borrow — never dispose — when that resource is absent.</param>
    internal TextBlobCache(int capacity, string typefaceResourceName, SKTypeface fallbackTypeface)
    {
        Capacity = Math.Max(1, capacity);
        _entries = new Dictionary<Key, Entry>(Math.Min(Capacity, 64));
        _order = new List<Key>(Math.Min(Capacity, 64));
        _typefaceResourceName = typefaceResourceName;
        _fallbackTypeface = fallbackTypeface;
    }

    /// <summary>Maximum live blobs.</summary>
    public int Capacity { get; }

    /// <summary>Live entry count. Test hook for the eviction case.</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     The embedded face, loaded on first use. Never null in a correctly built assembly; falls back
    ///     to <see cref="SKTypeface.Default" /> only if the manifest resource is missing, which is a
    ///     packaging bug the architecture test catches rather than a runtime condition to design around.
    /// </summary>
    public SKTypeface Typeface => _typeface ??= LoadEmbeddedTypeface();

    /// <summary>
    ///     True when <see cref="Typeface" /> is the embedded face this cache loaded and therefore owns.
    ///     False when the manifest resource was missing and the process-wide
    ///     <see cref="SKTypeface.Default" /> was substituted. Public so the packaging test can assert the
    ///     embedded face is the one actually in use, rather than asserting the resource exists and
    ///     hoping.
    /// </summary>
    public bool OwnsTypeface
    {
        get
        {
            _ = Typeface; // the answer is only meaningful once the load has been attempted
            return _ownsTypeface;
        }
    }

    /// <summary>
    ///     Releases every cached blob, every sized font, and the typeface <b>this cache owns</b>.
    ///     Idempotent.
    ///     <para>
    ///         The ownership test is the whole point: <see cref="LoadEmbeddedTypeface" /> falls back to
    ///         <see cref="SKTypeface.Default" />, which is a process-wide Skia singleton, and several
    ///         caches exist at once (every layer builds its own when no shared one is passed). Disposing
    ///         it here unref'd the singleton once per cache and killed text rendering for the whole
    ///         process — from a packaging fault whose only intended cost was the wrong font (D6 finding
    ///         23).
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Entry entry in _entries.Values)
        {
            entry.Blob.Dispose();
        }

        _entries.Clear();
        _order.Clear();

        foreach (SKFont font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
        if (_ownsTypeface)
        {
            _typeface?.Dispose();
        }

        _typeface = null;
        _ownsTypeface = false;
    }

    /// <summary>
    ///     The shaped blob for this string at this size, its tight ink bounds, its advance width and the
    ///     font's vertical metrics. Returns null for an empty string (Skia returns a null blob, and a
    ///     caller that draws nothing must not crash).
    ///     <para>
    ///         The returned blob is owned by the cache and is valid until it is evicted or the cache is
    ///         disposed — never dispose it at a call site.
    ///     </para>
    ///     <para>
    ///         <b>Everything is measured on the miss path.</b> A hit copies five values out of the entry
    ///         and touches the LRU; it allocates nothing, which
    ///         <c>TextBlobCacheTests.Get_SteadyState_AllocatesNothing</c> and the full-scene budget gate
    ///         both assert as a literal zero.
    ///     </para>
    /// </summary>
    /// <param name="text">The string to shape.</param>
    /// <param name="sizePx">Em size in device-independent pixels.</param>
    public ShapedText? Get(string? text, float sizePx)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        Key key = new(text, sizePx);
        if (_entries.TryGetValue(key, out Entry hit))
        {
            Touch(key);
            return hit.Shaped;
        }

        SKFont font = FontFor(sizePx);
        SKTextBlob? blob = SKTextBlob.Create(text, font);
        if (blob is null)
        {
            return null;
        }

        Entry entry = Measure(blob, font, text);
        Evict();
        _entries[key] = entry;
        _order.Add(key);
        return entry.Shaped;
    }

    /// <summary>
    ///     Measures one freshly shaped run.
    ///     <para>
    ///         <b>Not <c>SKTextBlob.Bounds</c>.</b> Skia computes a blob's bounds
    ///         <i>conservatively</i>, from the font's global glyph bounding box rather than from the
    ///         glyphs actually in the run: <c>Left</c> is the same large negative number for every
    ///         string, <c>Top</c>/<c>Bottom</c> are exactly <c>SKFontMetrics.Top</c>/<c>Bottom</c>, and
    ///         the width runs several times the real ink. Centring on that rect is correct arithmetic
    ///         over the wrong rectangle, which is what put marker initials ~6 px left of their disc.
    ///     </para>
    ///     <para>
    ///         <c>SKFont.MeasureText(ReadOnlySpan&lt;ushort&gt;, out SKRect)</c> <b>does</b> exist in
    ///         2.88.9 — it takes glyph ids rather than a string, which is the only reason the port
    ///         originally believed it did not — and returns the run's advance plus its tight ink box.
    ///         See <c>docs/playback2d-v2/plans/B1-skia-api-notes.md</c> §Text.
    ///     </para>
    /// </summary>
    /// <param name="blob">The shaped run to file.</param>
    /// <param name="font">The sized font it was shaped with.</param>
    /// <param name="text">The source string, re-encoded to glyph ids for measurement.</param>
    private static Entry Measure(SKTextBlob blob, SKFont font, string text)
    {
        SKFontMetrics metrics = font.Metrics;
        int count = font.CountGlyphs(text);
        if (count <= 0)
        {
            return new Entry(blob, SKRect.Empty, 0f, metrics.Ascent, metrics.Descent);
        }

        if (count > MaxStackGlyphs)
        {
            ushort[] heap = new ushort[count];
            font.GetGlyphs(text, heap);
            float wideAdvance = font.MeasureText(heap, out SKRect wideInk);
            return new Entry(blob, wideInk, wideAdvance, metrics.Ascent, metrics.Descent);
        }

        Span<ushort> buffer = stackalloc ushort[MaxStackGlyphs];
        Span<ushort> glyphs = buffer[..count];
        font.GetGlyphs(text, glyphs);
        float advance = font.MeasureText(glyphs, out SKRect ink);
        return new Entry(blob, ink, advance, metrics.Ascent, metrics.Descent);
    }

    /// <summary>Drops every cached blob but keeps the typeface and sized fonts. For a theme/DPI change.</summary>
    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Blob.Dispose();
        }

        _entries.Clear();
        _order.Clear();
    }

    private SKFont FontFor(float sizePx)
    {
        if (_fonts.TryGetValue(sizePx, out SKFont? font))
        {
            return font;
        }

        font = new SKFont(Typeface, sizePx)
        {
            // Subpixel + hinting are pinned rather than left at whatever the host default is: they
            // change rasterisation, and a golden that depends on an unstated default is not a golden.
            Subpixel = true,
            Hinting = SKFontHinting.None,
            Edging = SKFontEdging.Antialias
        };
        _fonts[sizePx] = font;
        return font;
    }

    private void Touch(Key key)
    {
        int index = _order.IndexOf(key);
        if (index < 0 || index == _order.Count - 1)
        {
            return;
        }

        _order.RemoveAt(index);
        _order.Add(key);
    }

    private void Evict()
    {
        while (_entries.Count >= Capacity && _order.Count > 0)
        {
            Key oldest = _order[0];
            _order.RemoveAt(0);
            if (_entries.Remove(oldest, out Entry entry))
            {
                entry.Blob.Dispose();
            }
        }
    }

    // Sets _ownsTypeface as it decides, because "did we load it or borrow it" is knowable exactly here
    // and nowhere else afterwards — SKTypeface.Default is not reference-comparable in any way worth
    // relying on, and re-deriving the answer at Dispose time would be re-deciding it.
    private SKTypeface LoadEmbeddedTypeface()
    {
        Stream? stream = typeof(TextBlobCache).Assembly
            .GetManifestResourceStream(_typefaceResourceName);
        if (stream is null)
        {
            _ownsTypeface = false;
            return _fallbackTypeface;
        }

        using (stream)
        {
            // SKTypeface.FromStream copies into Skia-owned memory, so the stream can close here.
            SKTypeface? loaded = SKTypeface.FromStream(stream);
            _ownsTypeface = loaded is not null;
            return loaded ?? _fallbackTypeface;
        }
    }

    private readonly record struct Key(string Text, float SizePx);

    private readonly record struct Entry(
        SKTextBlob Blob, SKRect Ink, float Advance, float Ascent, float Descent)
    {
        /// <summary>The value handed to a caller. A struct copy — no allocation on the hit path.</summary>
        public ShapedText Shaped => new(Blob, Ink, Advance, Ascent, Descent);
    }
}

/// <summary>
///     A cached blob with everything needed to place it: its tight ink box, its advance width, and the
///     font's vertical metrics. A value type so the hot path hands back no allocation; the blob itself
///     is owned by the <see cref="TextBlobCache" />.
///     <para>
///         <b>Why three measurements and not one rectangle.</b> Text is positioned two different ways
///         and they want two different numbers. Horizontal placement uses the <b>advance</b>, so a
///         string's position does not jitter with which glyphs happen to have side bearings — "AA" and
///         "WW" centre the same way. Vertical placement uses the font's <b>metrics</b>, so every label
///         in a scene shares one baseline instead of each one centring its own ink (which would put
///         "7" and "g" on visibly different lines). The ink box is kept for the one job it is right
///         for: asking where the drawn pixels actually landed.
///     </para>
/// </summary>
/// <param name="Blob">The shaped run, valid until evicted.</param>
/// <param name="Bounds">
///     <b>Tight</b> ink bounds relative to the blob's baseline origin — from
///     <c>SKFont.MeasureText</c>, not from <c>SKTextBlob.Bounds</c>, which is a conservative
///     font-wide box. <c>Bounds.Top</c> is negative (above the baseline).
/// </param>
/// <param name="Advance">The run's advance width: where the next glyph would start.</param>
/// <param name="Ascent">The font's ascent, negative (above the baseline).</param>
/// <param name="Descent">The font's descent, positive (below the baseline).</param>
public readonly record struct ShapedText(
    SKTextBlob Blob, SKRect Bounds, float Advance, float Ascent, float Descent)
{
    /// <summary>
    ///     Layout width in pixels — the <b>advance</b>, which is what a caller sizing a panel or
    ///     right-aligning a row means by "how wide is this text".
    /// </summary>
    public float Width => Advance;

    /// <summary>
    ///     Layout height in pixels — one line box (<c>Descent - Ascent</c>), which is what a caller
    ///     stacking rows means by "how tall is this text". Constant for a given size, so rows do not
    ///     shift depending on whether a line happens to contain a descender.
    /// </summary>
    public float Height => Descent - Ascent;

    /// <summary>
    ///     The baseline origin that puts the text's <b>line box</b> top-left at <paramref name="x" />,
    ///     <paramref name="y" /> — the conversion from the pre-v2
    ///     <c>DrawingContext.DrawText(text, point)</c> positioning, which also positioned a line box and
    ///     not an ink box, to Skia's baseline positioning.
    /// </summary>
    /// <param name="x">Desired left edge of the advance box.</param>
    /// <param name="y">Desired top edge of the line box.</param>
    public (float X, float Y) OriginForTopLeft(float x, float y) => (x, y - Ascent);

    /// <summary>
    ///     The baseline origin that centres the text on a point: the advance box horizontally, the
    ///     font's line box vertically. Exactly reproduces the pre-v2 control's
    ///     <c>Point(cx - text.Width / 2, cy - text.Height / 2)</c>, whose <c>Width</c> was an advance
    ///     and whose <c>Height</c> was a line height.
    /// </summary>
    /// <param name="cx">Centre X.</param>
    /// <param name="cy">Centre Y.</param>
    public (float X, float Y) OriginForCentre(float cx, float cy) =>
        (cx - (Advance / 2f), cy - ((Ascent + Descent) / 2f));
}

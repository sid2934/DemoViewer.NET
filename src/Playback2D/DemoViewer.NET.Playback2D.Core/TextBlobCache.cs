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
///         <b>Not thread-safe by design.</b> Entries are built during <c>Advance</c> on the UI thread and
///         only read during <c>Render</c> inside the host's gate, which is the same discipline every layer
///         cache follows. A cache that locked would be lying about where mutation happens.
///     </para>
/// </summary>
public sealed class TextBlobCache : IDisposable
{
    /// <summary>The manifest name of the embedded face. Public so a test can assert it is present.</summary>
    public const string TypefaceResourceName = "DemoViewer.NET.Playback2D.Core.Assets.Inter-Regular.ttf";

    /// <summary>Default LRU capacity — far above the ~12 live strings a scene actually draws.</summary>
    public const int DefaultCapacity = 512;

    // Insertion-ordered map doubling as the LRU: a hit re-inserts at the tail. A LinkedList<T> would
    // save the re-insert but costs one node allocation per entry, which is the thing being removed.
    private readonly Dictionary<Key, Entry> _entries;
    private readonly Dictionary<float, SKFont> _fonts = new(2);
    private readonly List<Key> _order;
    private bool _disposed;
    private SKTypeface? _typeface;

    /// <summary>Creates a cache with a bounded LRU.</summary>
    /// <param name="capacity">Maximum live blobs; the least recently used is evicted past it.</param>
    public TextBlobCache(int capacity = DefaultCapacity)
    {
        Capacity = Math.Max(1, capacity);
        _entries = new Dictionary<Key, Entry>(Math.Min(Capacity, 64));
        _order = new List<Key>(Math.Min(Capacity, 64));
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

    /// <summary>Releases every cached blob, every sized font, and the typeface. Idempotent.</summary>
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
        _typeface?.Dispose();
        _typeface = null;
    }

    /// <summary>
    ///     The shaped blob for this string at this size, and its measured bounds. Returns null for an
    ///     empty string (Skia returns a null blob, and a caller that draws nothing must not crash).
    ///     <para>
    ///         The returned blob is owned by the cache and is valid until it is evicted or the cache is
    ///         disposed — never dispose it at a call site.
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
            return new ShapedText(hit.Blob, hit.Bounds);
        }

        SKFont font = FontFor(sizePx);
        SKTextBlob? blob = SKTextBlob.Create(text, font);
        if (blob is null)
        {
            return null;
        }

        Evict();
        _entries[key] = new Entry(blob, blob.Bounds);
        _order.Add(key);
        return new ShapedText(blob, blob.Bounds);
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

    private static SKTypeface LoadEmbeddedTypeface()
    {
        Stream? stream = typeof(TextBlobCache).Assembly
            .GetManifestResourceStream(TypefaceResourceName);
        if (stream is null)
        {
            return SKTypeface.Default;
        }

        using (stream)
        {
            // SKTypeface.FromStream copies into Skia-owned memory, so the stream can close here.
            return SKTypeface.FromStream(stream) ?? SKTypeface.Default;
        }
    }

    private readonly record struct Key(string Text, float SizePx);

    private readonly record struct Entry(SKTextBlob Blob, SKRect Bounds);
}

/// <summary>
///     A cached blob and the bounds it measured to. A value type so the hot path hands back no
///     allocation; the blob itself is owned by the <see cref="TextBlobCache" />.
/// </summary>
/// <param name="Blob">The shaped run, valid until evicted.</param>
/// <param name="Bounds">
///     Tight ink bounds relative to the blob's baseline origin. <c>Bounds.Top</c> is negative
///     (above the baseline), which is what converts a top-left draw position into Skia's baseline one.
/// </param>
public readonly record struct ShapedText(SKTextBlob Blob, SKRect Bounds)
{
    /// <summary>Ink width in pixels.</summary>
    public float Width => Bounds.Width;

    /// <summary>Ink height in pixels.</summary>
    public float Height => Bounds.Height;

    /// <summary>
    ///     The baseline origin that puts the ink's top-left corner at <paramref name="x" />,
    ///     <paramref name="y" /> — the conversion from the pre-v2 <c>DrawingContext.DrawText(text, point)</c>
    ///     positioning to Skia's baseline positioning.
    /// </summary>
    /// <param name="x">Desired ink left edge.</param>
    /// <param name="y">Desired ink top edge.</param>
    public (float X, float Y) OriginForTopLeft(float x, float y) => (x - Bounds.Left, y - Bounds.Top);

    /// <summary>The baseline origin that centres the ink on a point.</summary>
    /// <param name="cx">Centre X.</param>
    /// <param name="cy">Centre Y.</param>
    public (float X, float Y) OriginForCentre(float cx, float cy) =>
        (cx - Bounds.MidX, cy - Bounds.MidY);
}

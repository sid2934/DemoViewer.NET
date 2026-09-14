#region

using System.Collections.Concurrent;
using DemoViewer.NET.GameIcons;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Hud;

/// <summary>
///     Serves the baked CS2 artwork to Core's HUD layers as decoded <see cref="SKImage" />s.
///     <para>
///         <b>This is the half Core cannot own.</b> Core states <see cref="IIconSource" /> and nothing
///         more, because its architecture contract forbids referencing the assembly the artwork lives in.
///         Pipeline can hold both, so the adapter lives here and both heads — the on-screen compositor
///         and the headless export — get the same one.
///     </para>
/// </summary>
public sealed class SkiaIconSource : IIconSource, IDisposable
{
    // Keyed on a value tuple, NOT an interpolated string. Lookup runs twice per icon per row per
    // frame (measure, then draw), and composing a cache key there put ~4 KB/frame on the heap in a
    // layer whose budget is zero. A ValueTuple key costs nothing to build and nothing to compare.
    private readonly ConcurrentDictionary<(string Key, int Scale), SKImage?> _cache = new();
    private bool _disposed;

    /// <summary>
    ///     The artwork for a key at a draw height, or null when CS2 ships none. Decoded once per
    ///     (key, baked scale) and kept: a feed redraws the same handful of icons every frame, and the
    ///     whole catalogue decoded is well under a megabyte.
    /// </summary>
    /// <param name="key">A namespace-qualified key, e.g. <c>equipment/ak47</c>.</param>
    /// <param name="pixelHeight">The height the icon will be drawn at.</param>
    public SKImage? Lookup(string key, float pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Demand, not Get: an export that asks for a key the bake lacks should register the miss the
        // same way the on-screen path does.
        if (IconCatalogue.Demand(key, out _) is null)
        {
            return null;
        }

        int scale = IconCatalogue.ScaleFor(pixelHeight);
        return _cache.GetOrAdd((key, scale), static entry =>
        {
            IconRef found = IconCatalogue.Get(entry.Key)!;
            using SKData data = SKData.CreateCopy(found.Bytes(entry.Scale));
            return SKImage.FromEncodedData(data);
        });
    }

    /// <inheritdoc />
    public bool IsBlankByDesign(string key) => IconCatalogue.IsBlank(key);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SKImage? image in _cache.Values)
        {
            image?.Dispose();
        }

        _cache.Clear();
    }
}

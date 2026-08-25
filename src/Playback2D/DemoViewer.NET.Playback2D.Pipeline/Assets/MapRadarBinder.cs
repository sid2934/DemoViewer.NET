#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>
///     Decides which baked radar image belongs to which floor band. Replaces the pre-v2
///     <c>Playback2DViewport.ResolveRadarImage</c> (lines 1096-1115), and is evaluated <b>once per
///     level-set rebuild</b> instead of once per band per frame — the old version ran an
///     <c>OrderBy</c> + <c>ToList</c> + <c>First</c> inside the render loop (plan §4 T15 item 6).
///     <para>
///         The three rules are the pre-v2 decisions, preserved exactly, plus one addition: when the
///         counts disagree the binding is reported <see cref="RadarBindingQuality.Degraded" /> so the UI
///         can say "no radar for this level" rather than silently showing the upper floor's picture on
///         the lower floor and letting the user conclude the alignment is broken.
///     </para>
/// </summary>
public sealed class MapRadarBinder : ILevelRadarBinder
{
    private readonly LoadedMapAsset? _asset;
    private readonly List<RadarLayerDto> _ascending = [];

    /// <summary>Creates a binder over a loaded bundle. A null asset binds nothing.</summary>
    /// <param name="asset">The loaded bundle, or null.</param>
    public MapRadarBinder(LoadedMapAsset? asset)
    {
        _asset = asset;
        if (asset is null)
        {
            return;
        }

        // Sorted ONCE here, not per band per frame. Insertion sort over ≤4 layers; the pre-v2 LINQ
        // allocated a comparer, an enumerator and a list on every band of every frame.
        _ascending.AddRange(asset.Bundle.RadarLayers);
        _ascending.Sort(static (a, b) => a.MinZ.CompareTo(b.MinZ));
    }

    /// <inheritdoc />
    public RadarBindingQuality Bind(IReadOnlyList<FloorSlice> bands, List<SKImage?> images, List<string?> names)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(names);

        images.Clear();
        names.Clear();

        if (_asset is null)
        {
            return RadarBindingQuality.None;
        }

        // Rule 1 — no layer metadata at all: every level gets the primary image, or none. (1100-1101)
        if (_ascending.Count == 0)
        {
            string? primary = _asset.Bundle.RadarImages is { Count: > 0 } all ? all[0] : null;
            Fill(bands.Count, primary, images, names);
            return primary is null ? RadarBindingQuality.None : RadarBindingQuality.Degraded;
        }

        // Rule 2 — one layer per band: index-match by ascending Z. (1107-1111)
        if (_ascending.Count == bands.Count)
        {
            for (int i = 0; i < bands.Count; i++)
            {
                string image = _ascending[i].Image;
                images.Add(Resolve(image));
                names.Add(image);
            }

            return RadarBindingQuality.Exact;
        }

        // Rule 3 — counts disagree: every level shows the highest-altitude image, and says so. (1114)
        string top = _ascending[^1].Image;
        Fill(bands.Count, top, images, names);
        return RadarBindingQuality.Degraded;
    }

    private void Fill(int count, string? image, List<SKImage?> images, List<string?> names)
    {
        SKImage? resolved = Resolve(image);
        for (int i = 0; i < count; i++)
        {
            images.Add(resolved);
            names.Add(image);
        }
    }

    private SKImage? Resolve(string? image) =>
        image is not null && _asset is not null && _asset.RadarImages.TryGetValue(image, out SKImage? decoded)
            ? decoded
            : null;
}

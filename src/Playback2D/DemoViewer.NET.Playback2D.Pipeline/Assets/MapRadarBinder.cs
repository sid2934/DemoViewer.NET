#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>
///     Decides which baked radar image belongs to which floor band. Replaces the pre-v2
///     <c>Playback2DViewport.ResolveRadarImage</c> (lines 1096-1115), and is evaluated
///     <b>
///         once per
///         level-set rebuild
///     </b>
///     instead of once per band per frame: the old version ran an
///     <c>OrderBy</c> + <c>ToList</c> + <c>First</c> inside the render loop.
///     <para>
///         <b>Binding is by Z-band overlap, not by count.</b> <c>RadarLayerDto</c> has carried
///         <c>MinZ</c>/<c>MaxZ</c> all along; the pre-v2 code index-matched sorted layers to sorted
///         bands only when the two counts happened to be equal, and otherwise handed <i>every</i> band
///         the highest-altitude picture. Three floors and two radar layers (an ordinary shape) put the
///         upper floor's image under the basement, silently. Overlap answers correctly for every shape,
///         and when a band overlaps nothing its level keeps <c>HasRadar == false</c> and the strip says
///         so (B3 plan T5).
///     </para>
/// </summary>
public sealed class MapRadarBinder : ILevelRadarBinder
{
    private readonly List<RadarLayerDto> _ascending = [];
    private readonly LoadedMapAsset? _asset;

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

        // Rule 1, no layer metadata at all: one picture is the whole map, so every level gets it. The
        // single-radar case is the overwhelming majority of maps and it is CORRECT there, which is why
        // a lone level reports Exact; several levels sharing one image is the honest Degraded.
        if (_ascending.Count == 0)
        {
            string? primary = _asset.Bundle.RadarImages is { Count: > 0 } all ? all[0] : null;
            Fill(bands.Count, primary, images, names);
            if (primary is null)
            {
                return RadarBindingQuality.None;
            }

            return bands.Count <= 1 ? RadarBindingQuality.Exact : RadarBindingQuality.Degraded;
        }

        // Rule 2: bind each band to the layer it shares the most of itself with. Any positive overlap
        // qualifies (a thin band inside a tall layer is a perfect match, not a weak one); ties go to the
        // lower layer, because _ascending is sorted and the comparison is strict.
        bool everyBandBound = true;
        for (int i = 0; i < bands.Count; i++)
        {
            FloorSlice band = bands[i];
            string? best = null;
            double bestScore = 0;

            for (int layer = 0; layer < _ascending.Count; layer++)
            {
                RadarLayerDto candidate = _ascending[layer];
                double score = MapSpace.OverlapScore(band.MinZ, band.MaxZ, candidate.MinZ, candidate.MaxZ);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate.Image;
                }
            }

            images.Add(Resolve(best));
            names.Add(best);
            everyBandBound &= best is not null;
        }

        return everyBandBound ? RadarBindingQuality.Exact : RadarBindingQuality.Degraded;
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

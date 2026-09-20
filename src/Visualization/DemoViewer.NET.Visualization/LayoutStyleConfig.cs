namespace DemoViewer.NET.Visualization;

/// <summary>
///     MSAGL Sugiyama tunables surfaced for theme-level adjustment.
///     <para>
///         Re-tuned for issue #4 against the captured shipped graph (434 nodes, 474 edges), which is
///         wide and shallow: few Sugiyama layers holding many nodes each. Under the 90-degree
///         rotation this pipeline applies, layers run left to right, so
///         <see cref="LayerSeparation" /> sets the canvas WIDTH and <see cref="NodeSeparation" /> sets
///         its HEIGHT. That is the wrong way round from the intuition, and it is why the height was
///         the number that would not move: it is the one 160 never touched.
///     </para>
///     <para>
///         Measured on that fixture, going from 160/60 to 110/30 takes the canvas from 2151 x 34726
///         to 1901 x 24425 (74.7 to 46.4 megapixels), total edge length from 2.10M to 1.49M, and edge
///         crossings from 4621 to 3434. All six hard-gate metrics stay at zero across all fourteen
///         fixtures. Tightening further (90/30) buys 5% more and is left on the table: whitespace is
///         not free either.
///     </para>
/// </summary>
public sealed record LayoutStyleConfig
{
    /// <summary>Edge-routing padding around node rects (used by the rectilinear router).</summary>
    public double EdgeRoutingPadding { get; init; } = 12;

    /// <summary>Min gap between adjacent layers. Under the 90-degree rotation this is horizontal.</summary>
    public double LayerSeparation { get; init; } = 110;

    /// <summary>Min gap between sibling nodes within a layer. Under the rotation this is vertical.</summary>
    public double NodeSeparation { get; init; } = 30;
}

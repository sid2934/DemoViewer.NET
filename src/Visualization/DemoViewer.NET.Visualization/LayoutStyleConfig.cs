namespace DemoViewer.NET.Visualization;

/// <summary>
///     MSAGL Sugiyama tunables surfaced for theme-level adjustment. The default values
///     match the long-standing in-tree settings; lower them to tighten dense graphs
///     (e.g. <c>BigStandard</c>), raise them for more whitespace.
/// </summary>
public sealed record LayoutStyleConfig
{
    /// <summary>Edge-routing padding around node rects (used by the rectilinear router).</summary>
    public double EdgeRoutingPadding { get; init; } = 12;

    /// <summary>Min vertical gap between adjacent layers (90° rotation → vertical).</summary>
    public double LayerSeparation { get; init; } = 160;

    /// <summary>Min horizontal gap between sibling nodes within a layer.</summary>
    public double NodeSeparation { get; init; } = 60;
}

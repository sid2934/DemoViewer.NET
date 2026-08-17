#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal sealed record NodePosition(double X, double Y);

internal sealed record GroupBounds(string Label, double X, double Y, double Width, double Height);

internal sealed record TableLayout(
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<double> ColumnXCenters);

internal sealed record TableLayoutWithEdges(
    TableLayout Layout,
    IReadOnlyList<IReadOnlyList<Point>>? ColumnEdgeRoutes);

/// <summary>
///     A placed edge-label rectangle in logical coordinates. Emitted by the
///     label-placement pass so the renderer (and the metrics analyzer) read the
///     real, collision-resolved label position instead of re-deriving it from a
///     segment midpoint + <c>length*6</c> estimate.
/// </summary>
internal sealed record LabelPlacement(double X, double Y, double Width, double Height)
{
    internal Rect ToRect() => new(X, Y, Width, Height);
}

/// <summary>Complete layout result used by the renderer.</summary>
internal sealed record LayoutResult(
    IReadOnlyDictionary<IGraphNode, NodePosition> NodePositions,
    IReadOnlyDictionary<IGraphEdge, IReadOnlyList<Point>> EdgeRoutes,
    IReadOnlyDictionary<IGraphEdge, IReadOnlyList<Point>> SelfLoopRoutes,
    IReadOnlyList<GroupBounds> Groups,
    IReadOnlyList<TableLayoutWithEdges> Tables,
    double TotalWidth,
    double TotalHeight)
{
    /// <summary>
    ///     Collision-resolved edge-label rectangles, keyed by edge, in logical
    ///     coordinates. Populated by the label-placement pass; empty when the graph
    ///     has no labelled edges.
    /// </summary>
    internal IReadOnlyDictionary<IGraphEdge, LabelPlacement> LabelPositions { get; init; }
        = new Dictionary<IGraphEdge, LabelPlacement>();
}

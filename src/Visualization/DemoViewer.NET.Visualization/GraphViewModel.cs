#region

using System.ComponentModel;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     The central state object that drives the <see cref="GraphView" /> control.
///     Two update paths:
///     <list type="bullet">
///         <item><see cref="SetGraphAsync" /> — full topology change, triggers MSAGL layout (expensive)</item>
///         <item><see cref="InvalidateNodeStates" /> — node state changed, re-render only (cheap)</item>
///     </list>
/// </summary>
public sealed class GraphViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<INodeGroup>? _groups;
    private bool _isLayoutComplete;
    private GraphStyle _style = new();

    /// <summary>Current layout result (used internally by GraphView).</summary>
    internal LayoutResult? CurrentLayout { get; private set; }

    /// <summary>
    ///     Where the layout put each node: the CENTRE of its box, in logical units. Empty until
    ///     <see cref="IsLayoutComplete" /> is true, and replaced wholesale by each layout.
    ///     <para>
    ///         This is the one part of the layout result that is public, because a second renderer
    ///         needs somewhere to put its nodes and MSAGL is what decides that (design.md §6.4). The
    ///         rest of <c>LayoutResult</c> stays internal on purpose: routes, label rectangles, group
    ///         bounds and table placements are the pipeline's working shape, not a contract, and the
    ///         readability pass changed three of them in one PR. Widening them now would make the
    ///         next such change a breaking one.
    ///     </para>
    ///     <para>
    ///         Positions and nothing else is also all a node-based renderer wants: it draws its own
    ///         connections between two node boxes rather than following a routed polyline, and it
    ///         measures its own extent from the boxes.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<IGraphNode, GraphNodePlacement> NodePlacements { get; private set; }
        = new Dictionary<IGraphNode, GraphNodePlacement>();

    internal IReadOnlyList<IGraphEdge> Edges { get; private set; } = [];

    /// <summary>True after layout computation completes.</summary>
    public bool IsLayoutComplete
    {
        get => _isLayoutComplete;
        private set
        {
            _isLayoutComplete = value;
            OnPropertyChanged(nameof(IsLayoutComplete));
        }
    }

    internal IReadOnlyList<IGraphNode> Nodes { get; private set; } = [];

    /// <summary>Style configuration for all visual elements.</summary>
    public GraphStyle Style
    {
        get => _style;
        set
        {
            _style = value;
            OnPropertyChanged(nameof(Style));
        }
    }

    internal IReadOnlyList<INodeTable>? Tables { get; private set; }

    /// <summary>Property changed.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     Notifies the view that node states (IsActive, DisplayValue) have changed.
    ///     Triggers a re-render without re-layout.
    /// </summary>
    public void InvalidateNodeStates() => OnPropertyChanged("NodeStates");

    /// <summary>
    ///     Notifies the view that table cell states have changed.
    ///     Triggers a re-render of the table region.
    /// </summary>
    public void InvalidateTableCells() => OnPropertyChanged("TableCells");

    /// <summary>
    ///     Sets the complete graph topology. Triggers MSAGL layout on a background thread.
    /// </summary>
    public async Task SetGraphAsync(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeGroup>? groups = null,
        IReadOnlyList<INodeTable>? tables = null)
    {
        Nodes = nodes;
        Edges = edges;
        _groups = groups;
        Tables = tables;
        IsLayoutComplete = false;

        GraphStyle style = _style;
        LayoutResult layout = await Task.Run(() =>
            LayoutPipeline.ComputeFullLayout(nodes, edges, groups, tables, style));

        CurrentLayout = layout;

        Dictionary<IGraphNode, GraphNodePlacement> placements = new(layout.NodePositions.Count);
        foreach ((IGraphNode node, NodePosition position) in layout.NodePositions)
        {
            placements[node] = new GraphNodePlacement(position.X, position.Y);
        }

        NodePlacements = placements;

        // Placements before the flag, so anything that reacts to IsLayoutComplete reads the new
        // positions rather than the previous layout's.
        IsLayoutComplete = true;
        OnPropertyChanged(nameof(NodePlacements));
        OnPropertyChanged(nameof(CurrentLayout));
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

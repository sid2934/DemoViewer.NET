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
        IsLayoutComplete = true;
        OnPropertyChanged(nameof(CurrentLayout));
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

namespace DemoViewer.NET.Visualization;

/// <summary>
///     A table consolidation: a set of per-instance nodes collapsed into a grid
///     displayed below the main graph. Each row is an instance (e.g. a player),
///     each column is a node type (e.g. "Kills", "Assists").
/// </summary>
public interface INodeTable
{
    /// <summary>
    ///     Edges from lifecycle graph nodes to table column headers.
    ///     Used to draw connector lines from the graph into the table.
    /// </summary>
    IReadOnlyList<ITableColumnEdge> ColumnEdges { get; }

    /// <summary>Column header names.</summary>
    IReadOnlyList<string> ColumnNames { get; }

    /// <summary>Table rows (one per instance).</summary>
    IReadOnlyList<ITableRow> Rows { get; }
}

/// <summary>One row in the node table (e.g. one player).</summary>
public interface ITableRow
{
    /// <summary>Cell values, one per column.</summary>
    IReadOnlyList<ITableCell> Cells { get; }

    /// <summary>Optional annotation to the left of the table (e.g. "slot == 3").</summary>
    string? FilterAnnotation { get; }

    /// <summary>Row header label (e.g. player name).</summary>
    string Label { get; }
}

/// <summary>One cell in the node table.</summary>
public interface ITableCell
{
    /// <summary>Display value, or null to show "ACTIVE"/"inactive" based on <see cref="IsActive" />.</summary>
    string? DisplayValue { get; }

    /// <summary>Whether this cell's corresponding node is active.</summary>
    bool IsActive { get; }
}

/// <summary>
///     An edge from a lifecycle graph node to a table column header.
/// </summary>
public interface ITableColumnEdge
{
    /// <summary>Zero-based column index in the table.</summary>
    int ColumnIndex { get; }

    /// <summary>Optional condition label.</summary>
    string? ConditionLabel { get; }

    /// <summary>Visual effect for coloring.</summary>
    VisualEdgeEffect Effect { get; }

    /// <summary>Edge label (event name).</summary>
    string Label { get; }

    /// <summary>The graph node this edge originates from.</summary>
    IGraphNode SourceNode { get; }

    /// <summary>
    ///     Whether the column edge is drawn at all. Return <c>false</c> to skip the line and
    ///     arrowhead entirely (e.g. when filtered out). Defaults to <c>true</c>. The column layout
    ///     is unaffected, so toggling visibility never triggers a relayout.
    /// </summary>
    bool IsVisible => true;

    /// <summary>Per-edge style override.</summary>
    EdgeStyle? Style => null;
}

#region

using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Analysis.Abstractions;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Table column edge view model data record.</summary>
/// <param name="SourceNode">The lifecycle graph node the column edge originates from.</param>
/// <param name="ColumnIndex">Zero-based column index this edge feeds.</param>
/// <param name="Label">Edge label (event name).</param>
/// <param name="Effect">Visual effect for coloring.</param>
/// <param name="ConditionLabel">Optional condition label.</param>
/// <param name="ChainId">
///     The <c>_chain_{id}</c> join-key of the per-player chain that produced this column, or
///     <c>null</c> when the column is not chain-associated. Drives sub-graph column projection
///     (which columns a selected per-player chain renders).
/// </param>
public sealed record TableColumnEdgeViewModel(
    GraphNodeViewModel SourceNode,
    int ColumnIndex,
    string Label,
    EdgeEffect Effect,
    string? ConditionLabel,
    string? ChainId = null) : ITableColumnEdge
{
    VisualEdgeEffect ITableColumnEdge.Effect => (VisualEdgeEffect)Effect;
    IGraphNode ITableColumnEdge.SourceNode => SourceNode;
}

/// <summary>Player table view model.</summary>
/// <remarks>Initializes a new <see cref="PlayerTableViewModel" /> instance.</remarks>
public sealed class PlayerTableViewModel(
    IReadOnlyList<string> columnNames,
    IReadOnlyList<TableRowViewModel> rows,
    IReadOnlyList<string?>? columnChainIds = null) : INodeTable
{
    /// <summary>Column edges.</summary>
    public IReadOnlyList<TableColumnEdgeViewModel> ColumnEdges { get; set; } = [];

    /// <summary>Rows.</summary>
    public IReadOnlyList<TableRowViewModel> Rows { get; } = rows;

    /// <summary>
    ///     The <c>_chain_{id}</c> join-key owning each column, aligned 1:1 with <see cref="ColumnNames" />
    ///     (<c>null</c> for unattributed columns). This is the COMPLETE column→chain authority — it is
    ///     populated from <c>PerPlayerColumnAssignment.ChainId</c> for every column, including computed
    ///     ones (Expression / threshold-tally) that have no lifecycle edge. Sub-graph column projection
    ///     selects columns off this, never off column edges (which only exist for event-driven columns).
    /// </summary>
    public IReadOnlyList<string?> ColumnChainIds { get; } = columnChainIds ?? [];

    IReadOnlyList<ITableColumnEdge> INodeTable.ColumnEdges => ColumnEdges;

    /// <summary>Column names.</summary>
    public IReadOnlyList<string> ColumnNames { get; } = columnNames;

    IReadOnlyList<ITableRow> INodeTable.Rows => Rows;
}

/// <summary>Table row view model.</summary>
/// <remarks>Initializes a new <see cref="TableRowViewModel" /> instance.</remarks>
public sealed class TableRowViewModel(string playerName, int playerSlot, string filterAnnotation, IReadOnlyList<TableCellViewModel> cells) : ITableRow
{
    /// <summary>Cells.</summary>
    public IReadOnlyList<TableCellViewModel> Cells { get; } = cells;

    /// <summary>Filter annotation.</summary>
    public string FilterAnnotation { get; } = filterAnnotation;

    /// <summary>Player name.</summary>
    public string PlayerName { get; } = playerName;

    /// <summary>Player slot.</summary>
    public int PlayerSlot { get; } = playerSlot;

    IReadOnlyList<ITableCell> ITableRow.Cells => Cells;
    string? ITableRow.FilterAnnotation => FilterAnnotation;

    string ITableRow.Label => PlayerName;
}

/// <summary>Table cell view model.</summary>
/// <remarks>Initializes a new <see cref="TableCellViewModel" /> instance.</remarks>
public sealed partial class TableCellViewModel(int nodeTrackedIndex) : ObservableObject, ITableCell
{
    [ObservableProperty]
    private string? _displayValue;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Node tracked index.</summary>
    public int NodeTrackedIndex { get; } = nodeTrackedIndex;
}

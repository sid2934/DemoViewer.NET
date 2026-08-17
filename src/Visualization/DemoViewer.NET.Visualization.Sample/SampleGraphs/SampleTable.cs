namespace DemoViewer.NET.Visualization.Sample.SampleGraphs;

/// <summary>Sample table.</summary>
public sealed class SampleTable : INodeTable
{
    /// <summary>Column edges.</summary>
    public IReadOnlyList<ITableColumnEdge> ColumnEdges { get; init; } = [];

    /// <summary>Column names.</summary>
    public IReadOnlyList<string> ColumnNames { get; init; } = [];

    /// <summary>Rows.</summary>
    public IReadOnlyList<ITableRow> Rows { get; init; } = [];
}

/// <summary>Sample table row.</summary>
public sealed class SampleTableRow : ITableRow
{
    /// <summary>Cells.</summary>
    public IReadOnlyList<ITableCell> Cells { get; init; } = [];

    /// <summary>Filter annotation.</summary>
    public string? FilterAnnotation { get; init; }

    /// <summary>Label.</summary>
    public string Label { get; init; } = "";
}

/// <summary>Sample table cell.</summary>
public sealed class SampleTableCell : ITableCell
{
    /// <summary>Display value.</summary>
    public string? DisplayValue { get; set; }

    /// <summary>Is active.</summary>
    public bool IsActive { get; set; }
}

/// <summary>Sample column edge data record.</summary>
public sealed record SampleColumnEdge(
    IGraphNode SourceNode,
    int ColumnIndex,
    string Label,
    VisualEdgeEffect Effect,
    string? ConditionLabel = null) : ITableColumnEdge
{
    /// <summary>Style.</summary>
    public EdgeStyle? Style => null;
}

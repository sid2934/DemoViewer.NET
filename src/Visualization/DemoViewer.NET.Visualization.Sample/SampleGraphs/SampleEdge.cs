namespace DemoViewer.NET.Visualization.Sample.SampleGraphs;

/// <summary>Sample edge data record.</summary>
public record SampleEdge(
    IGraphNode Source,
    IGraphNode Destination,
    string Label,
    VisualEdgeEffect Effect,
    string? ConditionLabel = null) : IGraphEdge
{
    /// <summary>Style.</summary>
    public virtual EdgeStyle? Style => null;
}

/// <summary>Sample group data record.</summary>
public record SampleGroup(string GroupName, IReadOnlyList<IGraphNode> Members) : INodeGroup;

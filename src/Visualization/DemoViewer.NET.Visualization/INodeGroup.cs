namespace DemoViewer.NET.Visualization;

/// <summary>
///     A named group of nodes displayed with a visual container (rounded rectangle).
/// </summary>
public interface INodeGroup
{
    /// <summary>Label rendered in the top-left corner of the group container.</summary>
    string GroupName { get; }

    /// <summary>The nodes belonging to this group.</summary>
    IReadOnlyList<IGraphNode> Members { get; }
}

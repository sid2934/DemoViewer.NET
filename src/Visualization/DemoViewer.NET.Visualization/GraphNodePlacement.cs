namespace DemoViewer.NET.Visualization;

/// <summary>
///     Where the layout put one node: the centre of its box, in logical (pre-zoom) units.
///     <para>
///         The centre rather than the top-left, because that is what the layout computes and what
///         every consumer inside this library already uses. A renderer that positions by top-left
///         subtracts half the node size, which it knows from
///         <see cref="IGraphNode.Style" /> or <see cref="NodeStyleConfig" />.
///     </para>
/// </summary>
/// <param name="X">Centre X in logical units.</param>
/// <param name="Y">Centre Y in logical units.</param>
public readonly record struct GraphNodePlacement(double X, double Y);

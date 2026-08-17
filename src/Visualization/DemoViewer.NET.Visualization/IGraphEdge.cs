namespace DemoViewer.NET.Visualization;

/// <summary>
///     Represents a directed edge between two nodes. Consumers implement this interface.
/// </summary>
public interface IGraphEdge
{
    /// <summary>
    ///     Optional condition label appended in brackets (e.g. "victim == player").
    ///     Rendered as: "event_name [condition]".
    /// </summary>
    string? ConditionLabel { get; }

    /// <summary>The destination node (head of the arrow).</summary>
    IGraphNode Destination { get; }

    /// <summary>Controls the edge's default color and dash style via the theme.</summary>
    VisualEdgeEffect Effect { get; }

    /// <summary>
    ///     Whether a debugger breakpoint is armed on this edge. When <c>true</c> the renderer draws a
    ///     breakpoint marker at the edge's route midpoint. Defaults to <c>false</c> (mirrors
    ///     <see cref="IsVisible" />). Read-only to the library; toggling never triggers a relayout.
    /// </summary>
    bool HasBreakpoint => false;

    /// <summary>
    ///     Whether the armed edge breakpoint carries an event condition. Drives the hollow-centre
    ///     conditional marker (matching the node convention). Defaults to <c>false</c>.
    /// </summary>
    bool HasConditionalBreakpoint => false;

    /// <summary>
    ///     Whether the edge is drawn at all. Return <c>false</c> to skip the line, arrowhead, and
    ///     label entirely (e.g. when filtered out). Defaults to <c>true</c>. The layout still
    ///     reserves the edge's route/label rect, so toggling visibility never triggers a relayout.
    /// </summary>
    bool IsVisible => true;

    /// <summary>Primary label displayed along the edge (e.g. event name).</summary>
    string Label { get; }

    /// <summary>The source node (tail of the arrow).</summary>
    IGraphNode Source { get; }

    /// <summary>
    ///     Per-edge style override. Return <c>null</c> to use the effect-based default from the theme.
    /// </summary>
    EdgeStyle? Style => null;
}

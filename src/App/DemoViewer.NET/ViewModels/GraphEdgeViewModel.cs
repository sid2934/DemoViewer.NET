#region

using CS2DemoKit.Analysis.Abstractions;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Graph edge view model.</summary>
/// <remarks>Initializes a new <see cref="GraphEdgeViewModel" /> instance.</remarks>
public sealed class GraphEdgeViewModel(
    GraphNodeViewModel source,
    GraphNodeViewModel destination,
    string label,
    EdgeEffect effect,
    string? conditionLabel = null) : IGraphEdge
{
    /// <summary>Destination.</summary>
    public GraphNodeViewModel Destination { get; } = destination;

    /// <summary>Effect.</summary>
    public EdgeEffect Effect { get; } = effect;

    /// <summary>Source.</summary>
    public GraphNodeViewModel Source { get; } = source;

    /// <summary>Condition label.</summary>
    public string? ConditionLabel { get; } = conditionLabel;

    /// <summary>
    ///     Whether a graph breakpoint is armed on this edge. Satisfies <see cref="IGraphEdge.HasBreakpoint" />
    ///     (overriding its <c>false</c> default); the renderer draws the route-midpoint marker when true.
    ///     Set by <see cref="AnalysisViewModel" /> when breakpoints change, followed by a node-state repaint
    ///     (which re-renders edges too) — a plain settable suffices since the repaint is push-triggered.
    /// </summary>
    public bool HasBreakpoint { get; set; }

    /// <summary>
    ///     Whether the armed edge breakpoint carries an event condition. Satisfies
    ///     <see cref="IGraphEdge.HasConditionalBreakpoint" />; drives the hollow-centre marker.
    /// </summary>
    public bool HasConditionalBreakpoint { get; set; }

    IGraphNode IGraphEdge.Destination => Destination;
    VisualEdgeEffect IGraphEdge.Effect => (VisualEdgeEffect)Effect;

    /// <summary>Label.</summary>
    public string Label { get; } = label;

    IGraphNode IGraphEdge.Source => Source;
}

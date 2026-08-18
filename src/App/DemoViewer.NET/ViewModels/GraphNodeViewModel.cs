#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Theming;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Graph node view model.</summary>
/// <remarks>Initializes a new <see cref="GraphNodeViewModel" /> instance.</remarks>
public sealed partial class GraphNodeViewModel(string name, bool isRoot = false, string? subtitle = null) : ObservableObject, IGraphNode
{
    private static readonly IReadOnlySet<string> _emptyChainIds = new HashSet<string>();

    [ObservableProperty]
    private string? _displayValue;

    /// <summary>
    ///     Whether a graph breakpoint is armed on this node. Satisfies <see cref="IGraphNode.HasBreakpoint" />
    ///     (overriding its <c>false</c> default); the renderer draws the marker when true. Set by
    ///     <see cref="AnalysisViewModel" /> when breakpoints change, followed by a node-state repaint.
    /// </summary>
    [ObservableProperty]
    private bool _hasBreakpoint;

    /// <summary>
    ///     Whether the armed breakpoint carries a condition. Satisfies
    ///     <see cref="IGraphNode.HasConditionalBreakpoint" />; drives the hollow-centre conditional marker.
    /// </summary>
    [ObservableProperty]
    private bool _hasConditionalBreakpoint;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    ///     True when this node comes from a per-player template — it materializes once per player at
    ///     evaluation time. Set by the Workbench's authoring-graph conversion so the renderer can flag it
    ///     (a distinct teal border via <see cref="Style" />), letting authors tell per-player rules from
    ///     the shared game-scope scaffolding. Purely cosmetic; never triggers a relayout.
    /// </summary>
    public bool IsPerPlayer { get; init; }

    /// <summary>
    ///     The set of <c>_chain_{id}</c> join-keys this node belongs to (game-scoped chains).
    ///     Empty when the node is not attributed to any chain (context / enrichment / counter
    ///     targets). Stamped from <see cref="CS2DemoKit.Analysis.Graphs.BuildResult.NodeChains" />.
    ///     Drives sub-graph selection (which nodes a chain pulls into its rendered view).
    /// </summary>
    public IReadOnlySet<string> ChainIds { get; init; } = _emptyChainIds;

    /// <summary>
    ///     This node's absolute column index into a per-message <c>NodeSnapshot[]</c> row
    ///     (i.e. its position in <c>EvaluationResult.FinalTrackedNodes</c>). Decouples a node's
    ///     state lookup from its position in the rendered list, so an arbitrary <em>subset</em> of
    ///     nodes can be rendered (a chain sub-graph) while each still resolves its own correct
    ///     snapshot column from the full, unchanged evaluation. <c>-1</c> means "no snapshot column"
    ///     (e.g. a node not present in the tracked set) — the seek loop then leaves it inert.
    ///     Mirrors <see cref="TableCellViewModel.NodeTrackedIndex" />, the proven pattern.
    /// </summary>
    public int TrackedIndex { get; init; } = -1;

    /// <summary>Is root.</summary>
    public bool IsRoot { get; } = isRoot;

    /// <summary>Name.</summary>
    public string Name { get; } = name;

    /// <summary>Subtitle.</summary>
    public string? Subtitle { get; } = subtitle;

    /// <summary>
    ///     Per-node style override (<see cref="IGraphNode.Style" />). Per-player nodes get a teal border
    ///     so they read as "materializes per player"; every other node inherits the global theme
    ///     (<c>null</c>). The border resolves the <c>GraphNodePerPlayerBorder</c> token at READ time
    ///     (v0.6.0 code-color promotion — was a fixed teal that ignored the theme); the graph re-reads
    ///     styles when it re-renders, which the theme-switch repaint already triggers.
    /// </summary>
    public NodeStyle? Style
    {
        get
        {
            if (!IsPerPlayer)
            {
                return null;
            }

            Avalonia.Media.Color border = ThemeColors.Get(
                "GraphNodePerPlayerBorder", Application.Current?.ActualThemeVariant, "#009688");
            return new NodeStyle
            {
                ActiveBorder = border,
                InactiveBorder = border
            };
        }
    }
}

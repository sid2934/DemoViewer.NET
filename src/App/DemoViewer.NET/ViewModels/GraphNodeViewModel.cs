#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
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

    // The per-player border as it looks before a theme is reachable. Static and immutable: the Style
    // getter is read from two threads and must not write shared state to serve them (see the getter).
    private static readonly NodeStyle _perPlayerFallbackStyle = BorderStyle(Color.Parse("#009688"));

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
    ///     True when this node comes from a per-player template: it materializes once per player at
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
    ///     (e.g. a node not present in the tracked set): the seek loop then leaves it inert.
    ///     Mirrors <see cref="TableCellViewModel.NodeTrackedIndex" />, the proven pattern.
    /// </summary>
    public int TrackedIndex { get; init; } = -1;

    /// <summary>Is root.</summary>
    public bool IsRoot { get; } = isRoot;

    /// <summary>
    ///     This node's stable identity (<see cref="IGraphNode.Key" />). Defaults to a game-scope key
    ///     over <see cref="Name" />, which is what the Workbench authoring graph and the pre-evaluation
    ///     skeleton want; the Analysis post-evaluation build overrides it with a per-player key for
    ///     every materialized copy. Anything persisted or looked up keys on this, NOT on
    ///     <see cref="Name" />, which repeats once per player.
    /// </summary>
    public GraphNodeKey NodeKey { get; init; } = GraphNodeKey.ForGameScope(name);

    string IGraphNode.Key => NodeKey.ToString();

    /// <summary>Name.</summary>
    public string Name { get; } = name;

    /// <summary>Subtitle.</summary>
    public string? Subtitle { get; } = subtitle;

    /// <summary>
    ///     Per-node style override (<see cref="IGraphNode.Style" />). Per-player nodes get a teal border
    ///     so they read as "materializes per player"; every other node inherits the global theme
    ///     (<c>null</c>). The border resolves the <c>GraphNodePerPlayerBorder</c> token at READ time
    ///     (v0.6.0 code-color promotion, was a fixed teal that ignored the theme); the graph re-reads
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

            // Application.ActualThemeVariant is a StyledProperty, so reading it off the UI thread
            // throws "Call from invalid thread". This getter is read from BOTH threads: the renderer
            // on the UI thread, and MsaglTranslator on the layout thread GraphViewModel.SetGraphAsync
            // pushes work onto. Resolve live on the UI thread, which is what keeps a theme switch
            // recolouring these borders; hand the layout pass a fixed style. NOTHING is cached in a
            // field: a getter that writes shared state is a data race when two threads read it, and
            // layout reads Style only for size overrides, which neither branch sets.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return _perPlayerFallbackStyle;
            }

            return BorderStyle(ThemeColors.Get(
                "GraphNodePerPlayerBorder", Application.Current?.ActualThemeVariant, "#009688"));
        }
    }

    private static NodeStyle BorderStyle(Color border) => new()
    {
        ActiveBorder = border,
        InactiveBorder = border
    };
}

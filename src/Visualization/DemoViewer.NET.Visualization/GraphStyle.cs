#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Top-level style configuration for the graph visualization.
///     Provides theme defaults for all visual elements. Per-node and per-edge
///     overrides via <see cref="IGraphNode.Style" /> and <see cref="IGraphEdge.Style" />
///     take precedence over these defaults.
/// </summary>
public sealed record GraphStyle
{
    /// <summary>Canvas background color.</summary>
    public Color CanvasBackground { get; init; } = Color.Parse("#0C0C1A");

    /// <summary>Edge appearance defaults (effect → color mapping).</summary>
    public EdgeStyleConfig Edge { get; init; } = new();

    /// <summary>Font family used for all text rendering.</summary>
    public string FontFamily { get; init; } = "Consolas,Menlo,monospace";

    /// <summary>Group container background.</summary>
    public Color GroupBackground { get; init; } = Color.Parse("#0E0E22");

    /// <summary>Group container border.</summary>
    public Color GroupBorder { get; init; } = Color.Parse("#1E1E42");

    /// <summary>Group label text color.</summary>
    public Color GroupLabelColor { get; init; } = Color.Parse("#505080");

    /// <summary>Label background opacity (semi-transparent backdrop behind edge labels).</summary>
    public Color LabelBackground { get; init; } = Color.Parse("#F00C0C1A");

    /// <summary>MSAGL Sugiyama tunables (node/layer separation, routing padding).</summary>
    public LayoutStyleConfig Layout { get; init; } = new();

    /// <summary>Node appearance defaults.</summary>
    public NodeStyleConfig Node { get; init; } = new();

    /// <summary>Table consolidation appearance.</summary>
    public TableStyleConfig Table { get; init; } = new();

    /// <summary>
    ///     Builds a <see cref="GraphStyle" /> for a theme <paramref name="variant" /> by resolving each colour
    ///     from its <c>Graph*</c> token in the app's theme dictionaries (T1c — so ANY theme, built-in or a user
    ///     drop-in, colours the graph with no code change here). Sizing / layout / font keep the record defaults,
    ///     so the graph LAYOUT and hit-testing are variant-independent (only colours change). Falls back to the
    ///     Dark defaults when app resources aren't available (tests / design surface). The plain
    ///     <c>new GraphStyle()</c> default remains the Dark look.
    /// </summary>
    public static GraphStyle FromTokens(ThemeVariant variant)
    {
        static Color C(string key, ThemeVariant v, string fb)
        {
            return Application.Current?.TryGetResource(key, v, out object? o) == true && o is ISolidColorBrush b
                ? b.Color
                : Color.Parse(fb);
        }

        return new GraphStyle
        {
            CanvasBackground = C("GraphCanvasBg", variant, "#0C0C1A"),
            GroupBackground = C("GraphGroupBg", variant, "#0E0E22"),
            GroupBorder = C("GraphGroupBorder", variant, "#1E1E42"),
            GroupLabelColor = C("GraphGroupLabel", variant, "#505080"),
            LabelBackground = C("GraphLabelBg", variant, "#F00C0C1A"),
            Node = new NodeStyleConfig
            {
                ActiveBackground = C("GraphNodeActiveBg", variant, "#0A2550"),
                ActiveBorder = C("GraphNodeActiveBorder", variant, "#3060A0"),
                ActiveForeground = C("GraphNodeActiveFg", variant, "#A0C8FF"),
                ActiveSubForeground = C("GraphNodeActiveSubFg", variant, "#4080C0"),
                InactiveBackground = C("GraphNodeInactiveBg", variant, "#14143A"),
                InactiveBorder = C("GraphNodeInactiveBorder", variant, "#252545"),
                InactiveForeground = C("GraphNodeInactiveFg", variant, "#606080"),
                InactiveSubForeground = C("GraphNodeInactiveSubFg", variant, "#30304A"),
                RootBackground = C("GraphNodeRootBg", variant, "#141428"),
                RootBorder = C("GraphNodeRootBorder", variant, "#303050"),
                RootForeground = C("GraphNodeRootFg", variant, "#505068")
            },
            Edge = new EdgeStyleConfig
            {
                ActivateColor = C("GraphEdgeActivate", variant, "#2E7D32"),
                ConjunctionColor = C("GraphEdgeConjunction", variant, "#7986CB"),
                DeactivateColor = C("GraphEdgeDeactivate", variant, "#E65100"),
                DisjunctionColor = C("GraphEdgeDisjunction", variant, "#CE93D8"),
                LabelForeground = C("GraphEdgeLabel", variant, "#9090B0"),
                SetValueColor = C("GraphEdgeSetValue", variant, "#F9A825")
            },
            Table = new TableStyleConfig
            {
                ActiveCellBackground = C("GraphTableActiveCellBg", variant, "#0A2550"),
                Background = C("GraphTableBg", variant, "#0E0E22"),
                CellForeground = C("GraphTableCellFg", variant, "#8090B0"),
                DimForeground = C("GraphTableDimFg", variant, "#404060"),
                GridLine = C("GraphTableGridLine", variant, "#1A1A3A"),
                HeaderBackground = C("GraphTableHeaderBg", variant, "#141438"),
                HeaderForeground = C("GraphTableHeaderFg", variant, "#A0C8FF")
            }
        };
    }
}

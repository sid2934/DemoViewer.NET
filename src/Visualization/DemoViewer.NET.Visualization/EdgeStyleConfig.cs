#region

using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Global theme defaults for edge appearance. Maps <see cref="VisualEdgeEffect" /> to colors.
///     Per-edge overrides via <see cref="IGraphEdge.Style" /> take precedence.
/// </summary>
public sealed record EdgeStyleConfig
{
    /// <summary>Activate color.</summary>
    public Color ActivateColor { get; init; } = Color.Parse("#2E7D32");

    /// <summary>Arrow size.</summary>
    public double ArrowSize { get; init; } = 11;

    /// <summary>Conjunction color.</summary>
    public Color ConjunctionColor { get; init; } = Color.Parse("#7986CB");

    /// <summary>Deactivate color.</summary>
    public Color DeactivateColor { get; init; } = Color.Parse("#E65100");

    /// <summary>Disjunction color.</summary>
    public Color DisjunctionColor { get; init; } = Color.Parse("#CE93D8");

    /// <summary>Label font size.</summary>
    public double LabelFontSize { get; init; } = 10;

    /// <summary>Label foreground.</summary>
    public Color LabelForeground { get; init; } = Color.Parse("#9090B0");

    /// <summary>Loop height.</summary>
    public double LoopHeight { get; init; } = 70;

    /// <summary>
    ///     Extra height added per additional self-loop on the same node, so multiple loops stack and remain visually
    ///     distinct.
    /// </summary>
    public double LoopStackOffset { get; init; } = 28;

    /// <summary>Set value color.</summary>
    public Color SetValueColor { get; init; } = Color.Parse("#F9A825");

    /// <summary>Stroke thickness.</summary>
    public double StrokeThickness { get; init; } = 1.5;

    /// <summary>Resolves the default color for a given edge effect.</summary>
    public Color ColorForEffect(VisualEdgeEffect effect) => effect switch
    {
        VisualEdgeEffect.Activate => ActivateColor,
        VisualEdgeEffect.Deactivate => DeactivateColor,
        VisualEdgeEffect.SetValue => SetValueColor,
        VisualEdgeEffect.Conjunction => ConjunctionColor,
        VisualEdgeEffect.Disjunction => DisjunctionColor,
        _ => SetValueColor
    };

    /// <summary>Whether an edge effect uses a dashed line by default.</summary>
    public static bool IsDashedByDefault(VisualEdgeEffect effect) =>
        effect is VisualEdgeEffect.Conjunction or VisualEdgeEffect.Disjunction;
}

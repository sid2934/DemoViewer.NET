#region

using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Global theme defaults for node appearance. Per-node overrides via
///     <see cref="IGraphNode.Style" /> take precedence over these values.
/// </summary>
public sealed record NodeStyleConfig
{
    /// <summary>Active background.</summary>
    public Color ActiveBackground { get; init; } = Color.Parse("#0A2550");

    /// <summary>Active border.</summary>
    public Color ActiveBorder { get; init; } = Color.Parse("#3060A0");

    /// <summary>Active border thickness.</summary>
    public double ActiveBorderThickness { get; init; } = 1.5;

    /// <summary>Active foreground.</summary>
    public Color ActiveForeground { get; init; } = Color.Parse("#A0C8FF");

    /// <summary>Active sub foreground.</summary>
    public Color ActiveSubForeground { get; init; } = Color.Parse("#4080C0");

    /// <summary>Corner radius.</summary>
    public double CornerRadius { get; init; } = 6;

    /// <summary>Height.</summary>
    public double Height { get; init; } = 56;

    /// <summary>Inactive background.</summary>
    public Color InactiveBackground { get; init; } = Color.Parse("#14143A");

    /// <summary>Inactive border.</summary>
    public Color InactiveBorder { get; init; } = Color.Parse("#252545");

    /// <summary>Inactive border thickness.</summary>
    public double InactiveBorderThickness { get; init; } = 1.0;

    /// <summary>Inactive foreground.</summary>
    public Color InactiveForeground { get; init; } = Color.Parse("#606080");

    /// <summary>Inactive sub foreground.</summary>
    public Color InactiveSubForeground { get; init; } = Color.Parse("#30304A");

    /// <summary>Name font size.</summary>
    public double NameFontSize { get; init; } = 12;

    /// <summary>Root background.</summary>
    public Color RootBackground { get; init; } = Color.Parse("#141428");

    /// <summary>Root border.</summary>
    public Color RootBorder { get; init; } = Color.Parse("#303050");

    /// <summary>Root foreground.</summary>
    public Color RootForeground { get; init; } = Color.Parse("#505068");

    /// <summary>State font size.</summary>
    public double StateFontSize { get; init; } = 10;

    /// <summary>Width.</summary>
    public double Width { get; init; } = 180;
}

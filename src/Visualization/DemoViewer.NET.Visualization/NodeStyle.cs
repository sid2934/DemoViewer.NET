#region

using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Per-node style override. All properties are nullable — <c>null</c> means inherit
///     from the global <see cref="NodeStyleConfig" /> theme. Consumers can override just
///     the properties they care about.
/// </summary>
public sealed record NodeStyle
{
    /// <summary>Active background.</summary>
    public Color? ActiveBackground { get; init; }

    /// <summary>Active border.</summary>
    public Color? ActiveBorder { get; init; }

    /// <summary>Active foreground.</summary>
    public Color? ActiveForeground { get; init; }

    /// <summary>Height.</summary>
    public double? Height { get; init; }

    /// <summary>Inactive background.</summary>
    public Color? InactiveBackground { get; init; }

    /// <summary>Inactive border.</summary>
    public Color? InactiveBorder { get; init; }

    /// <summary>Inactive foreground.</summary>
    public Color? InactiveForeground { get; init; }

    /// <summary>Width.</summary>
    public double? Width { get; init; }
}

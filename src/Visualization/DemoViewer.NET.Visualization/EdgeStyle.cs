#region

using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Per-edge style override. All properties are nullable — <c>null</c> means inherit
///     from the effect-based default in <see cref="EdgeStyleConfig" /> or the global theme.
/// </summary>
public sealed record EdgeStyle
{
    /// <summary>Color.</summary>
    public Color? Color { get; init; }

    /// <summary>Is dashed.</summary>
    public bool? IsDashed { get; init; }

    /// <summary>Thickness.</summary>
    public double? Thickness { get; init; }
}

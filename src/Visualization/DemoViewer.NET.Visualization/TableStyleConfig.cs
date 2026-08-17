#region

using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Global theme defaults for the table consolidation view.
/// </summary>
public sealed record TableStyleConfig
{
    /// <summary>Active cell background.</summary>
    public Color ActiveCellBackground { get; init; } = Color.Parse("#0A2550");

    /// <summary>Background.</summary>
    public Color Background { get; init; } = Color.Parse("#0E0E22");

    /// <summary>Cell foreground.</summary>
    public Color CellForeground { get; init; } = Color.Parse("#8090B0");

    /// <summary>Cell height.</summary>
    public double CellHeight { get; init; } = 28;

    /// <summary>Cell width.</summary>
    public double CellWidth { get; init; } = 110;

    /// <summary>Vertical spacing between consecutive column-edge channels above a table.</summary>
    public double ChannelSpacing { get; init; } = 28;

    /// <summary>Vertical gap between the topmost channel and the table's top edge.</summary>
    public double ChannelTopGap { get; init; } = 20;

    /// <summary>Dim foreground.</summary>
    public Color DimForeground { get; init; } = Color.Parse("#404060");

    /// <summary>Gap above table.</summary>
    public double GapAboveTable { get; init; } = 160;

    /// <summary>Grid line.</summary>
    public Color GridLine { get; init; } = Color.Parse("#1A1A3A");

    /// <summary>Header background.</summary>
    public Color HeaderBackground { get; init; } = Color.Parse("#141438");

    /// <summary>Header foreground.</summary>
    public Color HeaderForeground { get; init; } = Color.Parse("#A0C8FF");

    /// <summary>Header height.</summary>
    public double HeaderHeight { get; init; } = 32;

    /// <summary>Row header width.</summary>
    public double RowHeaderWidth { get; init; } = 160;
}

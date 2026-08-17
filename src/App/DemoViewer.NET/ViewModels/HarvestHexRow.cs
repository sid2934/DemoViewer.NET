namespace DemoViewer.NET.ViewModels;

/// <summary>One 16-byte row in the hex display.</summary>
/// <remarks>Initializes a new <see cref="HarvestHexRow" /> instance.</remarks>
public sealed class HarvestHexRow(string offset, HarvestHexCell[] cellsLeft, HarvestHexCell[] cellsRight, string ascii)
{
    /// <summary>Ascii.</summary>
    public string Ascii { get; } = ascii;

    /// <summary>Cells left.</summary>
    public HarvestHexCell[] CellsLeft { get; } = cellsLeft;

    /// <summary>Cells right.</summary>
    public HarvestHexCell[] CellsRight { get; } = cellsRight;

    /// <summary>Offset.</summary>
    public string Offset { get; } = offset;
}

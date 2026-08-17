#region

using Avalonia.Media;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>One byte cell within a <see cref="HarvestHexRow" />.</summary>
/// <remarks>Initializes a new <see cref="HarvestHexCell" /> instance.</remarks>
public sealed class HarvestHexCell(string hexText, char asciiChar, IBrush? highlightBrush, bool isValid, int absoluteOffset)
{
    /// <summary>
    ///     Absolute byte offset of this cell within the hex view's buffer (-1 for padding cells
    ///     past the end of data). Drives the reverse byte→node hit-test (F5.2): clicking a cell in
    ///     the Frame Details view resolves the encompassing payload tree node.
    /// </summary>
    public int AbsoluteOffset { get; } = absoluteOffset;

    /// <summary>Ascii char.</summary>
    public char AsciiChar { get; } = asciiChar;

    /// <summary>Hex text.</summary>
    public string HexText { get; } = hexText;

    /// <summary>Highlight brush.</summary>
    public IBrush? HighlightBrush { get; } = highlightBrush;

    /// <summary>Is valid.</summary>
    public bool IsValid { get; } = isValid;
}

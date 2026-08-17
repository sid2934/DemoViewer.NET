#region

using System.Windows.Input;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Represents one ULEB128 varint field in the CS2 demo frame header (cmd / tick / size).
///     Displayed in the Frame Details tab's header strip above the hex view.
///     Click <see cref="HighlightInRawCommand" /> to jump to those bytes in the RAW tab.
/// </summary>
public sealed class FrameHeaderFieldViewModel
{
    /// <summary>Human-readable decoded value, e.g. "DEM_Packet | DemIsCompressed".</summary>
    public string Decoded { get; init; } = "";

    /// <summary>Space-separated uppercase hex bytes of the raw varint encoding, e.g. "C1 02".</summary>
    public string Hex { get; init; } = "";

    /// <summary>
    ///     Highlights this varint's bytes in the RAW tab and switches to it.
    ///     Null only if the field was constructed without a raw-view back-reference.
    /// </summary>
    public ICommand? HighlightInRawCommand { get; init; }

    /// <summary>Field name, e.g. "cmd", "tick", "size".</summary>
    public string Label { get; init; } = "";

    /// <summary>Absolute file offset of this varint in the .dem file, e.g. "@ 0x3A1F".</summary>
    public string OffsetText { get; init; } = "";

    /// <summary>Tooltip shown on hover — explains the field and its encoding.</summary>
    public string Tooltip { get; init; } = "";
}

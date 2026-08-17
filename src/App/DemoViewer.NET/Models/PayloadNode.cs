#region

using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>Payload node.</summary>
public class PayloadNode : INotifyPropertyChanged
{
    // ── Selection highlight (set by MainViewModel when this node is selected) ──
    private IBrush? _highlightBrush;

    /// <summary>Byte length of this field's tag+value, or -1 if unknown.</summary>
    public int ByteLength { get; set; } = -1;

    /// <summary>Byte offset of this field's tag+value within the raw bytes, or -1 if unknown.</summary>
    public int ByteStart { get; set; } = -1;

    /// <summary>Children.</summary>
    public IReadOnlyList<PayloadNode> Children { get; init; } = [];

    /// <summary>Visual nesting depth in the payload tree (0 = root field).</summary>
    public int Depth { get; init; }

    /// <summary>Display.</summary>
    public string Display =>
        HasChildren ? $"{Name}  ({Children.Count})"
        : IsDelta ? $"{Name}: {PreviousValue} → {Value}"
        : $"{Name}: {Value}";

    // ── Byte-range annotation (populated by PayloadNodeBuilder when raw bytes are provided) ──
    /// <summary>Protobuf field number, or -1 if unknown / not applicable.</summary>
    public int FieldNumber { get; set; } = -1;

    /// <summary>Has byte range.</summary>
    public bool HasByteRange => ByteStart >= 0 && ByteLength > 0;

    /// <summary>Has children.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Highlight brush.</summary>

    public IBrush? HighlightBrush
    {
        get => _highlightBrush;
        set
        {
            if (!Equals(_highlightBrush, value))
            {
                _highlightBrush = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBrush)));
            }
        }
    }

    /// <summary>Is delta.</summary>
    public bool IsDelta => PreviousValue is not null;

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    // ── Delta annotation (set in tick-view delta mode) ──
    /// <summary>When non-null, this field changed: shows "PreviousValue → Value" in the display.</summary>
    public string? PreviousValue { get; set; }

    /// <summary>Value.</summary>
    public string Value { get; init; } = "";

    /// <summary>Watch command.</summary>
    public ICommand? WatchCommand { get; init; }

    /// <summary>Proto wire type string ("varint", "fixed32", "fixed64", "length-delimited").</summary>
    public string WireTypeName { get; set; } = "";

    /// <summary>Property changed.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}

#region

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Represents one decoded field row inside a HarvestCard.
///     Supports nested children (sub-messages) for the TreeView HierarchicalDataTemplate.
/// </summary>
public sealed partial class HarvestPropertyViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    // ── Observable state ────────────────────────────────────────────────
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Byte length.</summary>
    public int? ByteLength { get; init; }

    /// <summary>Byte offset.</summary>
    public int? ByteOffset { get; init; }

    /// Byte range label shown at the far right of the row.
    public string ByteRangeLabel => ByteOffset.HasValue && ByteLength.HasValue
        ? $"@{ByteOffset}+{ByteLength}"
        : "";

    // ── Hierarchy ───────────────────────────────────────────────────────
    /// Sub-properties (used for message fields, entity data groups, etc.).
    public IReadOnlyList<HarvestPropertyViewModel> Children { get; init; } = [];

    // ── Computed display helpers ─────────────────────────────────────────

    /// Full value string, with enrichment appended when available.
    public string DisplayValue => EnrichedHint is { Length: > 0 }
        ? $"{Value}  ·  {EnrichedHint}"
        : Value;

    /// Human-readable enrichment shown after the raw value (e.g. "→ Alice", "→ CCSPlayerController").
    public string? EnrichedHint { get; init; }

    // ── Identity / metadata ─────────────────────────────────────────────
    /// <summary>Field name.</summary>
    public required string FieldName { get; init; }

    /// <summary>Field number.</summary>
    public int FieldNumber { get; init; }

    /// <summary>Has byte range.</summary>
    public bool HasByteRange => ByteOffset.HasValue && ByteLength.HasValue;

    /// <summary>Has children.</summary>
    public bool HasChildren => Children.Count > 0;

    // Row highlight is a theme-aware wash driven by the .selected class on the InspectorCard row (→
    // DynamicResource PropRowSelectedBg), so IsSelected is the only state needed here.

    // ── Selection command (set by the card builder) ─────────────────────
    /// Fired when the user clicks this row; propagates selection to the owning card view-model.
    public ICommand? SelectCommand { get; set; }

    /// <summary>Back-reference to the original PayloadNode; used for hex highlighting and parse chain.</summary>
    public PayloadNode? Source { get; init; }

    /// <summary>Value.</summary>
    public required string Value { get; init; }

    /// Protobuf wire-type label: "varint", "fixed32", "fixed64", "bytes", "string", "bool", "message".
    public string WireType { get; init; } = "";

    // ── Wire-type badge category ─────────────────────────────────────────
    // One IsWt* is true per row; the InspectorCard badge binds it to a wt-* class → theme-aware
    // DynamicResource Wt<Kind>Bg / Wt<Kind>Fg tokens (dark tint in Dark, pale in Light) instead of a
    // fixed code-held brush (which stayed dark on a light card).
    public bool IsWtVarint => WireType == "varint";

    public bool IsWtFixed => WireType is "fixed32" or "fixed64";
    public bool IsWtBytes => WireType == "bytes";
    public bool IsWtString => WireType == "string";
    public bool IsWtBool => WireType == "bool";
    public bool IsWtMessage => WireType == "message";

    public bool IsWtDefault =>
        WireType is not ("varint" or "fixed32" or "fixed64" or "bytes" or "string" or "bool" or "message");
    // IsSelected drives the row's .selected class directly (the generated property raises PropertyChanged),
    // so no manual re-notify is needed.
}

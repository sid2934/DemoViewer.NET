#region

using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Parser;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Represents one decoded message card in the HarvestUI Custom tab.
///     Owns its list of decoded property rows and tracks which one is selected.
/// </summary>
public sealed partial class HarvestCardViewModel : ObservableObject
{
    // ── Expand / collapse ─────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _isExpanded;

    // ── Selection ─────────────────────────────────────────────────────────────
    // Selection now flips the .selected class on the InspectorCard header Border (→ DynamicResource
    // MsgHeaderSelected), so no header-brush property to re-notify here.
    [ObservableProperty]
    private bool _isSelected;

    // ── Constructor ──────────────────────────────────────────────────────────
    /// <summary>Initializes a new <see cref="HarvestCardViewModel" /> instance.</summary>
    /// <param name="messageTypeName">Proto-style type name (or <c>"unknown(N)"</c>).</param>
    /// <param name="byteSize">Payload byte length shown in the header.</param>
    /// <param name="isUnknown">
    ///     True for a net-message the parser could not decode — styled distinctly and rendered
    ///     from a raw proto-wire scan (<see cref="RawUnknownBytes" />) rather than a typed payload.
    /// </param>
    public HarvestCardViewModel(string messageTypeName, int byteSize, bool isUnknown = false)
    {
        MessageTypeName = messageTypeName;
        ByteSize = byteSize;
        IsUnknown = isUnknown;

        (CategoryLabel, HeaderKind) = isUnknown
            ? ("unknown", "Unknown")
            : Classify(messageTypeName);
    }

    // ── Computed from identity (set once in ctor) ────────────────────────────
    // The accent strip + cat badge no longer bind a code-held AccentBrush (v0.6.0 code-color
    // promotion): InspectorCard drives them from the same IsKind* flags the header wash uses,
    // through Classifier* theme tokens — so the whole card identity restyles per theme.

    /// <summary>Byte size.</summary>
    public int ByteSize { get; }

    /// <summary>Byte size label.</summary>
    public string ByteSizeLabel => $"{ByteSize} B";

    /// <summary>Category label.</summary>
    public string CategoryLabel { get; }

    /// <summary>Header label shown in the UI — appends the event sub-label when present.</summary>
    public string DisplayTypeName => EventSubLabel is { Length: > 0 }
        ? $"{MessageTypeName} / {EventSubLabel}"
        : MessageTypeName;

    /// <summary>
    ///     Optional event name suffix (e.g. "player_death") shown after a "/" in the card header.
    ///     Non-null only for <c>GameEventMessage</c> cards.
    /// </summary>
    public string? EventSubLabel { get; init; }

    /// <summary>
    ///     Message-type category key that selects the InspectorCard header background token
    ///     (<c>MsgHeader&lt;Kind&gt;</c>, theme-aware): Net / Svc / Dem / Cs / Clc / GameEvent / Default /
    ///     Unknown. The header Border binds one of the <c>IsKind*</c> bools below to a category style class,
    ///     so the wash resolves per theme (dark tint in Dark, pale tint in Light) instead of a fixed brush.
    /// </summary>
    public string HeaderKind { get; }

    /// <summary>Category-class selectors for the InspectorCard header background (exactly one is true).</summary>
    public bool IsKindNet => HeaderKind == "Net";

    public bool IsKindSvc => HeaderKind == "Svc";
    public bool IsKindDem => HeaderKind == "Dem";
    public bool IsKindCs => HeaderKind == "Cs";
    public bool IsKindClc => HeaderKind == "Clc";
    public bool IsKindGameEvent => HeaderKind == "GameEvent";
    public bool IsKindDefault => HeaderKind == "Default";
    public bool IsKindUnknown => HeaderKind == "Unknown";

    // ── Integration data (set by MainViewModel card builder) ─────────────────
    /// <summary>Message.</summary>
    public NetMessage? Message { get; init; }

    /// <summary>
    ///     True when this card represents a net-message the parser could not decode. Such cards
    ///     have a null <see cref="Message" /> and are populated from <see cref="RawUnknownBytes" />.
    /// </summary>
    public bool IsUnknown { get; }

    /// <summary>
    ///     Exact payload bytes of an undecoded (unknown) net-message, recovered bit-exact from the
    ///     bitstream. Non-null only when <see cref="IsUnknown" /> is true. Loaded standalone into
    ///     the hex view on selection so the raw mystery bytes can be reverse-engineered.
    /// </summary>
    public byte[]? RawUnknownBytes { get; init; }

    // ── Identity ─────────────────────────────────────────────────────────────
    /// <summary>Message type name.</summary>
    public string MessageTypeName { get; }

    /// <summary>Normalized offset.</summary>
    public int NormalizedOffset { get; init; }

    /// <summary>Normalized payload offset.</summary>
    public int NormalizedPayloadOffset { get; init; }

    // ── Properties ───────────────────────────────────────────────────────────
    /// <summary>Properties.</summary>
    public ObservableCollection<HarvestPropertyViewModel> Properties { get; } = [];

    /// <summary>
    ///     Raw PayloadNode tree — kept in sync with Properties for entity injection
    ///     and parse-chain building.
    /// </summary>
    internal List<PayloadNode> RawNodes { get; } = [];

    /// <summary>Card-level click handler — set by the card builder in MainViewModel.</summary>
    public ICommand? SelectCommand { get; set; }

    /// <summary>Clears IsSelected on every property row without triggering card-level callbacks.</summary>
    public void ClearPropertySelection()
    {
        foreach (HarvestPropertyViewModel prop in Properties)
        {
            ClearPropertiesRecursive(prop);
        }
    }

    // ── Category classification ───────────────────────────────────────────────
    private static (string label, string kind) Classify(string typeName) =>
        typeName switch
        {
            _ when typeName.StartsWith("net_", StringComparison.OrdinalIgnoreCase)
                => ("net", "Net"),
            _ when typeName.StartsWith("svc_", StringComparison.OrdinalIgnoreCase)
                => ("svc", "Svc"),
            _ when typeName.StartsWith("DEM_", StringComparison.OrdinalIgnoreCase)
                   || typeName.StartsWith("CDem", StringComparison.OrdinalIgnoreCase)
                => ("DEM", "Dem"),
            _ when typeName.StartsWith("cs_", StringComparison.OrdinalIgnoreCase)
                => ("cs", "Cs"),
            _ when typeName.StartsWith("GameEvent", StringComparison.OrdinalIgnoreCase)
                   || typeName.StartsWith("game_event", StringComparison.OrdinalIgnoreCase)
                => ("event", "GameEvent"),
            _ when typeName.StartsWith("CLC", StringComparison.OrdinalIgnoreCase)
                   || typeName.StartsWith("clc_", StringComparison.OrdinalIgnoreCase)
                => ("clc", "Clc"),
            _ => ("msg", "Default")
        };

    private static void ClearPropertiesRecursive(HarvestPropertyViewModel prop)
    {
        prop.IsSelected = false;
        foreach (HarvestPropertyViewModel child in prop.Children)
        {
            ClearPropertiesRecursive(child);
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

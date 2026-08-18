#region

using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Read-only display row for the frame timeline list (plus a single observable
///     <see cref="IsBreakpointSet" /> property the debugger toggles when the user clicks the
///     gutter). All other fields are <c>init</c>-only — no change-notification needed for them.
/// </summary>
public sealed partial class HarvestFrameRowViewModel : ObservableObject
{
    /// <summary>
    ///     True iff a FrameNumber breakpoint exists for this row's frame.
    ///     Toggled by MainViewModel.RefreshFrameBreakpointMarkers() after every breakpoint
    ///     add/remove. Drives the red-dot gutter indicator.
    /// </summary>
    [ObservableProperty]
    private bool _isBreakpointSet;

    // ── Frame-type kind flags (v0.6.0 code-color promotion) ──────────────────
    // The type pill's accent used to be a code-held 0xC0-alpha brush (theme-blind). The view now
    // binds these flags to fk-* classes → Classifier*Dim theme tokens, exactly one true per row.

    /// <summary>DEM_FullPacket rows (purple dim).</summary>
    public bool IsKindFullPacket => FrameType == "DEM_FullPacket";

    /// <summary>DEM_Tick rows (teal dim).</summary>
    public bool IsKindTick => FrameType == "DEM_Tick";

    /// <summary>DEM_SyncTick rows (slate dim).</summary>
    public bool IsKindSyncTick => FrameType == "DEM_SyncTick";

    /// <summary>DEM_Stop rows (red dim).</summary>
    public bool IsKindStop => FrameType == "DEM_Stop";

    /// <summary>Other DEM_/CDem rows (orange dim).</summary>
    public bool IsKindDem => !IsKindFullPacket && !IsKindTick && !IsKindSyncTick && !IsKindStop
                             && (FrameType.StartsWith("DEM_", StringComparison.Ordinal)
                                 || FrameType.StartsWith("CDem", StringComparison.Ordinal));

    /// <summary>svc_* rows (green dim).</summary>
    public bool IsKindSvc => FrameType.StartsWith("svc_", StringComparison.Ordinal);

    /// <summary>net_* rows (blue dim).</summary>
    public bool IsKindNet => FrameType.StartsWith("net_", StringComparison.Ordinal);

    /// <summary>Fallback rows (slate-blue dim).</summary>
    public bool IsKindDefault => !IsKindFullPacket && !IsKindTick && !IsKindSyncTick && !IsKindStop
                                 && !IsKindDem && !IsKindSvc && !IsKindNet;

    /// <summary>Byte size.</summary>
    public int ByteSize { get; init; }

    /// <summary>Byte size label.</summary>
    public string ByteSizeLabel => ByteSize > 0 ? $"{ByteSize:N0} B" : "—";

    // ── Raw data ─────────────────────────────────────────────────────────────

    /// <summary>Frame number.</summary>
    public int FrameNumber { get; init; }

    // ── Computed display values ───────────────────────────────────────────────

    /// <summary>Frame number label.</summary>
    public string FrameNumberLabel => $"#{FrameNumber:D4}";

    /// <summary>Frame type.</summary>
    public string FrameType { get; init; } = "";

    /// <summary>Has messages.</summary>
    public bool HasMessages => MessageCount > 0;

    /// <summary>Message count.</summary>
    public int MessageCount { get; init; }

    /// <summary>Msg count label.</summary>
    public string MsgCountLabel => MessageCount > 0 ? $"×{MessageCount}" : "—";

    /// <summary>Back-reference to the source frame; used for selection sync in MainViewModel.</summary>
    public DemoFrame? Source { get; init; }

    /// <summary>Type full label.</summary>
    public string TypeFullLabel => FrameType;

    /// <summary>Type pill label.</summary>
    public string TypePillLabel => AbbrevType(FrameType);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string AbbrevType(string t) => t switch
    {
        "DEM_Packet" => "PKT",
        "DEM_FullPacket" => "FULL",
        "DEM_Tick" => "TICK",
        "DEM_StringTables" => "STBL",
        "DEM_SendTables" => "SEND",
        "DEM_FileHeader" => "HDR",
        "DEM_FileInfo" => "INFO",
        "DEM_SyncTick" => "SYNC",
        "DEM_CustomData" => "CUST",
        "DEM_Stop" => "STOP",
        _ when t.Length > 6 => t[..6],
        _ => t
    };
}

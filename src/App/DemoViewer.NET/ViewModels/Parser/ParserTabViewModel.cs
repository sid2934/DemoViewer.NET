#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Models;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.Shell;
using Google.Protobuf;
using Google.Protobuf.Reflection;

#endregion

namespace DemoViewer.NET.ViewModels.Parser;

/// <summary>
///     Owns Parser-tab state: frame list, message cards, dual hex panes, parse chain,
///     frame-header fields strip, selection-coupled card / property highlight cycle,
///     and the entity_data injection / decompressed-payload caching that drive the
///     per-frame "decompressed" view.
///     <para>
///         Extracted from the legacy MainViewModel: the shell callbacks (<see cref="OnFrameSelected" />,
///         <see cref="RepoRootSource" />, <see cref="ProtoIndexSource" />, <see cref="FrameListSource" />,
///         <see cref="DemoBytesSource" />, <see cref="EntityTrackerSource" />,
///         <see cref="PopulateFrameGameEvents" />) replace the prior direct shell reads.
///     </para>
/// </summary>
public sealed partial class ParserTabViewModel : ObservableObject
{
    /// <summary>
    ///     Each entry in the normalized bitstream starts with an 8-byte fixed header:
    ///     [4-byte message index (uint32-LE)][4-byte payload length (uint32-LE)],
    ///     followed by the exact proto payload bytes.
    /// </summary>
    private const int NormalizedHeaderSize = 8;

    /// <summary>
    ///     A link into the parser's source on GitHub. The parse pipeline and entity decoder ship as
    ///     the CS2DemoKit packages, so their files are not in this checkout and there is nothing for
    ///     <c>code --goto</c> to open — the chain links out to the upstream repository instead.
    /// </summary>
    private const string EntityTrackerSourcePath = "src/CS2DemoKit.Parser/EntityTracking/EntityTracker.cs";

    private const string DemoParserSourcePath = "src/CS2DemoKit.Parser/DemoParser.cs";

    private byte[]? _cachedDecompressedPayload;

    // Unknown-message RE: the Frame Details buffer (normalized bitstream or raw
    // decompressed payload) + its header for the selected frame, cached so we can restore it
    // after an unknown card temporarily swapped HexViewDecompressed to its standalone bytes.
    private byte[]? _cachedFrameDetailsBytes;
    private string? _cachedFrameDetailsHeader;
    private bool _cardModeActive;

    // F5.2 — flat [byteRange → PayloadNode] index over the active card's PayloadNodes,
    // rebuilt whenever PayloadNodes changes. Coordinates are 0-based within the message
    // payload; the click handler subtracts the Frame Details shift before looking up.
    private List<ByteRangeEntry> _decompressedByteIndex = [];

    // ── Scalar state (3.3b) ───────────────────────────────────────────────────
    // Default ShowRawHex=true preserves the legacy initial state so the first
    // frame load opens on the Raw hex pane, not the (still empty) Decompressed one.
    [ObservableProperty]
    private string _frameHeaderText = "";

    [ObservableProperty]
    private bool _hasInnerMessages;

    [ObservableProperty]
    private bool _hasMessageCards;

    [ObservableProperty]
    private bool _hasParseChain;

    private bool _hexShowingUnknown;

    [ObservableProperty]
    private bool _isDecompressedTabAvailable;

    private List<HexSpan>? _msgDecompressedRanges;
    private string _msgHlInfo = "";

    // ── Card-cycle / decompressed-payload internals (3.5a) ────────────────────
    // _selectedCard               : card most recently clicked; drives node-highlight shifts
    // _selectedProp               : property row most recently clicked
    // _cachedDecompressedPayload  : decompressed payload of the selected frame
    // _isNormalizedView           : true when Frame Details shows a normalized (re-encoded)
    //                               bitstream instead of the raw decompressed bytes
    // _msgHlInfo / _msgDecompressedRanges : message-level highlight, restored when node selection clears
    // _cardModeActive             : INTRINSIC guard — keep it. SelectedMessage is dual-trigger:
    //                               the card UI sets it via HandleCardSelected, AND its change
    //                               handler (OnSelectedMessageChanged) runs the non-card payload/
    //                               highlight/parse-chain rebuild. Without this flag, a card click
    //                               would run BOTH paths, and the generic handler would clobber the
    //                               card-specific work (entity_data injection, card-based highlight).
    //                               This is NOT a leftover from the old god-VM shared state — the
    //                               duality is inherent to one bindable property having a side-
    //                               effecting handler, so splitting VMs does not remove the need.
    //                               Only a refactor (a separate SetSelectedMessageFromCard that
    //                               bypasses the handler) could retire it, and that's more code,
    //                               not less. Do not delete on a "VMs are split now" cleanup pass.
    private HarvestCardViewModel? _selectedCard;

    // ── Selection-coupled scalar state (3.5a) ─────────────────────────────────
    [ObservableProperty]
    private DemoFrame? _selectedFrame;

    [ObservableProperty]
    private HarvestFrameRowViewModel? _selectedFrameRow;

    [ObservableProperty]
    private NetMessage? _selectedMessage;

    [ObservableProperty]
    private PayloadNode? _selectedPayloadNode;

    private HarvestPropertyViewModel? _selectedProp;

    [ObservableProperty]
    private bool _showRawHex = true;

    /// <summary>Initializes a new <see cref="ParserTabViewModel" /> instance.</summary>
    public ParserTabViewModel(FrameNavigationViewModel navigation)
    {
        Navigation = navigation;

        HexViewRaw = new HarvestHexViewModel();
        HexViewDecompressed = new HarvestHexViewModel
        {
            PlaceholderText = "Select a frame to view decoded payload"
        };

        // F5.2 — reverse byte → node selection. Clicking a byte in the Frame Details
        // (decompressed) view resolves the encompassing payload-tree node and selects it,
        // which drives the existing node → hex highlight + parse-chain rebuild flow.
        HexViewDecompressed.ByteClicked += OnDecompressedByteClicked;
    }

    /// <summary>Source of the raw .dem bytes (used by frame-header LEB128 decoding and hex loads).</summary>
    public Func<byte[]?>? DemoBytesSource { get; set; }

    /// <summary>
    ///     Census of undecoded ("unknown") net-messages keyed by <see cref="DemoFrame.FrameNumber" />,
    ///     populated by the shell after parse from <see cref="DemoParser.OnUnknownMessageType" />.
    ///     Drives the unknown-message cards in <see cref="BuildCardsForFrame" />.
    /// </summary>
    public IReadOnlyDictionary<int, List<UnknownMessageInfo>>? UnknownByFrame { get; set; }

    /// <summary>
    ///     Shell-supplied resolver that turns a (semantic, raw int) pair into a human-readable
    ///     hint (e.g. "Lucky" for a PlayerUserId of 7). Owned by the shell because the
    ///     underlying player tables live there (player-info maps move with the file-load
    ///     pipeline in 3.5c).
    /// </summary>
    public Func<FieldSemantic, int, string?>? EnrichmentResolver { get; set; }

    /// <summary>
    ///     Source of the entity-field tree (owned by EntityTab) used by the entity
    ///     parse-chain builder. Wired by MainViewModel ctor.
    /// </summary>
    public Func<IEnumerable<PayloadNode>>? EntityFieldNodesSource { get; set; }

    /// <summary>
    ///     Source of the current <see cref="EntityTracker" />. Owned by EntityTab; consulted
    ///     only to inject decoded <c>entity_data</c> nodes into a PacketEntities card.
    /// </summary>
    public Func<EntityTracker?>? EntityTrackerSource { get; set; }

    /// <summary>Frame header fields.</summary>
    public ObservableCollection<FrameHeaderFieldViewModel> FrameHeaderFields { get; } = [];

    // ── Shell callbacks (wired by MainViewModel ctor, 3.5a) ───────────────────
    /// <summary>
    ///     Source of the parsed frame list. MainViewModel sets this so the selection-
    ///     coupled code can reverse-look-up the frame's index without a hard reference
    ///     to the shell. Returning <c>null</c> skips the index-dependent fallouts.
    /// </summary>
    public Func<List<DemoFrame>?>? FrameListSource { get; set; }

    /// <summary>Frame rows.</summary>
    public TrimmableObservableCollection<HarvestFrameRowViewModel> FrameRows { get; } = [];

    /// <summary>Hex view decompressed.</summary>
    public HarvestHexViewModel HexViewDecompressed { get; }

    // ── Stable-reference state surfaces ───────────────────────────────────────
    /// <summary>Hex view raw.</summary>
    public HarvestHexViewModel HexViewRaw { get; }

    /// <summary>Exposes the normalized-view flag for the still-on-shell tick-group card build.</summary>
    internal bool IsNormalizedView { get; private set; }

    /// <summary>Message cards.</summary>
    public ObservableCollection<HarvestCardViewModel> MessageCards { get; } = [];

    /// <summary>Navigation.</summary>
    public FrameNavigationViewModel Navigation { get; }

    /// <summary>
    ///     Notifies the shell that the user selected a new frame. The shell handler
    ///     updates <c>_selectedFrameIndex</c>, kicks off <c>EntityTab.SeekEntitiesAsync(idx)</c>, raises
    ///     <c>NotifyCanExecuteChanged</c> on the shell-owned commands, and invokes
    ///     the Analysis seek (guarded by <c>AnalysisTab.IsFrameSeekSuppressed</c>).
    ///     Argument is the new frame index, or -1 when selection cleared.
    /// </summary>
    public Action<int>? OnFrameSelected { get; set; }

    /// <summary>Parse chain.</summary>
    public ObservableCollection<ParseChainEntry> ParseChain { get; } = [];

    /// <summary>Payload nodes.</summary>
    public ObservableCollection<PayloadNode> PayloadNodes { get; } = [];

    /// <summary>
    ///     Fills the shell-owned <c>FrameGameEvents</c> collection from the selected
    ///     frame, or clears it when invoked with <c>null</c>. Kept on shell because
    ///     <c>OnSelectedTickGroupChanged</c> populates the same collection from a
    ///     different code path (3.5b territory).
    /// </summary>
    public Action<DemoFrame?>? PopulateFrameGameEvents { get; set; }

    /// <summary>
    ///     Source of the <see cref="ProtoIndex" /> singleton built at startup. The parse-chain
    ///     methods read field / message source locations from it.
    /// </summary>
    public Func<ProtoIndex>? ProtoIndexSource { get; set; }

    /// <summary>Source of the repo-root path used to construct local source links.</summary>
    public Func<string?>? RepoRootSource { get; set; }

    /// <summary>Selected frame messages.</summary>
    public ObservableCollection<NetMessage> SelectedFrameMessages { get; } = [];

    /// <summary>
    ///     External entry point for the EntityTab → ParserTab parse-chain refresh wire.
    ///     Wired by <c>MainViewModel</c>'s constructor as
    ///     <c>EntityTab.OnEntitySelectionChanged = entity =&gt; ParserTab.RebuildParseChainForEntity(entity);</c>.
    /// </summary>
    public void RebuildParseChainForEntity(EntityState? entity) =>
        RebuildParseChain(SelectedFrame, SelectedMessage, SelectedPayloadNode, entity);

    /// <summary>
    ///     Refreshes <c>entity_data</c> decoding in the currently selected card if it's a
    ///     PacketEntities message. Called by EntityTab via the <c>OnSeekCompleted</c> hook
    ///     (wired by MainViewModel's constructor).
    /// </summary>
    public void RefreshSelectedPacketEntitiesCard()
    {
        HarvestCardViewModel? selectedCard = MessageCards.FirstOrDefault(c => c.IsSelected);
        if (selectedCard?.Message?.Payload is CSVCMsg_PacketEntities pe)
        {
            InjectEntityDataNodes(selectedCard, pe);
            SyncPayloadNodesToCard(selectedCard);
        }
    }

    /// <summary>
    ///     Restores the within-frame Parser selection after the shell has already re-selected the
    ///     frame (which rebuilt <see cref="PayloadNodes" />). Restores the active hex pane and, if a
    ///     node name was persisted, re-selects the first matching payload node. Best-effort.
    /// </summary>
    public void RestoreState(TabSessionState s)
    {
        ShowRawHex = s.ShowRawHex;

        if (s.SelectedNodePath is { Length: > 0 } name)
        {
            PayloadNode? match = PayloadNodes.FirstOrDefault(n => n.Name == name);
            if (match is not null)
            {
                SelectedPayloadNode = match;
            }
        }
    }

    // ── Session state ─────────────────────────────────────────

    /// <summary>
    ///     Snapshots the Parser tab's durable selection for session persistence. The selected-frame
    ///     index is reverse-resolved via <see cref="FrameListSource" />; <c>SelectedNodePath</c> is the
    ///     selected payload-node's <c>Name</c> (best-effort — node names aren't strictly unique within
    ///     a card, mirroring the doc's <c>SelectedRow.FieldName</c> sketch).
    /// </summary>
    public TabSessionState SnapshotState()
    {
        int? frameIndex = null;
        List<DemoFrame>? frames = FrameListSource?.Invoke();
        if (frames is not null && SelectedFrame is not null)
        {
            int idx = frames.IndexOf(SelectedFrame);
            if (idx >= 0)
            {
                frameIndex = idx;
            }
        }

        return new TabSessionState(
            frameIndex,
            SelectedPayloadNode?.Name,
            ShowRawHex);
    }

    // ── Tick-view entry point used by shell-owned OnSelectedTickFrameChanged ──
    /// <summary>
    ///     Internal so the still-on-shell tick-view handler (<c>OnSelectedTickFrameChanged</c>,
    ///     3.5b scope) can call this without exposing it as part of the public surface.
    /// </summary>
    internal void BuildCardsForFrameExternal(DemoFrame frame) => BuildCardsForFrame(frame);

    /// <summary>Exposes the card factory for the still-on-shell tick-group + analysis card builds.</summary>
    internal HarvestCardViewModel BuildHarvestCardExternal(NetMessage msg, byte[]? msgBytes, int normalizedOffset = 0) =>
        BuildHarvestCard(msg, msgBytes, normalizedOffset);

    /// <summary>
    ///     Builds a byte array where each message occupies a fixed 8-byte header followed by its
    ///     exact proto payload bytes.  Header layout: [4-byte msg index (uint32-LE)][4-byte payload
    ///     length (uint32-LE)].  <paramref name="normalizedOffsets" /> receives the start byte of each
    ///     message's header within the returned array.
    /// </summary>
    internal static byte[] BuildNormalizedBitstream(
        IReadOnlyList<NetMessage> messages,
        byte[]?[] exactBytes,
        out int[] normalizedOffsets)
    {
        int count = messages.Count;
        normalizedOffsets = new int[count];

        int totalSize = 0;
        for (int i = 0; i < count; i++)
        {
            normalizedOffsets[i] = totalSize;
            int payloadLen = exactBytes.Length > i && exactBytes[i] is { } b ? b.Length : 0;
            totalSize += NormalizedHeaderSize + payloadLen;
        }

        byte[] buf = new byte[totalSize];
        for (int i = 0; i < count; i++)
        {
            int offset = normalizedOffsets[i];
            byte[]? payload = exactBytes.Length > i ? exactBytes[i] : null;
            int payloadLen = payload?.Length ?? 0;

            WriteUInt32Le(buf, offset, (uint)i);
            WriteUInt32Le(buf, offset + 4, (uint)payloadLen);
            payload?.CopyTo(buf, offset + NormalizedHeaderSize);
        }

        return buf;
    }

    // GetAccentBrush was deleted in the v0.6.0 code-color promotion: it had zero call sites (the
    // card accents moved to HarvestCardViewModel's IsKind* flags → Classifier* theme tokens long
    // ago), and a dead code-held palette is exactly what the promotion exists to remove.

    // ── Hex helpers ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns the decompressed payload of <paramref name="frame" /> by slicing
    ///     <paramref name="demoBytes" />. Returns null if <paramref name="demoBytes" />
    ///     is unavailable or the frame has no payload.
    /// </summary>
    internal static byte[]? GetDecompressedPayload(DemoFrame frame, byte[]? demoBytes)
    {
        if (demoBytes is null || frame.PayloadLength <= 0)
        {
            return null;
        }

        return DownstreamUtilities.GetDecompressedPayload(frame, demoBytes);
    }

    /// <summary>
    ///     Returns the raw proto bytes of <paramref name="msg" /> sliced from
    ///     <paramref name="decompressedPayload" />, or null if position info is missing.
    /// </summary>
    internal static byte[]? GetMsgBytes(NetMessage msg, byte[]? decompressedPayload)
    {
        if (decompressedPayload is null)
        {
            return null;
        }

        if (msg.DecompressedStart is not { } start || msg.DecompressedLength is not { } len)
        {
            return null;
        }

        if (start < 0 || start + len > decompressedPayload.Length)
        {
            return null;
        }

        return decompressedPayload.AsSpan(start, len).ToArray();
    }

    internal static int GetNetMessageTypeId(string typeName)
    {
        foreach (NET_Messages val in Enum.GetValues<NET_Messages>())
        {
            if (GetProtoEnumName(val) == typeName)
            {
                return (int)val;
            }
        }

        foreach (SVC_Messages val in Enum.GetValues<SVC_Messages>())
        {
            if (GetProtoEnumName(val) == typeName)
            {
                return (int)val;
            }
        }

        foreach (Bidirectional_Messages val in Enum.GetValues<Bidirectional_Messages>())
        {
            if (GetProtoEnumName(val) == typeName)
            {
                return (int)val;
            }
        }

        return -1;
    }

    /// <summary>True for frames whose inner messages live in a CDemoPacket.data bitstream.</summary>
    internal static bool IsPacketFrame(DemoFrame frame) =>
        frame.Command is "DEM_Packet" or "DEM_SignonPacket" or "DEM_FullPacket";

    // ── Static helpers (3.5a) ─────────────────────────────────────────────────

    /// <summary>
    ///     Builds the SelectionInfo string for a frame-level RAW highlight.
    ///     Includes a Snappy note for compressed frames so users know to check Frame Details.
    /// </summary>
    internal static string RawFrameHighlightInfo(DemoFrame frame)
    {
        string info = $"Frame: {frame.Command}  tick {frame.GameTick ?? frame.ServerTick}  ({frame.RawLength} bytes at 0x{frame.RawStart:X})";
        if (frame.IsCompressed)
        {
            info += "  •  Snappy-compressed — switch to Frame Details for decompressed bytes";
        }

        return info;
    }

    // ── Parse-chain cluster (3.5a) ────────────────────────────────────────────

    internal void RebuildParseChain(DemoFrame? frame, NetMessage? message, PayloadNode? node, object? entityItem)
    {
        ParseChain.Clear();

        if (frame is null && entityItem is null)
        {
            HasParseChain = false;
            return;
        }

        if (entityItem is EntityState entity)
        {
            BuildChainForEntity(entity);
        }
        else if (frame is not null)
        {
            BuildChainForFrame(frame, message, node);
        }

        HasParseChain = ParseChain.Count > 0;
    }

    /// <summary>
    ///     Demo-unload reset: everything <see cref="ResetForTickGroupBuild" /> drops, plus the two
    ///     demo-scale byte caches. Both hold slices of (or copies derived from) the demo buffer, so a
    ///     standalone close must release them or the whole file stays pinned.
    /// </summary>
    internal void ResetForDemoUnload()
    {
        // SelectedFrame/SelectedFrameRow hold a DemoFrame, and one live frame pins the entire demo byte
        // buffer (zero-copy slicing) — the shell nulls SelectedFrame through its shim, but the row (which
        // carries its own Source frame) has no shim, so it must be dropped here.
        SelectedFrameRow = null;
        SelectedFrame = null;
        ResetForTickGroupBuild();
        _cachedFrameDetailsBytes = null;
        _decompressedByteIndex = [];
        FrameHeaderFields.Clear();
        HasMessageCards = false;
    }

    /// <summary>
    ///     Lets the shell push tick-group card builds back into the parser tab without
    ///     duplicating the build logic. Called from the shell's
    ///     <c>BuildCardsForTickGroup</c> (3.5b scope) when a tick group is selected.
    /// </summary>
    internal void ResetForTickGroupBuild()
    {
        MessageCards.Clear();
        PayloadNodes.Clear();
        HasMessageCards = false;
        FrameHeaderText = "";
        _msgHlInfo = "";
        _msgDecompressedRanges = null;
        _selectedCard = null;
        _cachedDecompressedPayload = null;
        IsNormalizedView = false;
    }

    /// <summary>
    ///     Depth-first search for <paramref name="target" /> in the node tree.
    ///     On success, <paramref name="path" /> contains the full ancestor chain
    ///     (root → target) inclusive.
    /// </summary>
    internal static bool TryFindPath(
        IEnumerable<PayloadNode> nodes,
        PayloadNode target,
        List<PayloadNode> path)
    {
        foreach (PayloadNode node in nodes)
        {
            path.Add(node);
            if (ReferenceEquals(node, target))
            {
                return true;
            }

            if (node.HasChildren && TryFindPath(node.Children, target, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private HarvestPropertyViewModel AdaptNode(
        PayloadNode node,
        HarvestCardViewModel card,
        IReadOnlyDictionary<string, FieldSemantic>? semanticMap = null)
    {
        List<HarvestPropertyViewModel> children = node.HasChildren
            ? node.Children.Select(c => AdaptNode(c, card, semanticMap)).ToList()
            : [];

        // EnrichedHint resolution lives on the shell (it owns the player-name lookups).
        string? enrichedHint = null;
        if (semanticMap is not null
            && semanticMap.TryGetValue(node.Name, out FieldSemantic sem)
            && int.TryParse(node.Value, out int rawInt))
        {
            enrichedHint = EnrichmentResolver?.Invoke(sem, rawInt);
        }

        HarvestPropertyViewModel prop = new()
        {
            FieldName = node.Name,
            Value = node.Value ?? "",
            WireType = node.WireTypeName,
            FieldNumber = node.FieldNumber,
            ByteOffset = node.HasByteRange ? node.ByteStart : null,
            ByteLength = node.HasByteRange ? node.ByteLength : null,
            Source = node,
            Children = children,
            EnrichedHint = enrichedHint
        };
        prop.SelectCommand = new RelayCommand(() => HandlePropertySelected(card, prop));
        return prop;
    }

    private void Add(ParseChainEntry entry) => ParseChain.Add(entry);

    private void AddPayloadNodeSteps(string? parentMsgName, PayloadNode node, string ghBase, int indent)
    {
        string? pbBuilderPath = SrcPath("src", "App", "DemoViewer.NET", "Models", "PayloadNodeBuilder.cs");
        ProtoIndex protoIndex = ProtoIndexSource?.Invoke() ?? new ProtoIndex();

        Add(ParseChainEntry.Linked("PayloadNodeBuilder.Build()",
            localPath: pbBuilderPath, localLine: 19, indent: indent));
        Add(ParseChainEntry.Linked("BuildFields()",
            localPath: pbBuilderPath, localLine: 107, indent: indent + 1));

        List<PayloadNode> path = new();
        TryFindPath(PayloadNodes, node, path);

        string? currentMsg = parentMsgName;

        foreach (PayloadNode ancestor in path)
        {
            protoIndex.TryGetField(currentMsg ?? "", ancestor.Name, out SourceLocation fieldLoc);

            string wireDetail = ancestor.WireTypeName.Length > 0
                ? $"[{ancestor.WireTypeName}]"
                : "";
            if (ancestor.FieldNumber > 0)
            {
                wireDetail = $"field #{ancestor.FieldNumber}  {wireDetail}";
            }

            Add(ParseChainEntry.Linked(
                $"{ancestor.Name}",
                wireDetail.Length > 0 ? $"({wireDetail})" : null,
                fieldLoc.Line > 0 ? ProtoPath(fieldLoc.RelativeFile) : null,
                fieldLoc.Line > 0 ? fieldLoc.Line : null,
                fieldLoc.Line > 0 ? $"{ghBase}{fieldLoc.RelativeFile}#L{fieldLoc.Line}" : null,
                indent + 2 + ancestor.Depth));

            if (ancestor is { Name: "entity_data", HasChildren: true } && ancestor == node)
            {
                Add(ParseChainEntry.Linked("EntityTracker.PeekEntityUpdates()",
                    webUrl: KitUrl(EntityTrackerSourcePath), indent: indent + 3 + ancestor.Depth));
            }
        }
    }

    /// <summary>
    ///     Applies <paramref name="decompSpans" /> (decompressed-payload coordinates) to both hex
    ///     views atomically:
    ///     <list type="bullet">
    ///         <item><see cref="HexViewDecompressed" /> always receives the spans as-is.</item>
    ///         <item>
    ///             <see cref="HexViewRaw" /> receives spans translated by
    ///             <c>+frame.PayloadStart</c> for non-compressed frames; otherwise it is reverted to
    ///             the current frame-level highlight (or left untouched when no frame is selected).
    ///         </item>
    ///     </list>
    /// </summary>
    private void ApplyDecompressedHighlights(IReadOnlyList<HexSpan>? decompSpans)
    {
        // ── Frame Details (decompressed) view ────────────────────────────────
        if (decompSpans is { Count: > 0 })
        {
            HexViewDecompressed.SetSpans(decompSpans);
        }
        else
        {
            HexViewDecompressed.ClearSpans();
        }

        // ── RAW view ─────────────────────────────────────────────────────────
        // For non-compressed frames the decompressed payload IS the raw payload, so spans
        // translate 1-to-1 (offset by PayloadStart).  For compressed frames the bytes differ,
        // so we always keep/restore the frame-level highlight — never mirror node/message spans.
        if (SelectedFrame is not { } frame || DemoBytesSource?.Invoke() is null)
        {
            return;
        }

        // For normalized views Frame Details shows re-encoded bytes that don't correspond
        // byte-for-byte to the raw payload, so we never mirror those spans to the RAW view.
        if (!frame.IsCompressed && !IsNormalizedView && decompSpans is { Count: > 0 })
        {
            int rawBase = frame.PayloadStart;
            List<HexSpan> rawSpans = decompSpans
                .Select(s => new HexSpan(rawBase + s.Start, s.Length, s.Level, s.Label))
                .ToList();
            HexViewRaw.SetSpans(rawSpans);
        }
        else
        {
            // Compressed frame, or no spans to mirror → restore/keep frame-level highlight.
            HexViewRaw.SetSpans([new HexSpan(frame.RawStart, frame.RawLength)]);
        }
    }

    // ── Card building (3.5a) ──────────────────────────────────────────────────

    private void BuildCardsForFrame(DemoFrame frame)
    {
        MessageCards.Clear();
        PayloadNodes.Clear();
        HasMessageCards = false;
        FrameHeaderText = "";
        _msgHlInfo = "";
        _msgDecompressedRanges = null;
        _selectedCard = null;
        _hexShowingUnknown = false;
        IsNormalizedView = false;

        BuildFrameCardsCore(frame, false);
    }

    /// <summary>
    ///     Shared card-build core invoked by both the tick/replay path
    ///     (<see cref="BuildCardsForFrame" />) and the frame-selection path
    ///     (<c>OnSelectedFrameChanged</c>). Decompresses the frame payload, slices it into
    ///     per-message exact bytes, builds the normalized bitstream + Frame Details buffer, runs the
    ///     known-card construction loop, injects the "unknown" cards, and loads the hex views.
    ///     <para>
    ///         Each caller keeps its own pre-reset and post-selection work. The two in-span deltas
    ///         that exist only on the frame-selection path —
    ///         <see cref="HasInnerMessages" /> and populating <see cref="SelectedFrameMessages" /> —
    ///         are gated in place by <paramref name="isFrameSelectionPath" /> to preserve exact
    ///         ordering relative to the surrounding shared statements.
    ///     </para>
    /// </summary>
    private void BuildFrameCardsCore(DemoFrame frame, bool isFrameSelectionPath)
    {
        byte[]? demoBytes = DemoBytesSource?.Invoke();
        _cachedDecompressedPayload = GetDecompressedPayload(frame, demoBytes);

        if (isFrameSelectionPath)
        {
            HasInnerMessages = frame.InnerMessages.Count > 0;
        }

        string compressionLabel = frame.IsCompressed ? "compressed" : "uncompressed";
        FrameHeaderText = frame.InnerMessages.Count > 0
            ? $"{frame.Command}  •  tick {frame.GameTick ?? frame.ServerTick}  •  {frame.InnerMessages.Count} messages  •  {compressionLabel}"
            : $"{frame.Command}  •  tick {frame.GameTick ?? frame.ServerTick}  •  {compressionLabel}";

        // Type-id-aligned (not positional): in frames containing unknown net-messages, positional
        // extraction shifts every known card's bytes after the first unknown. See
        // DownstreamUtilities.ExtractInnerMessageBytesAligned.
        byte[]?[] exactBytes = _cachedDecompressedPayload is { } dp
            ? DownstreamUtilities.ExtractInnerMessageBytesAligned(frame, dp)
            : [];

        int[] normalizedOffsets;
        byte[]? frameDetailsBytes;
        if (IsPacketFrame(frame) && frame.InnerMessages.Count > 0)
        {
            frameDetailsBytes = BuildNormalizedBitstream(frame.InnerMessages, exactBytes, out normalizedOffsets);
            IsNormalizedView = true;
        }
        else
        {
            frameDetailsBytes = _cachedDecompressedPayload;
            normalizedOffsets = [];
        }

        for (int i = 0; i < frame.InnerMessages.Count; i++)
        {
            int normOffset = i < normalizedOffsets.Length ? normalizedOffsets[i] : 0;
            if (isFrameSelectionPath)
            {
                SelectedFrameMessages.Add(frame.InnerMessages[i]);
            }

            MessageCards.Add(BuildHarvestCard(frame.InnerMessages[i],
                i < exactBytes.Length ? exactBytes[i] : null,
                normOffset));
        }

        InjectUnknownMessageCards(frame);

        HasMessageCards = MessageCards.Count > 0;

        if (demoBytes is not null)
        {
            _cachedFrameDetailsBytes = frameDetailsBytes;
            _cachedFrameDetailsHeader = IsNormalizedView
                ? "Normalized bitstream — inner message headers re-encoded to 8-byte fixed width"
                : null;
            _hexShowingUnknown = false;
            HexViewRaw.SetSpans([new HexSpan(frame.RawStart, frame.RawLength)]);
            HexViewDecompressed.Load(frameDetailsBytes ?? []);
            HexViewDecompressed.Header = _cachedFrameDetailsHeader;
            HexViewDecompressed.ClearSpans();
            PopulateFrameHeaderFields(frame, _cachedDecompressedPayload);
            IsDecompressedTabAvailable = true;
        }
    }

    /// <summary>
    ///     Reverse-engineering aid: surface net-messages the parser could not decode as
    ///     distinct cards. They never become a <see cref="NetMessage" /> (so the known-card path
    ///     above is untouched); instead we re-walk the bitstream keeping type IDs and add a card —
    ///     with the message's exact bytes and a generic proto-wire decode — for each slice whose
    ///     type ID is in this frame's unknown census. Classification by census type-id set is exact:
    ///     a given type ID is either always-decoded or always-unknown. No-ops for frames without
    ///     unknowns, so clean frames are unaffected.
    /// </summary>
    private void InjectUnknownMessageCards(DemoFrame frame)
    {
        if (UnknownByFrame is null
            || !UnknownByFrame.TryGetValue(frame.FrameNumber, out List<UnknownMessageInfo>? frameUnknowns)
            || frameUnknowns.Count == 0
            || _cachedDecompressedPayload is not { } payload)
        {
            return;
        }

        HashSet<int> unknownTypeIds = [.. frameUnknowns.Select(u => u.TypeId)];
        Dictionary<int, string> typeNames = new();
        foreach (UnknownMessageInfo u in frameUnknowns)
        {
            typeNames[u.TypeId] = u.TypeName;
        }

        List<DownstreamUtilities.InnerMessageSlice> slices =
            DownstreamUtilities.ExtractInnerMessageSlices(frame, payload);

        // Walk slices in bitstream order. Known slices align positionally with the cards already
        // in MessageCards; unknown slices are inserted at the same position so cards stay ordered.
        int insertAt = 0;
        foreach (DownstreamUtilities.InnerMessageSlice slice in slices)
        {
            if (unknownTypeIds.Contains(slice.TypeId))
            {
                string name = typeNames.TryGetValue(slice.TypeId, out string? n) ? n : $"unknown({slice.TypeId})";
                MessageCards.Insert(Math.Min(insertAt, MessageCards.Count), BuildUnknownCard(name, slice.Bytes));
            }

            insertAt++;
        }
    }

    private HarvestCardViewModel BuildUnknownCard(string typeName, byte[] bytes)
    {
        HarvestCardViewModel card = new(typeName, bytes.Length, true)
        {
            Message = null,
            RawUnknownBytes = bytes,
            NormalizedOffset = 0,
            NormalizedPayloadOffset = 0
        };
        card.SelectCommand = new RelayCommand(() => HandleCardSelected(card));

        card.RawNodes.AddRange(PayloadNodeBuilder.BuildFromRawProto(bytes));
        BuildHarvestProperties(card.RawNodes, card);
        return card;
    }

    private void BuildChainForEntity(EntityState entity)
    {
        const string Cs2Schema = "https://sid2934.github.io/CS2-OpenDevDocs/schemas/server";

        Add(ParseChainEntry.Linked("EntityTracker.ProcessFrame()",
            webUrl: KitUrl(EntityTrackerSourcePath, 95), indent: 0));
        Add(ParseChainEntry.Linked("ProcessNetMessage(CSVCMsg_PacketEntities)",
            webUrl: KitUrl(EntityTrackerSourcePath, 119), indent: 1));
        Add(ParseChainEntry.Linked("ProcessPacketEntities()",
            webUrl: KitUrl(EntityTrackerSourcePath, 273), indent: 2));
        Add(ParseChainEntry.Linked("ProcessPacketEntitiesCore()",
            webUrl: KitUrl(EntityTrackerSourcePath, 292), indent: 3));
        Add(ParseChainEntry.Linked("ReadEntityFields()",
            webUrl: KitUrl(EntityTrackerSourcePath, 367), indent: 4));
        Add(ParseChainEntry.Linked(entity.ClassName,
            $"(serial {entity.Serial})",
            webUrl: Cs2Schema, indent: 5));

        // EntityFieldNodes lives on EntityTab; the entity selection already drove
        // its rebuild before this method runs (callback ordering wired in ctor).
        IEnumerable<PayloadNode> fieldNodes = EntityFieldNodesSource?.Invoke() ?? Array.Empty<PayloadNode>();
        List<PayloadNode> fieldList = fieldNodes.SelectMany(n => n.Children).Take(20).ToList();
        if (fieldList.Count > 0)
        {
            foreach (PayloadNode fieldNode in fieldList)
            {
                Add(ParseChainEntry.Linked(
                    fieldNode.Name,
                    $"= {fieldNode.Value}",
                    webUrl: Cs2Schema,
                    indent: 6));
            }

            if (entity.Fields.Count > 20)
            {
                Add(ParseChainEntry.Info($"… and {entity.Fields.Count - 20} more fields", indent: 6));
            }
        }
    }

    private void BuildChainForFrame(DemoFrame frame, NetMessage? message, PayloadNode? node)
    {
        const string GhBase = "https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/";
        ProtoIndex protoIndex = ProtoIndexSource?.Invoke() ?? new ProtoIndex();

        Add(ParseChainEntry.Linked("DemoParser.Parse()",
            webUrl: KitUrl(DemoParserSourcePath, 20), indent: 0));

        string frameCmd = frame.Command;
        protoIndex.TryGetField("EDemoCommands", frameCmd, out SourceLocation cmdLoc);

        Add(ParseChainEntry.Linked(
            $"Frame: {frameCmd}",
            $"(tick={frame.GameTick ?? frame.ServerTick})",
            cmdLoc.Line > 0 ? ProtoPath(cmdLoc.RelativeFile) : null,
            cmdLoc.Line > 0 ? cmdLoc.Line : null,
            cmdLoc.Line > 0 ? $"{GhBase}{cmdLoc.RelativeFile}#L{cmdLoc.Line}" : null,
            2));

        bool isPacketFrame = frameCmd is "DEM_Packet" or "DEM_SignonPacket" or "DEM_FullPacket";

        if (!isPacketFrame)
        {
            string? directTypeName = frame.InnerMessages.Count > 0
                ? frame.InnerMessages[0].Payload.Descriptor.Name
                : null;
            if (directTypeName is not null)
            {
                protoIndex.TryGetMessage(directTypeName, out SourceLocation directLoc);
                Add(ParseChainEntry.Linked(
                    $"{directTypeName}.Parser.ParseFrom(data)",
                    localPath: directLoc.Line > 0 ? ProtoPath(directLoc.RelativeFile) : null,
                    localLine: directLoc.Line > 0 ? directLoc.Line : null,
                    webUrl: directLoc.Line > 0 ? $"{GhBase}{directLoc.RelativeFile}#L{directLoc.Line}" : null,
                    indent: 3));
            }

            if (node is null)
            {
                return;
            }

            AddPayloadNodeSteps(directTypeName, node, GhBase, 4);
        }

        Add(ParseChainEntry.Linked("ParseInnerMessages()",
            $"({frame.InnerMessages.Count} messages)",
            webUrl: KitUrl(DemoParserSourcePath, 126), indent: 4));

        if (message is null)
        {
            return;
        }

        string msgTypeName = message.Payload.Descriptor.Name;
        protoIndex.TryGetMessage(msgTypeName, out SourceLocation msgLoc);

        int typeId = GetNetMessageTypeId(message.MessageTypeName);

        Add(ParseChainEntry.Linked(
            $"TryParseNetMessage(type={typeId})",
            $"→ {message.MessageTypeName}",
            webUrl: KitUrl(DemoParserSourcePath, 166), indent: 5));

        Add(ParseChainEntry.Linked(
            $"{msgTypeName}.Parser.ParseFrom(data)",
            message.DecompressedLength is { } dLen ? $"({dLen} bytes)" : null,
            msgLoc.Line > 0 ? ProtoPath(msgLoc.RelativeFile) : null,
            msgLoc.Line > 0 ? msgLoc.Line : null,
            msgLoc.Line > 0 ? $"{GhBase}{msgLoc.RelativeFile}#L{msgLoc.Line}" : null,
            6));

        if (node is not null)
        {
            AddPayloadNodeSteps(msgTypeName, node, GhBase, 7);
        }
    }

    private static PayloadNode BuildEntityUpdateNode(EntityUpdateInfo update, int depth)
    {
        string prefix = update.Kind switch
        {
            EntityUpdateInfo.UpdateType.Enter => "[+]",
            EntityUpdateInfo.UpdateType.Leave => "[-]",
            EntityUpdateInfo.UpdateType.Delta => "[Δ]",
            _ => "[?]"
        };

        string label = update.ClassName.Length > 0
            ? $"{prefix} #{update.EntityIndex} {update.ClassName}"
            : $"{prefix} #{update.EntityIndex}";

        if (update.Kind == EntityUpdateInfo.UpdateType.Leave)
        {
            return new PayloadNode
            {
                Name = label,
                Value = "(leaving PVS)",
                Depth = depth
            };
        }

        List<PayloadNode> fieldNodes = update.Fields
            .Select(kv => new PayloadNode
            {
                Name = kv.Key,
                Value = MainViewModel.FormatValue(kv.Value),
                Depth = depth + 1
            })
            .ToList();

        return new PayloadNode
        {
            Name = label,
            Depth = depth,
            Children = fieldNodes.Count > 0
                ? fieldNodes
                :
                [
                    new PayloadNode
                    {
                        Name = "(no fields)",
                        Value = "",
                        Depth = depth + 1
                    }
                ]
        };
    }

    private HarvestCardViewModel BuildHarvestCard(NetMessage msg, byte[]? msgBytes, int normalizedOffset = 0)
    {
        HarvestCardViewModel card = new(msg.MessageTypeName, msg.DecompressedLength ?? 0)
        {
            Message = msg,
            NormalizedOffset = normalizedOffset,
            NormalizedPayloadOffset = IsNormalizedView ? normalizedOffset + NormalizedHeaderSize : 0,
            EventSubLabel = msg is GameEventMessage gem0 ? gem0.DecodedEvent.Name : null
        };
        card.SelectCommand = new RelayCommand(() => HandleCardSelected(card));

        Dictionary<string, FieldSemantic>? semanticMap = null;
        IEnumerable<PayloadNode> rawNodes;
        if (msg is GameEventMessage gem1)
        {
            rawNodes = PayloadNodeBuilder.BuildDecodedEvent(gem1.DecodedEvent);
            IReadOnlyList<(string Field, FieldSemantic Kind)> semantics = gem1.DecodedEvent.GetFieldSemantics();
            if (semantics.Count > 0)
            {
                semanticMap = semantics.ToDictionary(x => x.Field, x => x.Kind);
            }
        }
        else
        {
            rawNodes = PayloadNodeBuilder.Build(msg.Payload, msgBytes);
        }

        card.RawNodes.AddRange(rawNodes);
        BuildHarvestProperties(card.RawNodes, card, semanticMap);

        return card;
    }

    private void BuildHarvestProperties(
        IEnumerable<PayloadNode> nodes,
        HarvestCardViewModel card,
        IReadOnlyDictionary<string, FieldSemantic>? semanticMap = null)
    {
        card.Properties.Clear();
        foreach (PayloadNode node in nodes)
        {
            card.Properties.Add(AdaptNode(node, card, semanticMap));
        }
    }

    // ── Tab-switch commands ───────────────────────────────────────────────────

    [RelayCommand]
    private void CollapseAllCards()
    {
        foreach (HarvestCardViewModel c in MessageCards)
        {
            c.IsExpanded = false;
        }
    }

    [RelayCommand]
    private void ExpandAllCards()
    {
        foreach (HarvestCardViewModel c in MessageCards)
        {
            c.IsExpanded = true;
        }
    }

    /// <summary>Overload that reads from <see cref="_cachedDecompressedPayload" />.</summary>
    private byte[]? GetMsgBytes(NetMessage msg) => GetMsgBytes(msg, _cachedDecompressedPayload);

    private static string? GetProtoEnumName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        string? memberName = Enum.GetName(value);
        if (memberName is null)
        {
            return null;
        }

        return typeof(TEnum).GetField(memberName)
            ?.GetCustomAttribute<OriginalNameAttribute>()
            ?.Name;
    }

    // ── Card selection & node selection (3.5a) ────────────────────────────────

    private void HandleCardSelected(HarvestCardViewModel card)
    {
        _selectedCard = card;
        _selectedProp = null;

        foreach (HarvestCardViewModel c in MessageCards)
        {
            c.IsSelected = ReferenceEquals(c, card);
            if (!ReferenceEquals(c, card))
            {
                c.ClearPropertySelection();
            }
        }

        SyncPayloadNodesToCard(card);

        _cardModeActive = true;
        try
        {
            SelectedMessage = card.Message;
        }
        finally
        {
            _cardModeActive = false;
        }

        if (card.IsUnknown)
        {
            // Show the undecoded message's exact bytes standalone in the hex view.
            ShowUnknownCardBytes(card);
        }
        else
        {
            // Restore the frame's decompressed/normalized buffer if an unknown card swapped it.
            RestoreFrameDetailsBuffer();

            if (card.Message is { } msg && _cachedDecompressedPayload is not null)
            {
                SetMessageHighlight(card);

                // Inject decoded entity_data if we have an active tracker.
                if (msg.Payload is CSVCMsg_PacketEntities pe)
                {
                    InjectEntityDataNodes(card, pe);
                    SyncPayloadNodesToCard(card);
                }
            }
            else
            {
                _msgHlInfo = "";
                _msgDecompressedRanges = null;
                ApplyDecompressedHighlights(null);
            }
        }

        // Clear any stale node highlight; OnSelectedPayloadNodeChanged(null) will restore msg highlight.
        SelectedPayloadNode = null;

        RebuildParseChain(SelectedFrame, SelectedMessage, null, null);
    }

    /// <summary>
    ///     Loads an unknown card's exact bytes standalone into the Frame Details hex view and
    ///     highlights its top-level proto-wire fields, so the raw mystery bytes can be inspected
    ///     byte-for-byte. The byte ranges are relative to the message itself (shift 0).
    /// </summary>
    private void ShowUnknownCardBytes(HarvestCardViewModel card)
    {
        byte[]? bytes = card.RawUnknownBytes;
        if (bytes is not { Length: > 0 })
        {
            _msgHlInfo = "";
            _msgDecompressedRanges = null;
            HexViewDecompressed.ClearSpans();
            return;
        }

        HexViewDecompressed.Load(bytes);
        HexViewDecompressed.Header =
            $"UNKNOWN net-message {card.MessageTypeName} — exact {bytes.Length} bytes (proto-wire scan)";
        _hexShowingUnknown = true;

        // Annotate every top-level wire field so the whole message reads as parsed bytes.
        List<HexSpan> spans = [];
        foreach (DownstreamUtilities.FieldSpan f in DownstreamUtilities.Scan(bytes))
        {
            spans.Add(new HexSpan(f.Start, f.Length, 1));
        }

        _msgHlInfo = $"{card.MessageTypeName}  •  {bytes.Length} B";
        _msgDecompressedRanges = spans;
        HexViewDecompressed.SetSpans(spans);
    }

    /// <summary>
    ///     Reloads the selected frame's cached Frame Details buffer into the hex view, undoing a
    ///     prior <see cref="ShowUnknownCardBytes" /> swap. No-op when not currently showing unknown bytes.
    /// </summary>
    private void RestoreFrameDetailsBuffer()
    {
        if (!_hexShowingUnknown)
        {
            return;
        }

        HexViewDecompressed.Load(_cachedFrameDetailsBytes ?? []);
        HexViewDecompressed.Header = _cachedFrameDetailsHeader;
        HexViewDecompressed.ClearSpans();
        _hexShowingUnknown = false;
    }

    private void HandlePropertySelected(HarvestCardViewModel card, HarvestPropertyViewModel prop)
    {
        if (!card.IsSelected)
        {
            HandleCardSelected(card);
        }
        else
        {
            SyncPayloadNodesToCard(card);
        }

        // Deselect previous property row (may be on a different card).
        if (_selectedProp is not null && !ReferenceEquals(_selectedProp, prop))
        {
            _selectedProp.IsSelected = false;
            _selectedProp = null;
        }

        foreach (HarvestCardViewModel c in MessageCards)
        {
            if (!ReferenceEquals(c, card))
            {
                c.ClearPropertySelection();
            }
        }

        _selectedProp = prop;
        prop.IsSelected = true;

        SelectedPayloadNode = prop.Source;
    }

    // ── entity_data payload injection (3.5a) ──────────────────────────────────

    private void InjectEntityDataNodes(HarvestCardViewModel card, CSVCMsg_PacketEntities pe)
    {
        EntityTracker? tracker = EntityTrackerSource?.Invoke();
        if (tracker is null)
        {
            return;
        }

        List<EntityUpdateInfo>? updates = tracker.PeekEntityUpdates(pe);
        if (updates is null)
        {
            return;
        }

        List<PayloadNode> nodes = card.RawNodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Name == "entity_data")
            {
                PayloadNode original = nodes[i];
                List<PayloadNode> children = updates.Select(u => BuildEntityUpdateNode(u, original.Depth + 1))
                    .ToList();

                nodes[i] = new PayloadNode
                {
                    Name = original.Name,
                    Depth = original.Depth,
                    Children = children,
                    FieldNumber = original.FieldNumber,
                    WireTypeName = original.WireTypeName,
                    ByteStart = original.ByteStart,
                    ByteLength = original.ByteLength
                };

                // Rebuild harvest properties to reflect the injected entity data.
                BuildHarvestProperties(nodes, card);
                break;
            }
        }
    }

    /// <summary>
    ///     Maps a clicked Frame Details byte offset back to the encompassing payload-tree node and
    ///     selects it. The offset arrives in Frame Details (decompressed/normalized) coordinates;
    ///     node byte ranges are 0-based within the message payload, so we subtract the same shift
    ///     the node → hex highlight path adds when going the other direction.
    /// </summary>
    private void OnDecompressedByteClicked(int frameDetailsOffset)
    {
        if (_decompressedByteIndex.Count == 0)
        {
            return;
        }

        int shift = _selectedCard?.NormalizedPayloadOffset
                    ?? SelectedMessage?.DecompressedStart
                    ?? 0;

        int nodeOffset = frameDetailsOffset - shift;
        if (nodeOffset < 0)
        {
            return;
        }

        PayloadNode? node = PayloadNodeByteRangeIndex.FindContainingNode(_decompressedByteIndex, nodeOffset);
        if (node is not null && !ReferenceEquals(node, SelectedPayloadNode))
        {
            SelectedPayloadNode = node;
        }
    }

    // ── Selection partials (3.5a) ─────────────────────────────────────────────

    partial void OnSelectedFrameChanged(DemoFrame? value)
    {
        MessageCards.Clear();
        SelectedFrameMessages.Clear();
        HasInnerMessages = false;
        HasMessageCards = false;
        FrameHeaderText = "";
        _msgHlInfo = "";
        _msgDecompressedRanges = null;
        _cachedDecompressedPayload = null;
        _selectedCard = null;
        _hexShowingUnknown = false;
        IsNormalizedView = false;
        _decompressedByteIndex = [];

        SelectedMessage = null;

        // Clear shell-owned FrameGameEvents unconditionally — matches the legacy
        // OnSelectedFrameChanged behaviour, which cleared them at the top before
        // the null check (PopulateFrameGameEvents accepts null and clears).
        PopulateFrameGameEvents?.Invoke(null);

        if (value is null)
        {
            HexViewRaw.ClearSpans();
            HexViewDecompressed.Clear();
            FrameHeaderFields.Clear();
            IsDecompressedTabAvailable = false;
            ShowRawHex = true;
            // Notify shell to clear index/commands.
            OnFrameSelected?.Invoke(-1);
            return;
        }

        BuildFrameCardsCore(value, true);

        int idx = -1;
        List<DemoFrame>? frames = FrameListSource?.Invoke();
        if (frames is not null)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                if (ReferenceEquals(frames[i], value))
                {
                    idx = i;
                    break;
                }
            }

            // Sync the frame-row selection without triggering OnSelectedFrameRowChanged loop.
            HarvestFrameRowViewModel? row = idx >= 0 && idx < FrameRows.Count ? FrameRows[idx] : null;
            SetProperty(ref _selectedFrameRow, row, nameof(SelectedFrameRow));
        }

        // Populate game events for this frame (shell-owned collection). Already
        // cleared above; this re-invocation fills it for the non-null branch.
        PopulateFrameGameEvents?.Invoke(value);

        RebuildParseChain(value, null, null, null);

        // Shell hook fires last — it does the index assignment, command CanExecute refresh,
        // SeekControls.SetCurrentFrame, EntityTab.SeekEntitiesAsync, and the Analysis seek.
        OnFrameSelected?.Invoke(idx);
    }

    partial void OnSelectedFrameRowChanged(HarvestFrameRowViewModel? value)
    {
        if (value?.Source is { } frame && !ReferenceEquals(_selectedFrame, frame))
        {
            SelectedFrame = frame;
        }
    }

    partial void OnSelectedMessageChanged(NetMessage? value)
    {
        // All selection changes in card mode are handled by HandleCardSelected.
        if (_cardModeActive)
        {
            return;
        }

        _msgHlInfo = "";
        _msgDecompressedRanges = null;

        if (value is null)
        {
            SetPayload(null);
            ApplyDecompressedHighlights(null);
            return;
        }

        // Non-card-mode path (kept for programmatic use outside the card UI).
        byte[]? msgBytes = GetMsgBytes(value);
        SetPayload(value.Payload, msgBytes);

        SetMessageHighlight(value);

        RebuildParseChain(SelectedFrame, value, SelectedPayloadNode, null);
    }

    partial void OnSelectedPayloadNodeChanged(PayloadNode? value)
    {
        // Unknown-message cards show their exact bytes standalone in the Frame Details view, so
        // node byte ranges map directly (shift 0) and only that view is highlighted (the Raw view
        // stays on the whole-frame highlight — these standalone bytes have no raw-buffer mapping).
        if (_hexShowingUnknown)
        {
            ApplyUnknownNodeHighlight(value);
            return;
        }

        if (value is null || !value.HasByteRange)
        {
            // Revert to message-level highlights on both views atomically.
            ApplyDecompressedHighlights(_msgDecompressedRanges);
            return;
        }

        // shift maps node.ByteStart (0-based within the message's proto payload) into Frame Details
        // hex view coordinates.  For normalized views this is the card's NormalizedPayloadOffset
        // (= normalized entry start + 8-byte header).  For direct-payload frames it is 0.
        // Fall back to the approximate DecompressedStart only for the legacy non-card path.
        int shift = _selectedCard?.NormalizedPayloadOffset
                    ?? SelectedMessage?.DecompressedStart
                    ?? 0;

        // Walk the ancestor chain (root → selected node).
        List<PayloadNode> path = new();
        TryFindPath(PayloadNodes, value, path);

        // Level 0 = selected (most prominent), Level 1 = parent, Level 2 = grandparent, etc.
        // path is root→selected, so path[last] is the selected node → level 0.
        List<HexSpan> spans = new();
        for (int i = 0; i < path.Count; i++)
        {
            PayloadNode node = path[i];
            if (node.HasByteRange)
            {
                spans.Add(new HexSpan(node.ByteStart + shift, node.ByteLength, path.Count - 1 - i));
            }
        }

        // Fallback: if the path search failed (shouldn't normally happen), still highlight
        // the selected node on its own so both views remain in a consistent state.
        if (spans.Count == 0)
        {
            spans.Add(new HexSpan(value.ByteStart + shift, value.ByteLength));
        }

        // Single call keeps both views in sync.
        ApplyDecompressedHighlights(spans);

        RebuildParseChain(SelectedFrame, SelectedMessage, value, null);
    }

    /// <summary>
    ///     Node-highlight path for an unknown card's standalone byte buffer: byte ranges are
    ///     0-based within the message (no shift), and only the Frame Details view is updated.
    ///     Deeper generic-proto nodes carry no byte range, so they fall back to the top-level
    ///     field highlights recorded in <see cref="_msgDecompressedRanges" />.
    /// </summary>
    private void ApplyUnknownNodeHighlight(PayloadNode? value)
    {
        if (value is null || !value.HasByteRange)
        {
            HexViewDecompressed.SetSpans(_msgDecompressedRanges ?? []);
            return;
        }

        List<PayloadNode> path = new();
        TryFindPath(PayloadNodes, value, path);

        List<HexSpan> spans = new();
        for (int i = 0; i < path.Count; i++)
        {
            PayloadNode node = path[i];
            if (node.HasByteRange)
            {
                spans.Add(new HexSpan(node.ByteStart, node.ByteLength, path.Count - 1 - i));
            }
        }

        if (spans.Count == 0)
        {
            spans.Add(new HexSpan(value.ByteStart, value.ByteLength));
        }

        HexViewDecompressed.SetSpans(spans);
        RebuildParseChain(SelectedFrame, SelectedMessage, value, null);
    }

    // ── Frame-header strip (3.5a) ─────────────────────────────────────────────

    /// <summary>
    ///     Parses the three ULEB128 header varints (cmd / tick / size) from the raw .dem bytes and
    ///     populates <see cref="FrameHeaderFields" /> for display in the Decompressed tab's header strip.
    /// </summary>
    private void PopulateFrameHeaderFields(DemoFrame frame, byte[]? decompressedPayload)
    {
        FrameHeaderFields.Clear();
        byte[]? demoBytes = DemoBytesSource?.Invoke();
        if (demoBytes is null || frame.HeaderLength <= 0)
        {
            return;
        }

        Span<byte> span = demoBytes.AsSpan(frame.RawStart, frame.HeaderLength);
        int pos = 0;

        // 1 — cmd (EDemoCommands with bit 6 = DemIsCompressed)
        Leb128Utils.TryReadUInt32(span[pos..], out uint rawCmd, out int cmdLen);
        bool isCompressed = (rawCmd & 0x40u) != 0;
        string cmdHex = string.Join(" ", span.Slice(pos, cmdLen).ToArray().Select(b => $"{b:X2}"));
        int cmdAbsOffset = frame.RawStart + pos;
        FrameHeaderFields.Add(new FrameHeaderFieldViewModel
        {
            Label = "cmd",
            Hex = cmdHex,
            Decoded = frame.Command + (isCompressed ? "  |  DemIsCompressed" : ""),
            OffsetText = $"@ 0x{cmdAbsOffset:X}",
            Tooltip = "ULEB128 varint — EDemoCommands enum value. "
                      + "Bit 6 (0x40) encodes the DemIsCompressed flag; "
                      + "the remaining bits are the actual command.\n"
                      + "Click to highlight these bytes in the RAW tab.",
            HighlightInRawCommand = new RelayCommand(() =>
            {
                HexViewRaw.SetSelection(cmdAbsOffset, cmdLen,
                    $"cmd  —  {frame.Command}{(isCompressed ? " | DemIsCompressed" : "")}  ({cmdLen} byte{(cmdLen > 1 ? "s" : "")} at 0x{cmdAbsOffset:X})");
                ShowRawHex = true;
            })
        });
        pos += cmdLen;

        // 2 — tick
        Leb128Utils.TryReadUInt32(span[pos..], out _, out int tickLen);
        string tickHex = string.Join(" ", span.Slice(pos, tickLen).ToArray().Select(b => $"{b:X2}"));
        int tickAbsOffset = frame.RawStart + pos;
        FrameHeaderFields.Add(new FrameHeaderFieldViewModel
        {
            Label = "tick",
            Hex = tickHex,
            Decoded = (frame.GameTick ?? frame.ServerTick).ToString(CultureInfo.InvariantCulture),
            OffsetText = $"@ 0x{tickAbsOffset:X}",
            Tooltip = "ULEB128 varint — Simulation tick at which this frame was recorded.\n"
                      + "Click to highlight these bytes in the RAW tab.",
            HighlightInRawCommand = new RelayCommand(() =>
            {
                HexViewRaw.SetSelection(tickAbsOffset, tickLen,
                    $"tick  —  {frame.GameTick ?? frame.ServerTick}  ({tickLen} byte{(tickLen > 1 ? "s" : "")} at 0x{tickAbsOffset:X})");
                ShowRawHex = true;
            })
        });
        pos += tickLen;

        // 3 — size (stored payload size; for compressed frames this is the Snappy-compressed length)
        Leb128Utils.TryReadUInt32(span[pos..], out uint rawSize, out int sizeLen);
        string sizeHex = string.Join(" ", span.Slice(pos, sizeLen).ToArray().Select(b => $"{b:X2}"));
        string sizeDecoded = frame.IsCompressed && decompressedPayload is not null
            ? $"{rawSize} bytes Snappy-compressed  →  {decompressedPayload.Length} decompressed"
            : $"{rawSize} bytes";
        int sizeAbsOffset = frame.RawStart + pos;
        FrameHeaderFields.Add(new FrameHeaderFieldViewModel
        {
            Label = "size",
            Hex = sizeHex,
            Decoded = sizeDecoded,
            OffsetText = $"@ 0x{sizeAbsOffset:X}",
            Tooltip = "ULEB128 varint — Payload length as stored in the .dem file. "
                      + "For compressed frames this is the Snappy-compressed size; "
                      + "the hex view below shows the fully decompressed payload.\n"
                      + "Click to highlight these bytes in the RAW tab.",
            HighlightInRawCommand = new RelayCommand(() =>
            {
                HexViewRaw.SetSelection(sizeAbsOffset, sizeLen,
                    $"size  —  {sizeDecoded}  ({sizeLen} byte{(sizeLen > 1 ? "s" : "")} at 0x{sizeAbsOffset:X})");
                ShowRawHex = true;
            })
        });
    }

    private string? ProtoPath(string relativeFile)
    {
        string? root = RepoRootSource?.Invoke();
        return root is not null
            ? Path.Combine(root, "cs2-opendocs", "data", "Protobufs", relativeFile)
            : null;
    }

    // ── Reverse byte → node mapping ───────────────────────────────────────────

    private void RebuildDecompressedByteIndex() =>
        _decompressedByteIndex = PayloadNodeByteRangeIndex.Build(PayloadNodes);

    [RelayCommand]
    private void SelectDecompressedTab()
    {
        if (IsDecompressedTabAvailable)
        {
            ShowRawHex = false;
        }
    }

    [RelayCommand]
    private void SelectRawTab() => ShowRawHex = true;

    /// <summary>
    ///     Card-path highlight: uses the card's exact normalized offset for Frame Details.
    ///     For normalized views, highlights the full 8-byte header + payload of the entry.
    /// </summary>
    private void SetMessageHighlight(HarvestCardViewModel card)
    {
        NetMessage? msg = card.Message;
        if (msg is null)
        {
            _msgHlInfo = "";
            _msgDecompressedRanges = null;
            ApplyDecompressedHighlights(null);
            return;
        }

        int payloadLen = msg.DecompressedLength ?? 0;
        if (IsNormalizedView)
        {
            int start = card.NormalizedOffset;
            int len = NormalizedHeaderSize + payloadLen;
            _msgHlInfo = $"{msg.MessageTypeName}  |  Normalized offset {card.NormalizedPayloadOffset}  ({payloadLen} bytes)";
            _msgDecompressedRanges = [new HexSpan(start, len, 0, _msgHlInfo)];
        }
        else if (msg.DecompressedStart is { } start && payloadLen > 0)
        {
            _msgHlInfo = $"{msg.MessageTypeName}  |  Byte offset {start} in frame  ({payloadLen} bytes)";
            _msgDecompressedRanges = [new HexSpan(start, payloadLen, 0, _msgHlInfo)];
        }
        else
        {
            _msgHlInfo = msg.MessageTypeName;
            _msgDecompressedRanges = null;
        }

        ApplyDecompressedHighlights(_msgDecompressedRanges);
    }

    /// <summary>
    ///     Legacy non-card path: uses the approximate <see cref="NetMessage.DecompressedStart" />.
    /// </summary>
    private void SetMessageHighlight(NetMessage msg)
    {
        if (msg is { DecompressedStart: { } start, DecompressedLength: { } len })
        {
            _msgHlInfo = $"{msg.MessageTypeName}  |  Byte offset {start} in frame  ({len} bytes)";
            _msgDecompressedRanges = [new HexSpan(start, len, 0, _msgHlInfo)];
        }
        else
        {
            _msgHlInfo = msg.MessageTypeName;
            _msgDecompressedRanges = null;
        }

        ApplyDecompressedHighlights(_msgDecompressedRanges);
    }

    private void SetPayload(IMessage? msg, byte[]? rawBytes = null)
    {
        SelectedPayloadNode = null;
        PayloadNodes.Clear();
        foreach (PayloadNode node in PayloadNodeBuilder.Build(msg, rawBytes))
        {
            PayloadNodes.Add(node);
        }

        RebuildDecompressedByteIndex();
    }

    private static string KitUrl(string repoRelativePath, int? line = null) =>
        $"https://github.com/CS2OpenDev/CS2DemoKit/blob/main/{repoRelativePath}"
        + (line.HasValue ? $"#L{line}" : "");

    private string? SrcPath(params string[] segments)
    {
        string? root = RepoRootSource?.Invoke();
        if (root is null)
        {
            return null;
        }

        string[] parts = [root, .. segments];
        return Path.Combine(parts);
    }

    private void SyncPayloadNodesToCard(HarvestCardViewModel card)
    {
        PayloadNodes.Clear();
        foreach (PayloadNode node in card.RawNodes)
        {
            PayloadNodes.Add(node);
        }

        RebuildDecompressedByteIndex();
    }

    private static void WriteUInt32Le(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}

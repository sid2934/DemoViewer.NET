#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.GameEvents;
using Cs2DemoKit.Parser.Models;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.Parser;

#endregion

namespace DemoViewer.NET.ViewModels.Replay;

/// <summary>
///     Owns Replay-tab PRESENTATION state: tick-group collection, the per-tick presentation surfaces
///     (<see cref="TickViewFrames" />, <see cref="SubTickEvents" />, <see cref="FrameGameEvents" />),
///     and the tick-group selection fan-out. The parallel tick/round/special navigation and the
///     orphaned ReplaySeekControls were removed in the navigation review — the shell NavStrip +
///     SemanticNavigator are the single nav surface.
///     <para>
///         Extracted from <c>MainViewModel</c>. The cluster was originally
///         scoped as "tick-navigation" but consists almost entirely of Replay-tab presentation
///         state, so it lives in its own <c>ViewModels/Replay/</c> namespace (mirrors the existing
///         per-tab folder pattern).
///     </para>
///     <para>
///         <see cref="TickGroups" /> moved here from EntityTab:
///         EntityTab never consumed the collection directly — its seek pipelines operate
///         on frame indices. The legacy <c>HasTickGroups</c> pass-through on the shell now
///         routes here instead.
///     </para>
///     <para>
///         Cross-VM dependencies follow the established callback pattern. The shell wires:
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="FrameSource" /> / <see cref="DemoBytesSource" /> — read parsed
///             frames + raw bytes for tick-group building and decompression.
///         </item>
///         <item>
///             <see cref="ParserCardReset" /> / <see cref="ParserCardAppend" /> /
///             <see cref="ParserHeaderSink" /> / <see cref="ParserHasMessageCardsSink" /> —
///             push card builds into <c>ParserTab</c>.
///         </item>
///         <item>
///             <see cref="OnTickGroupSelected" /> / <see cref="OnTickFrameSelected" /> —
///             shell kicks off the matching <c>EntityTab</c> seek for the new selection.
///         </item>
///         <item>
///             <see cref="SlotNameResolver" /> — resolves a game-event userid to a player
///             name (shell-owned name lookup tables).
///         </item>
///         <item>
///             <see cref="NotifyCanGoNextTickChanged" /> — re-evaluate the parser-tab
///             debugger commands' CanExecute (they observe <c>HasTickGroups</c> indirectly via
///             their own gates; tick-group rebuilds trigger a manual notify).
///         </item>
///     </list>
/// </summary>
public sealed partial class ReplayTabViewModel : ObservableObject
{
    // Cancellation source for in-flight card-build for the currently selected tick group.
    private CancellationTokenSource? _cardBuildCts;

    [ObservableProperty]
    private bool _hasFrameGameEvents;

    [ObservableProperty]
    private bool _hasSubTickEvents;

    [ObservableProperty]
    private bool _hasTickGroups;

    [ObservableProperty]
    private bool _isTickView;

    [ObservableProperty]
    private DemoFrame? _selectedTickFrame;

    [ObservableProperty]
    private HarvestFrameRowViewModel? _selectedTickFrameRow;

    // ── Scalar state ──────────────────────────────────────────────────────────
    [ObservableProperty]
    private TickGroup? _selectedTickGroup;

    /// <summary>Initializes a new <see cref="ReplayTabViewModel" /> instance.</summary>
    public ReplayTabViewModel(FrameNavigationViewModel navigation) => Navigation = navigation;

    // navigation-review Phase D: the orphaned ReplaySeekControls + its parallel tick/round/special
    // navigation are gone — the shell NavStrip + SemanticNavigator are the single nav surface. Only
    // this VM's tick-group PRESENTATION state (TickGroups / SelectedTickGroup / FrameGameEvents /
    // SubTickEvents / tick-view frame lists) and the entity-seek fan-out it drives remain.
    /// <summary>Source of the raw .dem byte buffer (used during card-build to decompress payloads).</summary>
    public Func<byte[]?>? DemoBytesSource { get; set; }

    /// <summary>Frame game events.</summary>
    public ObservableCollection<FrameGameEventViewModel> FrameGameEvents { get; } = [];

    // ── Callback hooks (wired by MainViewModel ctor) ──────────────────────────
    /// <summary>Source of the parsed frame list (full unfiltered list).</summary>
    public Func<List<DemoFrame>?>? FrameSource { get; set; }

    /// <summary>Navigation.</summary>
    public FrameNavigationViewModel Navigation { get; }

    /// <summary>
    ///     Optional notify hook fired after tick-group rebuild and after tick-group selection
    ///     changes. Shell uses it to <c>NotifyCanExecuteChanged</c> on debugger commands that
    ///     gate on <c>HasTickGroups</c>.
    /// </summary>
    public Action? NotifyCanGoNextTickChanged { get; set; }

    /// <summary>
    ///     Tells the shell that a new tick-view frame was selected. Shell pushes
    ///     <c>ParserTab.BuildCardsForFrameExternal</c> and kicks off
    ///     <c>EntityTab.SeekEntitiesForTickFrameAsync(idx)</c>.
    /// </summary>
    public Action<DemoFrame>? OnTickFrameSelected { get; set; }

    /// <summary>
    ///     Tells the shell that a new tick group was selected (after collections have been rebuilt).
    ///     The shell kicks off <c>EntityTab.SeekEntitiesWithDeltaAsync(group)</c>.
    /// </summary>
    public Action<TickGroup>? OnTickGroupSelected { get; set; }

    /// <summary>
    ///     Appends one harvest card view-model to <c>ParserTab.MessageCards</c>.
    ///     Wired to a lambda over <c>ParserTab.BuildHarvestCardExternal</c>.
    /// </summary>
    public Action<HarvestCardViewModel>? ParserCardAppend { get; set; }

    /// <summary>
    ///     Factory that builds one harvest card for the given message. Wired to
    ///     <c>ParserTab.BuildHarvestCardExternal</c>.
    /// </summary>
    public Func<NetMessage, byte[]?, int, HarvestCardViewModel>? ParserCardFactory { get; set; }

    /// <summary>
    ///     Resets parser-tab card state in preparation for a tick-group card build.
    ///     Wired to <c>ParserTab.ResetForTickGroupBuild</c>.
    /// </summary>
    public Action? ParserCardReset { get; set; }

    /// <summary>
    ///     Pushes the "has cards" flag into <c>ParserTab.HasMessageCards</c> once the build finishes.
    /// </summary>
    public Action<bool>? ParserHasMessageCardsSink { get; set; }

    /// <summary>
    ///     Pushes the formatted header text into <c>ParserTab.FrameHeaderText</c>.
    ///     Wired to a setter lambda.
    /// </summary>
    public Action<string>? ParserHeaderSink { get; set; }

    /// <summary>
    ///     Resolves a game-event userid (or slot index) to a display name.
    ///     Wired to the shell's <c>SlotToName</c>.
    /// </summary>
    public Func<int, string>? SlotNameResolver { get; set; }

    /// <summary>Sub tick events.</summary>
    public ObservableCollection<SubTickEventViewModel> SubTickEvents { get; } = [];

    // ── Stable-reference collections ──────────────────────────────────────────
    /// <summary>Tick groups.</summary>
    public TrimmableObservableCollection<TickGroup> TickGroups { get; } = [];

    /// <summary>Styled row view-models for the tick-view frame list — parallel to <see cref="TickViewFrames" />.</summary>
    public ObservableCollection<HarvestFrameRowViewModel> TickViewFrameRows { get; } = [];

    /// <summary>Tick view frames.</summary>
    public ObservableCollection<DemoFrame> TickViewFrames { get; } = [];

    // ── Tick-group build (called by shell on file load + IsTickView toggle) ───
    /// <summary>
    ///     Rebuilds <see cref="TickGroups" /> from the current <see cref="FrameSource" /> result.
    ///     Also resets the per-tick presentation collections and sets the Replay seek-control
    ///     range to the demo's game-tick span.
    /// </summary>
    public void BuildTickGroups()
    {
        TickGroups.Clear();
        TickViewFrames.Clear();
        SubTickEvents.Clear();
        FrameGameEvents.Clear();
        HasSubTickEvents = false;
        HasFrameGameEvents = false;

        List<DemoFrame>? allFrames = FrameSource?.Invoke();
        if (allFrames is null)
        {
            HasTickGroups = false;
            NotifyCanGoNextTickChanged?.Invoke();
            return;
        }

        List<TickGroup> groups = new();
        int start = 0;
        while (start < allFrames.Count)
        {
            // Group by raw ServerTick. In CS2 demos the pre-game frames share a single large
            // negative ServerTick (≈ −1 − server_start_tick), and actual game frames use
            // ServerTick = 1, 2, … which is already the correct user-visible game tick.
            // DemoFrame.GameTick is NOT used here because the Enrich-pass formula
            // (ServerTick − server_start_tick) shifts game ticks by −server_start_tick,
            // making frame 1 appear as −1343 instead of 1.
            int serverTick = allFrames[start].ServerTick;
            int end = start;
            while (end + 1 < allFrames.Count && allFrames[end + 1].ServerTick == serverTick)
            {
                end++;
            }

            TickGroup group = new()
            {
                Tick = serverTick,
                GameTick = serverTick,
                StartFrameIndex = start,
                EndFrameIndex = end,
                Frames = allFrames.Skip(start).Take(end - start + 1).ToList()
            };
            groups.Add(group);

            start = end + 1;
        }

        foreach (TickGroup g in groups)
        {
            TickGroups.Add(g);
        }

        HasTickGroups = TickGroups.Count > 0;

        NotifyCanGoNextTickChanged?.Invoke();
    }

    /// <summary>
    ///     Demo-unload reset: <see cref="ResetForFileLoad" /> plus <see cref="TickGroups" /> itself.
    ///     The reload path rebuilds the groups right after, so it may leave them standing; a standalone
    ///     close must drop them — every <see cref="TickGroup" /> holds a frame list, and frames slice
    ///     zero-copy into the demo byte buffer.
    /// </summary>
    public void ResetForDemoUnload()
    {
        // Order matters: drop the selections FIRST. SelectedTickFrame is a DemoFrame — one live frame
        // reference is enough to pin the whole demo byte buffer (zero-copy slicing), and the tick-view
        // selection survives the collection clears below because it is a scalar, not a collection member.
        SelectedTickFrame = null;
        SelectedTickFrameRow = null;
        SelectedTickGroup = null;
        ResetForFileLoad();
        // See TrimmableObservableCollection: Clear() leaves the grown backing array behind.
        TickGroups.ClearAndTrim();
        HasTickGroups = false;
        IsTickView = false;
    }

    /// <summary>
    ///     Clears the per-tick presentation surfaces (used by the file-load reset).
    ///     Does NOT touch <see cref="TickGroups" /> itself — those get rebuilt by
    ///     <see cref="BuildTickGroups" /> after re-parse.
    /// </summary>
    public void ResetForFileLoad()
    {
        _cardBuildCts?.Cancel();
        _cardBuildCts = null;
        SubTickEvents.Clear();
        FrameGameEvents.Clear();
        TickViewFrames.Clear();
        TickViewFrameRows.Clear();
        HasSubTickEvents = false;
        HasFrameGameEvents = false;
    }

    private async Task BuildCardsAsync(TickGroup group, CancellationToken ct)
    {
        // Snapshot fields used on the background thread.
        byte[]? demoBytes = DemoBytesSource?.Invoke();
        Func<NetMessage, byte[]?, int, HarvestCardViewModel>? cardFactory = ParserCardFactory;
        if (cardFactory is null)
        {
            return;
        }

        List<HarvestCardViewModel> cards = await Task.Run(() =>
        {
            List<HarvestCardViewModel> result = new();
            foreach (DemoFrame frame in group.Frames)
            {
                if (ct.IsCancellationRequested)
                {
                    return result;
                }

                byte[]? decompressed = demoBytes is not null && frame.PayloadLength > 0
                    ? DownstreamUtilities.GetDecompressedPayload(frame, demoBytes)
                    : null;
                // Type-id-aligned (not positional): keeps known-message bytes correct in frames that
                // contain unknown net-messages. See DownstreamUtilities.ExtractInnerMessageBytesAligned.
                byte[]?[] exactBytes = decompressed is { } dp
                    ? DownstreamUtilities.ExtractInnerMessageBytesAligned(frame, dp)
                    : [];

                int[] normalizedOffsets = [];
                if (ParserTabViewModel.IsPacketFrame(frame) && frame.InnerMessages.Count > 0)
                {
                    ParserTabViewModel.BuildNormalizedBitstream(frame.InnerMessages, exactBytes, out normalizedOffsets);
                }

                for (int i = 0; i < frame.InnerMessages.Count; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return result;
                    }

                    int normOffset = i < normalizedOffsets.Length ? normalizedOffsets[i] : 0;
                    result.Add(cardFactory(frame.InnerMessages[i],
                        i < exactBytes.Length ? exactBytes[i] : null,
                        normOffset));
                }
            }

            return result;
        }, ct);

        if (ct.IsCancellationRequested)
        {
            return;
        }

        foreach (HarvestCardViewModel card in cards)
        {
            ParserCardAppend?.Invoke(card);
        }

        ParserHasMessageCardsSink?.Invoke(cards.Count > 0);
    }

    // ── Tick-group card build ─────────────────────────────────────────────────
    /// <summary>
    ///     Builds parser-tab cards for every inner message across every frame in the
    ///     given <paramref name="group" />. Heavy work runs on a background thread.
    /// </summary>
    private void BuildCardsForTickGroup(TickGroup group)
    {
        // Cancel any in-progress card build for the previous tick group.
        _cardBuildCts?.Cancel();
        CancellationTokenSource cts = new();
        _cardBuildCts = cts;

        // Reset parser-tab card state.
        ParserCardReset?.Invoke();

        int totalMessages = group.Frames.Sum(f => f.InnerMessages.Count);
        int gameTick = group.GameTick;
        string header = group.Frames.Count > 1
            ? $"tick {gameTick}  •  {group.Frames.Count} frames  •  {totalMessages} messages"
            : $"{group.Frames[0].Command}  •  tick {gameTick}  •  {totalMessages} messages";
        ParserHeaderSink?.Invoke(header);

        // Build cards on a background thread — decompression + proto decoding can be heavy for
        // large tick groups (e.g. the pre-game group with hundreds of frames).
        _ = BuildCardsAsync(group, cts.Token);
    }

    // navigation-review Phase D — the parallel tick-group navigation (NextTick / PreviousTick /
    // NextRoundTick / PreviousRoundTick / NextSpecialTick / PreviousSpecialTick / NextGameEventTick),
    // their CanExecute gates, the SeekToGameTick replay-seek, and the FrameContains*/TickGroupContains*
    // helpers are gone — the shell NavStrip + SemanticNavigator are the single nav surface. The
    // tick-group PRESENTATION (TickGroups / SelectedTickGroup selection fan-out) stays below.

    partial void OnIsTickViewChanged(bool value)
    {
        if (value && FrameSource?.Invoke() is not null)
        {
            BuildTickGroups();
        }
    }

    partial void OnSelectedTickFrameChanged(DemoFrame? value)
    {
        if (value is null)
        {
            return;
        }

        OnTickFrameSelected?.Invoke(value);
    }

    // ── Tick-frame selection (tick-view list inside the Replay tab) ───────────
    partial void OnSelectedTickFrameRowChanged(HarvestFrameRowViewModel? value)
    {
        if (value?.Source is { } frame && !ReferenceEquals(SelectedTickFrame, frame))
        {
            SelectedTickFrame = frame;
        }
    }

    // ── Tick-group selection ──────────────────────────────────────────────────
    partial void OnSelectedTickGroupChanged(TickGroup? value)
    {
        TickViewFrames.Clear();
        TickViewFrameRows.Clear();
        SubTickEvents.Clear();
        FrameGameEvents.Clear();
        // Clear parser-tab cards via the shell-wired hook (ResetForTickGroupBuild
        // empties MessageCards, PayloadNodes, etc.).
        ParserCardReset?.Invoke();
        HasSubTickEvents = false;
        HasFrameGameEvents = false;
        ParserHasMessageCardsSink?.Invoke(false);

        if (value is null)
        {
            return;
        }

        int tickFrameNum = value.StartFrameIndex;
        foreach (DemoFrame f in value.Frames)
        {
            TickViewFrames.Add(f);
            TickViewFrameRows.Add(new HarvestFrameRowViewModel
            {
                FrameNumber = tickFrameNum + 1,
                FrameType = f.Command,
                MessageCount = f.InnerMessages.Count,
                ByteSize = f.RawLength,
                Source = f
            });
            tickFrameNum++;
        }

        // Game events for this tick (across all frames in the tick group).
        Func<int, string>? playerName = SlotNameResolver;
        if (playerName is not null)
        {
            foreach (DemoFrame f in value.Frames)
            foreach (NetMessage msg in f.InnerMessages)
            {
                if (msg is GameEventMessage gem)
                {
                    FrameGameEvents.Add(new FrameGameEventViewModel(gem.DecodedEvent, playerName));
                }
            }
        }

        HasFrameGameEvents = FrameGameEvents.Count > 0;

        // Sub-tick events
        List<SubTickEvent> events = SubTickExtractor.Extract(value.Frames);
        foreach (SubTickEvent e in events)
        {
            SubTickEvents.Add(new SubTickEventViewModel(e));
        }

        HasSubTickEvents = SubTickEvents.Count > 0;

        // Build cards for every message across all frames in this game tick.
        BuildCardsForTickGroup(value);

        // Shell-side fallout: kick off entity-tracking seek for this tick group.
        OnTickGroupSelected?.Invoke(value);
    }

    [RelayCommand]
    private void ToggleTickView() => IsTickView = !IsTickView;
}

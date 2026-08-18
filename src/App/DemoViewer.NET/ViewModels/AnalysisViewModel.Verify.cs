#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Debugging;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     "Verify in CS2" — the UI half of the CSVG integration
///. Seeks a live CS2 game to the rule-trigger moment the user is
///     inspecting so they can eyeball whether the rule caught the right instant.
///     <para>
///         The primary Analysis-tab surface is the graph node / edge <b>context menu on pointer-release</b>
///         (the same idiom the breakpoint items already use). The command lives here on the VM (not
///         the view code-behind) so the gating + tick/name resolution is unit-testable headlessly; the code-
///         behind only adds the menu item and routes clicks to <see cref="VerifyInCs2Command" /> with the
///         right-clicked <see cref="ConditionTarget" /> as the command parameter.
///     </para>
///     <para>
///         This VM stays decoupled from the desktop-only Live Sync engine: the shell wires three delegates
///         (present / can-verify / do-verify), exactly the dependency direction as
///         <see cref="CardFactory" /> and <see cref="OnFrameSeeked" />. No reference to
///         <c>Services.LiveSync</c> from here.
///     </para>
/// </summary>
public sealed partial class AnalysisViewModel
{
    /// <summary>
    ///     True while a verification is in flight — drives the command's <c>CanExecute</c> to prevent
    ///     double-invocation. The shell status chip already shows "Seeking…" (engine-side) as the
    ///     primary busy surface.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyInCs2Command))]
    private bool _isVerifying;

    /// <summary>
    ///     Level-1 gate (present vs absent): whether the "Verify in CS2" affordance should be shown
    ///     at all. Wired by the shell to <c>IsLiveSyncEnabled</c> (chrome.livesync + desktop). When this
    ///     returns false the menu item is <b>absent</b> — not shown-then-disabled — for users who never
    ///     opted into Live Sync.
    /// </summary>
    public Func<bool>? IsVerifyInCs2Present { get; set; }

    /// <summary>
    ///     Level-2 gate (enabled vs disabled+prompt): whether a live <c>Synced</c> session exists to
    ///     verify against. Wired by the shell to <c>IsLiveSyncEnabled &amp;&amp; LiveSync.State.IsSynced</c>.
    ///     ("For the current demo" is approximated by IsSynced — genuine demo divergence downgrades the
    ///     engine to <c>Degraded</c>, which is not IsSynced; only v1.0-invisible divergence is a known gap
    ///     the engine can't detect either.) When present but this is false, the item is disabled with the
    ///     "enable Live Sync first" prompt — we never auto-launch CS2 from here.
    /// </summary>
    public Func<bool>? CanVerifyMoment { get; set; }

    /// <summary>
    ///     The verify action. Wired by the shell to
    ///     <c>ILiveSyncService.VerifyMomentAsync(frameClockTick, spectateName:, cancellationToken:)</c>
    ///     (default pre/post roll 192/64). Returns <c>true</c> on a confirmed deterministic-paused arrival,
    ///     <c>false</c> for any failure (never throws for playback failures — the engine contract).
    /// </summary>
    public Func<int, string?, CancellationToken, Task<bool>>? VerifyMomentHandler { get; set; }

    /// <summary>
    ///     The frame-clock tick of the moment currently displayed in the Analysis step-through (the frame
    ///     backing <see cref="CurrentMessageIndex" />). Used as the fallback when a target has no recorded
    ///     trigger fire (a context/root node, or a pre-analysis position). This value is passed to
    ///     <c>VerifyMomentAsync</c> <b>AS-IS</b> — <c>RuleChainEvent.Tick</c> and
    ///     <see cref="DemoFrame.ServerTick" /> share the same frame-clock space (the evaluator stamps the
    ///     event with <c>frame.ServerTick</c>), so there is no <c>ServerStartTick</c> subtraction
    ///     (the <c>FrameIndexOfMessage</c> seam). Null when no message is positioned.
    /// </summary>
    internal int? CurrentFrameClockTick => ResolveFrameClockTick(_messageList, CurrentMessageIndex);

    /// <summary>
    ///     The exact raw in-demo name to spectate, or null when no single player is attributed. On the
    ///     Analysis graph the attributed player is the graph filter's selected player (a specific slot);
    ///     "All players" (or none) yields null — spectate is optional and the graph node/edge VMs
    ///     carry no per-slot attribution of their own.
    /// </summary>
    internal string? VerifySpectateName => ResolveSpectateName(Filter.SelectedPlayer);

    /// <summary>
    ///     Pure gate for <see cref="VerifyInCs2Command" />: not already verifying, a live Synced session
    ///     exists, and a moment is positioned. Static so the truth table is unit-testable without a VM.
    /// </summary>
    internal static bool CanVerify(bool isVerifying, bool canVerifyMoment, int? frameClockTick) =>
        !isVerifying && canVerifyMoment && frameClockTick is not null;

    private bool CanVerifyInCs2(ConditionTarget? target) =>
        CanVerify(IsVerifying, CanVerifyMoment?.Invoke() ?? false, ResolveVerifyTick(target));

    /// <summary>
    ///     Verifies the right-clicked trigger's firing in CS2. Resolves the frame-clock tick for
    ///     <paramref name="target" /> + the optional spectate name, sets the busy flag (blocking
    ///     re-entry), and awaits the shell-wired handler. On a <c>false</c> return (session dropped
    ///     mid-seek) it surfaces an honest inline note; the shell chip is the primary failure surface.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanVerifyInCs2))]
    private async Task VerifyInCs2Async(ConditionTarget? target)
    {
        int? tick = ResolveVerifyTick(target);
        if (tick is null || VerifyMomentHandler is null)
        {
            return;
        }

        string? spectateName = VerifySpectateName;
        IsVerifying = true;
        try
        {
            bool arrived = await VerifyMomentHandler(tick.Value, spectateName, CancellationToken.None);
            if (!arrived)
            {
                StatusText = "Couldn't verify in CS2 — the sync session dropped. See the CS2 chip in the status bar.";
            }
        }
        finally
        {
            IsVerifying = false;
        }
    }

    // ── Target → firing tick ───────────────────────────────────────────────────

    /// <summary>
    ///     The frame-clock tick to verify for a menu target:
    ///     <list type="bullet">
    ///         <item>
    ///             an <b>edge</b> is the trigger itself — its recorded fire nearest at-or-before the
    ///             playhead (else its first);
    ///         </item>
    ///         <item>
    ///             a <b>node</b> — the nearest fire among the trigger edges that activate it (else the
    ///             playhead — a context/root node has no incoming trigger, and per-player fires live in the
    ///             tables, not the game-scoped <c>_appliedByEdgeKey</c>, so those fall back too);
    ///         </item>
    ///         <item>no target / no recorded fire — the current step-through position.</item>
    ///     </list>
    ///     Every branch maps a MESSAGE index → its frame's ServerTick (frame clock, AS-IS).
    /// </summary>
    internal int? ResolveVerifyTick(ConditionTarget? target)
    {
        IReadOnlyList<int>? fires = TriggerFireMessagesFor(target);
        if (fires is { Count: > 0 } && NearestFireMessageIndex(fires, CurrentMessageIndex) is { } fireMsg
                                    && ResolveFrameClockTick(_messageList, fireMsg) is { } fireTick)
        {
            return fireTick;
        }

        return CurrentFrameClockTick;
    }

    // The trigger-fire MESSAGE indices for a menu target: an edge's own applied fires, or the union of the
    // applied fires of every trigger edge that activates a node. Null/empty when the element has no recorded
    // game-scoped trigger fire. Same message-index space as CurrentMessageIndex / _messageList.
    private IReadOnlyList<int>? TriggerFireMessagesFor(ConditionTarget? target)
    {
        switch (target)
        {
            case { Kind: GraphBreakpointTarget.Edge, Edge: { } edge }:
                return _appliedByEdgeKey.GetValueOrDefault(EdgeKey(edge));

            case { Kind: GraphBreakpointTarget.Node, Node: { } node }:
                List<int>? union = null;
                foreach (KeyValuePair<(string Source, string Dest, string Label, string? Condition),
                             IReadOnlyList<int>> kv in _appliedByEdgeKey)
                {
                    if (kv.Key.Dest == node.Name && kv.Value.Count > 0)
                    {
                        (union ??= []).AddRange(kv.Value);
                    }
                }

                return union;

            default:
                return null;
        }
    }

    // ── Pure resolvers (unit-tested; see AnalysisVerifyInCs2Tests) ─────────────

    /// <summary>
    ///     The trigger fire to verify from a candidate set: the latest fire at or before the current
    ///     playhead (the fire the user has stepped to / most recently passed), or the first fire when the
    ///     playhead sits before all of them. Null only when the set is empty.
    /// </summary>
    internal static int? NearestFireMessageIndex(IReadOnlyList<int> fireMessageIndices, int currentMessageIndex)
    {
        int? atOrBefore = null;
        int? first = null;
        foreach (int fire in fireMessageIndices)
        {
            if (first is null || fire < first)
            {
                first = fire;
            }

            if (fire <= currentMessageIndex && (atOrBefore is null || fire > atOrBefore))
            {
                atOrBefore = fire;
            }
        }

        return atOrBefore ?? first;
    }

    /// <summary>
    ///     The frame-clock tick backing message <paramref name="currentMessageIndex" />, or null when the
    ///     index is unpositioned / out of range. The frame's <see cref="DemoFrame.ServerTick" /> is the
    ///     frame clock — passed to the engine unmodified.
    /// </summary>
    internal static int? ResolveFrameClockTick(
        IReadOnlyList<(DemoFrame Frame, NetMessage Message)>? messageList, int currentMessageIndex) =>
        messageList is not null && currentMessageIndex >= 0 && currentMessageIndex < messageList.Count
            ? messageList[currentMessageIndex].Frame.ServerTick
            : null;

    /// <summary>
    ///     The raw in-demo name of the filter-selected player (a real slot), or null for "All players" /
    ///     no selection. The name is the player's materialization-time in-demo name — the exact roster
    ///     string CSVG spectates by.
    /// </summary>
    internal static string? ResolveSpectateName(PlayerFilterOption? selectedPlayer) =>
        selectedPlayer is { Slot: >= 0 } player ? player.Name : null;

    // ── Test seams (App.Tests via InternalsVisibleTo; not used by production code) ──

    /// <summary>Positions a synthetic message list so tick resolution runs without parsing a demo.</summary>
    internal void SetVerifyPositionForTests(
        IReadOnlyList<(DemoFrame Frame, NetMessage Message)> messages, int currentIndex)
    {
        _messageList = messages;
        CurrentMessageIndex = currentIndex;
    }

    /// <summary>Seeds an edge's applied-fire message indices so target→fire routing can be tested.</summary>
    internal void SetVerifyEdgeFiresForTests(
        (string Source, string Dest, string Label, string? Condition) edgeKey, IReadOnlyList<int> fires) =>
        _appliedByEdgeKey[edgeKey] = fires;
}

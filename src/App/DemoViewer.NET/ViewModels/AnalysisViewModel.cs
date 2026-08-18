#region

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Yaml;
// The v2 model also defines a TriggerDef; explicit aliases keep the v1 Config.TriggerDef
// references in this file unambiguous.
using RulesetDoc = CS2DemoKit.Analysis.RulesetsV2.Model.RulesetDoc;
using StatDef = CS2DemoKit.Analysis.RulesetsV2.Model.StatDef;
using TallyThreshold = CS2DemoKit.Analysis.RulesetsV2.Model.TallyThreshold;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Debugging;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.Visualization;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     ViewModel for the Analysis Engine tab.
///     <para>
///         Evaluation produces a per-message snapshot of node states, allowing the user to step
///         through the analysis engine's processing one message at a time via
///         <see cref="PreviousMessageCommand" /> / <see cref="NextMessageCommand" />. The graph
///         redraws on each step and <see cref="CurrentCardList" /> shows the message card for the
///         current message.
///     </para>
/// </summary>
public sealed partial class AnalysisViewModel : ViewModelBase, IDisposable
{
    // Diagnostics-pillar logger (v0.6.0 — the failure surfaces show clean text, this carries the
    // real exception). Lazy: the ambient factory is wired after construction.
    private ILogger? _diagLog;
    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.Analysis");

    // Upper bound on the pre-warm fire-frame set. A normal match's breakpointable events span a few
    // thousand distinct frames; far above that means a high-frequency edge whose full capture would bloat
    // the cache, so pre-warm declines and breakpoints on it lazy-build their own narrower set.
    private const int PrewarmFrameCap = 12_000;
    private static readonly IReadOnlySet<string> _emptyChainKeys = new HashSet<string>();

    // A per-fire accessor that always resolves to null — the fallback when a Recompute closure is ever
    // invoked without a positioned cache (not on the pending path; the host always supplies EntityAccessorAt).
    private static readonly Func<int, IEntityValueAt?> _noAccessor = _ => null;

    // Per-demo breakpoint persistence. _demoKey is the SHA-256 of the loaded demo's bytes
    // (null when no demo is loaded — that null gates the save off so Reset()'s Clear can't wipe the
    // previous demo's file). _loadingBreakpoints suppresses the save while we Add the restored set, so
    // a load doesn't immediately re-persist what it just read (one write per real user edit, not N).
    private readonly GraphBreakpointStore _breakpointStore = new();

    // Per-player / entity-relative edge conditions. The default provider registry (the same set
    // the rule builder uses) resolves `*.entity.<provider>` names. _breakpointPlayerSlot tracks the slot a
    // bare `player` reference is currently compiled against, so a selection change recomputes those hits.
    private readonly PerPlayerEntityValueProviderRegistry _perPlayerProviders =
        PerPlayerEntityValueProviderRegistry.CreateDefault();

    // The autocomplete pool for the CURRENT edit: the node set (above) when editing a node, or the
    // edited edge's event.<field> identifiers when editing an edge. UpdateConditionSuggestions filters
    // this, so the same suggestion UI serves both modes.
    private IReadOnlyList<string> _activeConditionIdentifiers = [];
    private List<GraphEdgeViewModel> _allGraphEdges = [];

    // ── Full graph (source of truth for sub-graph projection) ──────────────────
    // RunAsync builds the complete VM sets once; the chain filter renders a projected
    // subset of them via SetGraphAsync. These hold the full sets so we can re-project
    // (or restore the whole graph) without rebuilding VMs or re-evaluating.
    private List<GraphNodeViewModel> _allGraphNodes = [];
    private List<INodeGroup> _allGroups = [];
    private IReadOnlyList<PlayerTableViewModel> _allTables = [];

    // Per-edge applied (fired) message indices, keyed by (source, dest, label, conditionLabel) — the
    // default hit set for an edge breakpoint. The condition label is part of the key because one rule
    // can wire two same-event triggers between the same node pair differing only by condition (foe vs
    // friend); without it they'd collapse and a breakpoint would track the wrong fire set. ONLY
    // contains edges with a backing StateEdge (trigger edges); logic/conjunction edges are absent,
    // which is how the menu gates them out. A backed-but-never-fired edge has an empty list.
    private Dictionary<(string Source, string Dest, string Label, string? Condition), IReadOnlyList<int>> _appliedByEdgeKey = new();

    // Referenceable identifiers for the editor's autocomplete (tracked node names + value/active),
    // built once per evaluation in RunAsync. The code-behind filters this by the token at the caret.
    private IReadOnlyList<string> _availableConditionIdentifiers = [];
    private int _breakpointPlayerSlot = GraphFilterViewModel.AllPlayersSlot;

    // ── Chain summary strip ───────────────────────────────────────────────────

    private IReadOnlyList<AnalysisChainSummaryViewModel> _chainSummaries = [];

    // ── Current message card ──────────────────────────────────────────────────

    private IReadOnlyList<HarvestCardViewModel> _currentCardList = [];

    // Frame-tracking for cross-navigation
    private int _currentFrameIndex = -1;

    // ── Message navigation ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessagePositionText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextMessageCommand))]
    private int _currentMessageIndex = -1;

    // The snapshot row backing the currently-displayed message — kept so ApplyFilters (fired
    // outside a seek) can recompute surviving cells' live values when the filter toggles.
    private NodeSnapshot[]? _currentSnapshot;

    // The entity-value cache for entity-read edge conditions, plus the fire-frame union it covers. Built
    // off-thread (one entity replay) and assigned (frozen) on the UI thread; reused without a rebuild when
    // the union is unchanged (a condition edit / selection change only re-filters / recompiles). The tail +
    // token collapse a burst of recomputes to one build to the final state (mirrors RunSwapAsync).
    private IReadOnlyList<DemoFrame>? _demoFrames;
    private string? _demoKey;

    /// <summary>Validation error for <see cref="EditingConditionText" />, or <c>null</c> when valid.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyConditionCommand))]
    [NotifyPropertyChangedFor(nameof(HasConditionError))]
    private string? _editingConditionError;

    /// <summary>The expression being edited; validated live as the user types.</summary>
    [ObservableProperty]
    private string? _editingConditionText;

    /// <summary>Whether entity-check rows apply here — the target has a trigger event to scope them against.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEntityChecks))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedNote))]
    private bool _editingRowsApplicable;

    /// <summary>
    ///     Whether the editor shows the structured view (event-match box + entity rows) vs. a single
    ///     advanced free-text box. False when the saved condition can't decompose into AND-clauses (a
    ///     top-level <c>||</c>, parens — see <see cref="StructuredCondition.Decomposed" />).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEntityChecks))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedNote))]
    private bool _editingStructured;

    // The element whose condition is being edited. A single field (vs the old _editingNode XOR
    // _editingEdge pair) makes the "exactly one is being edited" invariant structural — the source of
    // the earlier CanApplyCondition dead-disable bug.
    private ConditionTarget? _editingTarget;

    /// <summary>The trigger event scoping the entity rows (a node's input event; <c>null</c> for an edge).</summary>
    [ObservableProperty]
    private string? _editingTriggerEvent;

    private List<EntityCheckRowViewModel.Choice> _editProviderOptions = [];
    private IReadOnlySet<string> _editSlotFields = new HashSet<string>();

    // Edit-scoped scope metadata, set in BeginEdit: the slot prefix (input.<event>. for nodes, "" for
    // edges), the trigger event's *Slot field names, and the dropdown options the rows draw from.
    private string _editSlotPrefix = "";
    private List<EntityCheckRowViewModel.Choice> _editSubjectOptions = [];

    // Aborts an in-flight entity-cache replay when a newer recompute (or demo reset) supersedes it.
    private CancellationTokenSource? _entityBuildCts;
    private HashSet<int> _entityCacheFrames = [];
    private Task _entityRecomputeTail = Task.CompletedTask;
    private int _entityRecomputeToken;

    // Diagnostics tab plumbing. Retained from the last RunAsync build so the
    // Diagnostics tab can READ per-layer profiling snapshots and re-invoke evaluation with a live
    // listener attached ("Re-run captured"). Both are cleared in ClearFullGraph() alongside
    // _demoFrames so a reload can't hand back stale state. The scanner retention keeps its
    // EntityStateLayer (and precomputed digests) alive for the session — a modest working-set
    // cost accepted unconditionally rather than coupling App code to a parser compile symbol.

    private EntityValueCache? _entityValueCache;

    // Determinate eval progress 0..1. Driven by StateGraphEvaluator's per-frame
    // IProgress callback (marshaled to the UI thread) so the load shows a moving bar instead of an
    // indeterminate "Evaluating…" spinner.
    [ObservableProperty]
    private double _evaluationProgress;

    // Per-edge event metadata (CLR event type + field accessors) for edges whose Label resolves to a
    // registered game event or net message — the basis for conditional edge breakpoints (event.<field>
    // predicates). Same 4-tuple key as _appliedByEdgeKey. Entity-change-backed edges DON'T appear here
    // (their Label isn't a registry event), so they stay default-only — that's the second gate
    // (SupportsCondition), independent of IsBreakpointable.
    private Dictionary<(string Source, string Dest, string Label, string? Condition),
        (Type EventType, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields)> _eventMetaByEdgeKey = new();

    private Dictionary<int, int>? _firstMessageByFrame; // frameIdx → first global msg idx
    private Dictionary<DemoFrame, int>? _frameIndexByFrame; // DemoFrame → frameIdx

    // ── Graph visualization ───────────────────────────────────────────────────

    private IReadOnlyList<GraphNodeViewModel> _graphNodes = [];

    private GraphViewModel _graphViewModel = new();

    /// <summary>Whether any autocomplete suggestion is currently offered (drives the list's visibility).</summary>
    [ObservableProperty]
    private bool _hasConditionSuggestions;

    [ObservableProperty]
    private bool _hasCurrentCard;

    // ── Breakpoint-driven navigation (Run / Continue) ─────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueBackCommand))]
    private bool _hasGraphBreakpoints;

    // ── Breakpoint list panel ─────────────────────────────────────────────────

    /// <summary>Whether the breakpoint-list overlay is open (driven by the toolbar toggle).</summary>
    [ObservableProperty]
    private bool _isBreakpointListOpen;

    /// <summary>
    ///     True while an entity-state replay is in flight — the one-time pre-warm at demo load, or a rebuild
    ///     kicked by a new/edited entity-read breakpoint. Drives the toolbar "computing entity state…"
    ///     indicator so the ~one-time replay reads as working, not as a broken/empty breakpoint.
    /// </summary>
    [ObservableProperty]
    private bool _isComputingEntityCache;

    // ── Rule diagnostics (P1-2.2) ─────────────────────────────────────────────

    /// <summary>Whether the rule-diagnostics overlay is open.</summary>
    [ObservableProperty]
    private bool _isDiagnosticsOpen;

    // ── Conditional-breakpoint editor (in-tab overlay) ────────────────────────

    /// <summary>True while the condition editor overlay is open.</summary>
    [ObservableProperty]
    private bool _isEditingCondition;

    // ── Visual node picker (click a graph node → append it to the condition) ──────

    /// <summary>
    ///     True while the "select a node" gesture is armed: the graph swaps to crosshair pick mode and
    ///     the next node click appends that node's current-state snippet to the condition. Two-way
    ///     bound to the editor's pick toggle and (one-way) to <c>GraphView.PickMode</c>.
    /// </summary>
    [ObservableProperty]
    private bool _isPickingNode;
    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RerunAnalysisCommand))]
    private bool _isRunning;

    private bool _loadingBreakpoints;

    private IReadOnlyList<(DemoFrame Frame, NetMessage Message)>? _messageList;

    // ── Evaluation data ───────────────────────────────────────────────────────

    private SnapshotTable? _messageSnapshots; // [globalMsgIdx, nodeIdx]

    private IReadOnlyList<PlayerTableViewModel> _playerTables = [];

    // The chain selection currently RENDERED (vs Filter.SelectedChainKeys, the live UI state). Lets
    // ApplyFilters tell a chain change (needs a relayout swap) from a player-only change. Empty =
    // full graph rendered. Cleared on reload so a fresh demo always re-swaps.
    private HashSet<string> _renderedChainKeys = new(StringComparer.Ordinal);

    // The player slot currently RENDERED into the tables (vs Filter.SelectedPlayer, the live UI
    // state). null = all players shown. Selecting a player REMOVES the other rows (structural), so a
    // change here routes through the swap/relayout like a chain change — not a cheap cell repaint.
    private int? _renderedPlayerSlot;

    // The graph Root VM (always included in a sub-graph so chains stay anchored to a common origin).
    private GraphNodeViewModel? _rootNode;

    private IReadOnlyList<RuleDiagnostic> _ruleDiagnostics = [];

    private IReadOnlyList<RuleFireStat> _ruleFireStats = [];

    // Owns the in-flight evaluation's lifetime; a new RunAsync cancels and replaces it (P1-5.2).
    private CancellationTokenSource? _runCts;

    // Suppresses re-compose/validate while BeginEdit is seeding the editor from a saved condition (the
    // text + each row would otherwise each fire a recompute); one runs at the end of seeding.
    private bool _seedingEditor;

    [ObservableProperty]
    private string _statusText = "No demo loaded.";

    // Debounce for the swap: rapid multi-select toggles (click A, B, C) collapse into ONE swap to
    // {A,B,C}. This also serializes swaps — without it, overlapping async SetGraphAsync calls race
    // and CurrentLayout/Nodes can desync. Token is bumped on each request; only the latest survives.
    private int _swapRequestToken;

    // Serializes swaps so two SetGraphAsync layouts never run concurrently. All swap dispatch is on
    // the UI thread, so a plain task-tail (await the previous swap before starting the next)
    // serializes without a disposable primitive. The token (bumped synchronously per toggle) both
    // collapses click bursts and drops a swap superseded while queued behind the running one.
    private Task _swapTail = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessagePositionText))]
    private int _totalMessages;

    // FinalTrackedNodes by snapshot column — lets a node-breakpoint condition reference any other
    // tracked node (and game entity contexts) by name, resolved against the snapshot. Set in RunAsync.
    private IReadOnlyList<StateNode> _trackedNodesByColumn = [];

    // dest node VM → its immediate upstream source node VMs (built from the full edge set).
    // Drives the "+1-hop upstream" closure so a chain's nodes pull in the context/enrichment
    // nodes that feed them (those have empty ChainIds and would otherwise leave dangling edges).
    private Dictionary<GraphNodeViewModel, List<GraphNodeViewModel>> _upstreamOf = new();

    // Set once per demo load: the next entity-cache build widens to every breakpointable fire frame so the
    // replay is paid once (pre-warm) and the first entity breakpoint is instant. See RecomputeBreakpointHits.
    private bool _warmEntityCacheOnNextBuild;

    /// <summary>Initializes a new <see cref="AnalysisViewModel" /> instance.</summary>
    public AnalysisViewModel()
    {
        Filter = new GraphFilterViewModel();
        Filter.FiltersChanged += ApplyFilters;
        GraphBreakpoints.Changed += OnBreakpointsChanged;
        GraphBreakpoints.HitChanged += OnBreakpointHitChanged;
    }

    // ── Graph breakpoints (debugger affordance) ───────────────────────────────

    /// <summary>
    ///     The Analysis-graph breakpoint set + Run/Continue driver. Right-clicking a node arms a
    ///     breakpoint; <see cref="ContinueCommand" /> seeks the message timeline to the next index
    ///     where an enabled breakpoint's condition holds.
    /// </summary>
    public GraphBreakpointService GraphBreakpoints { get; } = new();

    /// <summary>The graph filter sub-VM (chain chips + player picker). Drives the no-relayout dim.</summary>
    public GraphFilterViewModel Filter { get; }

    /// <summary>
    ///     Rule-config load errors and semantic warnings for the last run — the rule author's build
    ///     log and the user's "why is my stat empty" in one list.
    /// </summary>
    public IReadOnlyList<RuleDiagnostic> RuleDiagnostics
    {
        get => _ruleDiagnostics;
        private set
        {
            if (SetProperty(ref _ruleDiagnostics, value))
            {
                OnPropertyChanged(nameof(HasRuleDiagnostics));
                OnPropertyChanged(nameof(RuleDiagnosticsLabel));
                OnPropertyChanged(nameof(HasDiagnosticsPanelContent));
            }
        }
    }

    /// <summary>True when the last run produced any diagnostic.</summary>
    public bool HasRuleDiagnostics => _ruleDiagnostics.Count > 0;

    /// <summary>
    ///     Toolbar toggle label: the diagnostic count when there are issues, otherwise the
    ///     fire-badge entry point (the toggle stays reachable on lint-free runs — authoring
    ///     visibility is work item 0.2's whole point).
    /// </summary>
    public string RuleDiagnosticsLabel => _ruleDiagnostics.Count > 0
        ? $"⚠ {_ruleDiagnostics.Count} rule issue(s)"
        : "✓ rule fires";

    /// <summary>
    ///     Per-rule fire-count badges for the last run (work item 0.2): every trigger-backed
    ///     authored rule with its total edge applies, per-player rules aggregated across
    ///     materialized players. Rebuilt by <see cref="PopulateRuleDiagnostics" /> on every run.
    /// </summary>
    public IReadOnlyList<RuleFireStat> RuleFireStats
    {
        get => _ruleFireStats;
        private set
        {
            if (SetProperty(ref _ruleFireStats, value))
            {
                OnPropertyChanged(nameof(HasRuleFireStats));
                OnPropertyChanged(nameof(HasDiagnosticsPanelContent));
            }
        }
    }

    /// <summary>True when the last run produced fire-count badges.</summary>
    public bool HasRuleFireStats => _ruleFireStats.Count > 0;

    /// <summary>Drives the toolbar toggle: diagnostics or badges, either makes the panel worth opening.</summary>
    public bool HasDiagnosticsPanelContent => HasRuleDiagnostics || HasRuleFireStats;

    // ── Delegates wired by MainViewModel ─────────────────────────────────────

    /// <summary>Builds a display card for the given message. Set by MainViewModel.</summary>
    public Func<NetMessage, HarvestCardViewModel>? CardFactory { get; set; }

    /// <summary>Chain summaries.</summary>

    public IReadOnlyList<AnalysisChainSummaryViewModel> ChainSummaries
    {
        get => _chainSummaries;
        private set => SetProperty(ref _chainSummaries, value);
    }

    /// <summary>Current card list.</summary>

    public IReadOnlyList<HarvestCardViewModel> CurrentCardList
    {
        get => _currentCardList;
        private set => SetProperty(ref _currentCardList, value);
    }

    /// <summary>Graph nodes.</summary>

    public IReadOnlyList<GraphNodeViewModel> GraphNodes
    {
        get => _graphNodes;
        private set => SetProperty(ref _graphNodes, value);
    }

    /// <summary>Graph view model.</summary>

    public GraphViewModel GraphViewModel
    {
        get => _graphViewModel;
        private set => SetProperty(ref _graphViewModel, value);
    }

    /// <summary>Message position text.</summary>
    public string MessagePositionText =>
        TotalMessages == 0 ? "" : $"msg  {CurrentMessageIndex + 1} / {TotalMessages}";

    /// <summary>
    ///     Called when message-level navigation crosses a frame boundary, with the new frame index.
    ///     Set by MainViewModel to keep the global seek controls in sync.
    /// </summary>
    public Action<int>? OnFrameSeeked { get; set; }

    /// <summary>Player tables.</summary>

    public IReadOnlyList<PlayerTableViewModel> PlayerTables
    {
        get => _playerTables;
        private set => SetProperty(ref _playerTables, value);
    }

    /// <summary>
    ///     The resolved shipped-rules directory. Surfaced read-only for the Diagnostics
    ///     tab's Session card. Pure path resolution; zero cost; no parser dependency.
    /// </summary>
    public static string RulesDirectory => RuleSetLocator.ResolveShippedRulesDirectory();

    /// <summary>
    ///     The <see cref="EntityChangeScanner" /> retained from the last evaluation, or
    ///     <c>null</c> until a demo is evaluated. The Diagnostics tab reads its profiling
    ///     snapshot — both this and <c>scanner.Layer.Tracker.GetProfilingSnapshot()</c> return
    ///     <c>default</c> (Enabled=false) in a normal build, so consumption is zero-cost there.
    /// </summary>
    public EntityChangeScanner? EntityScanner { get; private set; }

    /// <summary>
    ///     The <see cref="ParsedDemo" /> retained from the last evaluation, or <c>null</c>
    ///     until a demo is evaluated. Lets the Diagnostics tab re-invoke <see cref="RunAsync" /> with a
    ///     live capture listener attached.
    /// </summary>
    public ParsedDemo? LastEvaluatedDemo { get; private set; }

    /// <summary>Non-null when the timeline halted on a breakpoint; drives the toolbar "stopped" label.</summary>
    public string? StoppedText { get; private set; }

    /// <summary>Name of the node whose condition is being edited (shown in the editor header).</summary>
    public string EditingConditionTarget { get; private set; } = "";

    /// <summary>
    ///     The editing node's value kind (<c>bool</c> / <c>number</c> / <c>text</c>, or empty), shown
    ///     next to its name so the user knows what <c>value</c> means for it.
    /// </summary>
    public string EditingConditionTargetKind { get; private set; } = "";

    /// <summary>The editor header: the target node name plus its kind (when known).</summary>
    public string EditingConditionHeader =>
        string.IsNullOrEmpty(EditingConditionTargetKind)
            ? $"BREAKPOINT CONDITION — {EditingConditionTarget}"
            : $"BREAKPOINT CONDITION — {EditingConditionTarget}  ·  {EditingConditionTargetKind}";

    /// <summary>A kind-specific hint for what <c>value</c> means (e.g. "value is a bool — try value == true").</summary>
    public string EditingValueHint { get; private set; } = "";

    /// <summary>Whether a value hint is available for the current target.</summary>
    public bool HasValueHint => !string.IsNullOrEmpty(EditingValueHint);

    /// <summary>
    ///     Mode-appropriate watermark for the condition box: node value-expression examples when
    ///     editing a node, event-field examples when editing an edge (so the empty-box hint never
    ///     suggests the wrong grammar — `value`/node names don't apply to an edge's event condition).
    /// </summary>
    public string EditingConditionWatermark => _editingTarget is { Kind: GraphBreakpointTarget.Edge }
        ? "event.IsHeadshot == true   •   event.Weapon == \"ak47\"   •   event.DmgHealth > 50      (blank = break on every fire)"
        : "value >= 3   •   value > 0 && OtherNode   •   entity.game.freeze_period      (blank/'active' = node becomes active)";

    /// <summary>Whether the current editor text has a validation error.</summary>
    public bool HasConditionError => EditingConditionError is not null;

    /// <summary>
    ///     The structured entity-check rows for the current edit (the scope-aware editor). Each row is a
    ///     subject (the trigger event's <c>*Slot</c> players or the selected player) · provider · operator ·
    ///     value; the host composes them into the canonical condition string. Empty when the condition is
    ///     advanced free text (<see cref="EditingStructured" /> is false).
    /// </summary>
    public ObservableCollection<EntityCheckRowViewModel> EditingEntityRows { get; } = [];

    /// <summary>Show the structured entity-check rows section (decomposable AND rows apply to this target).</summary>
    public bool ShowEntityChecks => EditingStructured && EditingRowsApplicable;

    /// <summary>
    ///     Show the "advanced expression — edit as text" note: rows apply here, but this particular
    ///     condition couldn't decompose into them (a top-level <c>||</c> / parens), so it's plain text.
    /// </summary>
    public bool ShowAdvancedNote => EditingRowsApplicable && !EditingStructured;

    /// <summary>The legend of subjects in scope for the entity rows (shown under the rows).</summary>
    public string EditingScopeLegend { get; private set; } = "";

    /// <summary>The input events a node could scope rows against (for a future selector); empty for an edge.</summary>
    public IReadOnlyList<string> EditingTriggerEvents { get; private set; } = [];

    /// <summary>
    ///     Whether the node-picker affordance applies to the current edit — true only when editing a
    ///     NODE condition (picking a graph node makes no sense for an edge's event-field condition).
    ///     Bound to the pick toggle's visibility.
    /// </summary>
    public bool CanPickNode => _editingTarget?.SupportsPicker ?? false;

    // ── Inline autocomplete suggestions ───────────────────────────────────────────

    /// <summary>
    ///     The live-filtered identifier suggestions for the condition editor (bound to the inline
    ///     suggestion list). Populated by <see cref="UpdateConditionSuggestions" /> from the token the
    ///     code-behind extracts at the caret.
    /// </summary>
    public ObservableCollection<string> ConditionSuggestions { get; } = new();

    /// <summary>Cancels any in-flight evaluation / entity replay and releases their token sources.</summary>
    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _entityBuildCts?.Cancel();
        _entityBuildCts?.Dispose();
        _entityBuildCts = null;
    }

    // The filter's selected player slot, or AllPlayersSlot (-1) when none / "All players" is selected.
    // Bound for a bare `player` reference in an edge condition; a negative value short-circuits such a
    // condition to no hits (the entity coalesce-to-default would otherwise make `< N` match every fire).
    private int SelectedPlayerSlotOrAll() => Filter.SelectedPlayer?.Slot ?? GraphFilterViewModel.AllPlayersSlot;

    /// <summary>
    ///     Opens a diagnostic's source file in the editor (VS Code, falling back to the OS
    ///     handler), jumping to the diagnostic's line when the loader captured one.
    /// </summary>
    [RelayCommand]
    private void OpenDiagnosticFile(RuleDiagnostic diagnostic)
    {
        if (diagnostic.FilePath is not null
            && !OpenExternal.OpenLocalFile(diagnostic.FilePath, diagnostic.Line, diagnostic.Column))
        {
            // v0.6.0: a silent no-op click reads as broken — say what didn't open.
            StatusText = $"Couldn't open {diagnostic.FilePath} — no editor or file handler responded.";
        }
    }

    /// <summary>Closes the diagnostics overlay.</summary>
    [RelayCommand]
    private void CloseDiagnostics() => IsDiagnosticsOpen = false;

    /// <summary>
    ///     Raised on the UI thread after each successful evaluation with the result and the demo it
    ///     came from. The Stats tab projects its scoreboard/round tables from this — the engine's
    ///     "same data, multiple consumers" seam, without the consumer coupling to RunAsync internals.
    /// </summary>
    public event Action<AnalysisRun, ParsedDemo>? EvaluationCompleted;

    // ── Reset ─────────────────────────────────────────────────────────────────

    /// <summary>Reset.</summary>
    public void Reset()
    {
        // The demo is going away — abort any in-flight evaluation with it (P1-5.2).
        _runCts?.Cancel();

        RuleDiagnostics = [];
        RuleFireStats = [];
        IsDiagnosticsOpen = false;
        GraphNodes = [];
        ChainSummaries = [];
        CurrentCardList = [];
        HasCurrentCard = false;
        CurrentMessageIndex = -1;
        TotalMessages = 0;
        _messageSnapshots = null;
        _messageList = null;
        _trackedNodesByColumn = [];
        _availableConditionIdentifiers = [];
        _appliedByEdgeKey = new Dictionary<(string, string, string, string?), IReadOnlyList<int>>();
        _eventMetaByEdgeKey = new Dictionary<(string, string, string, string?), (Type, Type?, IReadOnlyDictionary<string, EventFieldAccessor>)>();
        CloseConditionEditor();
        _firstMessageByFrame = null;
        _frameIndexByFrame = null;
        _currentFrameIndex = -1;
        _currentSnapshot = null;
        PlayerTables = [];
        ClearFullGraph();
        Filter.Clear();
        // Drop graph breakpoints on reset so stale node names from a previous demo can't linger. Null
        // the demo key FIRST: the Clear below raises Changed, and a null key gates PersistBreakpoints
        // off — otherwise we'd save an empty set under the previous demo's key and wipe its file.
        _demoKey = null;
        GraphBreakpoints.Clear();
        _graphViewModel = new GraphViewModel();
        OnPropertyChanged(nameof(GraphViewModel));
        StatusText = "No demo loaded.";
    }

    // Drops the full-graph projection source of truth (set in RunAsync). Called on reset / reload
    // so a stale full set can't be re-projected against a new demo's snapshots.
    private void ClearFullGraph()
    {
        _allGraphNodes = [];
        _allGraphEdges = [];
        _allGroups = [];
        _allTables = [];
        _upstreamOf = new Dictionary<GraphNodeViewModel, List<GraphNodeViewModel>>();
        _rootNode = null;
        // RunAsync renders the full graph directly (empty rendered-chain set), so a subsequent
        // player-only change must not spuriously swap; a reload must re-swap from empty.
        _renderedChainKeys = new HashSet<string>(StringComparer.Ordinal);
        _renderedPlayerSlot = null; // full graph renders all rows; a reload must re-swap from "all"
        _swapRequestToken++; // cancel any in-flight debounced swap from the previous demo

        // Drop the entity-read cache and cancel any in-flight rebuild from the previous demo so
        // a stale build can't hand back hits against the new demo's frames.
        _demoFrames = null;
        // Diagnostics retention cleared here (with _demoFrames) so a reload can't hand back stale
        // profiling snapshots or re-run a previous demo.
        EntityScanner = null;
        LastEvaluatedDemo = null;
        _entityValueCache = null;
        _entityCacheFrames = [];
        _warmEntityCacheOnNextBuild = false;
        IsComputingEntityCache = false;
        _entityRecomputeToken++;
        // Abort (not just supersede) any in-flight cache replay — see RunEntityRecomputeAsync.
        _entityBuildCts?.Cancel();
        // _lastEvaluatedDemo was cleared above — the re-run button must gray out with it.
        RerunAnalysisCommand.NotifyCanExecuteChanged();
    }

    // ── RunAsync ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Evaluates the state graph for <paramref name="demo" /> and renders it. <paramref name="demoKey" />
    ///     (the SHA-256 of the demo's bytes) keys per-demo breakpoint persistence; <c>null</c> (tests, WASM,
    ///     or a caller without the bytes) runs in-memory only — no breakpoints are restored or saved.
    /// </summary>
    public async Task RunAsync(ParsedDemo demo, string? demoKey = null)
    {
        // One live evaluation at a time: a new run (reload, second demo, re-run) cancels the
        // in-flight one. The canceled run unwinds through its own OperationCanceledException
        // handler below; its cleared-at-entry state is exactly the clean "not analyzed" state.
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        CancellationToken runToken = _runCts.Token;

        IsRunning = true;
        EvaluationProgress = 0;
        StatusText = "Evaluating state graph…";
        RuleDiagnostics = [];
        RuleFireStats = [];
        IsDiagnosticsOpen = false;
        _demoKey = demoKey;
        _demoFrames = demo.Frames; // backs the entity-read cache replay

        GraphNodes = [];
        ChainSummaries = [];
        CurrentCardList = [];
        HasCurrentCard = false;
        CurrentMessageIndex = -1;
        TotalMessages = 0;
        _messageSnapshots = null;
        _messageList = null;
        _firstMessageByFrame = null;
        _frameIndexByFrame = null;
        _currentFrameIndex = -1;
        _currentSnapshot = null;
        ClearFullGraph();
        Filter.Clear();

        try
        {
            (BuildResult build, RuleConfigLoadResult loadedRules) = BuildFromConfig(demo);
            // Diagnostics tab retention. The scanner is otherwise a discarded local
            // here; keep it (and the demo) so the Diagnostics profiling/re-run panels can read it for the session.
            EntityScanner = build.EntityScanner;
            LastEvaluatedDemo = demo;

            // Progressive reveal: paint the graph SKELETON (nodes + edges + groups — all known
            // right after Build, ~0.1 s) so the user sees the rule-graph topology during the eval wait
            // instead of a blank canvas. Best-effort and self-contained: the authoritative post-eval
            // render below is left untouched and replaces this with the fully-evaluated graph (live
            // values + per-player tables), so the final graph cannot regress.
            await RenderGraphSkeletonAsync(build);

            // Created on the UI thread so Progress<T>.Report marshals the per-frame eval progress
            // back here off the background evaluation thread (determinate progress bar).
            Progress<double> evalProgress = new(p => EvaluationProgress = p);
            AnalysisRun run = await Task.Run(() => DemoAnalysis.Evaluate(demo, build,
                new AnalysisOptions
                {
                    Progress = evalProgress,
                    CancellationToken = runToken
                }), runToken);
            EvaluationResult result = run.Snapshots!;
            EvaluationProgress = 1;

            _messageSnapshots = result.MessageSnapshots;
            _messageList = result.Messages;
            _trackedNodesByColumn = result.FinalTrackedNodes;
            // Autocomplete pool for the condition editor: every referenceable tracked node + the
            // value/active keywords (built once per evaluation; the universe doesn't change mid-demo).
            _availableConditionIdentifiers = NodeBreakpointConditions.AvailableIdentifiers(result.FinalTrackedNodes);

            // Build frame-index lookup maps.
            Dictionary<DemoFrame, int> frameIndexByFrame = new(ReferenceEqualityComparer.Instance);
            Dictionary<int, int> firstMessageByFrame = new();

            for (int i = 0; i < demo.Frames.Count; i++)
            {
                frameIndexByFrame[demo.Frames[i]] = i;
            }

            for (int msgIdx = 0; msgIdx < result.Messages.Count; msgIdx++)
            {
                DemoFrame frame = result.Messages[msgIdx].Frame;
                if (frameIndexByFrame.TryGetValue(frame, out int fi) && !firstMessageByFrame.ContainsKey(fi))
                {
                    firstMessageByFrame[fi] = msgIdx;
                }
            }

            _frameIndexByFrame = frameIndexByFrame;
            _firstMessageByFrame = firstMessageByFrame;

            // ── Node view-models ────────────────────────────────────────────
            // Map each tracked StateNode to its absolute snapshot column. FinalTrackedNodes is the
            // column authority for MessageSnapshots (a superset of build.Nodes — it appends
            // materialized per-player nodes), so stamping each node VM with this index decouples
            // its state lookup from its position in the rendered list. This is the keystone that
            // lets us render an arbitrary subset (a chain sub-graph) while the full evaluation —
            // and every node's correct snapshot column — stays intact.
            Dictionary<StateNode, int> snapshotIndexByNode = new(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < result.FinalTrackedNodes.Count; i++)
            {
                snapshotIndexByNode[result.FinalTrackedNodes[i]] = i;
            }

            Dictionary<StateNode, GraphNodeViewModel> nodeVmByNode = new(ReferenceEqualityComparer.Instance);
            List<GraphNodeViewModel> nodeVms = new();

            foreach (StateNode node in build.Nodes)
            {
                IReadOnlySet<string> chainIds =
                    build.NodeChains is not null && build.NodeChains.TryGetValue(node, out IReadOnlySet<string>? keys)
                        ? keys
                        : _emptyChainKeys;

                GraphNodeViewModel vm = new(node.Name, node is RootNode, node.Subtitle)
                {
                    IsActive = node.IsActive,
                    DisplayValue = node.GetDisplayValue(),
                    ChainIds = chainIds,
                    TrackedIndex = snapshotIndexByNode.GetValueOrDefault(node, -1)
                };
                nodeVmByNode[node] = vm;
                nodeVms.Add(vm);
            }

            // ── Edge view-models ────────────────────────────────────────────
            List<GraphEdgeViewModel> edgeVms = new();
            foreach (GraphEdgeDescriptor e in build.Edges)
            {
                if (!nodeVmByNode.TryGetValue(e.Source, out GraphNodeViewModel? srcVm))
                {
                    continue;
                }

                if (!nodeVmByNode.TryGetValue(e.Destination, out GraphNodeViewModel? dstVm))
                {
                    continue;
                }

                edgeVms.Add(new GraphEdgeViewModel(srcVm, dstVm, e.Label, e.Effect, e.ConditionLabel));
            }

            // ── Edge → applied-message-index map (edge breakpoint default hits) ──
            // descriptor → backing StateEdge (build.EdgeBacking) → applied indices
            // (result.AppliedMessagesByEdge). Keyed by the (source, dest, label) identity the
            // breakpoint uses. Only trigger-backed descriptors are present — that's the gate that
            // keeps logic/conjunction edges (no StateEdge) from arming a never-firing breakpoint.
            Dictionary<(string, string, string, string?), IReadOnlyList<int>> appliedByEdgeKey = new();
            Dictionary<(string, string, string, string?), (Type, Type?, IReadOnlyDictionary<string, EventFieldAccessor>)> eventMetaByEdgeKey = new();
            if (build.EdgeBacking is not null)
            {
                EventRegistry registry = EventRegistry.Build(); // cheap reflection over the fixed event set
                foreach (GraphEdgeDescriptor e in build.Edges)
                {
                    if (!build.EdgeBacking.TryGetValue(e, out StateEdge? stateEdge))
                    {
                        continue;
                    }

                    (string, string, string, string?) key = (e.Source.Name, e.Destination.Name, e.Label, e.ConditionLabel);

                    IReadOnlyList<int> applied = [];
                    if (result.AppliedMessagesByEdge is not null
                        && result.AppliedMessagesByEdge.TryGetValue(stateEdge, out List<int>? hits))
                    {
                        applied = hits;
                    }

                    appliedByEdgeKey[key] = applied;

                    // Condition support: only edges whose Label is a registered game event / net
                    // message expose typed fields for an event.<field> predicate. Entity-change edges
                    // (Label = an entity context name) don't resolve → default-only. A game event
                    // compiles against the GameEvent envelope (ParameterType) so per-fire transport
                    // (event.tick) resolves; a net message has no envelope — its payload is the
                    // parameter (null).
                    EventRegistration? ev = registry.GetEvent(e.Label);
                    if (ev is not null)
                    {
                        eventMetaByEdgeKey[key] = (ev.EventType, typeof(GameEvent), ev.Fields);
                    }
                    else if (registry.GetNetMessage(e.Label) is { } nm)
                    {
                        eventMetaByEdgeKey[key] = (nm.PayloadType, null, nm.Fields);
                    }
                }
            }

            _appliedByEdgeKey = appliedByEdgeKey;
            _eventMetaByEdgeKey = eventMetaByEdgeKey;

            // ── Groups ──────────────────────────────────────────────────────
            List<INodeGroup> groups = new();
            foreach (NodeGroupHint hint in build.GroupHints)
            {
                List<IGraphNode> members = new();
                foreach (StateNode member in hint.Members)
                {
                    if (nodeVmByNode.TryGetValue(member, out GraphNodeViewModel? vm))
                    {
                        members.Add(vm);
                    }
                }

                if (members.Count > 0)
                {
                    groups.Add(new AnalysisNodeGroup(hint.GroupName, members));
                }
            }

            // ── Per-player tables (one per template × column-group) ──────────
            // Column edges are built INSIDE BuildPlayerTables' per-group loop, where the
            // group's local column list and column nodes are both in hand — so each edge's
            // ColumnIndex is LOCAL to its table (aligned 1:1 with that table's ColumnNames).
            PlayerTables = BuildPlayerTables(result, nodeVmByNode, build.Nodes);

            // ── Capture the full graph as the projection source of truth ─────
            _allGraphNodes = nodeVms;
            _allGraphEdges = edgeVms;
            _allGroups = groups;
            _allTables = PlayerTables;
            _rootNode = nodeVms.FirstOrDefault(n => n.IsRoot);
            _upstreamOf = BuildUpstreamAdjacency(edgeVms);

            // ── Set graph on visualization library (triggers MSAGL layout) ──
            // Initial render is the full graph (no chain selected yet).
            List<IGraphNode> vizNodes = nodeVms.Cast<IGraphNode>().ToList();
            List<IGraphEdge> vizEdges = edgeVms.Cast<IGraphEdge>().ToList();
            await _graphViewModel.SetGraphAsync(vizNodes, vizEdges,
                groups.Count > 0 ? groups : null,
                PlayerTables.Count > 0 ? PlayerTables.Cast<INodeTable>().ToList() : null);

            // ── Chain summaries ───────────────────────────────────────────────
            List<AnalysisChainSummaryViewModel> summaries = result.Timeline.Events
                .Select(ev => ev.ChainName).Distinct().Order()
                .Select(name => new AnalysisChainSummaryViewModel(name, result.Timeline.CountFor(name)))
                .ToList();

            GraphNodes = nodeVms;
            ChainSummaries = summaries;
            TotalMessages = result.Messages.Count;

            // ── Populate the graph filter (chips + players) ───────────────────
            PopulateFilter(build, result, summaries);

            int playerCount = result.MaterializedPlayers.Count;
            StatusText = result.Messages.Count == 0
                ? "No messages to process."
                : $"{result.Messages.Count} messages  •  {summaries.Count} chain(s)  •  {result.Timeline.Events.Count} event(s)  •  {playerCount} player node(s)";

            // Load errors + semantic warnings feed the diagnostics panel; errors pop it open
            // unprompted (a skipped user file must not be discoverable only behind a toggle).
            PopulateRuleDiagnostics(loadedRules, demo, build, result);
            if (!loadedRules.Success)
            {
                IsDiagnosticsOpen = true;
            }

            EvaluationCompleted?.Invoke(run, demo);

            if (result.Messages.Count > 0)
            {
                SeekToMessage(0);
            }

            // Pre-warm: the next entity-cache build widens to every breakpointable fire frame, so the
            // one-time entity replay is paid here (in the background, with the toolbar indicator) and the
            // first entity breakpoint the user adds is instant. Restoring an entity breakpoint below folds
            // into this same single build (no second replay). Set BEFORE the restore so it's consumed by the
            // restore's recompute when there is one, or kicks an empty pre-warm build when there isn't.
            // DESKTOP ONLY: the WASM host is single-threaded, so an unconditional ~14s Task.Run on every load
            // would freeze the tab. There, the entity build stays lazy/opt-in (kicked only when the user adds
            // an entity breakpoint, as it was before pre-warm) — the toolbar indicator still covers that.
            _warmEntityCacheOnNextBuild = !OperatingSystem.IsBrowser();

            // Restore this demo's persisted breakpoints and bind them to the fresh evaluation
            // (recompute hit indices + repaint markers on the new node VMs). Falls back to a clean
            // rebind of the empty set when there's no key / nothing saved.
            LoadPersistedBreakpoints();

            string? dumpDir = Environment.GetEnvironmentVariable("DEMOVIEWER_GRAPH_DUMP_DIR");
            if (!string.IsNullOrEmpty(dumpDir))
            {
                try
                {
                    Directory.CreateDirectory(dumpDir);
                    string pngPath = Path.Combine(dumpDir, "analysis-graph.png");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        GraphScreenshot.ExportToPng(_graphViewModel, pngPath));
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(dumpDir, "analysis-graph-error.txt"), ex.ToString());
                    }
                    catch
                    {
                        // Developer-only dump path (DEMOVIEWER_GRAPH_DUMP_DIR): if even the error
                        // file can't be written the disk/path is the problem — nothing to do.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run (or the demo was closed). The entry-point reset already put
            // the tab in the clean "not analyzed" state; only say so if no newer run has started.
            if (runToken == _runCts?.Token)
            {
                StatusText = "Analysis canceled.";
            }
        }
        catch (RuleConfigException rce)
        {
            // Shipped-tier rule errors hard-fail the run — surface every collected
            // error in the diagnostics panel instead of one truncated status line.
            RuleDiagnostics = rce.Errors
                .Select(RuleDiagnostic.FromError)
                .ToList();
            IsDiagnosticsOpen = true;
            StatusText = $"Analysis failed: {rce.Errors.Count} rule-config error(s) — see the ⚠ panel.";
        }
        catch (Exception ex)
        {
            // Unlike the RuleConfigException branch above, this used to show raw ex.Message and
            // never log (v0.6.0 fix): clean text to the user, full exception to Diagnostics.
            AppLog.OperationFailed(DiagLog, "run the analysis", ex);
            StatusText = UserFacingError.Describe("run the analysis", ex);
        }
        finally
        {
            // Only the CURRENT run owns the busy flag — a canceled run racing a fresh one must not
            // clear the newcomer's spinner.
            if (runToken == _runCts?.Token)
            {
                IsRunning = false;
            }
        }
    }

    /// <summary>
    ///     Progressive-reveal pre-render: paints the graph skeleton from
    ///     <see cref="BuildResult" /> alone — nodes, edges, and group hints are all known after Build,
    ///     before the multi-second evaluation. Node values are the pre-eval defaults (filled in by the
    ///     authoritative post-eval render in <see cref="RunAsync" />); per-player tables aren't included
    ///     because they need the evaluated result. Best-effort: any failure is swallowed so a cosmetic
    ///     pre-render can never abort the load — the post-eval render is the source of truth.
    /// </summary>
    private async Task RenderGraphSkeletonAsync(BuildResult build)
    {
        try
        {
            // Shared skeleton conversion — the Workbench's ruleset-structure graph reuses the same helper.
            RuleGraphSkeleton.Skeleton skeleton = RuleGraphSkeleton.Build(build);
            await _graphViewModel.SetGraphAsync(
                skeleton.Nodes,
                skeleton.Edges,
                skeleton.Groups);
        }
        catch
        {
            // Cosmetic pre-render only; the authoritative post-eval render follows regardless.
        }
    }

    /// <summary>Seek to first message of frame.</summary>
    public void SeekToFirstMessageOfFrame(int frameIndex)
    {
        if (_firstMessageByFrame is null)
        {
            return;
        }

        if (!_firstMessageByFrame.TryGetValue(frameIndex, out int msgIdx))
        {
            return;
        }

        _currentFrameIndex = frameIndex;
        SeekToMessageCore(msgIdx, false);
    }

    // ── SeekToMessage ─────────────────────────────────────────────────────────

    /// <summary>Seek to message.</summary>
    public void SeekToMessage(int index) => SeekToMessageCore(index, true);

    // ── Config-driven graph builder ─────────────────────────────────────

    /// <summary>
    ///     Builds the diagnostics list for a completed run and publishes it plus the per-rule
    ///     fire-count badges onto the VM. Thin wrapper over the pure
    ///     <see cref="ComputeRuleDiagnostics" />.
    /// </summary>
    private void PopulateRuleDiagnostics(RuleConfigLoadResult loadedRules, ParsedDemo demo,
        BuildResult build, EvaluationResult result)
    {
        (List<RuleDiagnostic> diags, List<RuleFireStat> fireStats) =
            ComputeRuleDiagnostics(loadedRules, demo, build, result);
        RuleDiagnostics = diags;
        RuleFireStats = fireStats;
    }

    /// <summary>
    ///     Pure computation behind the rule-diagnostics panel: the loader's attributed errors,
    ///     plus the per-stat fire-count badge rows and never-fired lints (work item 0.2, fed by
    ///     0.1's always-on counters). Internal + static so tests can pin the contract from the
    ///     engine thread without booting the UI.
    /// </summary>
    internal static (List<RuleDiagnostic> Diagnostics, List<RuleFireStat> FireStats)
        ComputeRuleDiagnostics(RuleConfigLoadResult loadedRules, ParsedDemo demo,
            BuildResult build, EvaluationResult result)
    {
        List<RuleDiagnostic> diags = new();
        List<RuleFireStat> fireStats = new();
        Dictionary<StateNode, int> fireCountByNode = BuildFireCountByNode(build, result);

        foreach (RuleConfigError err in loadedRules.Errors)
        {
            diags.Add(RuleDiagnostic.FromError(err));
        }

        // ── Rulesets v2 badges + never-fired lint ─────────────────────────────
        // Resolution follows "never guess": a stat
        // gets a badge only when its qualified {ruleset}.{stat} node exists AND some dispatched
        // StateEdge writes it (i.e. it appears in fireCountByNode). That naturally excludes
        // compute:/live stats and count-on-flag counters — those advance via rising-edge
        // actions or round-end recomputes, not dispatch edges, so a 0× badge for them would be
        // a fabrication, not a measurement.
        foreach (RulesetDoc ruleset in loadedRules.Rulesets)
        {
            HashSet<string> seenStatIds = new(StringComparer.Ordinal);
            foreach (StatDef stat in ruleset.Stats)
            {
                AddV2FireStat(ruleset.Id, stat.Id, seenStatIds, build, result, fireCountByNode,
                    fireStats, diags);

                // tally: buckets increment their TARGET counters — the stat id itself carries
                // no counting node, the targets do. Badge each target under its own id.
                if (stat.Thresholds is { } thresholds)
                {
                    foreach (TallyThreshold threshold in thresholds)
                    {
                        AddV2FireStat(ruleset.Id, threshold.Target, seenStatIds, build, result,
                            fireCountByNode, fireStats, diags);
                    }
                }
            }
        }

        return (diags, fireStats);
    }

    /// <summary>
    ///     Emits the badge row (and, at zero, the never-fired lint) for one v2 stat id, resolved
    ///     via the qualified <c>{ruleset}.{stat}</c> key — across every materialized player for
    ///     <c>for: each_player</c> rulesets, then the game-scope map. Stats whose node no
    ///     dispatched edge writes produce no row (see the call site's rationale).
    /// </summary>
    private static void AddV2FireStat(string rulesetId, string statId, HashSet<string> seenStatIds,
        BuildResult build, EvaluationResult result, Dictionary<StateNode, int> fireCountByNode,
        List<RuleFireStat> fireStats, List<RuleDiagnostic> diags)
    {
        if (!seenStatIds.Add(statId))
        {
            return;
        }

        string qualifiedId = $"{rulesetId}.{statId}";
        int total = 0;
        bool resolved = false;
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in result.MaterializedPlayers)
        {
            if (player.NodesByRuleId is not null
                && player.NodesByRuleId.TryGetValue(qualifiedId, out StateNode? node)
                && fireCountByNode.TryGetValue(node, out int count))
            {
                resolved = true;
                total += count;
            }
        }

        if (!resolved
            && build.GameNodesByRuleId?.GetValueOrDefault(qualifiedId) is { } gameNode
            && fireCountByNode.TryGetValue(gameNode, out int gameCount))
        {
            resolved = true;
            total = gameCount;
        }

        if (!resolved)
        {
            return;
        }

        fireStats.Add(new RuleFireStat(rulesetId, statId, total));
        if (total == 0)
        {
            diags.Add(new RuleDiagnostic("warning",
                "rule fired 0 times in this demo — its triggers/conditions never matched",
                ChainId: rulesetId, RuleId: statId));
        }
    }

    /// <summary>
    ///     Accumulates <see cref="StateEdge.FireCount" /> (0.1's always-on counters) by written
    ///     node, over both the game-scoped trigger edges (via <see cref="BuildResult.EdgeBacking" />)
    ///     and every materialized player's edges. A rule's badge is the sum over the edges that
    ///     write its node.
    /// </summary>
    private static Dictionary<StateNode, int> BuildFireCountByNode(BuildResult build, EvaluationResult result)
    {
        Dictionary<StateNode, int> counts = new(ReferenceEqualityComparer.Instance);

        if (build.EdgeBacking is not null)
        {
            foreach (StateEdge edge in build.EdgeBacking.Values)
            {
                if (edge.WrittenNode is { } node)
                {
                    counts[node] = counts.GetValueOrDefault(node) + edge.FireCount;
                }
            }
        }

        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in result.MaterializedPlayers)
        {
            foreach (StateEdge edge in player.Edges)
            {
                if (edge.WrittenNode is { } node)
                {
                    counts[node] = counts.GetValueOrDefault(node) + edge.FireCount;
                }
            }
        }

        return counts;
    }

    private static (BuildResult Build, RuleConfigLoadResult Rules) BuildFromConfig(ParsedDemo demo)
    {
        string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
        // The user overlay needs a writable filesystem; the WASM host has none worth provisioning.
        string? userDir = OperatingSystem.IsBrowser()
            ? null
            : RuleSetLocator.EnsureUserRulesDirectory(shippedDir);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
        return (DemoAnalysis.Build(demo, rules.Rulesets), rules);
    }

    // ── Filter population ────────────────────────────────────────────────

    private void PopulateFilter(
        BuildResult build, EvaluationResult result,
        IReadOnlyList<AnalysisChainSummaryViewModel> summaries)
    {
        // Scope each chain key by the POSITIVE game signal. Every game chain that fired events has
        // a satisfaction conjunction node, and the graph build stamps that node's "_chain_{id}" key into
        // NodeChains — so a key present in NodeChains is unambiguously game-scoped. Per-player
        // chains never reach NodeChains (their factory doesn't touch it), so the two sets are
        // disjoint. Default unknowns to PerPlayer: a per-player chain that fires but declares no
        // columns would otherwise be mis-scoped Game and, on selection, project to an empty
        // sub-graph (no graph node carries its key → ChainIds.Overlaps is false everywhere).
        HashSet<string> gameKeys = new(StringComparer.Ordinal);
        if (build.NodeChains is not null)
        {
            foreach (IReadOnlySet<string> keys in build.NodeChains.Values)
            {
                gameKeys.UnionWith(keys);
            }
        }

        // One chip per chain that produced events. ChainSummaries also include auto-activate
        // logic-rule nodes (the timeline records every rising conjunction/disjunction), so keep
        // only the chain-satisfaction conjunctions — named with the "_chain_" prefix at both the
        // game (RuleChainBuilder:408) and per-player (:646) sites. This keeps chips actionable
        // (a non-chain logic name would join to nothing and render an inert chip) and matches the
        // _chain_{id} join-key discipline. Label = the key; the human RuleChainDef.Name isn't
        // threaded to the timeline (cosmetic, noted as a known item).
        List<(string Key, string Label, ChainScope Scope, int Count)> chips = summaries
            .Where(s => s.ChainName.StartsWith("_chain_", StringComparison.Ordinal))
            .Select(s => (
                Key: s.ChainName,
                Label: s.ChainName,
                Scope: gameKeys.Contains(s.ChainName) ? ChainScope.Game : ChainScope.PerPlayer,
                s.Count))
            .ToList();

        // Players: distinct by slot across all materializations.
        List<(int Slot, string Name)> players = result.MaterializedPlayers
            .GroupBy(p => p.PlayerSlot)
            .Select(g => (Slot: g.Key, Name: g.First().PlayerName))
            .OrderBy(p => p.Slot)
            .ToList();

        Filter.Populate(chips, players);
    }

    // ── Table builder ────────────────────────────────────────────────────

    private static List<PlayerTableViewModel> BuildPlayerTables(
        EvaluationResult result,
        Dictionary<StateNode, GraphNodeViewModel> nodeVmByNode,
        IReadOnlyList<StateNode> lifecycleNodeList)
    {
        if (result.MaterializedPlayers.Count == 0)
        {
            return [];
        }

        // Lifecycle (game-chain) nodes are the only valid column-edge sources — these are the
        // graph nodes that actually render. Membership test by reference identity.
        HashSet<StateNode> lifecycleNodes = new(lifecycleNodeList, ReferenceEqualityComparer.Instance);

        List<PlayerTableViewModel> tables = new();
        IOrderedEnumerable<IGrouping<int, PerPlayerNodeTemplate.MaterializedPlayer>> byTemplate = result.MaterializedPlayers
            .GroupBy(p => p.TemplateIndex)
            .OrderBy(g => g.Key);

        foreach (IGrouping<int, PerPlayerNodeTemplate.MaterializedPlayer> templateGroup in byTemplate)
        {
            List<PerPlayerNodeTemplate.MaterializedPlayer> players = templateGroup.ToList();
            if (players.Count == 0 || players[0].ColumnAssignments.Count == 0)
            {
                continue;
            }

            IOrderedEnumerable<string> columnGroups = players[0].ColumnAssignments
                .Select(a => a.GroupName ?? "")
                .Distinct()
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase);

            foreach (string colGroup in columnGroups)
            {
                List<PerPlayerColumnAssignment> groupAssignments = players[0].ColumnAssignments
                    .Where(a => (a.GroupName ?? "") == colGroup)
                    .ToList();

                if (groupAssignments.Count == 0)
                {
                    continue;
                }

                List<string> columnNames = groupAssignments.Select(a => a.ColumnName).ToList();
                // Column → owning chain key, aligned 1:1 with columnNames. Set for EVERY column
                // (incl. computed ones with no lifecycle edge) — this is what sub-graph column
                // projection selects on.
                List<string?> columnChainIds = groupAssignments.Select(a => a.ChainId).ToList();

                List<TableRowViewModel> rows = new();
                foreach (PerPlayerNodeTemplate.MaterializedPlayer player in players)
                {
                    List<PerPlayerColumnAssignment> playerGroupAssignments = player.ColumnAssignments
                        .Where(a => (a.GroupName ?? "") == colGroup)
                        .ToList();

                    List<TableCellViewModel> cells = new();
                    foreach (PerPlayerColumnAssignment assignment in playerGroupAssignments)
                    {
                        int nodeIdx = -1;
                        for (int i = 0; i < result.FinalTrackedNodes.Count; i++)
                        {
                            if (ReferenceEquals(result.FinalTrackedNodes[i], assignment.Node))
                            {
                                nodeIdx = i;
                                break;
                            }
                        }

                        cells.Add(new TableCellViewModel(nodeIdx));
                    }

                    rows.Add(new TableRowViewModel(player.PlayerName, player.PlayerSlot, $"slot == {player.PlayerSlot}", cells));
                }

                PlayerTableViewModel table = new(columnNames, rows, columnChainIds);

                // ── Column edges (lifecycle node → table column) ─────────────
                // Built here so ColumnIndex is LOCAL to THIS group's column list. Each descriptor
                // whose Destination is one of this group's column nodes yields a connector; the
                // per-group restriction falls out for free (non-group destinations just don't match).
                List<TableColumnEdgeViewModel> colEdges = new();
                foreach (GraphEdgeDescriptor desc in players[0].EdgeDescriptors)
                {
                    if (!lifecycleNodes.Contains(desc.Source))
                    {
                        continue;
                    }

                    if (!nodeVmByNode.TryGetValue(desc.Source, out GraphNodeViewModel? srcVm))
                    {
                        continue;
                    }

                    int localColIdx = -1;
                    for (int i = 0; i < groupAssignments.Count; i++)
                    {
                        if (ReferenceEquals(groupAssignments[i].Node, desc.Destination))
                        {
                            localColIdx = i;
                            break;
                        }
                    }

                    if (localColIdx < 0)
                    {
                        continue;
                    }

                    // Carry the column's per-player chain key so the filter can project on it.
                    colEdges.Add(new TableColumnEdgeViewModel(
                        srcVm, localColIdx, desc.Label, desc.Effect, desc.ConditionLabel,
                        groupAssignments[localColIdx].ChainId));
                }

                table.ColumnEdges = colEdges;
                tables.Add(table);
            }
        }

        return tables;
    }

    private bool CanGoNext() => CurrentMessageIndex < TotalMessages - 1;
    private bool CanGoPrevious() => CurrentMessageIndex > 0;

    /// <summary>
    ///     Opens the user rules overlay directory in the OS file manager, provisioning it first
    ///     (README + editor schema) so a first-time user lands in a ready-to-edit folder.
    /// </summary>
    [RelayCommand]
    private void OpenUserRulesFolder()
    {
        string dir = RuleSetLocator.EnsureUserRulesDirectory(RuleSetLocator.ResolveShippedRulesDirectory());
        if (!OpenExternal.OpenUri(dir))
        {
            // v0.6.0: surface the failure instead of a silent no-op click.
            StatusText = $"Couldn't open the rules folder ({dir}) — no file manager responded.";
        }
    }

    /// <summary>
    ///     Reloads the rule config from disk and re-evaluates the already-loaded demo (P1-1.3) —
    ///     the edit-a-rule → see-the-stat loop, without re-parsing the demo. Rules are read fresh
    ///     inside <see cref="RunAsync" /> on every run; the entity-value cache survives (same demo,
    ///     same frames), so breakpoint recompute usually re-filters without a second ~14 s replay.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRerunAnalysis))]
    private async Task RerunAnalysisAsync()
    {
        if (LastEvaluatedDemo is { } demo)
        {
            await RunAsync(demo, _demoKey);
        }
    }

    private bool CanRerunAnalysis() => !IsRunning && LastEvaluatedDemo is not null;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextMessage() => SeekToMessage(CurrentMessageIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousMessage() => SeekToMessage(CurrentMessageIndex - 1);

    private bool CanContinue() => _messageSnapshots is not null && HasGraphBreakpoints;

    /// <summary>Run-to-breakpoint forward: seek to the next message where an enabled breakpoint holds.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        (GraphBreakpoint Breakpoint, int Index)? hit = GraphBreakpoints.NextHit(CurrentMessageIndex);
        if (hit is null)
        {
            // An entity breakpoint reads no hits until its replay finishes — say so rather than the
            // misleading "no hit ahead" (the report that started this: ran, found nothing, looked broken).
            StatusText = IsComputingEntityCache
                ? "Computing entity state — try Continue again in a moment."
                : "No breakpoint hit ahead.";
            return;
        }

        GraphBreakpoints.MarkHit(hit.Value.Breakpoint, hit.Value.Index);
        SeekToMessage(hit.Value.Index);
    }

    /// <summary>Run-to-breakpoint backward: seek to the previous breakpoint hit.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void ContinueBack()
    {
        (GraphBreakpoint Breakpoint, int Index)? hit = GraphBreakpoints.PrevHit(CurrentMessageIndex);
        if (hit is null)
        {
            StatusText = IsComputingEntityCache
                ? "Computing entity state — try Continue again in a moment."
                : "No breakpoint hit behind.";
            return;
        }

        GraphBreakpoints.MarkHit(hit.Value.Breakpoint, hit.Value.Index);
        SeekToMessage(hit.Value.Index);
    }

    // The list and the condition editor both anchor to the top of the graph; opening the list closes
    // the editor so only one shows at a time (BeginEdit closes the list symmetrically).
    partial void OnIsBreakpointListOpenChanged(bool value)
    {
        if (value)
        {
            CloseConditionEditor();
        }
    }

    /// <summary>
    ///     Seeks to a breakpoint's first hit and halts there. Mirrors <see cref="Continue" />
    ///     (MarkHit then seek to the same index) so the "stopped" amber lands on this breakpoint and
    ///     the manual-nav clear-guard doesn't immediately wipe it.
    /// </summary>
    [RelayCommand]
    private void JumpToBreakpoint(GraphBreakpoint? bp)
    {
        if (bp is null || bp.HitIndices.Count == 0)
        {
            StatusText = "This breakpoint has no hits in this demo.";
            return;
        }

        int target = bp.HitIndices[0];
        GraphBreakpoints.MarkHit(bp, target);
        SeekToMessage(target);
    }

    /// <summary>Removes a breakpoint from the list (the service Changed hook repaints + re-persists).</summary>
    [RelayCommand]
    private void RemoveBreakpointFromList(GraphBreakpoint? bp)
    {
        if (bp is not null)
        {
            GraphBreakpoints.Remove(bp.Id);
        }
    }

    /// <summary>Closes the breakpoint-list overlay (its header ✕).</summary>
    [RelayCommand]
    private void CloseBreakpointList() => IsBreakpointListOpen = false;

    // Context-menu entry points (invoked from AnalysisTabView code-behind on the hit element, wrapped
    // in a ConditionTarget). One path serves nodes and edges; the only node-vs-edge difference here is
    // the two capability gates, which read the edge maps (nodes are always capable).

    // The edge identity 4-tuple used across the edge maps and the breakpoint identity. Single source
    // so the tuple isn't copy-pasted at every lookup site.
    private static (string, string, string, string?) EdgeKey(IGraphEdge e) =>
        (e.Source.Name, e.Destination.Name, e.Label, e.ConditionLabel);

    /// <summary>
    ///     Whether the target can carry a breakpoint. A node always can; an edge only if it's
    ///     trigger-backed (has recorded fire indices) — logic / conjunction edges would arm a
    ///     breakpoint that can never fire and are gated out.
    /// </summary>
    public bool IsBreakpointable(ConditionTarget target) =>
        target.Kind == GraphBreakpointTarget.Node || _appliedByEdgeKey.ContainsKey(EdgeKey(target.Edge!));

    /// <summary>
    ///     Whether the target supports an authored condition. A node always does; an edge only if its
    ///     event exposes typed fields (game / net-message edges) — entity-change edges are default-only.
    ///     Read independently of <see cref="IsBreakpointable" />.
    /// </summary>
    public bool SupportsCondition(ConditionTarget target) =>
        target.Kind == GraphBreakpointTarget.Node || _eventMetaByEdgeKey.ContainsKey(EdgeKey(target.Edge!));

    /// <summary>Whether the target already carries a breakpoint.</summary>
    public bool HasBreakpoint(ConditionTarget target) => target.Find(GraphBreakpoints) is not null;

    /// <summary>Arms a default breakpoint on the target (no-op if one already exists).</summary>
    public void AddBreakpoint(ConditionTarget target) => ReportBreakpointArmed(target.Add(GraphBreakpoints, null));

    /// <summary>Removes the breakpoint on the target, if any.</summary>
    public void RemoveBreakpoint(ConditionTarget target)
    {
        GraphBreakpoint? bp = target.Find(GraphBreakpoints);
        if (bp is not null)
        {
            GraphBreakpoints.Remove(bp.Id);
        }
    }

    // Surfaces a breakpoint's hit count the moment it's armed/edited. Hits are recomputed synchronously
    // by the time Add/Apply returns (service Changed → OnBreakpointsChanged → recompute), so a
    // breakpoint that will never stop — an edge that fired 0 times this demo, a node that never went
    // active — reads as "0 hits" instead of a silently-dead marker.
    private void ReportBreakpointArmed(GraphBreakpoint bp)
    {
        int n = bp.HitIndices.Count;
        StatusText = n == 0
            ? $"Breakpoint armed on {bp.DisplayText} — 0 hits in this demo (Continue won't stop here)"
            : $"Breakpoint armed on {bp.DisplayText} — {n} hit{(n == 1 ? "" : "s")} in this demo";
    }

    /// <summary>
    ///     Appends a graph-picked node to the condition, seeded with its <em>current</em> state
    ///     (<c>name == true</c> for an active bool, <c>name == &lt;value&gt;</c> for a numeric node,
    ///     …) via <see cref="NodeBreakpointConditions.SuggestPickSnippet" />. Blank / <c>"active"</c>
    ///     text is replaced; otherwise the snippet is <c>&amp;&amp;</c>-joined. Exits pick mode after.
    /// </summary>
    public void InsertPickedNode(IGraphNode node)
    {
        // Resolve the picked node's snapshot column → its tracked StateNode (kind) + current value.
        GraphNodeViewModel? nodeVm = _allGraphNodes.FirstOrDefault(n => n.Name == node.Name);
        int col = nodeVm?.TrackedIndex ?? -1;

        NodeBreakpointConditions.ValueKind kind = NodeBreakpointConditions.ValueKind.None;
        bool isActive = false;
        double? numeric = null;
        string? display = null;

        if (col >= 0 && col < _trackedNodesByColumn.Count)
        {
            kind = NodeBreakpointConditions.Classify(_trackedNodesByColumn[col]);
            NodeSnapshot[]? snap = _currentSnapshot;
            if (snap is not null && col < snap.Length)
            {
                isActive = snap[col].IsActive;
                display = snap[col].DisplayValue;
                numeric = snap[col].NumericValue; // float? → double?
            }
        }

        string snippet = NodeBreakpointConditions.SuggestPickSnippet(node.Name, kind, isActive, numeric, display);

        string trimmed = (EditingConditionText ?? "").Trim();
        EditingConditionText = trimmed.Length == 0 || trimmed == "active"
            ? snippet
            : $"{trimmed} && {snippet}"; // setter re-runs live validation

        IsPickingNode = false;
    }

    /// <summary>
    ///     Filters the available identifiers by <paramref name="prefix" /> (the token before the caret)
    ///     into <see cref="ConditionSuggestions" />. Case-insensitive prefix match; the exact-typed
    ///     identifier is omitted (nothing left to complete). Capped only to bound the per-keystroke
    ///     work on large node sets — high enough to show an event's full field set when you type
    ///     <c>event.</c> (the list scrolls); the box itself is height-limited in XAML.
    /// </summary>
    public void UpdateConditionSuggestions(string? prefix)
    {
        ConditionSuggestions.Clear();
        if (!string.IsNullOrEmpty(prefix))
        {
            const int Max = 50;
            foreach (string id in _activeConditionIdentifiers)
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(id, prefix, StringComparison.Ordinal))
                {
                    ConditionSuggestions.Add(id);
                    if (ConditionSuggestions.Count >= Max)
                    {
                        break;
                    }
                }
            }
        }

        HasConditionSuggestions = ConditionSuggestions.Count > 0;
    }

    /// <summary>Clears the autocomplete suggestion list (e.g. on accept, or when the editor closes).</summary>
    public void ClearConditionSuggestions()
    {
        ConditionSuggestions.Clear();
        HasConditionSuggestions = false;
    }

    // Live validation: recompile on each keystroke; the message (if any) shows inline and gates Apply.
    partial void OnEditingConditionTextChanged(string? value) => RecomposeAndValidate();

    /// <summary>
    ///     Opens the condition editor for a target (node or edge), pre-filled with its existing
    ///     condition. The substrate-specific metadata (kind, value/fields hint, autocomplete pool, and
    ///     whether the node-picker shows) is set per <see cref="ConditionTarget.Kind" />; the overlay
    ///     itself is identical for both.
    /// </summary>
    public void BeginEdit(ConditionTarget target)
    {
        _editingTarget = target;
        EditingConditionTarget = target.DisplayName;

        if (target.Kind == GraphBreakpointTarget.Node)
        {
            // Classify the node so the header can show its kind and the hint can explain `value`, and
            // fold this node's input.<event>.<field> identifiers into the autocomplete pool.
            string kind = DescribeKind(NodeColumn(target.Node!));
            EditingConditionTargetKind = kind;

            Dictionary<string, NodeBreakpointConditions.InputEventInfo> inputs = NodeInputEventsByName(target.Node!.Name);
            // The free-text event-match box autocompletes node value-references, the input.<event>.<field>
            // shapes, and the bare `player` slot-comparison token — but NOT the entity-read grammar, which
            // the scope-aware rows below author (includeEntityReads: false).
            _activeConditionIdentifiers = inputs.Count == 0
                ? _availableConditionIdentifiers
                : _availableConditionIdentifiers
                    .Concat(NodeBreakpointConditions.InputFieldIdentifiers(inputs, _perPlayerProviders, false))
                    .ToList();
            EditingValueHint = NodeValueHint(kind, inputs);
            SetupStructuredScope(inputs, null, target.Find(GraphBreakpoints)?.Condition);
        }
        else
        {
            SetEdgeEditorMetadata(target.Edge!);
        }

        OpenEditor(target.Find(GraphBreakpoints)?.Condition);
    }

    // The node editor hint: what `value` means for the node's kind, plus the input events it can
    // condition on (input.<event>.<field>) when it has any.
    private static string NodeValueHint(string kind, IReadOnlyDictionary<string, NodeBreakpointConditions.InputEventInfo> inputs)
    {
        string baseHint = ValueHintFor(kind);
        if (inputs.Count == 0)
        {
            return baseHint;
        }

        List<string> events = inputs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        string ev0 = events[0];
        // Advertise the field and bare-player shapes (autocomplete fills the concrete fields). `player`
        // resolves to the filter's selected slot; entity reads (health/armor/…) are authored via the rows
        // below, so the free-text box no longer advertises that grammar.
        string inputHint = $"inputs: {string.Join(", ", events)} — e.g.  input.{ev0}.<field> == …"
                           + $"  ·  input.{ev0}.<Slot> == player   (entity checks: use the rows below)";
        return string.IsNullOrEmpty(baseHint) ? inputHint : $"{baseHint}   ·   {inputHint}";
    }

    // Edge-specific editor metadata: the event-field autocomplete pool + a few field names as a hint.
    private void SetEdgeEditorMetadata(IGraphEdge edge)
    {
        EditingConditionTargetKind = "event";
        if (_eventMetaByEdgeKey.TryGetValue(EdgeKey(edge),
                out (Type EventType, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields) meta))
        {
            // Autocomplete: event fields + the bare `player` slot-comparison token. The entity-read grammar
            // (`<SlotField>.entity.<provider>`, `player.entity.<provider>`) is authored via the rows below,
            // so the free-text box no longer suggests it (includeEntityReads: false).
            IReadOnlyList<string> ids =
                EdgeBreakpointConditions.FieldIdentifiers(meta.Fields, _perPlayerProviders, false);

            // A game-event edge (envelope-typed) also resolves per-fire transport; `event.tick` is
            // the documented instant (ServerTick/GameTick/FrameNumber resolve too, tick is the vocabulary).
            _activeConditionIdentifiers = meta.ParameterType is null
                ? ids
                : ids.Append("event.tick").OrderBy(s => s, StringComparer.Ordinal).ToList();
            EditingValueHint =
                "event.<field>  ·  player (pick one in the filter)  —  e.g.  event.Attacker == player"
                + "   ·   entity checks: use the rows below";
            SetupStructuredScope(null, meta, null);
        }
        else
        {
            _activeConditionIdentifiers = [];
            EditingValueHint = "";
            SetupStructuredScope(null, null, null); // no event → rows N/A (free-text only)
        }
    }

    // Shared editor-open tail: prefill text, validate, reset transient helpers, raise the header/hint
    // notifications, and show the overlay. Used by both the node and edge entry points.
    private void OpenEditor(string? existingCondition)
    {
        OnPropertyChanged(nameof(EditingConditionTarget));
        OnPropertyChanged(nameof(EditingConditionTargetKind));
        OnPropertyChanged(nameof(EditingConditionHeader));
        OnPropertyChanged(nameof(EditingValueHint));
        OnPropertyChanged(nameof(HasValueHint));
        OnPropertyChanged(nameof(CanPickNode));
        OnPropertyChanged(nameof(EditingConditionWatermark));
        OnPropertyChanged(nameof(EditingScopeLegend));

        // Parse the saved condition into the event-match box + structured rows (or advanced free text);
        // this sets EditingConditionText / EditingEntityRows / EditingStructured and validates once.
        SeedEditorFromCondition(existingCondition);
        IsPickingNode = false;
        ClearConditionSuggestions();
        IsBreakpointListOpen = false; // editor and list both anchor top — show one at a time
        IsEditingCondition = true;
    }

    // The editing node's value kind as a display string ("bool" / "number" / "text", or "" for none).
    private string DescribeKind(int col)
    {
        if (col < 0 || col >= _trackedNodesByColumn.Count)
        {
            return "";
        }

        return NodeBreakpointConditions.Classify(_trackedNodesByColumn[col]) switch
        {
            NodeBreakpointConditions.ValueKind.Bool => "bool",
            NodeBreakpointConditions.ValueKind.Number => "number",
            NodeBreakpointConditions.ValueKind.Text => "text",
            _ => ""
        };
    }

    // A kind-specific one-liner explaining what `value` resolves to, to steer the user toward a
    // condition that type-checks (a bool node's `value` is a bool, so `value == true`, not `>= 1`).
    private static string ValueHintFor(string kind) => kind switch
    {
        "bool" => "value is a bool — e.g.  value == true   (or leave blank / 'active' to stop when the node is active)",
        "number" => "value is numeric — e.g.  value >= 3",
        "text" => "value is text — e.g.  value == \"de_mirage\"",
        _ => ""
    };

    // Apply is enabled whenever a target is being edited and the text is valid. One field (vs the old
    // node-OR-edge disjunction) removes the dead-disable bug class entirely.
    private bool CanApplyCondition() => _editingTarget is not null && EditingConditionError is null;

    [RelayCommand(CanExecute = nameof(CanApplyCondition))]
    private void ApplyCondition()
    {
        if (_editingTarget is not { } target)
        {
            return;
        }

        // Compose the event-match box + structured rows into the canonical string (or take the raw
        // free-text in advanced mode). Blank / "active" normalise to null (the default condition).
        string composed = ComposedEditingCondition().Trim();
        string? cond = composed.Length == 0 || composed == "active" ? null : composed;

        GraphBreakpoint? existing = target.Find(GraphBreakpoints);
        GraphBreakpoint bp;
        if (existing is null)
        {
            bp = target.Add(GraphBreakpoints, cond); // → Changed → recompute + markers
        }
        else
        {
            existing.Condition = cond; // setter → service Changed → recompute
            bp = existing;
        }

        ReportBreakpointArmed(bp);
        CloseConditionEditor();
    }

    [RelayCommand]
    private void CancelCondition() => CloseConditionEditor();

    // ── Structured (scope-aware) editor ─────────────────────────────────────────

    // Sets up the row dropdowns + slot prefix for the current target's trigger event. For a node it's an
    // input event (the one the saved condition references, else the first); for an edge it's the edge's
    // own event. Rows don't apply to a node with no input event (nothing to anchor entity reads to).
    private void SetupStructuredScope(
        IReadOnlyDictionary<string, NodeBreakpointConditions.InputEventInfo>? nodeInputs,
        (Type EventType, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields)? edgeMeta,
        string? existingCondition)
    {
        _editProviderOptions = _perPlayerProviders.All
            .Select(p => new EntityCheckRowViewModel.Choice(ProviderLabel(p.Name), p.Name, p.ValueType == typeof(string)))
            .ToList();

        if (edgeMeta is { } edge)
        {
            _editSlotPrefix = "";
            _editSlotFields = SlotFieldsOf(edge.Fields);
            EditingTriggerEvent = null;
            EditingTriggerEvents = [];
            EditingRowsApplicable = true;
        }
        else if (nodeInputs is { Count: > 0 })
        {
            string trigger = PickTriggerEvent(nodeInputs.Keys, existingCondition);
            _editSlotPrefix = $"input.{trigger}.";
            _editSlotFields = SlotFieldsOf(nodeInputs[trigger].Fields);
            EditingTriggerEvent = trigger;
            EditingTriggerEvents = nodeInputs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            EditingRowsApplicable = true;
        }
        else
        {
            _editSlotPrefix = "";
            _editSlotFields = new HashSet<string>();
            EditingTriggerEvent = null;
            EditingTriggerEvents = [];
            EditingRowsApplicable = false; // no event to anchor entity reads against → free text only
        }

        _editSubjectOptions = BuildSubjectOptions(_editSlotFields);
        EditingScopeLegend = EditingRowsApplicable
            ? "in scope:  " + string.Join("  ·  ", _editSubjectOptions.Select(o => o.Label)) + "      (entities read pre-frame)"
            : "";
    }

    // Subject options: each trigger-event *Slot field (friendly label) plus the selected player.
    private static List<EntityCheckRowViewModel.Choice> BuildSubjectOptions(IEnumerable<string> slotFields)
    {
        List<EntityCheckRowViewModel.Choice> options = slotFields
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new EntityCheckRowViewModel.Choice(SubjectLabel(f), f))
            .ToList();
        options.Add(new EntityCheckRowViewModel.Choice("selected player", "player"));
        return options;
    }

    private static HashSet<string> SlotFieldsOf(IReadOnlyDictionary<string, EventFieldAccessor> fields) =>
        fields.Where(kv => ExpressionCompiler.IsPlayerSlotField(kv.Key, kv.Value.FieldType))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

    // "VictimSlot" -> "victim"; an SDK-named field ("UserId", "Attacker") has no suffix to strip and
    // reads as itself.
    private static string SubjectLabel(string slotField) =>
        (slotField.EndsWith("Slot", StringComparison.Ordinal) ? slotField[..^4] : slotField).ToLowerInvariant();

    // "entity.pawn.health" -> "health".
    private static string ProviderLabel(string providerName)
    {
        int dot = providerName.LastIndexOf('.');
        return dot >= 0 && dot < providerName.Length - 1 ? providerName[(dot + 1)..] : providerName;
    }

    // Picks the trigger event: the one the saved condition references via input.<event>., else the first.
    private static string PickTriggerEvent(IEnumerable<string> events, string? existing)
    {
        List<string> ordered = events.OrderBy(e => e, StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            Match m = Regex.Match(existing, @"input\.([A-Za-z0-9_]+)\.");
            if (m.Success && ordered.Contains(m.Groups[1].Value))
            {
                return m.Groups[1].Value;
            }
        }

        return ordered[0];
    }

    // Seeds the editor from a saved condition: parse it into the event-match box + rows (when it decomposes
    // and rows apply), else show it as advanced free text. Runs under _seedingEditor so the per-field
    // change handlers don't each recompute — one validate runs at the end.
    private void SeedEditorFromCondition(string? existing)
    {
        _seedingEditor = true;
        EditingEntityRows.Clear();

        if (!EditingRowsApplicable)
        {
            EditingStructured = false;
            EditingConditionText = existing ?? "";
        }
        else
        {
            StructuredCondition parsed =
                StructuredCondition.Parse(existing, _editSlotPrefix, _editSlotFields, _perPlayerProviders);
            EditingStructured = parsed.Decomposed;
            EditingConditionText = parsed.Decomposed ? parsed.EventMatch : existing ?? "";
            foreach (EntityCheckRow row in parsed.Rows)
            {
                EditingEntityRows.Add(NewRowFrom(row));
            }
        }

        _seedingEditor = false;
        RecomposeAndValidate();
    }

    private EntityCheckRowViewModel NewRow(
        EntityCheckRowViewModel.Choice? subject, EntityCheckRowViewModel.Choice? provider, string? op, string value) =>
        new(_editSubjectOptions, _editProviderOptions, RecomposeAndValidate, RemoveEntityRow, subject, provider, op, value);

    private EntityCheckRowViewModel NewRowFrom(EntityCheckRow row) =>
        NewRow(
            _editSubjectOptions.FirstOrDefault(o => o.Token == row.Subject)
            ?? (_editSubjectOptions.Count > 0 ? _editSubjectOptions[0] : null),
            _editProviderOptions.FirstOrDefault(o => o.Token == row.Provider)
            ?? (_editProviderOptions.Count > 0 ? _editProviderOptions[0] : null),
            row.Op, row.Value);

    private void RemoveEntityRow(EntityCheckRowViewModel row)
    {
        EditingEntityRows.Remove(row);
        RecomposeAndValidate();
    }

    [RelayCommand]
    private void AddEntityRow()
    {
        if (!EditingRowsApplicable)
        {
            return;
        }

        EditingEntityRows.Add(NewRow(null, null, null, ""));
        RecomposeAndValidate();
    }

    // The condition the editor currently represents: the composed string when structured, else the raw
    // free-text box. Blank stays blank (→ the default condition on apply).
    private string ComposedEditingCondition()
    {
        if (!EditingStructured)
        {
            return EditingConditionText ?? "";
        }

        List<EntityCheckRow> rows = EditingEntityRows.Select(r => r.ToRow()).OfType<EntityCheckRow>().ToList();
        return StructuredCondition.Compose(EditingConditionText ?? "", rows, _editSlotPrefix, _perPlayerProviders);
    }

    // Re-composes from the event-match box + rows, validates the result, and surfaces per-row warnings.
    // The single choke point for every edit (text, row field, add/remove); a no-op while seeding.
    private void RecomposeAndValidate()
    {
        if (_seedingEditor)
        {
            return;
        }

        EditingConditionError = ValidateCondition(ComposedEditingCondition());

        int selectedSlot = SelectedPlayerSlotOrAll();
        foreach (EntityCheckRowViewModel row in EditingEntityRows)
        {
            row.Warning = row.Subject?.Token == "player" && selectedSlot < 0
                ? "pick a player in the filter for this row to match"
                : null;
        }
    }

    // Tears down the editor overlay and its transient helpers (pick mode, suggestions) in one place
    // so Apply, Cancel and a reload all leave the same clean state.
    private void CloseConditionEditor()
    {
        IsEditingCondition = false;
        IsPickingNode = false;
        ClearConditionSuggestions();
        EditingEntityRows.Clear();
        EditingStructured = false;
        EditingRowsApplicable = false;
        _editingTarget = null;
        OnPropertyChanged(nameof(CanPickNode));
    }

    // Returns a validation message, or null when valid. Dispatches once on the editing target's kind:
    // the NODE validator (value / other-nodes / entity against the snapshot) or the EDGE validator
    // (event.<field> against the edge's event type). Both share the exact compile path their
    // ComputeHits use, so a validated condition can't then fail to resolve at scan time.
    private string? ValidateCondition(string? expr)
    {
        if (_editingTarget is not { } target)
        {
            return null;
        }

        return target.Kind == GraphBreakpointTarget.Node
            ? NodeBreakpointConditions.Validate(expr, _trackedNodesByColumn, NodeColumn(target.Node!),
                NodeInputEventsByName(target.Node!.Name), SelectedPlayerSlotOrAll(), _perPlayerProviders)
            : ValidateEdgeCondition(expr, target.Edge!);
    }

    // Validates an edge's event-field condition against its event type's fields. Blank → valid
    // (default: break on every fire). An edge with no event metadata can't carry a condition.
    private string? ValidateEdgeCondition(string? expr, IGraphEdge edge)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            return null;
        }

        return _eventMetaByEdgeKey.TryGetValue(EdgeKey(edge),
            out (Type EventType, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields) meta)
            ? EdgeBreakpointConditions.Validate(expr, meta.EventType, meta.Fields,
                SelectedPlayerSlotOrAll(), _perPlayerProviders, meta.ParameterType)
            : "This edge has no event fields to condition on (it breaks on every fire).";
    }

    // The snapshot column of a node (-1 if not tracked).
    private int NodeColumn(IGraphNode node) => NodeColumnByName(node.Name);

    private int NodeColumnByName(string name)
    {
        GraphNodeViewModel? vm = _allGraphNodes.FirstOrDefault(n => n.Name == name);
        return vm?.TrackedIndex ?? -1;
    }

    // Fired on any breakpoint set/condition/enabled change: rebind to the current evaluation and
    // persist the new set. While LoadPersistedBreakpoints is Adding the restored set this early-returns
    // (it rebinds once at the end), so a load is O(N) and never re-saves what it just read.
    private void OnBreakpointsChanged()
    {
        if (_loadingBreakpoints)
        {
            return;
        }

        RebindBreakpoints();
        PersistBreakpoints();
    }

    // Recompute hit indices against the current evaluation and repaint the node/edge markers.
    private void RebindBreakpoints()
    {
        HasGraphBreakpoints = GraphBreakpoints.Breakpoints.Count > 0;
        RecomputeBreakpointHits();
        RefreshBreakpointMarkers();
    }

    // Best-effort save of the current set under the loaded demo's key. No key (no demo / WASM / a
    // caller without the bytes) → in-memory only. Suppressed during a restore so a load isn't a write.
    private void PersistBreakpoints()
    {
        if (_loadingBreakpoints || _demoKey is null)
        {
            return;
        }

        _breakpointStore.Save(_demoKey, GraphBreakpoints.Breakpoints);
    }

    // Restores the loaded demo's persisted breakpoints. Suppresses the per-Add Changed
    // (no save-back, no O(N²) rebind), then binds hits + markers once for the final set. Self-contained:
    // the defensive Clear keeps RunAsync idempotent even if Reset() didn't precede it.
    private void LoadPersistedBreakpoints()
    {
        _loadingBreakpoints = true;
        try
        {
            GraphBreakpoints.Clear();
            if (_demoKey is not null)
            {
                foreach (PersistedGraphBreakpoint persisted in _breakpointStore.Load(_demoKey))
                {
                    GraphBreakpoints.Add(persisted.ToBreakpoint());
                }
            }
        }
        finally
        {
            _loadingBreakpoints = false;
        }

        RebindBreakpoints();
    }

    private void OnBreakpointHitChanged()
    {
        GraphBreakpoint? hit = GraphBreakpoints.LastHit;
        StoppedText = hit is null ? null : $"⏸ Stopped: {hit.DisplayText}";
        OnPropertyChanged(nameof(StoppedText));
    }

    // A node breakpoint hits on the RISING EDGE of its condition (false→true). The default "active"
    // condition is the IsActive rising edge over the node's snapshot column. Edge
    // breakpoints come from EvaluationResult.AppliedMessagesByEdge instead.
    private void RecomputeBreakpointHits()
    {
        SnapshotTable? snaps = _messageSnapshots;
        if (snaps is null)
        {
            return;
        }

        // Bump the recompute token up front, so EVERY recompute — not just the union-grew one that kicks a
        // build — supersedes any in-flight entity build. Without this, a recompute that resolves synchronously
        // (the pending==0 or cache-reuse path below) would leave a prior build's token valid; its later
        // hand-back would then clobber the hits we just set. Capture it locally for the build we may kick.
        // The CTS additionally ABORTS the superseded build mid-replay (P1-5.2) — the token alone only
        // discarded its result, leaving the ~14 s replay burning CPU to completion.
        int token = ++_entityRecomputeToken;
        _entityBuildCts?.Cancel();
        _entityBuildCts?.Dispose();
        _entityBuildCts = new CancellationTokenSource();

        // Node + sync-edge breakpoints compute here. Entity-read edge breakpoints (which need the entity
        // replay) are collected as pending; their fire frames form the union the cache must cover.
        List<PendingEntityHit> pending = [];
        HashSet<int> union = [];

        foreach (GraphBreakpoint bp in GraphBreakpoints.Breakpoints)
        {
            (List<int> hits, PendingEntityHit? entity) = bp.TargetKind == GraphBreakpointTarget.Node
                ? ComputeNodeHits(bp, snaps)
                : ComputeEdgeHits(bp);

            if (entity is { } e)
            {
                pending.Add(e);
                foreach (int i in e.FireMessages)
                {
                    union.Add(FrameIndexOfMessage(i));
                }
            }
            else
            {
                bp.HitIndices = hits;
                bp.Computing = false;
            }
        }

        // Pre-warm: the entity replay to the last fire frame is the whole cost (~all-match), so the FIRST
        // build at demo load widens the union to every breakpointable edge's fire frames. One replay then
        // serves any entity breakpoint the user later adds — its frames are a subset → instant re-filter, no
        // second replay. Consumed once; later edits build narrow (and hit the reuse path below anyway).
        // Declined when the widened set is implausibly large (a high-frequency net-message edge approaching
        // every frame) — capturing that many frames would bloat the cache; such breakpoints lazy-build their
        // own narrower set instead.
        bool warm = _warmEntityCacheOnNextBuild && _demoFrames is not null;
        _warmEntityCacheOnNextBuild = false;
        if (warm)
        {
            HashSet<int> wide = AllEdgeFireFrames();
            if (wide.Count <= PrewarmFrameCap)
            {
                union.UnionWith(wide);
            }
            else
            {
                warm = false; // too many fire frames to pre-warm; fall back to lazy per-breakpoint builds
            }
        }

        if (pending.Count == 0 && !warm)
        {
            IsComputingEntityCache = false; // nothing to replay this cycle (the token bump dropped any in-flight build)
            return;
        }

        // Cache already covers every needed fire frame → re-filter synchronously (no replay). This is the
        // condition-edit / selection-change path (and any add once the load-time pre-warm has landed): the
        // predicate recompiled with the new slot reads the already-captured (all-slot) values.
        if (_entityValueCache is not null && union.IsSubsetOf(_entityCacheFrames))
        {
            foreach (PendingEntityHit e in pending)
            {
                e.Bp.HitIndices = e.Recompute(EntityAccessorAt);
                e.Bp.Computing = false;
            }

            IsComputingEntityCache = false;
            return;
        }

        // Union grew (new demo / new entity-read breakpoint / the load-time pre-warm) → rebuild off-thread
        // over the union, then re-filter on the UI thread. Hits read empty and the toolbar + list show
        // "computing…" until the hand-back.
        foreach (PendingEntityHit e in pending)
        {
            e.Bp.HitIndices = [];
            e.Bp.Computing = true;
        }

        IsComputingEntityCache = true;
        int[] unionFrames = union.OrderBy(f => f).ToArray();
        _entityRecomputeTail = RunEntityRecomputeAsync(_entityRecomputeTail, token,
            unionFrames, pending, _entityBuildCts!.Token);
    }

    // Every breakpointable edge's fire frames — the superset any entity breakpoint (edge or node, since node
    // input fires come from the same applied-edge sets) could ever query. The load-time pre-warm builds the
    // cache over this so the first entity breakpoint needs no replay of its own.
    private HashSet<int> AllEdgeFireFrames()
    {
        HashSet<int> frames = [];
        foreach (IReadOnlyList<int> applied in _appliedByEdgeKey.Values)
        {
            foreach (int i in applied)
            {
                int f = FrameIndexOfMessage(i);
                if (f >= 0)
                {
                    frames.Add(f);
                }
            }
        }

        return frames;
    }

    // The per-fire entity accessor backed by the current frozen cache (frameIndexOfMessage → pre-frame
    // state). Passed to a PendingEntityHit's Recompute once the cache covers its fire frames; a null cache
    // yields a null accessor at every fire (defensive — Recompute treats that as no hits).
    private IEntityValueAt? EntityAccessorAt(int msgIdx) => _entityValueCache?.At(FrameIndexOfMessage(msgIdx));

    // Builds the entity-value cache over the fire-frame union off the UI thread (one entity replay), then
    // — back on the UI thread — assigns the frozen cache and re-filters the pending breakpoints. Mirrors
    // RunSwapAsync: the tail serializes builds, the token drops a build superseded by a newer recompute
    // (a condition edit, a selection change, or a demo switch), so a stale cache never lands.
    private async Task RunEntityRecomputeAsync(Task previous, int token,
        int[] unionFrames, List<PendingEntityHit> pending, CancellationToken buildToken)
    {
        try
        {
            await previous;
        }
        catch
        {
            // A prior build's failure is its own concern; don't break the chain.
        }

        if (token != _entityRecomputeToken)
        {
            return; // superseded before this build started
        }

        IReadOnlyList<DemoFrame>? frames = _demoFrames;
        if (frames is null)
        {
            IsComputingEntityCache = false;
            return;
        }

        EntityValueCache cache;
        try
        {
            cache = await Task.Run(
                () => EntityValueCache.Build(frames, unionFrames, _perPlayerProviders.All, buildToken),
                buildToken);
        }
        catch (OperationCanceledException)
        {
            return; // aborted by a supersede — the newer cycle owns IsComputingEntityCache
        }
        catch (Exception ex)
        {
            StatusText = $"Entity breakpoint compute failed: {ex.Message}";
            IsComputingEntityCache = false;
            return;
        }

        if (token != _entityRecomputeToken)
        {
            return; // superseded while building → drop this (now-stale) cache, never hand it back (the
            // superseding cycle owns IsComputingEntityCache and will clear it when its build lands)
        }

        _entityValueCache = cache;
        _entityCacheFrames = [.. unionFrames];
        IsComputingEntityCache = false;

        // Always clear Computing per breakpoint — even if its Recompute throws — so a single bad predicate
        // can't strand the breakpoint (or the ones after it) in a permanent "computing…" spinner. A throw
        // is surfaced to the status line rather than swallowed by this fire-and-forget task.
        foreach (PendingEntityHit e in pending)
        {
            try
            {
                e.Bp.HitIndices = e.Recompute(EntityAccessorAt);
            }
            catch (Exception ex)
            {
                e.Bp.HitIndices = [];
                StatusText = $"Entity breakpoint compute failed: {ex.Message}";
            }
            finally
            {
                e.Bp.Computing = false;
            }
        }

        RefreshBreakpointMarkers();
    }

    // The frame index a message belongs to (for positioning the entity accessor at the fire's frame).
    private int FrameIndexOfMessage(int msgIdx)
    {
        if (_messageList is null || _frameIndexByFrame is null || msgIdx < 0 || msgIdx >= _messageList.Count)
        {
            return -1;
        }

        return _frameIndexByFrame.GetValueOrDefault(_messageList[msgIdx].Frame, -1);
    }

    // An edge breakpoint's default hits are the discrete message indices where its backing StateEdge
    // FIRED (recorded during the eval pass) — NOT a rising edge: two adjacent applies are two hits.
    // A conditional edge breakpoint narrows that set to the fires whose decoded event payload (and, for an
    // entity-read condition, the pre-frame entity state) satisfies the predicate. Returns the synchronous
    // hits, OR — for an entity-read condition — a Pending descriptor the caller resolves against the cache.
    private (List<int> Hits, PendingEntityHit? Pending) ComputeEdgeHits(GraphBreakpoint bp)
    {
        if (bp.EdgeSource is null || bp.EdgeDest is null || bp.EdgeLabel is null)
        {
            return ([], null);
        }

        (string, string, string, string?) key = (bp.EdgeSource, bp.EdgeDest, bp.EdgeLabel, bp.EdgeConditionLabel);
        if (!_appliedByEdgeKey.TryGetValue(key, out IReadOnlyList<int>? applied))
        {
            return ([], null);
        }

        // Default (no condition) → every fire.
        if (string.IsNullOrWhiteSpace(bp.Condition)
            || !_eventMetaByEdgeKey.TryGetValue(key,
                out (Type EventType, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields) meta))
        {
            return ([.. applied], null);
        }

        // Conditional → compile via the player/entity-aware path (handles pure-event + bare-`player` +
        // entity reads). The selected slot is baked into the predicate, so a selection change recompiles.
        int selectedSlot = SelectedPlayerSlotOrAll();
        EdgeConditionCompileResult compiled;
        try
        {
            compiled = ExpressionCompiler.CompileEdgePlayerEntityCondition(
                bp.Condition!, meta.EventType, meta.Fields, selectedSlot, _perPlayerProviders,
                meta.ParameterType);
        }
        catch
        {
            return ([], null); // invalid (editor blocks saving these) → no hits rather than every fire
        }

        // References the selected player but none is selected → no hits (invariant: a negative slot's
        // entity reads coalesce to default and would otherwise match every fire).
        if (compiled.ReferencesSelectedPlayer && selectedSlot < 0)
        {
            return ([], null);
        }

        // Pure-event / bare-`player` run synchronously with a no-op accessor (they never read it).
        if (!compiled.NeedsEntityCache)
        {
            return (EdgeBreakpointConditions.FilterAppliedWithEntities(
                applied, compiled.Predicate, PayloadAt, _ => NoopEntityValueAt.Instance), null);
        }

        // Entity reads → defer to the cache (built/reused by the caller). The Recompute closure filters the
        // fires against the positioned accessor; the slot is already baked into the compiled predicate.
        Delegate predicate = compiled.Predicate;
        return ([], new PendingEntityHit(bp, applied,
            acc => EdgeBreakpointConditions.FilterAppliedWithEntities(applied, predicate, PayloadAt, acc ?? _noAccessor)));
    }

    // The decoded subject backing message index i: a game event yields the FIRE — breakpoint
    // predicates on game-event edges compile envelope-typed (ParameterType = GameEvent), reaching
    // wire fields through Payload and per-fire transport (event.tick) off the fire itself, so
    // synthesized events (GameEvent subclasses declaring their own fields, no payload) need no
    // special case. A net message has no envelope; its raw payload stays the subject.
    // Only ever called for condition-supported edges, whose messages are game-event / net-message.
    private object? PayloadAt(int i)
    {
        if (_messageList is null || i < 0 || i >= _messageList.Count)
        {
            return null;
        }

        NetMessage msg = _messageList[i].Message;
        return msg is GameEventMessage gem ? gem.DecodedEvent : msg.Payload;
    }

    private (List<int> Hits, PendingEntityHit? Pending) ComputeNodeHits(GraphBreakpoint bp, SnapshotTable snaps)
    {
        int col = NodeColumnByName(bp.NodeName ?? "");
        if (col < 0)
        {
            return ([], null);
        }

        // The condition may reference the target's own `value`, other tracked nodes / game entity
        // contexts (state, rising-edge), OR input.<event>.<field> for the events activating this node
        // (discrete over those fires, optionally joined with state) — including a bare `player` comparison
        // and event-subject / selected-player entity reads — the helper dispatches by substrate. An
        // entity-read input condition returns a DEFERRED plan the entity cache fulfils; everything else
        // resolves synchronously. Invalid conditions yield no hits (the editor blocks saving them).
        NodeBreakpointConditions.NodeHitPlan plan = NodeBreakpointConditions.PlanNodeHits(
            snaps, _trackedNodesByColumn, col, bp.Condition,
            NodeInputEventsByName(bp.NodeName ?? ""), PayloadAt, SelectedPlayerSlotOrAll(), _perPlayerProviders);

        return plan.NeedsEntityCache
            ? ([], new PendingEntityHit(bp, plan.FireMessageIndices, plan.Recompute!))
            : (plan.SyncHits ?? [], null);
    }

    // The node's DIRECT input events (event name → type, fields, union of fire indices), for
    // input.<event>.<field> conditions. Only direct incoming edges with event metadata count;
    // entity-change input edges (no registry event) are excluded. Input edges sharing an event union
    // their fire sets (same event type/fields).
    private Dictionary<string, NodeBreakpointConditions.InputEventInfo> NodeInputEventsByName(string nodeName)
    {
        Dictionary<string, (Type Type, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields, List<int> Fires)> acc =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (GraphEdgeViewModel e in _allGraphEdges)
        {
            if (e.Destination.Name != nodeName)
            {
                continue;
            }

            (string, string, string, string?) key = EdgeKey(e);
            if (!_eventMetaByEdgeKey.TryGetValue(key,
                    out (Type EventType, Type? ParameterType, IReadOnlyDictionary<string, EventFieldAccessor> Fields) meta))
            {
                continue;
            }

            if (!acc.TryGetValue(e.Label, out (Type, Type?, IReadOnlyDictionary<string, EventFieldAccessor>, List<int>) entry))
            {
                entry = (meta.EventType, meta.ParameterType, meta.Fields, new List<int>());
                acc[e.Label] = entry;
            }

            if (_appliedByEdgeKey.TryGetValue(key, out IReadOnlyList<int>? fires))
            {
                entry.Item4.AddRange(fires); // List is shared by reference → union accumulates
            }
        }

        return acc.ToDictionary(
            kv => kv.Key,
            kv => new NodeBreakpointConditions.InputEventInfo(
                kv.Value.Item1, kv.Value.Item3, kv.Value.Item4.Distinct().OrderBy(x => x).ToList(),
                kv.Value.Item2),
            StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshBreakpointMarkers()
    {
        foreach (GraphNodeViewModel vm in _allGraphNodes)
        {
            GraphBreakpoint? bp = GraphBreakpoints.FindNode(vm.Name);
            vm.HasBreakpoint = bp is not null;
            vm.HasConditionalBreakpoint = bp?.Condition is not null;
        }

        foreach (GraphEdgeViewModel vm in _allGraphEdges)
        {
            GraphBreakpoint? bp = GraphBreakpoints.FindEdge(vm.Source.Name, vm.Destination.Name, vm.Label, vm.ConditionLabel);
            vm.HasBreakpoint = bp is not null;
            vm.HasConditionalBreakpoint = bp?.Condition is not null;
        }

        _graphViewModel.InvalidateNodeStates(); // re-renders edges too (push-triggered full repaint)
    }

    private void SeekToMessageCore(int index, bool notifyFrameChange)
    {
        if (_messageSnapshots is null || _messageList is null)
        {
            return;
        }

        if (index < 0 || index >= _messageSnapshots.Count)
        {
            return;
        }

        NodeSnapshot[] snap = _messageSnapshots.MaterializeRow(index);
        _currentSnapshot = snap;

        // Each node pulls its own absolute snapshot column (TrackedIndex), not its position in the
        // list — so this stays correct whether GraphNodes is the full set or an arbitrary chain
        // sub-graph in arbitrary order. A node with no column (TrackedIndex < 0 or out of range)
        // is left inert.
        foreach (GraphNodeViewModel node in GraphNodes)
        {
            int idx = node.TrackedIndex;
            if (idx >= 0 && idx < snap.Length)
            {
                node.IsActive = snap[idx].IsActive;
                node.DisplayValue = snap[idx].DisplayValue;
            }
            else
            {
                node.IsActive = false;
                node.DisplayValue = null;
            }
        }

        _graphViewModel.InvalidateNodeStates();

        // Re-assert the player + per-player-chain inert gate against this snapshot. The gate holds
        // across in-load seeks (filters don't persist across loads, but do across message steps).
        WriteTableCells(snap);

        if (PlayerTables.Count > 0)
        {
            _graphViewModel.InvalidateTableCells();
        }

        (DemoFrame frame, NetMessage msg) = _messageList[index];
        if (CardFactory is not null)
        {
            HarvestCardViewModel card = CardFactory(msg);
            card.IsExpanded = true;
            CurrentCardList = [card];
            HasCurrentCard = true;
        }
        else
        {
            CurrentCardList = [];
            HasCurrentCard = false;
        }

        if (notifyFrameChange && _frameIndexByFrame is not null)
        {
            int frameIdx = _frameIndexByFrame.GetValueOrDefault(frame, -1);
            if (frameIdx >= 0 && frameIdx != _currentFrameIndex)
            {
                _currentFrameIndex = frameIdx;
                OnFrameSeeked?.Invoke(frameIdx);
            }
        }

        // Navigating away from the breakpoint we halted on clears the "stopped" amber. Continue / Back /
        // JumpToBreakpoint all MarkHit then seek to that SAME index, so this guard skips them; every other
        // route here (Next/Previous message, a frame click) lands on a different index and clears.
        if (GraphBreakpoints.LastHitMessageIndex >= 0 && index != GraphBreakpoints.LastHitMessageIndex)
        {
            GraphBreakpoints.ClearHit();
        }

        CurrentMessageIndex = index;
    }

    // ── Filtering ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Reacts to a filter change. The chain selection now drives a <em>sub-graph swap</em>:
    ///     the rendered graph becomes the induced sub-graph of the selected chains (an explicit,
    ///     deliberate view change that relayouts compactly), while the full graph stays evaluated.
    ///     Empty chain selection restores the whole graph. The player picker still gates table rows.
    ///     Wired to <c>Filter.FiltersChanged</c>.
    /// </summary>
    private void ApplyFilters()
    {
        IReadOnlySet<string> selectedChains = Filter.SelectedChainKeys;
        int? selectedSlot = Filter.SelectedPlayer is { Slot: >= 0 } sel ? sel.Slot : null;

        // A player-selection change re-binds any breakpoint whose condition references `player` — both
        // edge conditions (`event.Attacker == player`) and node input conditions
        // (`input.player_death.Attacker == player`) recompile against the new slot; conditions that
        // don't reference `player` are inert. Independent of the sub-graph swap below; cheap (no entity
        // replay in the bare-player path).
        int breakpointSlot = SelectedPlayerSlotOrAll();
        if (breakpointSlot != _breakpointPlayerSlot)
        {
            _breakpointPlayerSlot = breakpointSlot;
            RebindBreakpoints();
        }

        // Nothing rendered differently → no work. Both the chain selection AND the player slot are
        // structural inputs to the rendered tables now (selecting a player REMOVES the other rows),
        // so an unchanged pair means the current render already matches the filter.
        if (selectedChains.SetEquals(_renderedChainKeys) && selectedSlot == _renderedPlayerSlot)
        {
            return;
        }

        // Chain or player change → swap the rendered sub-graph (rows are filtered structurally
        // inside the swap). The token (bumped synchronously here) collapses a burst of multi-select
        // toggles into one swap to the final state; the task-tail serializes the async layout so
        // overlapping SetGraphAsync can't race CurrentLayout.
        int token = ++_swapRequestToken;
        HashSet<string> target = selectedChains.ToHashSet(StringComparer.Ordinal);

        // Chain to the previous swap's tail so layouts serialize (no overlap → no out-of-order
        // CurrentLayout). Assignment + chaining happen on the UI thread, so no lock is needed.
        _swapTail = RunSwapAsync(_swapTail, token, target, selectedSlot);
    }

    /// <summary>
    ///     Serialized swap runner: waits for the prior swap's tail, then runs the swap if it's still
    ///     the latest request. Always completes (never faults the tail) so a failed swap can't poison
    ///     the chain.
    ///     <para>
    ///         Burst collapse is handled by the token, not a timer: every click in a burst runs
    ///         <c>++_swapRequestToken</c> synchronously on the UI thread before any continuation
    ///         executes, so by the time A's/B's continuation runs the token already points at the
    ///         last click — they see the mismatch and skip; only the final swap runs. The
    ///         <c>previous</c> await serializes the actual layouts so they can't overlap.
    ///     </para>
    /// </summary>
    private async Task RunSwapAsync(Task previous, int token, HashSet<string> target, int? playerSlot)
    {
        try
        {
            await previous; // serialize: the prior swap's layout finishes before this one starts
        }
        catch
        {
            // Prior swap's failure is its own concern; don't let it break the chain.
        }

        if (token != _swapRequestToken)
        {
            return; // superseded by a newer toggle while queued behind the running swap
        }

        try
        {
            _renderedChainKeys = target;
            _renderedPlayerSlot = playerSlot;
            await ShowSubGraphAsync(target, playerSlot);
        }
        catch (Exception ex)
        {
            StatusText = $"Filter failed: {ex.Message}";
        }
    }

    /// <summary>
    ///     Renders the induced sub-graph for <paramref name="selectedChains" /> (or the full graph
    ///     when the selection is empty) and re-applies the current message state. Does NOT touch
    ///     evaluation data (<c>_messageSnapshots</c>/<c>_messageList</c>/<c>_currentSnapshot</c>),
    ///     so the full graph stays analysed and every surviving node keeps resolving its own
    ///     snapshot column via <see cref="GraphNodeViewModel.TrackedIndex" />.
    /// </summary>
    private async Task ShowSubGraphAsync(HashSet<string> selectedChains, int? selectedSlot)
    {
        if (_allGraphNodes.Count == 0)
        {
            return; // nothing loaded yet
        }

        IReadOnlyList<GraphNodeViewModel> renderNodes;
        IReadOnlyList<GraphEdgeViewModel> renderEdges;
        IReadOnlyList<INodeGroup>? renderGroups;
        IReadOnlyList<PlayerTableViewModel> renderTables;

        if (selectedChains.Count == 0)
        {
            // No chain selected → full graph.
            renderNodes = _allGraphNodes;
            renderEdges = _allGraphEdges;
            renderGroups = _allGroups.Count > 0 ? _allGroups : null;
            renderTables = _allTables;
        }
        else
        {
            // Build the sub-graph node set: chain-member nodes + their 1-hop upstream context /
            // enrichment neighbours + the Root (so chains stay anchored). Edges and groups are
            // then induced from that set by GraphProjection.
            HashSet<GraphNodeViewModel> include = BuildSubGraphNodeSet(selectedChains);

            SubGraph sub = GraphProjection.Induce(
                _allGraphNodes,
                _allGraphEdges,
                _allGroups,
                node => node is GraphNodeViewModel vm && include.Contains(vm));

            renderNodes = sub.Nodes.Cast<GraphNodeViewModel>().ToList();
            renderEdges = sub.Edges.Cast<GraphEdgeViewModel>().ToList();
            renderGroups = sub.Groups.Count > 0 ? sub.Groups : null;

            // Per-player tables: keep only those that contribute a column to a SELECTED per-player
            // chain, projecting each to just that chain's columns. Pure game-chain selections show
            // no tables.
            HashSet<string> perPlayerKeys = selectedChains
                .Where(k => Filter.ScopeOf(k) == ChainScope.PerPlayer)
                .ToHashSet(StringComparer.Ordinal);
            renderTables = perPlayerKeys.Count == 0
                ? []
                : ProjectTables(_allTables, perPlayerKeys, include);
        }

        // Player selection REMOVES the non-selected rows (structural), uniformly across both the
        // full-graph and sub-graph branches. Null slot → keep every row. A pure render projection:
        // evaluation is untouched, every player is still analysed, surviving cells still resolve via
        // NodeTrackedIndex.
        if (selectedSlot is { } slot)
        {
            renderTables = FilterTableRows(renderTables, slot);
        }

        // Re-point the rendered sets. GraphNodes drives the seek loop; PlayerTables the table render.
        GraphNodes = renderNodes;
        PlayerTables = renderTables;

        await _graphViewModel.SetGraphAsync(
            renderNodes.Cast<IGraphNode>().ToList(),
            renderEdges.Cast<IGraphEdge>().ToList(),
            renderGroups,
            renderTables.Count > 0 ? renderTables.Cast<INodeTable>().ToList() : null);

        // Re-apply the current message state to the freshly-rendered set (nodes pull their own
        // TrackedIndex column; table cells honour the player gate). Does not move the position.
        if (_currentSnapshot is not null)
        {
            ReapplyCurrentState();
        }
    }

    /// <summary>
    ///     Builds the node set for the selected chains: every node whose <c>ChainIds</c> overlaps
    ///     the selection, plus each such node's immediate upstream neighbours (1-hop), plus the
    ///     graph Root. The 1-hop pull-in keeps a chain's edges anchored to the context/enrichment
    ///     nodes that feed it (those carry no chain membership of their own).
    /// </summary>
    private HashSet<GraphNodeViewModel> BuildSubGraphNodeSet(HashSet<string> selectedChains)
    {
        HashSet<GraphNodeViewModel> include = new(ReferenceEqualityComparer.Instance);

        // Seed: chain members.
        List<GraphNodeViewModel> members = new();
        foreach (GraphNodeViewModel node in _allGraphNodes)
        {
            if (node.ChainIds.Overlaps(selectedChains))
            {
                include.Add(node);
                members.Add(node);
            }
        }

        // Per-player chains contribute NO graph nodes of their own (their nodes live in the player
        // table). Seed the graph context from the lifecycle source nodes that feed a selected
        // per-player chain's columns, so the graph shows the events producing those stats instead of
        // collapsing to Root alone.
        foreach (PlayerTableViewModel table in _allTables)
        {
            foreach (TableColumnEdgeViewModel ce in table.ColumnEdges)
            {
                if (ce.ChainId is { } key && selectedChains.Contains(key))
                {
                    include.Add(ce.SourceNode);
                    members.Add(ce.SourceNode);
                }
            }
        }

        // +1-hop upstream of each seed (chain members + per-player column sources).
        foreach (GraphNodeViewModel member in members)
        {
            if (_upstreamOf.TryGetValue(member, out List<GraphNodeViewModel>? sources))
            {
                foreach (GraphNodeViewModel src in sources)
                {
                    include.Add(src);
                }
            }
        }

        // Always anchor to Root.
        if (_rootNode is not null)
        {
            include.Add(_rootNode);
        }

        return include;
    }

    /// <summary>
    ///     Projects the player tables to the selected per-player chains. A column survives iff its
    ///     chain (from <see cref="PlayerTableViewModel.ColumnChainIds" />) is selected — full stop,
    ///     independent of whether it has a column edge. This keeps computed columns (KAST%, 2K–5K
    ///     etc., which produce no lifecycle edge) that the old edge-based selection silently dropped.
    ///     Connector edges are projected only for surviving columns whose source node is rendered.
    /// </summary>
    private static List<PlayerTableViewModel> ProjectTables(
        IReadOnlyList<PlayerTableViewModel> tables,
        HashSet<string> perPlayerKeys,
        HashSet<GraphNodeViewModel> includedNodes)
    {
        List<PlayerTableViewModel> result = new();

        foreach (PlayerTableViewModel table in tables)
        {
            // Surviving column indices: the COLUMN's own chain key is selected. Edge-independent.
            List<int> ordered = new();
            for (int c = 0; c < table.ColumnNames.Count; c++)
            {
                string? chainId = c < table.ColumnChainIds.Count ? table.ColumnChainIds[c] : null;
                if (chainId is not null && perPlayerKeys.Contains(chainId))
                {
                    ordered.Add(c);
                }
            }

            if (ordered.Count == 0)
            {
                continue;
            }

            // Old→new column index remap (preserve order).
            Dictionary<int, int> remap = new();
            for (int newIdx = 0; newIdx < ordered.Count; newIdx++)
            {
                remap[ordered[newIdx]] = newIdx;
            }

            List<string> columnNames = ordered.Select(c => table.ColumnNames[c]).ToList();
            List<string?> columnChainIds = ordered
                .Select(c => c < table.ColumnChainIds.Count ? table.ColumnChainIds[c] : null)
                .ToList();

            List<TableRowViewModel> rows = new();
            foreach (TableRowViewModel row in table.Rows)
            {
                List<TableCellViewModel> cells = ordered
                    .Where(c => c < row.Cells.Count)
                    .Select(c => row.Cells[c])
                    .ToList();
                rows.Add(new TableRowViewModel(row.PlayerName, row.PlayerSlot, row.FilterAnnotation, cells));
            }

            PlayerTableViewModel projected = new(columnNames, rows, columnChainIds)
            {
                // Connector edges only for surviving columns whose source graph node is actually
                // rendered. Edge presence is now cosmetic (it draws the connector); it never decides
                // whether the column exists.
                ColumnEdges = table.ColumnEdges
                    .Where(ce => remap.ContainsKey(ce.ColumnIndex) && includedNodes.Contains(ce.SourceNode))
                    .Select(ce => ce with
                    {
                        ColumnIndex = remap[ce.ColumnIndex]
                    })
                    .ToList()
            };
            result.Add(projected);
        }

        return result;
    }

    /// <summary>
    ///     Projects each table to just the selected player's row, REMOVING the others (vs the old
    ///     in-place dim). Columns, column chain ids and column edges are preserved verbatim (row
    ///     filtering never touches the column axis). A table with no matching row is dropped. Cells
    ///     are reused by reference — they keep resolving their own <c>NodeTrackedIndex</c> snapshot
    ///     column, so no evaluation work is duplicated.
    /// </summary>
    private static List<PlayerTableViewModel> FilterTableRows(
        IReadOnlyList<PlayerTableViewModel> tables, int slot)
    {
        List<PlayerTableViewModel> result = new();

        foreach (PlayerTableViewModel table in tables)
        {
            List<TableRowViewModel> rows = table.Rows.Where(r => r.PlayerSlot == slot).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            result.Add(new PlayerTableViewModel(table.ColumnNames, rows, table.ColumnChainIds)
            {
                ColumnEdges = table.ColumnEdges
            });
        }

        return result;
    }

    /// <summary>Builds the dest→sources adjacency from the full edge set for the 1-hop closure.</summary>
    private static Dictionary<GraphNodeViewModel, List<GraphNodeViewModel>> BuildUpstreamAdjacency(
        IReadOnlyList<GraphEdgeViewModel> edges)
    {
        Dictionary<GraphNodeViewModel, List<GraphNodeViewModel>> map = new(ReferenceEqualityComparer.Instance);
        foreach (GraphEdgeViewModel e in edges)
        {
            if (!map.TryGetValue(e.Destination, out List<GraphNodeViewModel>? sources))
            {
                map[e.Destination] = sources = new List<GraphNodeViewModel>();
            }

            sources.Add(e.Source);
        }

        return map;
    }

    /// <summary>
    ///     Re-applies the current snapshot to the currently-rendered nodes and tables without
    ///     moving the message position. Used after a sub-graph swap so the new set shows live state.
    /// </summary>
    private void ReapplyCurrentState()
    {
        NodeSnapshot[]? snap = _currentSnapshot;
        if (snap is null)
        {
            return;
        }

        foreach (GraphNodeViewModel node in GraphNodes)
        {
            int idx = node.TrackedIndex;
            if (idx >= 0 && idx < snap.Length)
            {
                node.IsActive = snap[idx].IsActive;
                node.DisplayValue = snap[idx].DisplayValue;
            }
            else
            {
                node.IsActive = false;
                node.DisplayValue = null;
            }
        }

        _graphViewModel.InvalidateNodeStates();

        WriteTableCells(snap);
        if (PlayerTables.Count > 0)
        {
            _graphViewModel.InvalidateTableCells();
        }
    }

    /// <summary>
    ///     Writes every table cell from <paramref name="snap" />, honoring the player filter: cells
    ///     whose row is a non-selected player are written inert (<c>IsActive=false</c>, <c>"-"</c>).
    ///     Column-scope is handled structurally now (sub-graph projection trims columns), so the
    ///     remaining gate here is the row/player one. Row/column counts are unchanged → no relayout.
    /// </summary>
    private void WriteTableCells(NodeSnapshot[] snap)
    {
        // Player-row filtering is structural now (FilterTableRows removes non-selected rows in the
        // swap), so every row present here is a kept row — just write its cells from the snapshot.
        foreach (PlayerTableViewModel table in PlayerTables)
        {
            foreach (TableRowViewModel row in table.Rows)
            {
                foreach (TableCellViewModel cell in row.Cells)
                {
                    if (cell.NodeTrackedIndex < snap.Length)
                    {
                        cell.IsActive = snap[cell.NodeTrackedIndex].IsActive;
                        cell.DisplayValue = snap[cell.NodeTrackedIndex].DisplayValue;
                    }
                    else
                    {
                        cell.IsActive = false;
                        cell.DisplayValue = null;
                    }
                }
            }
        }
    }

    // A breakpoint awaiting the entity cache — an entity-read EDGE condition OR an entity-read NODE input
    // condition. FireMessages are the fire MESSAGE indices whose frames the cache must cover; Recompute,
    // given a per-fire accessor (frameIndexOfMessage → pre-frame entity state), produces the matching
    // message indices. Edge: filters the cached fires by the compiled predicate. Node: re-runs the input
    // matcher feeding pre-event node state + the accessor. Uniform so the cache lifecycle serves both.
    private readonly record struct PendingEntityHit(
        GraphBreakpoint Bp,
        IReadOnlyList<int> FireMessages,
        Func<Func<int, IEntityValueAt?>?, List<int>> Recompute);
}

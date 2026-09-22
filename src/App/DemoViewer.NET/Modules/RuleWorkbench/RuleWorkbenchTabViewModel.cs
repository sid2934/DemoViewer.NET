#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.Visualization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// RuleGraphSkeleton / GraphViewModel: MSAGL layout for the document-derived canvas

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     The Authoring Workbench tab's view-model. It hosts the in-process demo-less v2
///     checker plus the editor: it lists the user rules folder's <c>*.rules.yaml</c> files, opens one
///     into the editor document, and runs the check over the <b>edited buffer</b> (the open file taken
///     from the editor, the rest from disk) so diagnostics reflect unsaved edits. Save writes the buffer
///     and re-checks; a <see cref="FileSystemWatcher" /> (desktop only) re-checks on external edits and
///     reloads the open file when it changes on disk and the buffer is clean. Selecting a diagnostic for
///     the open file raises <see cref="JumpRequested" /> so the View moves the caret.
/// </summary>
public sealed partial class RuleWorkbenchTabViewModel : ObservableObject, IWorkspaceTabViewModel, IDisposable
{
    private static readonly Regex _catalogVersionLine =
        new(@"^catalog_version:\s*\d+\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex _rulesetLine =
        new(@"^ruleset:.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    // How long the canvas waits for typing to stop. Long enough that a burst of keystrokes renders
    // once, short enough that a pause feels like the canvas is keeping up.
    private static readonly TimeSpan _graphIdle = TimeSpan.FromMilliseconds(250);

    // The live user-settings monitor (null on the designer / test path) + its OnChange
    // subscription, disposed in Dispose.
    private readonly IOptionsMonitor<AppSettings>? _settings;
    private readonly IDisposable? _settingsSub;
    private readonly string? _shippedDir;
    private readonly string? _userDir; // null on WASM / no writable filesystem

    private IModuleContext? _context;

    private ICurrentDemoSource? _demoSource;

    // Diagnostics-pillar logger (v0.6.0: failure surfaces show clean text, this carries the real
    // exception). Lazy: the ambient factory is wired after construction.
    private ILogger? _diagLog;

    [ObservableProperty]
    private string _documentText = "";

    [ObservableProperty]
    private string _evalSummary = "Evaluate the open ruleset against the loaded demo.";

    /// <summary>Node count of the open ruleset's canvas (0 = nothing open), the deterministic, sync test hook.</summary>
    [ObservableProperty]
    private int _graphNodeCount;

    /// <summary>Caption for the canvas pane, reflects the last render or an empty state.</summary>
    [ObservableProperty]
    private string _graphSummary = "Open a ruleset to see its nodes.";

    [ObservableProperty]
    private bool _isClean;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    ///     True while an evaluation is in flight. Gates <see cref="CanEvaluate" /> so BOTH Evaluate and
    ///     Advanced Evaluate are disabled until the running one finishes, a single evaluation at a time. The
    ///     heavy build/evaluate/project runs off the UI thread (<see cref="EvaluateDocsAsync" />), so this flag
    ///     genuinely spans the work rather than flipping within one synchronous UI beat.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    [NotifyCanExecuteChangedFor(nameof(EvaluateAdvancedCommand))]
    private bool _isEvaluating;

    private bool _loadingDocument; // guards the disk→buffer load from marking the doc dirty

    // Canvas-node title -> what the OPEN document declares it to be. Rebuilt with the graph, and
    // the reason a node can be edited at all; see RulesetNodeBinding.
    private IReadOnlyDictionary<string, RulesetNodeBinding> _nodeBindings =
        new Dictionary<string, RulesetNodeBinding>(StringComparer.Ordinal);

    // Canvas-node title -> where its stats:/highlights: entry starts. This is the half of the
    // node-to-caret sync that AuthoringGraph could not supply at all: its nodes carry no
    // back-reference to the YAML (design.md §6.1), and every node of the document model carries one.
    private IReadOnlyDictionary<string, SourcePosition> _nodePositions =
        new Dictionary<string, SourcePosition>(StringComparer.Ordinal);

    // Set while a render re-applies the selection it captured, so the restore does not read as a
    // user click and yank the caret to the node the author had merely left selected.
    private bool _restoringSelection;

    // Set while one direction of the node/caret sync is running, so the other does not answer it.
    private bool _syncingCaret;

    // The idle-debounce token. Only the newest scheduled render runs; see ScheduleGraphRender.
    private int _renderGeneration;

    private bool _disposed;

    /// <summary>The open file's proposed save-as name (the read-only save-as prompt).</summary>
    [ObservableProperty]
    private string _saveAsName = "";

    [ObservableProperty]
    private RulesetFileRef? _selectedFile;

    [ObservableProperty]
    private WorkbenchTraceTarget? _selectedTraceTarget;

    /// <summary>Whether the node canvas pane is shown (toolbar toggle). Desktop-only; see <see cref="GraphSupported" />.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GraphPaneWidth))]
    private bool _showGraph;

    [ObservableProperty]
    private string _summary = "Loading rulesets…";

    private bool _suppressWatcherReload;

    /// <summary>The last evaluation's trace, source of the <see cref="TraceFires" /> lookups.</summary>
    private WorkbenchTraceReport _traceReport = WorkbenchTraceReport.Empty;

    [ObservableProperty]
    private string _traceSummary = "Evaluate, then pick a stat or highlight to see when it fired.";

    private FileSystemWatcher? _watcher;

    /// <param name="settings">
    ///     Live user-settings monitor. When present, <see cref="DeveloperMode" /> reads
    ///     <c>AppSettings.Features.DeveloperMode</c> live and this VM re-raises <see cref="IsReadOnlyFile" />
    ///     on change. Null → the DEMOVIEWER_DEVELOPER_MODE env fallback (designer / tests).
    /// </param>
    public RuleWorkbenchTabViewModel(IOptionsMonitor<AppSettings>? settings = null)
    {
        _settings = settings;
        _shippedDir = SafeShippedDir();
        _userDir = OperatingSystem.IsBrowser() ? null : SafeUserDir(_shippedDir);
        Paths = BuildPaths();
        PathTree = WorkbenchPathTree.Build(Paths);

        // The box MSAGL reserves has to be the box the canvas draws, or the spacing is computed for
        // one node and applied to another. A document node is a header, a descriptor line and a row
        // of event chips, which is taller than the 56 the default config assumes.
        GraphViewModel.Style = GraphViewModel.Style with
        {
            Node = GraphViewModel.Style.Node with
            {
                Width = 200,
                Height = 86
            }
        };

        RefreshFiles();
        StartWatcher();
        Check();

        // Live developer-mode toggle: re-evaluate shipped-file editability when settings change. External
        // edits arrive on a threadpool thread (SettingsService.Write's file watcher), so marshal to the UI
        // thread before touching the observable IsReadOnlyFile / CanSave surface.
        _settingsSub = _settings?.OnChange(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsReadOnlyFile));
                OnPropertyChanged(nameof(CanSave));
            }));
    }

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.RuleWorkbench");

    // Shipped rulesets are read-only unless developer mode is on. A save on a read-only file
    // prompts for a new name (save-as to the user overlay).
    //
    // BEHAVIORAL CHANGE: this was a load-time `static readonly` env read, a gate fixed for
    // the process lifetime. It is now a LIVE read of AppSettings.Features.DeveloperMode via the injected
    // IOptionsMonitor, so toggling developer mode in settings.json flips shipped-file editability without a
    // restart (the ctor subscribes OnChange to re-raise IsReadOnlyFile). When no settings monitor is
    // injected (designer / tests) it falls back to the DEMOVIEWER_DEVELOPER_MODE env var; when a monitor is
    // present, the settings value is authoritative (env is not consulted).
    private bool DeveloperMode =>
        _settings is not null
            ? _settings.CurrentValue.Features.DeveloperMode
            : Environment.GetEnvironmentVariable("DEMOVIEWER_DEVELOPER_MODE") is "1" or "true" or "True";

    /// <summary>Shipped + user <c>*.rules.yaml</c> in the Authoring dropdown.</summary>
    public ObservableCollection<RulesetFileRef> OpenableFiles { get; } = [];

    /// <summary>True when the open file is a shipped baseline and DeveloperMode is off. Save must save-as.</summary>
    public bool IsReadOnlyFile => SelectedFile is { IsShipped: true } && !DeveloperMode;

    /// <summary>Every diagnostic from the last check (all rulesets).</summary>
    public ObservableCollection<WorkbenchDiagnostic> Diagnostics { get; } = [];

    /// <summary>Diagnostics whose file is the currently-open document: the inline set.</summary>
    public ObservableCollection<WorkbenchDiagnostic> OpenFileDiagnostics { get; } = [];

    /// <summary>Neither Evaluate command may start while one is already running (shared single-flight guard).</summary>
    private bool CanEvaluate => !IsEvaluating;

    /// <summary>
    ///     Rendered evaluation outputs: the per-player scoreboard first, then the declared
    ///     <c>tables:</c> and keyed tables, each a real grid, like the Stats tab.
    /// </summary>
    public ObservableCollection<WorkbenchScoreboard> Boards { get; } = [];

    /// <summary>Traceable stats + highlights from the last evaluation (applied-fire picker).</summary>
    public ObservableCollection<WorkbenchTraceTarget> TraceTargets { get; } = [];

    /// <summary>Applied fires of the selected trace target: when/where/who it fired.</summary>
    public ObservableCollection<WorkbenchTraceFire> TraceFires { get; } = [];

    /// <summary>The draggable authoring vocabulary (catalog context + entity-read paths): the data browser palette.</summary>
    public IReadOnlyList<WorkbenchPath> Paths { get; }

    /// <summary>The vocabulary as an expandable tree: match → match.* → sub-properties.</summary>
    public IReadOnlyList<WorkbenchPathNode> PathTree { get; }

    /// <summary>Live player values at the current frame (name / team / position), populated while a demo is loaded.</summary>
    public ObservableCollection<LivePlayerRow> LivePlayers { get; } = [];

    // ── Ruleset node canvas ──────────────────────────────────────────────────────────────────────

    /// <summary>The MSAGL graph of the OPEN ruleset (demo-less, structural), reuses the shipped Visualization stack.</summary>
    public GraphViewModel GraphViewModel { get; } = new();

    /// <summary>
    ///     Graph rendering is desktop-only (MSAGL layout runs off-thread); the toggle hides on WASM.
    ///     <para>
    ///         Unchanged by the move to a node-based renderer, and worth saying why, because the
    ///         obvious guess is that nodify is what cannot run there. It is not: the vendored fork
    ///         loads, themes and realizes controls on the real browser-wasm runtime (design.md §6.5),
    ///         and the Browser head publishes with it. What this gate is about is MSAGL on a runtime
    ///         with one thread. Issue #25 has what it would take to lift it.
    ///     </para>
    /// </summary>
    public bool GraphSupported { get; } = !OperatingSystem.IsBrowser();

    /// <summary>
    ///     The canvas column's width, which is how the pane collapses without leaving a gap where it
    ///     was.
    ///     <para>
    ///         <b>The canvas is a PANE, not an overlay.</b> It used to be a
    ///         <c>Grid.ColumnSpan="3"</c> border with an opaque background, so switching it on covered
    ///         the YAML editor and the vocabulary browser and the two surfaces were alternating
    ///         full-screen modes. design.md §9 decision 8 makes the canvas co-equal with the editor
    ///         because both author the same document, and a mode that hides the other one cannot be
    ///         co-equal with it.
    ///     </para>
    /// </summary>
    public GridLength GraphPaneWidth => ShowGraph ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);

    /// <summary>
    ///     The open ruleset's nodes, positioned by MSAGL, for the node-based renderer. Reconciled by
    ///     title on every render rather than cleared and refilled: a container costs 2.5 to 3.2 ms to
    ///     realize (design.md §6.5), so tearing all of them down on a committed keystroke is the
    ///     single most expensive thing the canvas did.
    /// </summary>
    public ObservableCollection<RulesetGraphNode> RulesetNodes { get; } = [];

    /// <summary>The edges between those nodes, as the two points each runs between.</summary>
    public ObservableCollection<RulesetGraphConnection> RulesetConnections { get; } = [];

    /// <summary>The node the graph has selected, or <c>null</c>. Drives the field editor.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedNodeCommand))]
    private RulesetGraphNode? _selectedRulesetNode;

    /// <summary>The selected node's editable keys and their current values.</summary>
    public ObservableCollection<RulesetNodeField> SelectedNodeFields { get; } = [];

    /// <summary>What the field editor says about the selection when it offers no fields.</summary>
    [ObservableProperty]
    private string _selectedNodeSummary = "Select a node to edit the stat behind it.";

    /// <summary>Whether the selected node is one the open document declares and can therefore be edited.</summary>
    public bool CanEditSelectedNode => SelectedRulesetNode?.IsEditable == true && !IsReadOnlyFile;

    /// <summary>Save-in-place is allowed only for an editable open file (user, or shipped in DeveloperMode) that is dirty.</summary>
    public bool CanSave => _userDir is not null && SelectedFile is not null && IsDirty && !IsReadOnlyFile;

    /// <summary>Save-As is offered whenever a file is open and the user overlay is writable.</summary>
    public bool CanSaveAs => _userDir is not null && SelectedFile is not null;

    /// <summary>
    ///     Evaluates the open ruleset (+ its sibling rulesets, the open file from the edited buffer)
    ///     against the loaded demo and projects the per-player game board: the in-Workbench 2MUCH
    ///     display. Composes and builds via the same seam the shipped analysis path uses
    ///     (<see cref="DemoAnalysis.Build(ParsedDemo, IReadOnlyList{RulesetDoc}, AnalysisOptions?)" />).
    /// </summary>
    /// <summary>Files available to "Advanced Evaluate": shipped + user rulesets, each toggleable.</summary>
    public ObservableCollection<EvaluableFile> EvaluableFiles { get; } = [];

    /// <summary>
    ///     Releases the file watcher and the settings subscription. (The module framework retains the
    ///     VM for the tab's life; this is the correctness/analyzer hook. The watcher is a live OS handle and
    ///     the settings subscription pins this VM otherwise.)
    /// </summary>
    public void Dispose()
    {
        _disposed = true; // a render queued by ScheduleGraphRender lands after this and must no-op
        _settingsSub?.Dispose();
        if (_watcher is not null)
        {
            _watcher.Changed -= OnUserDirChanged;
            _watcher.Created -= OnUserDirChanged;
            _watcher.Deleted -= OnUserDirChanged;
            _watcher.Renamed -= OnUserDirChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Re-check on activation, and start reflecting the loaded demo's live values.</summary>
    public void OnActivated(IModuleContext context)
    {
        _context = context;
        _demoSource = context as ICurrentDemoSource; // first-party demo access
        _context.Advanced += OnAdvanced;
        _context.DemoReset += OnDemoReset;
        RefreshFiles();
        RefreshLivePlayers();
        Check();
    }

    public void OnDeactivated()
    {
        if (_context is not null)
        {
            _context.Advanced -= OnAdvanced;
            _context.DemoReset -= OnDemoReset;
            _context = null;
        }

        _demoSource = null;
    }

    /// <summary>Raised when the View should move the caret to <c>(line, column)</c> (1-based).</summary>
    public event Action<int, int>? JumpRequested;

    /// <summary>
    ///     Default Evaluate: only the OPEN ruleset plus its transitive <c>use:</c> dependencies,
    ///     not every loaded ruleset. "Advanced Evaluate" (<see cref="EvaluateAdvanced" />) picks an
    ///     arbitrary set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private Task Evaluate() => EvaluateDocsAsync(LoadOpenFileWithDeps(), "the open ruleset");

    /// <summary>Advanced Evaluate: evaluate exactly the selected files (right-click menu multiselect).</summary>
    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private Task EvaluateAdvanced()
    {
        HashSet<string> selected = EvaluableFiles.Where(f => f.IsSelected)
            .Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<RulesetDoc> docs = [];
        foreach ((string path, string text) in EnumerateSources())
        {
            if (selected.Contains(path) && RulesetDocumentLoader.Load(text, path).Doc is { } doc)
            {
                docs.Add(doc);
            }
        }

        return EvaluateDocsAsync(docs, $"{docs.Count} selected ruleset(s)");
    }

    /// <summary>
    ///     Builds + evaluates the given docs on the loaded demo and renders the REAL outputs:
    ///     the per-player game scoreboard, then the declared <c>tables:</c> and keyed tables (via the same
    ///     projectors the Stats tab uses), plus the applied-fire trace, all from one evaluation.
    ///     <para>
    ///         The heavy build/evaluate/project runs on a background thread (<c>Task.Run</c>)
    ///         so the UI stays responsive; <see cref="IsEvaluating" /> is held across the whole run so BOTH
    ///         Evaluate commands are disabled until it finishes, only one evaluation at a time. Results are
    ///         computed as plain data off-thread and only applied to the observable collections after the await
    ///         (back on the UI thread). Re-entry is additionally short-circuited by the guard.
    ///     </para>
    /// </summary>
    private async Task EvaluateDocsAsync(List<RulesetDoc> docs, string scopeLabel)
    {
        if (IsEvaluating)
        {
            return; // a run is already in flight, belt-and-suspenders behind CanEvaluate
        }

        Boards.Clear();
        ClearTrace();
        ParsedDemo? demo = _demoSource?.CurrentDemo;
        if (demo is null)
        {
            EvalSummary = "No demo loaded — open a demo (Library / Parser tab) to evaluate.";
            return;
        }

        if (docs.Count == 0)
        {
            EvalSummary = "No rulesets to evaluate.";
            return;
        }

        IsEvaluating = true;
        EvalSummary = $"Evaluating {scopeLabel}…";
        try
        {
            EvalOutcome outcome = await Task.Run(() => ComputeEvaluation(docs, demo));

            // Back on the UI thread: apply the off-thread results to the observable collections.
            foreach (WorkbenchScoreboard board in outcome.Boards)
            {
                Boards.Add(board);
            }

            ApplyTrace(outcome.Trace);
            EvalSummary = outcome.HadSnapshots
                ? Boards.Count > 0
                    ? $"Evaluated {scopeLabel} — {Boards.Count} output table(s)."
                    : $"Evaluated {scopeLabel} — no output tables (add show: scoreboard / tables)."
                : $"Evaluated {scopeLabel} — no snapshots.";
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "evaluate the ruleset", ex);
            EvalSummary = UserFacingError.Describe("evaluate the ruleset", ex);
        }
        finally
        {
            IsEvaluating = false;
        }
    }

    /// <summary>The off-thread half of an evaluation: pure compute (no UI-collection mutation).</summary>
    private static EvalOutcome ComputeEvaluation(List<RulesetDoc> docs, ParsedDemo demo)
    {
        BuildResult build = DemoAnalysis.Build(demo, docs);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);
        EvaluationResult? result = run.Snapshots;
        WorkbenchTraceReport trace = WorkbenchTraceModel.Build(result, docs, demo); // applied-fire trace
        if (result is null)
        {
            return new EvalOutcome([], trace, false);
        }

        List<WorkbenchScoreboard> boards = [];

        // 1. Per-player game scoreboard.
        IReadOnlyList<MetricTable> gameTables = new PlayerGameStatsProjector().Project(result, demo);
        if (gameTables.Count > 0 && gameTables[0].Rows.Count > 0)
        {
            boards.Add(ToScoreboard(gameTables[0], "Scoreboard — game totals"));
        }

        // 2. Declared show: tables: outputs, then 3. keyed (bucket) tables.
        foreach (MetricTable table in run.ProjectConfiguredOutputs(demo))
        {
            if (table.Rows.Count > 0)
            {
                boards.Add(ToScoreboard(table, table.Name));
            }
        }

        foreach (MetricTable table in new KeyedStatsProjector().Project(result, demo))
        {
            if (table.Rows.Count > 0)
            {
                boards.Add(ToScoreboard(table, table.Name));
            }
        }

        return new EvalOutcome(boards, trace, true);
    }

    /// <summary>Projects a <see cref="MetricTable" /> into a Workbench grid (label + aligned column cells).</summary>
    private static WorkbenchScoreboard ToScoreboard(MetricTable table, string title)
    {
        List<WorkbenchScoreRow> rows = new(table.Rows.Count);
        foreach (MetricRow row in table.Rows)
        {
            List<string> cells = table.ValueColumns.Select(c => Fmt(row.Values.GetValueOrDefault(c))).ToList();
            rows.Add(new WorkbenchScoreRow(RowLabel(row), cells));
        }

        return new WorkbenchScoreboard(title, table.ValueColumns, rows);
    }

    /// <summary>Re-render the canvas when the toggle is switched on.</summary>
    partial void OnShowGraphChanged(bool value)
    {
        if (value)
        {
            RenderGraphForOpenFile();
        }
    }

    /// <summary>
    ///     Renders the <b>open ruleset's</b> node canvas into <see cref="GraphViewModel" />, structural and
    ///     independent of any evaluation. Every node is one <c>stats:</c> or <c>highlights:</c> entry of the
    ///     open document or of a <c>use:</c> dependency, and every edge is a value reference between two
    ///     siblings. With nothing open the canvas is empty (no fallback to "all rulesets").
    ///     <para>
    ///         <b>The source is <see cref="RulesetDocumentGraph" />, not <c>AuthoringGraph</c>, and the
    ///         reason is measured.</b> Over <c>rules/</c> the authoring graph hangs 137 of 240 edges off
    ///         four lifecycle hubs, leaves 108 of 230 nodes with out-degree 0, and draws all 18 of the
    ///         corpus's <c>compute:</c> stats with no edge at all, which in <c>aim_rating</c> means its 17
    ///         isolated nodes ARE the 17 board columns the file exists to produce (design.md §6.7). It also
    ///         carries no back-reference to the YAML, so no node could be jumped to.
    ///     </para>
    ///     <para>
    ///         The canvas builds with or without a demo, because a document's entries do not depend on one.
    ///         A bound demo supplies the tick rate and source profile the composition resolves against.
    ///     </para>
    ///     Node count is set synchronously (the deterministic test hook); the MSAGL layout is fired best-effort.
    /// </summary>
    public void RenderGraphForOpenFile()
    {
        if (!ShowGraph)
        {
            return;
        }

        if (OpenFilePath() is null)
        {
            SetEmptyGraph("Open a ruleset to see its nodes.");
            return;
        }

        List<RulesetDoc> docs = LoadOpenFileWithDeps();
        RenderGraph(docs, _demoSource?.CurrentDemo);
    }

    /// <summary>Clears the canvas to an empty state with the given caption.</summary>
    private void SetEmptyGraph(string summary)
    {
        GraphNodeCount = 0;
        GraphSummary = summary;
        RulesetNodes.Clear();
        RulesetConnections.Clear();
        _nodeBindings = new Dictionary<string, RulesetNodeBinding>(StringComparer.Ordinal);
        _nodePositions = new Dictionary<string, SourcePosition>(StringComparer.Ordinal);
        if (GraphSupported)
        {
            _ = GraphViewModel.SetGraphAsync([], []);
        }
    }

    /// <summary>
    ///     Queues a canvas render for the next idle beat, replacing any render already queued.
    ///     <para>
    ///         <b>The debounce is on the RENDER, not on the check.</b> Inline diagnostics have to track
    ///         the edit, so <see cref="Check" /> stays synchronous; what cannot run per keystroke is the
    ///         canvas, where realizing one node control costs 2.5 to 3.2 ms (design.md §6.5) and a
    ///         100-node ruleset therefore blocks the UI thread for about 280 ms. A generation token rather
    ///         than a timer, so the view-model needs no dispatcher to be constructed and nothing has to be
    ///         disposed on a host that never started one.
    ///     </para>
    /// </summary>
    private void ScheduleGraphRender()
    {
        if (!ShowGraph || _disposed)
        {
            return;
        }

        int generation = ++_renderGeneration;
        _ = Task.Delay(_graphIdle).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (generation == _renderGeneration && !_disposed)
                {
                    RenderGraphForOpenFile();
                }
            }, DispatcherPriority.Background),
            TaskScheduler.Default);
    }

    /// <summary>
    ///     Composes <paramref name="docs" /> under <paramref name="demo" /> (null = demo-less), projects the
    ///     composed rulesets into the document graph, and renders it. A failure is reported into
    ///     <see cref="GraphSummary" />.
    ///     <para>
    ///         No <c>RuleChainBuilder.Build</c> runs here any more. The builder is still constructed, for the
    ///         profile name the composition resolves against, but the graph is a projection of the CHECKED
    ///         model, so the chain build the old canvas paid for on every keystroke is gone.
    ///     </para>
    /// </summary>
    private void RenderGraph(List<RulesetDoc> docs, ParsedDemo? demo)
    {
        try
        {
            RuleChainBuilder builder = new(
                EventRegistry.Build(),
                demo is null ? null : AnalysisTarget.From(demo),
                entityProviders: EntityValueProviderRegistry.CreateDefault(),
                perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
            CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
            List<CheckedRuleset> rulesets =
            [
                .. RulesetComposition.Compose(docs, adapter, demo?.TickRate ?? 64.0, builder.Profile.GetType().Name)
                    .Rulesets
            ];

            RulesetDocumentGraph document = RulesetDocumentGraph.Build(rulesets, OpenDocument()?.Id);
            CanvasModel canvas = BuildCanvas(document);
            _nodeBindings = canvas.Bindings;
            _nodePositions = canvas.Positions;

            GraphNodeCount = canvas.Skeleton.Nodes.Count;
            int projected = document.Nodes.Count(n => !n.IsAuthorable);
            GraphSummary = $"open ruleset: {canvas.Skeleton.Nodes.Count} node(s), "
                           + $"{canvas.Skeleton.Edges.Count} value reference(s)"
                           + (projected > 0 ? $", {projected} from use:" : "")
                           + (demo is not null ? " (with demo)." : ".");
            if (GraphSupported)
            {
                _ = RenderNodeGraphAsync(canvas.Skeleton);
            }
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "render the rule graph", ex);
            GraphNodeCount = 0;
            GraphSummary = UserFacingError.Describe("render the rule graph", ex);
        }
    }

    /// <summary>
    ///     Projects the document graph onto the view models MSAGL lays out, plus the two lookups the
    ///     canvas joins back through.
    ///     <para>
    ///         <b>A node's title is unique across the composed set.</b> Everything that has to survive a
    ///         render keys on it: the editing binding, the source position, the selection restore. An id
    ///         is unique only inside one ruleset (the corpus declares <c>enemy_kills_round</c> twice), so
    ///         a node the open document does not declare is titled <c>ruleset.id</c>, which is also what
    ///         an author would have to write to read it and is the read-only marking's first signal.
    ///     </para>
    /// </summary>
    private static CanvasModel BuildCanvas(RulesetDocumentGraph document)
    {
        Dictionary<RulesetDocumentNodeKey, GraphNodeViewModel> byKey = [];
        Dictionary<string, RulesetNodeBinding> bindings = new(StringComparer.Ordinal);
        Dictionary<string, SourcePosition> positions = new(StringComparer.Ordinal);
        List<IGraphNode> nodes = new(document.Nodes.Count);

        foreach (RulesetDocumentNode node in document.Nodes)
        {
            string title = node.IsAuthorable ? node.Id : node.Key.ToString();
            GraphNodeViewModel vm = new(title, false, SubtitleOf(node, title))
            {
                // The trigger's wire events, which the canvas draws as header chips rather than as
                // wires. Promoting them to edges is what produced AuthoringGraph's 21-way hubs.
                DisplayValue = string.Join(' ', node.Events),
                TrackedIndex = -1
            };

            byKey[node.Key] = vm;
            nodes.Add(vm);
            positions[title] = node.Position;
            if (node.IsAuthorable)
            {
                bindings[title] = new RulesetNodeBinding(
                    node.IsHighlight ? RulesetNodeKind.Highlight : RulesetNodeKind.Stat, node.Id);
            }
        }

        // Parallel reads between the same pair (a stat and its `<id>.count` pseudo-member, say) are
        // one wire carrying both labels. Drawn separately they are two lines between the same two box
        // edges, which is one line with a doubled stroke and two labels fighting over it.
        Dictionary<(RulesetDocumentNodeKey, RulesetDocumentNodeKey), List<string>> merged = [];
        foreach (RulesetDocumentEdge edge in document.Edges)
        {
            if (!byKey.ContainsKey(edge.Source) || !byKey.ContainsKey(edge.Target))
            {
                continue;
            }

            if (!merged.TryGetValue((edge.Source, edge.Target), out List<string>? labels))
            {
                labels = [];
                merged[(edge.Source, edge.Target)] = labels;
            }

            if (!labels.Contains(edge.Reference, StringComparer.Ordinal))
            {
                labels.Add(edge.Reference);
            }
        }

        List<IGraphEdge> edges = new(merged.Count);
        foreach (((RulesetDocumentNodeKey source, RulesetDocumentNodeKey target), List<string> labels) in merged)
        {
            edges.Add(new GraphEdgeViewModel(
                byKey[source], byKey[target], string.Join(", ", labels), EdgeEffect.SetValue));
        }

        return new CanvasModel(new RuleGraphSkeleton.Skeleton(nodes, edges, null), bindings, positions);
    }

    /// <summary>
    ///     The node's second line: what it is, what type it produces, and anything about it the canvas
    ///     would otherwise lose without trace.
    /// </summary>
    private static string SubtitleOf(RulesetDocumentNode node, string title)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(node.Label) && !string.Equals(node.Label, title, StringComparison.Ordinal))
        {
            parts.Add(node.Label!);
        }

        parts.Add(node.Kind.ToString().ToLowerInvariant());
        parts.Add(node.ValueType.Kind.ToString().ToLowerInvariant());

        if (!node.IsAuthorable)
        {
            parts.Add("declared in " + node.Key.Ruleset);
        }

        // A read that leaves the ruleset resolves to a node in another document, so it is not an edge
        // and there is nowhere else on the canvas for it to appear.
        if (node.CrossRulesetReads.Count > 0)
        {
            parts.Add("reads " + string.Join(", ", node.CrossRulesetReads));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    ///     Lays the skeleton out and reconciles the collections the node renderer binds to.
    ///     <para>
    ///         The layout runs on a background thread inside <c>SetGraphAsync</c>, so the collections
    ///         are only touched after awaiting it, back on the UI thread. Refilling them from the
    ///         layout thread is the class of bug that turned an off-thread theme read into four
    ///         unrelated-looking test failures during the graph fix (design.md §3.2).
    ///     </para>
    /// </summary>
    private async Task RenderNodeGraphAsync(RuleGraphSkeleton.Skeleton skeleton)
    {
        await GraphViewModel.SetGraphAsync(skeleton.Nodes, skeleton.Edges, skeleton.Groups);

        (IReadOnlyList<RulesetGraphNode> nodes, IReadOnlyList<RulesetGraphConnection> connections) =
            RulesetGraphProjection.Project(GraphViewModel, skeleton.Nodes, skeleton.Edges, _nodeBindings);

        string? selected = SelectedRulesetNode?.Title;
        ReconcileNodes(nodes);
        ReconcileConnections(connections);
        RestoreSelection(selected);
    }

    /// <summary>
    ///     Brings <see cref="RulesetNodes" /> to <paramref name="next" /> by title, replacing only what
    ///     actually differs.
    ///     <para>
    ///         <b>An unchanged node keeps its instance, and therefore its realized container.</b> That is
    ///         the whole point: a <c>Clear()</c> and refill costs 2.5 to 3.2 ms a node to re-realize, and
    ///         an edit that does not move the layout changes nothing the canvas draws. A moved node is
    ///         still replaced, because a position is baked into the record and there is no setter for it.
    ///     </para>
    /// </summary>
    private void ReconcileNodes(IReadOnlyList<RulesetGraphNode> next)
    {
        for (int i = 0; i < next.Count; i++)
        {
            RulesetGraphNode want = next[i];
            if (i < RulesetNodes.Count && Titled(RulesetNodes[i], want.Title))
            {
                if (!RulesetNodes[i].Equals(want))
                {
                    RulesetNodes[i] = want;
                }

                continue;
            }

            int found = IndexOfTitle(want.Title, i);
            if (found < 0)
            {
                RulesetNodes.Insert(i, want);
                continue;
            }

            RulesetNodes.Move(found, i);
            if (!RulesetNodes[i].Equals(want))
            {
                RulesetNodes[i] = want;
            }
        }

        while (RulesetNodes.Count > next.Count)
        {
            RulesetNodes.RemoveAt(RulesetNodes.Count - 1);
        }
    }

    private static bool Titled(RulesetGraphNode node, string title) =>
        string.Equals(node.Title, title, StringComparison.Ordinal);

    private int IndexOfTitle(string title, int from)
    {
        for (int i = from; i < RulesetNodes.Count; i++)
        {
            if (Titled(RulesetNodes[i], title))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     The same walk for the connections, positionally: they have no identity of their own (a
    ///     <see cref="RulesetGraphConnection" /> is two points and a label), so equality is all there is
    ///     to compare, and it still spares the redraw when the layout did not move.
    /// </summary>
    private void ReconcileConnections(IReadOnlyList<RulesetGraphConnection> next)
    {
        for (int i = 0; i < next.Count; i++)
        {
            if (i < RulesetConnections.Count)
            {
                if (!RulesetConnections[i].Equals(next[i]))
                {
                    RulesetConnections[i] = next[i];
                }
            }
            else
            {
                RulesetConnections.Add(next[i]);
            }
        }

        while (RulesetConnections.Count > next.Count)
        {
            RulesetConnections.RemoveAt(RulesetConnections.Count - 1);
        }
    }

    /// <summary>
    ///     Re-selects the node that was selected before the render, by title.
    ///     <para>
    ///         Replacing an item the editor has selected makes it push <c>null</c> back through the
    ///         two-way <c>SelectedItem</c> binding, so without this a field edit deselects the node it
    ///         edited and empties the field panel mid-edit. The restore is flagged, so it does not read
    ///         as a click and move the caret.
    ///     </para>
    /// </summary>
    private void RestoreSelection(string? title)
    {
        if (title is null)
        {
            return;
        }

        // Re-point at the INSTANCE the collection now holds, not just at the title: an edited node
        // is a new record, and leaving the selection on the record it replaced would hand the field
        // panel a value the document no longer has.
        RulesetGraphNode? now = RulesetNodes.FirstOrDefault(n => Titled(n, title));
        if (ReferenceEquals(now, SelectedRulesetNode))
        {
            return;
        }

        _restoringSelection = true;
        try
        {
            SelectedRulesetNode = now;
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    /// <summary>The skeleton plus the two title-keyed lookups the canvas joins back through.</summary>
    private readonly record struct CanvasModel(
        RuleGraphSkeleton.Skeleton Skeleton,
        IReadOnlyDictionary<string, RulesetNodeBinding> Bindings,
        IReadOnlyDictionary<string, SourcePosition> Positions);

    /// <summary>The open file's doc + its transitive <c>use:</c> dependencies (by ruleset id) from the loaded set.</summary>
    private List<RulesetDoc> LoadOpenFileWithDeps()
    {
        Dictionary<string, RulesetDoc> byPath = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RulesetDoc> byId = new(StringComparer.Ordinal);
        foreach ((string path, string text) in EnumerateSources())
        {
            if (RulesetDocumentLoader.Load(text, path).Doc is { } doc)
            {
                byPath[path] = doc;
                byId[doc.Id] = doc;
            }
        }

        string? openPath = OpenFilePath();
        if (openPath is null || !byPath.TryGetValue(openPath, out RulesetDoc? open))
        {
            return byPath.Values.ToList(); // nothing open → fall back to everything
        }

        List<RulesetDoc> docs = [open];
        HashSet<string> seen = new(StringComparer.Ordinal)
        {
            open.Id
        };
        Queue<string> pending = new(open.Use);
        while (pending.Count > 0)
        {
            string id = pending.Dequeue();
            if (seen.Add(id) && byId.TryGetValue(id, out RulesetDoc? dep))
            {
                docs.Add(dep);
                foreach (string next in dep.Use)
                {
                    pending.Enqueue(next);
                }
            }
        }

        return docs;
    }

    /// <summary>Rebuilds the Advanced-Evaluate file list (shipped + user), preserving prior selections.</summary>
    private void RefreshEvaluableFiles()
    {
        HashSet<string> wasSelected = EvaluableFiles.Where(f => f.IsSelected)
            .Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        EvaluableFiles.Clear();
        foreach (string? dir in new[]
                 {
                     _shippedDir, _userDir
                 })
        {
            if (dir is null || !Directory.Exists(dir))
            {
                continue;
            }

            bool shipped = string.Equals(dir, _shippedDir, StringComparison.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(dir, "*.rules.yaml"))
            {
                string name = Path.GetFileName(path);
                EvaluableFiles.Add(new EvaluableFile
                {
                    FullPath = path,
                    Display = shipped ? $"{name} (shipped)" : name,
                    IsShipped = shipped,
                    IsSelected = wasSelected.Count == 0 || wasSelected.Contains(path)
                });
            }
        }
    }

    /// <summary>
    ///     Captures the applied-fire trace for the just-completed evaluation: rebuilds the picker of
    ///     declared stats/highlights and auto-selects the first that fired. The trace reads the same
    ///     <see cref="EvaluationResult" /> the results board projects from, no second evaluation.
    /// </summary>
    private void ApplyTrace(WorkbenchTraceReport report)
    {
        _traceReport = report;
        TraceTargets.Clear();
        foreach (WorkbenchTraceTarget target in _traceReport.Targets)
        {
            TraceTargets.Add(target);
        }

        // Prefer a target that actually fired so the panel opens on something non-empty.
        SelectedTraceTarget = TraceTargets.FirstOrDefault(t => t.FireCount > 0)
                              ?? (TraceTargets.Count > 0 ? TraceTargets[0] : null);
        if (TraceTargets.Count == 0)
        {
            TraceSummary = "No stats or highlights declared to trace.";
        }
    }

    private void ClearTrace()
    {
        _traceReport = WorkbenchTraceReport.Empty;
        TraceTargets.Clear();
        TraceFires.Clear();
        SelectedTraceTarget = null;
        TraceSummary = "Evaluate, then pick a stat or highlight to see when it fired.";
    }

    /// <summary>Repopulates the fires list for the picked target.</summary>
    partial void OnSelectedTraceTargetChanged(WorkbenchTraceTarget? value)
    {
        TraceFires.Clear();
        if (value is null)
        {
            return;
        }

        foreach (WorkbenchTraceFire fire in _traceReport.FiresFor(value.Id))
        {
            TraceFires.Add(fire);
        }

        TraceSummary = value.FireCount == 0
            ? $"'{value.Label}' never fired on this demo."
            : $"'{value.Label}' fired {value.FireCount} time(s) — tick / round / player below.";
    }

    /// <summary>
    ///     A readable row label: the player name first, then any non-structural dimensions (weapon, site,
    ///     round_number …) joined, so a per-player scoreboard shows the name and a keyed table shows
    ///     "name · weapon". Falls back to the slot.
    /// </summary>
    private static string RowLabel(MetricRow row)
    {
        List<string> parts = [];
        if (row.Dimensions.TryGetValue("player_name", out object? name) && name is { } n
                                                                        && !string.IsNullOrEmpty(n.ToString()))
        {
            parts.Add(n.ToString()!);
        }

        foreach (KeyValuePair<string, object?> dim in row.Dimensions)
        {
            if (dim.Key is not ("map" or "match_id" or "player_name" or "player_slot" or "team")
                && dim.Value is { } v && !string.IsNullOrEmpty(v.ToString()))
            {
                parts.Add(v.ToString()!);
            }
        }

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : $"slot {row.Dimensions.GetValueOrDefault("player_slot")}";
    }

    private static string Fmt(object? value) =>
        value switch
        {
            null => "0",
            bool b => b ? "1" : "0",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "0"
        };

    private void OnAdvanced(IPlaybackSnapshot snapshot) => RefreshLivePlayers();
    private void OnDemoReset() => RefreshLivePlayers();

    private void RefreshLivePlayers()
    {
        LivePlayers.Clear();
        if (_context is null)
        {
            return;
        }

        Dictionary<int, string> names = new();
        foreach (PlayerRosterEntry entry in _context.Players)
        {
            names[entry.Slot] = entry.Name;
        }

        foreach (IPlayerState ps in _context.CurrentPlayers)
        {
            if (!ps.HasLivePawn)
            {
                continue;
            }

            string name = names.TryGetValue(ps.Slot, out string? n) ? n : $"slot {ps.Slot}";
            string pos = ps.WorldPosition is { } wp
                ? $"({wp.X:F0}, {wp.Y:F0}, {wp.Z:F0})"
                : "—";
            LivePlayers.Add(new LivePlayerRow(name, ps.Team, pos));
        }
    }

    private static List<WorkbenchPath> BuildPaths()
    {
        try
        {
            CatalogRoot catalog = CatalogResource.Load();
            List<WorkbenchPath> paths = [];
            foreach (CatalogContextRule ctx in catalog.Contexts)
            {
                if (ctx.V2Name is { } v)
                {
                    paths.Add(new WorkbenchPath(v, "context"));
                }
            }

            foreach (CatalogProvider provider in catalog.Providers)
            {
                if (provider.V2Name is { } v)
                {
                    paths.Add(new WorkbenchPath(v, "entity"));
                }
            }

            return paths
                .GroupBy(p => p.Path, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The View calls this to jump the caret to a diagnostic's position.</summary>
    public void RequestJump(WorkbenchDiagnostic diagnostic) =>
        JumpRequested?.Invoke(Math.Max(1, diagnostic.Line), Math.Max(1, diagnostic.Column));

    /// <summary>
    ///     The View calls this when the caret moves: selects the node whose entry the caret is inside.
    ///     <para>
    ///         <b>The entry that starts at or above the caret owns it.</b> A node carries the position its
    ///         <c>stats:</c> key starts at and not the span it covers, so the nearest key above the caret
    ///         is the enclosing one, and a caret in the document's prose or its <c>use:</c> block sits
    ///         above every key and selects nothing rather than selecting the first.
    ///     </para>
    /// </summary>
    /// <param name="line">The caret's 1-based line.</param>
    public void SelectNodeAtLine(int line)
    {
        if (_syncingCaret || RulesetNodes.Count == 0 || OpenFilePath() is not { } openPath)
        {
            return;
        }

        string? title = null;
        int nearest = 0;
        foreach ((string candidate, SourcePosition at) in _nodePositions)
        {
            if (at.Line <= nearest || at.Line > line
                || !string.Equals(at.File, openPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nearest = at.Line;
            title = candidate;
        }

        if (title is null || RulesetNodes.FirstOrDefault(n => Titled(n, title)) is not { } node
            || ReferenceEquals(node, SelectedRulesetNode))
        {
            return;
        }

        _syncingCaret = true;
        try
        {
            SelectedRulesetNode = node;
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    /// <summary>
    ///     Reveals the selected node's source, which is the other half of the caret sync.
    ///     <para>
    ///         It goes out through <see cref="JumpRequested" />, the event the diagnostics list already
    ///         uses, so a node and a diagnostic reveal a line the same way rather than through two
    ///         mechanisms that can drift apart. Guarded on both sides: a jump raised here must not come
    ///         back as a caret move that re-selects, and a selection the render merely restored is not a
    ///         click and moves nothing.
    ///     </para>
    /// </summary>
    private void RevealSelectedNode(RulesetGraphNode? node)
    {
        if (_syncingCaret || _restoringSelection || node is null
            || !_nodePositions.TryGetValue(node.Title, out SourcePosition at) || at.Line <= 0
            || !string.Equals(at.File, OpenFilePath(), StringComparison.OrdinalIgnoreCase))
        {
            // A node a `use:` dependency declares lives in another file, and the editor has this one
            // open, so there is no line here to reveal.
            return;
        }

        _syncingCaret = true;
        try
        {
            JumpRequested?.Invoke(Math.Max(1, at.Line), Math.Max(1, at.Column));
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    partial void OnSelectedFileChanged(RulesetFileRef? value)
    {
        OnPropertyChanged(nameof(IsReadOnlyFile));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSaveAs));
        SaveAsName = SuggestSaveAsName(value);
        LoadSelected();
        RenderGraphForOpenFile(); // graph follows the open ruleset
    }

    partial void OnDocumentTextChanged(string value)
    {
        if (_loadingDocument)
        {
            return;
        }

        IsDirty = true;
        OnPropertyChanged(nameof(CanSave));
        Check(); // buffer-aware, inline diagnostics track the edit
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    /// <summary>Creates a starter draft in the user folder and opens it.</summary>
    [RelayCommand]
    private void NewFile()
    {
        if (_userDir is null)
        {
            return;
        }

        string name = UniqueName("draft");
        string path = Path.Combine(_userDir, name);
        try
        {
            File.WriteAllText(path, StarterTemplate(Path.GetFileNameWithoutExtension(name)));
        }
        catch
        {
            return;
        }

        RefreshFiles();
        SelectedFile = FindOpenable(path); // triggers LoadSelected
    }

    /// <summary>Writes the editor buffer to the open file in place (only for editable files, see CanSave).</summary>
    [RelayCommand]
    private void Save()
    {
        if (SelectedFile is null || IsReadOnlyFile)
        {
            return; // a read-only shipped file must go through Save-As
        }

        WriteBuffer(SelectedFile.FullPath);
    }

    /// <summary>
    ///     Writes the buffer to a NEW user-overlay file named <see cref="SaveAsName" /> and opens it:
    ///     the "prompt for a new name" path (used for shipped files, or any explicit fork).
    /// </summary>
    [RelayCommand]
    private void SaveAs()
    {
        if (_userDir is null)
        {
            return;
        }

        string name = SaveAsName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        if (!name.EndsWith(".rules.yaml", StringComparison.OrdinalIgnoreCase))
        {
            name = (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name) + ".rules.yaml";
        }

        string path = Path.Combine(_userDir, name);
        WriteBuffer(path);
        RefreshFiles();
        SelectedFile = FindOpenable(path);
    }

    /// <summary>Stamps + writes the buffer to <paramref name="path" />, clears dirty, and re-checks.</summary>
    private void WriteBuffer(string path)
    {
        try
        {
            _suppressWatcherReload = true;
            string stamped = StampCatalogVersion(DocumentText); // provenance on save
            _loadingDocument = true; // stamping the buffer must not re-dirty it
            DocumentText = stamped;
            _loadingDocument = false;
            File.WriteAllText(path, stamped);
            IsDirty = false;
        }
        catch
        {
            // best-effort; the check summary still reflects the buffer
        }
        finally
        {
            _suppressWatcherReload = false;
        }

        Check();
    }

    /// <summary>Re-runs the demo-less check over the edited buffer + on-disk rulesets.</summary>
    [RelayCommand]
    private void RunCheck() => Check();

    private RulesetFileRef? FindOpenable(string fullPath) =>
        OpenableFiles.FirstOrDefault(f => string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    private static string SuggestSaveAsName(RulesetFileRef? file)
    {
        if (file is null)
        {
            return "";
        }

        string stem = file.FileName.EndsWith(".rules.yaml", StringComparison.OrdinalIgnoreCase)
            ? file.FileName[..^".rules.yaml".Length]
            : file.FileName;
        return file.IsShipped ? $"{stem}-copy" : stem;
    }

    private void LoadSelected()
    {
        if (SelectedFile is null)
        {
            return;
        }

        string path = SelectedFile.FullPath;
        try
        {
            _loadingDocument = true;
            DocumentText = File.Exists(path) ? File.ReadAllText(path) : "";
            IsDirty = false;
        }
        catch
        {
            DocumentText = "";
        }
        finally
        {
            _loadingDocument = false;
        }

        OnPropertyChanged(nameof(CanSave));
        Check();
    }

    /// <summary>
    ///     The unified check: builds the ruleset-doc set (shipped + user <c>*.rules.yaml</c>, the open
    ///     file taken from the editor buffer), collects mapping diagnostics, composes demo-less for the
    ///     resolve/cross-ruleset diagnostics (D11a), and splits out the open-file inline set. Exception-
    ///     safe, a filesystem-less host degrades to a summary line.
    /// </summary>
    private void Check()
    {
        Diagnostics.Clear();
        OpenFileDiagnostics.Clear();
        try
        {
            List<RulesetDoc> docs = [];
            List<RulesetDiagnostic> all = [];
            int fileCount = 0;

            foreach ((string path, string text) in EnumerateSources())
            {
                fileCount++;
                RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(text, path);
                all.AddRange(outcome.Diagnostics);
                if (outcome.Doc is { } doc)
                {
                    docs.Add(doc);
                }
            }

            CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
            all.AddRange(RulesetComposition.ComposeDraft(docs, adapter).Diagnostics);

            string? openPath = OpenFilePath();
            foreach (RulesetDiagnostic d in all)
            {
                WorkbenchDiagnostic row = WorkbenchDiagnostic.From(d);
                Diagnostics.Add(row);
                if (openPath is not null && string.Equals(row.File, openPath, StringComparison.OrdinalIgnoreCase))
                {
                    OpenFileDiagnostics.Add(row);
                }
            }

            IsClean = Diagnostics.Count == 0;
            Summary = IsClean
                ? $"✓ {fileCount} ruleset(s) — no problems."
                : $"{Diagnostics.Count} problem(s) across {fileCount} ruleset(s)"
                  + (OpenFileDiagnostics.Count > 0 ? $" ({OpenFileDiagnostics.Count} in this file)." : ".");

            ScheduleGraphRender(); // the canvas follows the edited buffer, one idle beat behind it
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "check the rulesets", ex);
            IsClean = false;
            Summary = UserFacingError.Describe("check the rulesets", ex);
        }
    }

    /// <summary>Yields (path, text) for every shipped + user ruleset; the open file comes from the buffer.</summary>
    private IEnumerable<(string Path, string Text)> EnumerateSources()
    {
        string? openPath = OpenFilePath();
        foreach (string? dir in new[]
                 {
                     _shippedDir, _userDir
                 })
        {
            if (dir is null || !Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(dir, "*.rules.yaml"))
            {
                if (openPath is not null && string.Equals(path, openPath, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (path, DocumentText); // unsaved edits
                }
                else
                {
                    string? text = SafeRead(path);
                    if (text is not null)
                    {
                        yield return (path, text);
                    }
                }
            }
        }
    }

    private string? OpenFilePath() => SelectedFile?.FullPath;

    /// <summary>Rebuilds the Authoring dropdown (shipped + user rulesets), preserving the open file by path.</summary>
    private void RefreshFiles()
    {
        string? previousPath = SelectedFile?.FullPath;
        OpenableFiles.Clear();
        foreach (string? dir in new[]
                 {
                     _shippedDir, _userDir
                 })
        {
            if (dir is null || !Directory.Exists(dir))
            {
                continue;
            }

            bool shipped = string.Equals(dir, _shippedDir, StringComparison.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(dir, "*.rules.yaml"))
            {
                string name = Path.GetFileName(path);
                OpenableFiles.Add(new RulesetFileRef(path, name, shipped ? $"{name}  (shipped)" : name, shipped));
            }
        }

        if (previousPath is not null)
        {
            SelectedFile = FindOpenable(previousPath);
        }

        RefreshEvaluableFiles();
    }

    private void StartWatcher()
    {
        if (_userDir is null || !Directory.Exists(_userDir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_userDir, "*.rules.yaml")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnUserDirChanged;
            _watcher.Created += OnUserDirChanged;
            _watcher.Deleted += OnUserDirChanged;
            _watcher.Renamed += OnUserDirChanged;
        }
        catch
        {
            _watcher = null; // watching is best-effort; the Re-check button always works
        }
    }

    private void OnUserDirChanged(object? sender, FileSystemEventArgs e)
    {
        if (_suppressWatcherReload)
        {
            return; // our own Save
        }

        // Marshal to the UI thread; reload the open file if it changed and the buffer is clean.
        Dispatcher.UIThread.Post(() =>
        {
            RefreshFiles();
            string? open = OpenFilePath();
            if (open is not null && string.Equals(e.FullPath, open, StringComparison.OrdinalIgnoreCase) && !IsDirty)
            {
                LoadSelected();
            }
            else
            {
                Check();
            }
        });
    }

    private string UniqueName(string stem)
    {
        string name = $"{stem}.rules.yaml";
        int n = 1;
        while (_userDir is not null && File.Exists(Path.Combine(_userDir, name)))
        {
            name = $"{stem}-{++n}.rules.yaml";
        }

        return name;
    }

    /// <summary>
    ///     Stamps the current catalog version onto the ruleset (provenance): replaces an existing
    ///     <c>catalog_version:</c> line, else inserts one after <c>ruleset:</c>. Returns the text
    ///     unchanged if the catalog can't load.
    /// </summary>
    private static string StampCatalogVersion(string text)
    {
        int version;
        try
        {
            version = CatalogResource.Load().CatalogVersion;
        }
        catch
        {
            return text;
        }

        if (_catalogVersionLine.IsMatch(text))
        {
            return _catalogVersionLine.Replace(text, $"catalog_version: {version}");
        }

        Match ruleset = _rulesetLine.Match(text);
        return ruleset.Success
            ? text.Insert(ruleset.Index + ruleset.Length, $"\ncatalog_version: {version}")
            : $"catalog_version: {version}\n{text}";
    }

    private static string StarterTemplate(string id) =>
        "# yaml-language-server: $schema=./cs2demokit-rules.schema.json\n"
        + "ruleset: " + id + "\n"
        + "for: each_player\n"
        + "stats:\n"
        + "  kills:\n"
        + "    count: kill\n"
        + "    per: match\n"
        + "show:\n"
        + "  scoreboard:\n"
        + "    - { stat: kills, label: Kills, group: game }\n";

    private static string? SafeShippedDir()
    {
        try
        {
            return RuleSetLocator.ResolveShippedRulesDirectory();
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeUserDir(string? shipped)
    {
        try
        {
            return shipped is null ? null : RuleSetLocator.EnsureUserRulesDirectory(shipped);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The pure result of an off-thread evaluation, applied to the UI collections after the await.</summary>
    private sealed record EvalOutcome(
        IReadOnlyList<WorkbenchScoreboard> Boards,
        WorkbenchTraceReport Trace,
        bool HadSnapshots);
}

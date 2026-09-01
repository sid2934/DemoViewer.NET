#region

using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>
///     ViewModel for the Stats tab: the user-facing scoreboard surface (release plan P1-3.1).
///     Projects each completed evaluation through the built-in <see cref="IOutputProjector" />s into
///     two tables: the end-of-match scoreboard (<see cref="PlayerGameStatsProjector" />) and the
///     per-round browser (<see cref="PlayerRoundStatsProjector" />). Columns follow whatever rules
///     are loaded. A user-authored chain's <c>columns:</c> appear here (and in the export) with no
///     app changes. The exported files are these same <see cref="MetricTable" />s, byte-identical to
///     <c>AnalysisBench --export</c> (one data path, two consumers).
/// </summary>
public sealed partial class StatsTabViewModel : ObservableObject, IDisposable
{
    // ══ 3D visibility ══════════════════════════════════════════════════════════
    // On-demand compute: NOT part of the evaluation, a Stats-tab action, visible
    // only when the loaded demo's map has a resolvable collision bake. Everything for it lives in this
    // delimited section (plus one-line hooks in the view-toggle members above) so parallel work on
    // the Highlights/table regions doesn't collide with it.

    private readonly Func<string?, string?> _collisionResolver;
    private readonly Func<string?> _demoPathAccessor;

    // Optional analyzer options seam (frame window / stride / FOV). null → analyzer defaults over
    // the whole demo (the product path). Tests inject a frame window so the LOS replay covers a
    // representative slice instead of the full match.
    private readonly VisibilityAnalyzer.Options? _visibilityOptions;

    private string? _collisionTrisPath;

    // Diagnostics-pillar logger (v0.6.0: failure surfaces show clean text, this carries the real
    // exception). Lazy: the ambient factory is wired after construction.
    private ILogger? _diagLog;

    // ── Extra tables: configured outputs (F2) + keyed breakdowns (F3) ─────────

    // Engine keys in catalogue display order, one list per table (their column sets differ:
    // the match table excludes round-scoped columns). The *visible* lists are the category-filtered
    // projections (Core anchor ∪ selected group) the headers/rows/totals are built from.
    private List<string> _gameColumnOrder = [];

    /// <summary>True once an evaluation produced at least one scoreboard row.</summary>
    [ObservableProperty]
    private bool _hasStats;

    /// <summary>True once a compute produced visibility rows (shows the view toggle).</summary>
    [ObservableProperty]
    private bool _hasVisibilityStats;

    /// <summary>True while a visibility replay is running (drives the busy indicator).</summary>
    [ObservableProperty]
    private bool _isComputingVisibility;

    /// <summary>True = the generic extra-table view is active.</summary>
    [ObservableProperty]
    private bool _isExtraTableView;

    /// <summary>True = the chain-satisfaction highlights log (achievements view).</summary>
    [ObservableProperty]
    private bool _isHighlightsView;

    // ── Player-details overlay ─────────────
    // Inline overlay over this tab (WASM-safe, no OS Window), built from the tables this VM
    // already holds. Opened from a scoreboard/round row (double-tap or context menu), closed by
    // Esc / Back, and force-closed by Update (lifecycle coupling).

    /// <summary>True while the player-details overlay covers the tab.</summary>
    [ObservableProperty]
    private bool _isPlayerDetailsOpen;

    /// <summary>False = match scoreboard, true = per-round browser (mutually exclusive with highlights).</summary>
    [ObservableProperty]
    private bool _isRoundView;

    /// <summary>True = the computed visibility table view (mutually exclusive with the others).</summary>
    [ObservableProperty]
    private bool _isVisibilityView;

    /// <summary>The open player's dashboard VM; null when the overlay is closed.</summary>
    [ObservableProperty]
    private PlayerDetailsViewModel? _playerDetails;

    private List<string> _roundColumnOrder = [];

    // ── Category sub-rail: one chip per StatGroup in the active view ──────────────────────────

    /// <summary>The selected category chip; visible columns = Core anchor ∪ this group.</summary>
    [ObservableProperty]
    private StatGroup _selectedCategory = StatGroup.Core;

    /// <summary>The extra table currently rendered by the generic table view.</summary>
    [ObservableProperty]
    private MetricTable? _selectedExtraTable;

    [ObservableProperty]
    private int _selectedRound;

    private bool _sortDescending;

    // Sort state for the match scoreboard: the sorted column's ENGINE KEY (null = player name).
    // Key-based, not index-based. Visible column indices re-base on every category-chip switch,
    // so a stored index would silently point at a different column.
    private string? _sortKey;

    /// <summary>Empty-state / progress message shown when there are no rows.</summary>
    [ObservableProperty]
    private string _statusMessage = "Load a demo to see match stats.";

    // Per-team derived round-win score, precomputed in Update from the FULL table (CTW/TW are
    // RoundWins-group columns. Reading them from visible cells would lose the score under every
    // other category chip). Key = TeamSort (0 = CT, 1 = T).
    private Dictionary<int, int?> _teamScoreBySort = [];

    // House superseded-run pattern (AnalysisViewModel.RunAsync): a new compute, or a new
    // evaluation, cancels the in-flight one; the stale run's result is discarded on arrival.
    // VisibilityAnalyzer.Analyze observes the token per replayed frame, so a superseded replay now
    // unwinds instead of running to completion off-thread with its result thrown away.
    private CancellationTokenSource? _visibilityCts;

    // Retained from the last Update: the compute call needs the frames + players + map name.
    private ParsedDemo? _visibilityDemo;

    private List<string> _visibleGameColumnOrder = [];
    private List<string> _visibleRoundColumnOrder = [];

    /// <param name="analysis">
    ///     The engine VM whose completed evaluations feed this tab; null for tests/doubles that call
    ///     <see cref="Update" /> directly.
    /// </param>
    /// <param name="demoPathAccessor">Reads the loaded demo's path (match_id dimension source).</param>
    /// <param name="collisionResolver">
    ///     Maps a map name to its baked <c>collision.tris</c> path, or null when the map has no bake
    ///     (gates the visibility compute action). Defaults to
    ///     <see cref="CollisionAssetLocator.FindCollisionTris" />; injectable for tests.
    /// </param>
    /// <param name="visibilityOptions">
    ///     Optional <see cref="VisibilityAnalyzer.Options" /> (frame window / stride / FOV) for the
    ///     visibility compute; null → analyzer defaults over the whole demo (the product path).
    ///     Tests inject a frame window so the LOS replay covers a representative slice.
    /// </param>
    public StatsTabViewModel(AnalysisViewModel? analysis, Func<string?> demoPathAccessor,
        Func<string?, string?>? collisionResolver = null,
        VisibilityAnalyzer.Options? visibilityOptions = null)
    {
        _demoPathAccessor = demoPathAccessor;
        _collisionResolver = collisionResolver ?? CollisionAssetLocator.FindCollisionTris;
        _visibilityOptions = visibilityOptions;
        if (analysis is not null)
        {
            analysis.EvaluationCompleted += UpdateFromRun;
        }
    }

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.Stats");

    /// <summary>Match-scoreboard column headers, in catalogue display order.</summary>
    public IReadOnlyList<StatColumn> Columns { get; private set; } = [];

    /// <summary>Round-browser column headers (a different set: includes round-scoped columns).</summary>
    public IReadOnlyList<StatColumn> RoundColumns { get; private set; } = [];

    /// <summary>The active view's column headers.</summary>
    public IReadOnlyList<StatColumn> CurrentColumns => IsRoundView ? RoundColumns : Columns;

    /// <summary>True when the scoreboard sorts by player name (drives the player header glyph).</summary>
    public bool IsSortedByPlayer => _sortKey is null;

    /// <summary>The chip rail items for the active view, Core ("Overview") first.</summary>
    public IReadOnlyList<CategoryChip> Categories { get; private set; } = [];

    /// <summary>Sort glyph for the player header column.</summary>
    public string PlayerSortGlyph => !IsSortedByPlayer ? "" : _sortDescending ? " ▼" : " ▲";

    /// <summary>CT/T scoreboard sections with per-team totals and derived score.</summary>
    public IReadOnlyList<TeamSection> TeamSections { get; private set; } = [];

    /// <summary>Match scoreboard rows (one per player), in current sort order.</summary>
    public IReadOnlyList<StatsRow> GameRows { get; private set; } = [];

    /// <summary>Rows of the selected round (one per player).</summary>
    public IReadOnlyList<StatsRow> RoundRows { get; private set; } = [];

    /// <summary>Live round numbers available in the round browser.</summary>
    public IReadOnlyList<int> Rounds { get; private set; } = [];

    /// <summary>True when the match scoreboard is the active view.</summary>
    public bool IsMatchView => !IsRoundView && !IsHighlightsView && !IsVisibilityView && !IsExtraTableView;

    /// <summary>True while either stat table (match / rounds) is showing.</summary>
    public bool IsTableView => !IsHighlightsView && !IsVisibilityView && !IsExtraTableView;

    /// <summary>Table visibility gate: a stat-table view is active AND there are stats to show.</summary>
    public bool IsTableVisible => IsTableView && HasStats;

    /// <summary>Chain-satisfaction highlights (one per rising edge), in tick order.</summary>
    public IReadOnlyList<HighlightRow> Highlights { get; private set; } = [];

    /// <summary>The rows of whichever stat table is active.</summary>
    public IReadOnlyList<StatsRow> CurrentRows => IsRoundView ? RoundRows : GameRows;

    /// <summary>
    ///     The tables backing this tab: what the export writes. The two visibility tables join
    ///     only after an on-demand compute; extra tables
    ///     (configured outputs, keyed breakdowns) are per-evaluation.
    /// </summary>
    public IReadOnlyList<MetricTable> ExportTables =>
        new[]
            {
                GameTable, RoundTable, EventsTable, VisibilityPlayersTable, VisibilityPairsTable
            }
            .Where(t => t is not null).Cast<MetricTable>()
            .Concat(ExtraTables)
            .ToList();

    /// <summary>The table picker's items: every non-built-in table this evaluation produced.</summary>
    public IReadOnlyList<MetricTable> ExtraTables { get; private set; } = [];

    /// <summary>True when the evaluation produced any extra table (shows the picker).</summary>
    public bool HasExtraTables => ExtraTables.Count > 0;

    /// <summary>The selected extra table's header: dimension columns then value columns.</summary>
    public IReadOnlyList<string> ExtraColumns =>
        SelectedExtraTable is { } t ? [.. t.DimensionColumns, .. t.ValueColumns] : [];

    /// <summary>The selected extra table's rows, cells aligned with <see cref="ExtraColumns" />.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ExtraRows
    {
        get
        {
            if (SelectedExtraTable is not { } t)
            {
                return [];
            }

            List<IReadOnlyList<string>> rows = new(t.Rows.Count);
            foreach (MetricRow row in t.Rows)
            {
                List<string> cells = new(t.DimensionColumns.Count + t.ValueColumns.Count);
                foreach (string c in t.DimensionColumns)
                {
                    cells.Add(new StatCell(row.Dimensions.GetValueOrDefault(c)).Display);
                }

                foreach (string c in t.ValueColumns)
                {
                    cells.Add(new StatCell(row.Values.GetValueOrDefault(c)).Display);
                }

                rows.Add(cells);
            }

            return rows;
        }
    }

    /// <summary>Players for the details header switcher (CT then T, then name).</summary>
    public IReadOnlyList<PlayerRef> DetailPlayers { get; private set; } = [];

    // Read-only table access for the details VM, same instances the export writes; never copies.
    internal MetricTable? GameTable { get; private set; }

    internal MetricTable? RoundTable { get; private set; }

    internal MetricTable? EventsTable { get; private set; }

    internal MetricTable? VisibilityPlayersTable { get; private set; }

    internal MetricTable? VisibilityPairsTable { get; private set; }

    /// <summary>
    ///     Derived per-team round wins keyed by team-sort (0 = CT, 1 = T); a null value means the wins
    ///     disagreed across that team's rows and no score could be trusted. Exposed so the Match Overview
    ///     landing page can show the final score without re-deriving it. A second derivation is a second
    ///     thing to drift, and two different scores for one match is worse than none.
    /// </summary>
    internal IReadOnlyDictionary<int, int?> TeamScoresBySort => _teamScoreBySort;

    /// <summary>
    ///     Gate for the "Compute visibility…" action: an evaluation is loaded AND the map's
    ///     collision bake resolves. Unbaked map → hidden (the status line explains why).
    /// </summary>
    public bool CanComputeVisibility => HasStats && _collisionTrisPath is not null;

    /// <summary>Per-player visibility rows (team-grouped), populated by the compute action.</summary>
    public IReadOnlyList<VisibilityRow> VisibilityRows { get; private set; } = [];

    /// <summary>Cancels and releases any in-flight visibility compute (house VM pattern).</summary>
    public void Dispose()
    {
        _visibilityCts?.Cancel();
        _visibilityCts?.Dispose();
        _visibilityCts = null;
    }

    /// <summary>
    ///     Demo-unload reset: cancels the visibility compute and drops every projected table plus the
    ///     retained <c>ParsedDemo</c> the visibility replay holds. Without this a standalone close leaves
    ///     the whole demo pinned through <c>_visibilityDemo</c>.
    /// </summary>
    public void ResetForDemoUnload()
    {
        Dispose();
        ClosePlayerDetails();

        _visibilityDemo = null;
        _collisionTrisPath = null;
        VisibilityPlayersTable = null;
        VisibilityPairsTable = null;
        VisibilityRows = [];
        HasVisibilityStats = false;

        GameTable = null;
        RoundTable = null;
        EventsTable = null;
        // The derived score is per-demo state like everything else here. It is only ever reassigned inside
        // Update(), so leaving it would carry the PREVIOUS demo's score across an unload into any consumer
        // that reads it before the next evaluation lands (the Match Overview landing page reads it the
        // moment the analysis stage completes, including when that run produced no table at all).
        _teamScoreBySort = [];
        SetExtraTables([]);
        GameRows = [];
        RoundRows = [];
        Highlights = [];
        Rounds = [];
        DetailPlayers = [];
        HasStats = false;
        StatusMessage = "No demo loaded.";

        OnPropertyChanged(nameof(GameRows));
        OnPropertyChanged(nameof(RoundRows));
        OnPropertyChanged(nameof(CurrentRows));
        OnPropertyChanged(nameof(Highlights));
        OnPropertyChanged(nameof(Rounds));
        OnPropertyChanged(nameof(DetailPlayers));
        OnPropertyChanged(nameof(VisibilityRows));
        OnPropertyChanged(nameof(CanComputeVisibility));
    }

    /// <summary>
    ///     Event-path update: the built-in tables plus every additional table the run carries,
    ///     configured <c>outputs:</c> declarations (F2) and keyed-counter breakdowns like
    ///     per-weapon stats (F3). Tests without a full <see cref="AnalysisRun" /> call
    ///     <see cref="Update(EvaluationResult, ParsedDemo, IReadOnlyList{MetricTable}?)" /> directly.
    /// </summary>
    public void UpdateFromRun(AnalysisRun run, ParsedDemo demo)
    {
        string? matchId = _demoPathAccessor() is { } path ? Path.GetFileName(path) : null;
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("Stats tab requires snapshot-mode evaluation.");

        List<MetricTable> extras = new();
        extras.AddRange(run.ProjectConfiguredOutputs(demo, matchId));
        extras.AddRange(new KeyedStatsProjector
        {
            MatchId = matchId
        }.Project(result, demo));

        Update(result, demo, extras);
    }

    partial void OnSelectedCategoryChanged(StatGroup value)
    {
        RebuildCategories();
        RebuildGameRows();
        RebuildRoundRows();
    }

    /// <summary>Selects a category chip.</summary>
    [RelayCommand]
    private void SelectCategory(CategoryChip chip) => SelectedCategory = chip.Group;

    /// <summary>Distinct groups present in the active view's FULL column set, Core forced first.</summary>
    private void RebuildCategories()
    {
        List<string> fullOrder = IsRoundView ? _roundColumnOrder : _gameColumnOrder;
        List<CategoryChip> chips = fullOrder
            .Select(c => ColumnCatalogue.Resolve(c).Group)
            .Distinct()
            .OrderBy(g => g == StatGroup.Core ? -1 : (int)g)
            .Select(g => new CategoryChip(g, CategoryChip.LabelFor(g), g == SelectedCategory))
            .ToList();
        if (chips.Count == 0 || chips[0].Group != StatGroup.Core)
        {
            chips.Insert(0, new CategoryChip(StatGroup.Core, CategoryChip.LabelFor(StatGroup.Core),
                SelectedCategory == StatGroup.Core));
        }

        Categories = chips;
        OnPropertyChanged(nameof(Categories));
    }

    /// <summary>The category-filtered projection: Core anchor ∪ the selected group, catalogue order.</summary>
    private List<string> VisibleColumns(List<string> fullOrder) =>
        fullOrder
            .Where(c => ColumnCatalogue.Resolve(c).Group is var g && (g == StatGroup.Core || g == SelectedCategory))
            .ToList();

    /// <summary>Steps the round browser to the previous live round.</summary>
    [RelayCommand]
    private void PrevRound() => StepRound(-1);

    /// <summary>Steps the round browser to the next live round.</summary>
    [RelayCommand]
    private void NextRound() => StepRound(+1);

    private void StepRound(int delta)
    {
        int idx = Rounds.ToList().IndexOf(SelectedRound) + delta;
        if (idx >= 0 && idx < Rounds.Count)
        {
            SelectedRound = Rounds[idx];
        }
    }

    partial void OnSelectedRoundChanged(int value) => RebuildRoundRows();

    partial void OnIsRoundViewChanged(bool value)
    {
        if (value)
        {
            IsHighlightsView = false;
            IsVisibilityView = false;
            IsExtraTableView = false;
        }

        OnPropertyChanged(nameof(CurrentRows));
        OnPropertyChanged(nameof(CurrentColumns));
        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsTableVisible));
        OnPropertyChanged(nameof(IsMatchView));

        // The selected category persists across Match↔Rounds only when the target view has
        // that chip; otherwise land on Overview. Rebuild the rail either way (chip sets differ).
        List<string> targetOrder = value ? _roundColumnOrder : _gameColumnOrder;
        if (SelectedCategory != StatGroup.Core
            && !targetOrder.Any(c => ColumnCatalogue.Resolve(c).Group == SelectedCategory))
        {
            SelectedCategory = StatGroup.Core; // triggers the rebuilds via its changed handler
        }
        else
        {
            RebuildCategories();
            RebuildRoundRows();
        }
    }

    /// <summary>Returns to the match scoreboard from any other view.</summary>
    [RelayCommand]
    private void ShowMatchView()
    {
        IsRoundView = false;
        IsHighlightsView = false;
        IsVisibilityView = false;
        IsExtraTableView = false;
    }

    partial void OnIsHighlightsViewChanged(bool value)
    {
        if (value)
        {
            IsRoundView = false;
            IsVisibilityView = false;
            IsExtraTableView = false;
        }

        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsTableVisible));
        OnPropertyChanged(nameof(IsMatchView));
    }

    partial void OnHasStatsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTableVisible));
        OnPropertyChanged(nameof(CanComputeVisibility));
    }

    partial void OnSelectedExtraTableChanged(MetricTable? value)
    {
        if (value is not null)
        {
            IsExtraTableView = true;
        }

        OnPropertyChanged(nameof(ExtraColumns));
        OnPropertyChanged(nameof(ExtraRows));
    }

    partial void OnIsExtraTableViewChanged(bool value)
    {
        if (value)
        {
            IsRoundView = false;
            IsHighlightsView = false;
            IsVisibilityView = false;
        }

        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsTableVisible));
        OnPropertyChanged(nameof(IsMatchView));
    }

    private void SetExtraTables(IReadOnlyList<MetricTable> tables)
    {
        ExtraTables = tables;
        OnPropertyChanged(nameof(ExtraTables));
        OnPropertyChanged(nameof(HasExtraTables));
        // A fresh evaluation invalidates the previous selection (tables are new instances).
        SelectedExtraTable = null;
        IsExtraTableView = false;
    }

    // ── Update from a completed evaluation ───────────────────────────────────

    /// <summary>
    ///     Projects a completed evaluation into the scoreboard/round tables shown by this tab.
    ///     <paramref name="extraTables" /> (configured outputs, keyed breakdowns) feed the table
    ///     picker and the export set.
    /// </summary>
    public void Update(EvaluationResult result, ParsedDemo demo, IReadOnlyList<MetricTable>? extraTables = null)
    {
        // Lifecycle coupling: every table below is replaced with a new
        // instance, so an open details overlay would point at a stale slot. Close it FIRST.
        ClosePlayerDetails();

        string? matchId = _demoPathAccessor() is { } path ? Path.GetFileName(path) : null;

        ResetVisibilityForNewEvaluation(demo);
        SetExtraTables(extraTables ?? []);

        GameTable = new PlayerGameStatsProjector
        {
            MatchId = matchId
        }.Project(result, demo).Single();
        RoundTable = new PlayerRoundStatsProjector
        {
            MatchId = matchId
        }.Project(result, demo).Single();
        EventsTable = new RuleChainEventProjector
        {
            MatchId = matchId
        }.Project(result, demo).Single();

        Highlights = EventsTable.Rows
            .Select(r => new HighlightRow(
                Convert.ToInt32(r.Dimensions.GetValueOrDefault("round_number") ?? 0, CultureInfo.InvariantCulture),
                Convert.ToInt32(r.Dimensions.GetValueOrDefault("tick") ?? 0, CultureInfo.InvariantCulture),
                r.Dimensions.GetValueOrDefault("chain")?.ToString() ?? "?",
                r.Dimensions.GetValueOrDefault("player_name")?.ToString() ?? ""))
            .OrderBy(h => h.Tick)
            .ToList();
        OnPropertyChanged(nameof(Highlights));

        // Column display order comes from the catalogue (canonical analyst order: Core first,
        // then grouped detail), independent of YAML union order. Rows are built in the SAME
        // (visible) order, so StatColumn.Index keeps indexing Cells directly. Match and Rounds
        // have different column sets after the round-scope fix, so each view owns its ordered list.
        _gameColumnOrder = OrderColumns(GameTable.ValueColumns);
        _roundColumnOrder = OrderColumns(RoundTable.ValueColumns);

        // Player list for the details header switcher (CT first, then T, then name).
        DetailPlayers = BuildDetailPlayers(GameTable);
        OnPropertyChanged(nameof(DetailPlayers));

        // Per-team derived score from the FULL table (CTW/TW live in the RoundWins group and are
        // not visible under most category chips. The section header must not lose its score).
        _teamScoreBySort = ComputeTeamScores(GameTable);

        // Fresh evaluation → Overview chip + the scoreboard default sort. (Setting the property
        // triggers its rebuild handler; when already on Overview only the rail needs refreshing.)
        if (SelectedCategory != StatGroup.Core)
        {
            SelectedCategory = StatGroup.Core;
        }
        else
        {
            RebuildCategories();
        }

        (_sortKey, _sortDescending) = DefaultSort(_gameColumnOrder);
        RebuildGameRows();

        Rounds = RoundTable.Rows
            .Select(r => Convert.ToInt32(r.Dimensions["round_number"], CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToList();
        OnPropertyChanged(nameof(Rounds));
        SelectedRound = Rounds.Count > 0 ? Rounds[0] : 0;
        RebuildRoundRows();

        HasStats = GameRows.Count > 0;
        StatusMessage = !HasStats
            ? "Analysis produced no per-player stats for this demo."
            : CanComputeVisibility
                ? ""
                : $"No collision bake for {demo.MapName} — visibility stats unavailable.";
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    /// <summary>Sorts the scoreboard by a stat column; a second click flips the direction.</summary>
    [RelayCommand]
    private void SortByColumn(StatColumn column)
    {
        if (IsRoundView)
        {
            return; // the round browser keeps its fixed team/name order
        }

        if (string.Equals(_sortKey, column.Label, StringComparison.Ordinal))
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = column.Label;
            _sortDescending = true; // stats read best-first
        }

        RebuildGameRows();
    }

    /// <summary>Sorts the scoreboard by player name; a second click flips the direction.</summary>
    [RelayCommand]
    private void SortByPlayer()
    {
        if (_sortKey is null)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = null;
            _sortDescending = false;
        }

        RebuildGameRows();
    }

    /// <summary>
    ///     Catalogue display order: the Core anchor first REGARDLESS of raw catalogue position
    ///     (shared labels like Shots carry their game-section order into the round table. Without
    ///     the group-rank key they'd sort ahead of the round Core columns), then groups in
    ///     catalogue order, unknown columns last.
    /// </summary>
    private static List<string> OrderColumns(IReadOnlyList<string> valueColumns) =>
        valueColumns
            .Select((name, i) => (Name: name, Meta: ColumnCatalogue.Resolve(name), Original: i))
            .OrderBy(c => c.Meta.Group == StatGroup.Core ? 0 : 1)
            .ThenBy(c => c.Meta.Order)
            .ThenBy(c => c.Original)
            .Select(c => c.Name)
            .ToList();

    /// <summary>
    ///     Builds the header list for one visible column set, stamping the sort indicator on the
    ///     column whose engine key is the active sort key. (Group-band labels are suppressed. The
    ///     category chip already names the group.)
    /// </summary>
    private static List<StatColumn> BuildStatColumns(List<string> order, string? sortKey, bool descending)
    {
        List<StatColumn> columns = new(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            ColumnMeta meta = ColumnCatalogue.Resolve(order[i]);
            columns.Add(new StatColumn(order[i], i, meta,
                string.Equals(order[i], sortKey, StringComparison.Ordinal),
                descending));
        }

        return columns;
    }

    private void RebuildGameRows()
    {
        if (GameTable is null)
        {
            return;
        }

        _visibleGameColumnOrder = VisibleColumns(_gameColumnOrder);

        // Key-based sort with survival: keep the key if it's still visible under this category,
        // else fall back to the kills column (a Core column, visible everywhere) or player name.
        int sortIdx = _sortKey is null ? -1 : _visibleGameColumnOrder.IndexOf(_sortKey);
        if (_sortKey is not null && sortIdx < 0)
        {
            (_sortKey, _sortDescending) = DefaultSort(_visibleGameColumnOrder);
            sortIdx = _sortKey is null ? -1 : _visibleGameColumnOrder.IndexOf(_sortKey);
        }

        Columns = BuildStatColumns(_visibleGameColumnOrder, _sortKey, _sortDescending);
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(CurrentColumns));
        OnPropertyChanged(nameof(IsSortedByPlayer));
        OnPropertyChanged(nameof(PlayerSortGlyph));

        List<StatsRow> rows = GameTable.Rows
            .Select(r => BuildRow(r, _visibleGameColumnOrder))
            .ToList();

        rows.Sort((a, b) =>
        {
            int cmp = sortIdx < 0
                ? StringComparer.OrdinalIgnoreCase.Compare(a.PlayerName, b.PlayerName)
                : CompareCellValues(a.Cells[sortIdx].Raw, b.Cells[sortIdx].Raw);
            return _sortDescending ? -cmp : cmp;
        });

        GameRows = rows;
        TeamSections = BuildTeamSections(rows, _visibleGameColumnOrder, _teamScoreBySort);
        OnPropertyChanged(nameof(GameRows));
        OnPropertyChanged(nameof(TeamSections));
        OnPropertyChanged(nameof(CurrentRows));
    }

    /// <summary>The scoreboard convention: kills descending when a kills column exists, else name.</summary>
    private static (string? Key, bool Descending) DefaultSort(List<string> visibleOrder)
    {
        string? kills = visibleOrder.FirstOrDefault(c => string.Equals(c, "Kills", StringComparison.OrdinalIgnoreCase))
                        ?? visibleOrder.FirstOrDefault(c => string.Equals(c, "TotalK", StringComparison.OrdinalIgnoreCase));
        return (kills, kills is not null);
    }

    /// <summary>
    ///     Groups sorted rows into CT/T sections with a totals/average row each and the
    ///     precomputed round-win score (derived from CTW+TW over the FULL table so it survives
    ///     every category chip; shown only when every team member agreed. A missing score beats a
    ///     wrong one).
    /// </summary>
    private static List<TeamSection> BuildTeamSections(
        List<StatsRow> sortedRows, List<string> order, Dictionary<int, int?> scoreBySort)
    {
        List<TeamSection> sections = new(2);
        foreach (IGrouping<int, StatsRow> group in sortedRows.GroupBy(r => r.TeamSort).OrderBy(g => g.Key))
        {
            List<StatsRow> members = group
                .Select((r, i) => r with
                {
                    IsAlt = i % 2 == 1
                })
                .ToList();
            bool isCt = group.Key == 0;
            string side = members[0].TeamLabel is { Length: > 0 } label ? label : "—";
            int? score = scoreBySort.GetValueOrDefault(group.Key);

            sections.Add(new TeamSection(side, isCt, score, members, BuildTotalsRow(members, order)));
        }

        return sections;
    }

    /// <summary>Per-team round-win score from the full game table's CTW+TW values (unanimous or null).</summary>
    private static Dictionary<int, int?> ComputeTeamScores(MetricTable gameTable)
    {
        Dictionary<int, List<int>> winsByTeamSort = new();
        foreach (MetricRow row in gameTable.Rows)
        {
            if (row.Values.GetValueOrDefault("CTW") is not { } ctw
                || row.Values.GetValueOrDefault("TW") is not { } tw)
            {
                continue;
            }

            int team = row.Dimensions.GetValueOrDefault("team") is { } t
                ? Convert.ToInt32(t, CultureInfo.InvariantCulture)
                : 0;
            int teamSort = team switch { 3 => 0, 2 => 1, _ => 2 };
            (winsByTeamSort.TryGetValue(teamSort, out List<int>? list)
                ? list
                : winsByTeamSort[teamSort] = []).Add(
                Convert.ToInt32(ctw, CultureInfo.InvariantCulture) + Convert.ToInt32(tw, CultureInfo.InvariantCulture));
        }

        Dictionary<int, int?> scores = new();
        foreach ((int teamSort, List<int> wins) in winsByTeamSort)
        {
            scores[teamSort] = wins.Distinct().Count() == 1 ? wins[0] : null;
        }

        return scores;
    }

    /// <summary>Per-team totals row: counts sum, rates average, everything else blank.</summary>
    private static StatsRow BuildTotalsRow(List<StatsRow> members, List<string> order)
    {
        List<StatCell> cells = new(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            ColumnMeta meta = ColumnCatalogue.Resolve(order[i]);
            List<double> values = members
                .Select(r => r.Cells[i].Raw)
                .Where(v => v is int or long or double or float)
                .Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture))
                .ToList();

            object? total = meta.Aggregate switch
            {
                ColumnAggregate.Sum when values.Count > 0 => values.Sum(),
                ColumnAggregate.Average when values.Count > 0 => Math.Round(values.Average(), 2),
                _ => null
            };

            cells.Add(new StatCell(total, meta));
        }

        return new StatsRow("team", 0, cells)
        {
            IsTotals = true
        };
    }

    // ── Export (P1-4.1) ───────────────────────────────────────────────────────

    /// <summary>
    ///     Writes every backing table to <paramref name="directoryPath" /> in the given format
    ///     (file per table, named by table id: the same output <c>AnalysisBench --export</c>
    ///     produces). Returns a user-facing result message.
    /// </summary>
    public string ExportTo(string directoryPath, string formatId)
    {
        IOutputFormatter? formatter = OutputFormatterRegistry.Get(formatId);
        if (formatter is null)
        {
            return $"Unknown export format '{formatId}'.";
        }

        IReadOnlyList<MetricTable> tables = ExportTables;
        if (tables.Count == 0)
        {
            return "Nothing to export — run an analysis first.";
        }

        try
        {
            foreach (MetricTable table in tables)
            {
                formatter.WriteToFile(table, Path.Combine(directoryPath, $"{table.Name}.{formatter.FileExtension}"));
            }

            return $"Exported {tables.Count} table(s) to {directoryPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.OperationFailed(DiagLog, "export the stats tables", ex);
            return UserFacingError.Describe("export the stats tables", ex);
        }
    }

    /// <summary>
    ///     Mixed-type-safe cell comparison: a column can legitimately hold boxed ints AND doubles
    ///     (the value coercion parses "0" as int and "88.5" as double), and <c>int.CompareTo(object)</c>
    ///     throws across types: numerics compare as doubles, same-type comparables directly, and
    ///     everything else by string. Nulls sort below every value.
    /// </summary>
    private static int CompareCellValues(object? a, object? b)
    {
        if (a is null)
        {
            return b is null ? 0 : -1;
        }

        if (b is null)
        {
            return 1;
        }

        if (a is int or long or double or float && b is int or long or double or float)
        {
            return Convert.ToDouble(a, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        }

        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            return comparable.CompareTo(b);
        }

        return string.CompareOrdinal(a.ToString(), b.ToString());
    }

    private void RebuildRoundRows()
    {
        if (RoundTable is null)
        {
            return;
        }

        _visibleRoundColumnOrder = VisibleColumns(_roundColumnOrder);
        RoundColumns = BuildStatColumns(_visibleRoundColumnOrder, null, false);
        OnPropertyChanged(nameof(RoundColumns));
        // The header binds CurrentColumns (a computed switch). It must re-notify whenever either
        // side rebuilds, or the Rounds header renders the PREVIOUS category's columns while the
        // rows are fresh (the header/cell misalignment bug).
        OnPropertyChanged(nameof(CurrentColumns));

        int round = SelectedRound;
        RoundRows = RoundTable.Rows
            .Where(r => Convert.ToInt32(r.Dimensions["round_number"], CultureInfo.InvariantCulture) == round)
            .Select(r => BuildRow(r, _visibleRoundColumnOrder))
            .OrderBy(r => r.TeamSort)
            .ThenBy(r => r.PlayerName, StringComparer.OrdinalIgnoreCase)
            .Select((r, i) => r with
            {
                IsAlt = i % 2 == 1
            })
            .ToList();
        OnPropertyChanged(nameof(RoundRows));
        OnPropertyChanged(nameof(CurrentRows));
    }

    private static StatsRow BuildRow(MetricRow row, List<string> orderedColumns)
    {
        string player = row.Dimensions.GetValueOrDefault("player_name")?.ToString() ?? "?";
        int team = row.Dimensions.GetValueOrDefault("team") is { } t
            ? Convert.ToInt32(t, CultureInfo.InvariantCulture)
            : 0;

        List<StatCell> cells = new(orderedColumns.Count);
        foreach (string column in orderedColumns)
        {
            cells.Add(new StatCell(row.Values.GetValueOrDefault(column), ColumnCatalogue.Resolve(column)));
        }

        return new StatsRow(player, team, cells)
        {
            PlayerSlot = RowSlot(row)
        };
    }

    /// <summary>
    ///     The <c>player_slot</c> dimension: the join key every stats table shares
    ///     (names collide, slots don't). <c>-1</c> when absent (totals/synthetic rows).
    /// </summary>
    internal static int RowSlot(MetricRow row) =>
        row.Dimensions.GetValueOrDefault("player_slot") is { } slot
            ? Convert.ToInt32(slot, CultureInfo.InvariantCulture)
            : -1;

    /// <summary>
    ///     Opens the details overlay for a row's player. Totals rows have no slot and are guarded
    ///     out. A fresh open always starts a new drill-down (section resets to
    ///     Overview).
    /// </summary>
    [RelayCommand]
    private void OpenPlayerDetails(StatsRow? row)
    {
        if (row is null || row.IsTotals || row.PlayerSlot < 0 || GameTable is null)
        {
            return;
        }

        PlayerDetails?.Detach();
        PlayerDetails = new PlayerDetailsViewModel(this, row.PlayerSlot);
        IsPlayerDetailsOpen = true;
    }

    /// <summary>Closes the details overlay and releases its VM.</summary>
    [RelayCommand]
    private void ClosePlayerDetails()
    {
        IsPlayerDetailsOpen = false;
        PlayerDetails?.Detach();
        PlayerDetails = null;
    }

    private static List<PlayerRef> BuildDetailPlayers(MetricTable gameTable) =>
        gameTable.Rows
            .Select(r => new PlayerRef(
                RowSlot(r),
                r.Dimensions.GetValueOrDefault("player_name")?.ToString() ?? "?",
                r.Dimensions.GetValueOrDefault("team") is { } t
                    ? Convert.ToInt32(t, CultureInfo.InvariantCulture)
                    : 0))
            .Where(p => p.Slot >= 0)
            .OrderBy(p => p.TeamSort)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    partial void OnIsVisibilityViewChanged(bool value)
    {
        if (value)
        {
            IsRoundView = false;
            IsHighlightsView = false;
            IsExtraTableView = false;
        }

        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsTableVisible));
        OnPropertyChanged(nameof(IsMatchView));
    }

    /// <summary>Called at the top of <see cref="Update" />: new evaluation → visibility resets.</summary>
    private void ResetVisibilityForNewEvaluation(ParsedDemo demo)
    {
        _visibilityCts?.Cancel();
        _visibilityCts?.Dispose();
        _visibilityCts = null;

        _visibilityDemo = demo;
        _collisionTrisPath = _collisionResolver(demo.MapName);
        VisibilityPlayersTable = null;
        VisibilityPairsTable = null;
        VisibilityRows = [];
        HasVisibilityStats = false;
        IsVisibilityView = false;
        IsComputingVisibility = false;
        OnPropertyChanged(nameof(VisibilityRows));
        OnPropertyChanged(nameof(CanComputeVisibility));
    }

    /// <summary>
    ///     Runs the 3D line-of-sight replay (<see cref="VisibilityAnalyzer.Analyze" />) off-thread over
    ///     the loaded demo, projects the report into the two visibility tables, and switches to the
    ///     Visibility view. One-time per demo (~seconds); re-clicking recomputes.
    /// </summary>
    [RelayCommand]
    private async Task ComputeVisibilityAsync()
    {
        if (_visibilityDemo is not { } demo || _collisionTrisPath is not { } trisPath)
        {
            return;
        }

        _visibilityCts?.Cancel();
        _visibilityCts?.Dispose();
        _visibilityCts = new CancellationTokenSource();
        CancellationToken token = _visibilityCts.Token;

        IsComputingVisibility = true;
        StatusMessage = "Computing visibility…";
        string? matchId = _demoPathAccessor() is { } path ? Path.GetFileName(path) : null;

        try
        {
            IReadOnlyList<MetricTable> tables = await Task.Run(() =>
            {
                VisibilityEngine engine = VisibilityEngine.Load(trisPath);
                token.ThrowIfCancellationRequested();
                // Bundles are selected by map NAME, so a report says nothing about WHICH bake it
                // raycast unless the manifest sitting next to the just-loaded blob is attached
                // (VIS-4). Absent for a flat <dir>/<map>.tris override, hence optional.
                VisibilityAnalyzer.Options options = (_visibilityOptions ?? new VisibilityAnalyzer.Options())
                    with
                    {
                        Bundle = MapAssetBundleReader.TryReadIdentity(Path.GetDirectoryName(trisPath))
                    };
                VisibilityAnalyzer.Report report =
                    VisibilityAnalyzer.Analyze(demo.Frames, engine, PositionUtil.CellToWorld,
                        options, token);
                token.ThrowIfCancellationRequested();
                return new VisibilityStatsProjector
                {
                    MatchId = matchId
                }.Project(report, demo);
            }, token);

            if (token.IsCancellationRequested)
            {
                return; // superseded: the newer run owns the UI state
            }

            VisibilityPlayersTable = tables[0];
            VisibilityPairsTable = tables[1];
            VisibilityRows = BuildVisibilityRows(tables[0]);
            OnPropertyChanged(nameof(VisibilityRows));
            HasVisibilityStats = VisibilityRows.Count > 0;
            StatusMessage = HasVisibilityStats ? "" : "Visibility replay sampled no live enemy pairs.";
            if (HasVisibilityStats)
            {
                IsVisibilityView = true;
            }
        }
        catch (OperationCanceledException)
        {
            // superseded run unwinding: the replacement owns the UI state
        }
#pragma warning disable CA1031 // engine load/replay failure degrades to a status line, never a crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!token.IsCancellationRequested)
            {
                AppLog.OperationFailed(DiagLog, "compute visibility stats", ex);
                StatusMessage = UserFacingError.Describe("compute visibility stats", ex);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsComputingVisibility = false;
            }
        }
    }

    private static List<VisibilityRow> BuildVisibilityRows(MetricTable playersTable)
    {
        return playersTable.Rows
            .Select(r => new VisibilityRow(
                r.Dimensions.GetValueOrDefault("player_name")?.ToString() ?? "?",
                Convert.ToInt32(r.Dimensions.GetValueOrDefault("team") ?? 0, CultureInfo.InvariantCulture),
                AsDouble(r.Values.GetValueOrDefault("ExposedToEnemiesSec")),
                AsDouble(r.Values.GetValueOrDefault("CouldSeeEnemySec")),
                AsDouble(r.Values.GetValueOrDefault("ExposedShare")),
                AsDouble(r.Values.GetValueOrDefault("VisionShare"))))
            .OrderBy(r => r.TeamSort)
            .ThenBy(r => r.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static double AsDouble(object? v)
        {
            return v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
    }

    // ══ end 3D visibility ══════════════════════════════════════════════════════
}

/// <summary>
///     One stat column header. <see cref="Label" /> is the engine key (export/golden identity);
///     <see cref="Index" /> indexes every row's <see cref="StatsRow.Cells" /> (rows and columns are
///     built in the same catalogue display order). <see cref="Meta" /> carries the view-only
///     presentation (friendly name, group, width, alignment) from <see cref="ColumnCatalogue" />.
/// </summary>
public sealed record StatColumn(string Label, int Index, ColumnMeta Meta, bool IsSorted = false, bool SortDescending = false)
{
    /// <summary>Friendly header text.</summary>
    public string Display => Meta.Display;

    /// <summary>Header sort glyph: empty when this column isn't the sort key.</summary>
    public string SortGlyph => !IsSorted ? "" : SortDescending ? " ▼" : " ▲";

    /// <summary>Header cell width (tiered per column, not uniform).</summary>
    public double Width => Meta.Width;

    /// <summary>Full stat description for the header tooltip.</summary>
    public string Tooltip => Meta.Tooltip;

    /// <summary>Group label shown when this column starts a new group band.</summary>
    public string GroupLabel { get; init; } = "";
}

/// <summary>
///     One chain-satisfaction highlight: which chain fired, in which round, at which tick, and by
///     whom (<paramref name="Player" /> is empty for game-scoped chains, which have no owning player).
/// </summary>
public sealed record HighlightRow(int Round, int Tick, string Chain, string Player)
{
    /// <summary>"round 7" / "warmup" locator text.</summary>
    public string RoundLabel => Round > 0
        ? $"round {Round}"
        : "warmup";
}

/// <summary>One category chip on the Stats sub-rail.</summary>
public sealed record CategoryChip(StatGroup Group, string Label, bool IsSelected)
{
    /// <summary>Short rail label per group; the chip is the group's display surface.</summary>
    public static string LabelFor(StatGroup group) => group switch
    {
        StatGroup.Core => "Overview",
        StatGroup.OpeningDuels => "Opening",
        StatGroup.SpecialKills => "Special",
        StatGroup.MultiKill => "Multi-Kill",
        StatGroup.RoundWins => "Round Wins",
        _ => group.ToString()
    };
}

/// <summary>One scoreboard/round row: player identity plus cells aligned with the column list.</summary>
public sealed record StatsRow(string PlayerName, int Team, IReadOnlyList<StatCell> Cells)
{
    /// <summary>
    ///     The player's <c>player_slot</c>: the join key into every other stats table (player-details
    ///     design P0-1, the linchpin). <c>-1</c> sentinel on totals rows (guarded from opening details).
    /// </summary>
    public int PlayerSlot { get; init; } = -1;

    /// <summary>Team tag shown next to the player (CS2 wire values: 2 = T, 3 = CT).</summary>
    public string TeamLabel => Team switch
    {
        2 => "T",
        3 => "CT",
        _ => ""
    };

    /// <summary>CT before T before spectators, for grouped displays.</summary>
    public int TeamSort => Team switch
    {
        3 => 0,
        2 => 1,
        _ => 2
    };

    /// <summary>Zebra-stripe flag (odd row within its team section).</summary>
    public bool IsAlt { get; init; }

    /// <summary>True for the per-team totals/average row.</summary>
    public bool IsTotals { get; init; }

    /// <summary>Side-color hook for the row bullet (CT blue / T amber).</summary>
    public bool IsCt => Team == 3;

    /// <summary>Row bullet, hidden on totals rows.</summary>
    public string Bullet => IsTotals ? "" : "●";
}

/// <summary>
///     One entry in the player-details header switcher: slot (the join key), name, and team.
/// </summary>
public sealed record PlayerRef(int Slot, string Name, int Team)
{
    /// <summary>Team tag (CS2 wire values: 2 = T, 3 = CT).</summary>
    public string TeamLabel => Team switch
    {
        2 => "T",
        3 => "CT",
        _ => ""
    };

    /// <summary>CT before T before spectators.</summary>
    public int TeamSort => Team switch
    {
        3 => 0,
        2 => 1,
        _ => 2
    };

    /// <summary>Dropdown text: name plus side tag.</summary>
    public string Display => TeamLabel.Length > 0 ? $"{Name}  ·  {TeamLabel}" : Name;
}

/// <summary>
///     One team's scoreboard section: side header (badge + optional derived round-win score), the
///     team's player rows in current sort order, and a totals/average row.
/// </summary>
public sealed record TeamSection(
    string SideLabel,
    bool IsCt,
    int? Score,
    IReadOnlyList<StatsRow> Rows,
    StatsRow Totals)
{
    /// <summary>"CT — 13" or just "CT" when the score couldn't be derived reliably.</summary>
    public string Header => Score is { } s ? $"{SideLabel}   {s}" : SideLabel;
}

/// <summary>
///     One per-player 3D-visibility row: union seconds a player was
///     exposed to / could see any enemy, plus the shares of total sampled time.
/// </summary>
public sealed record VisibilityRow(
    string PlayerName,
    int Team,
    double ExposedSec,
    double CouldSeeSec,
    double ExposedShare,
    double VisionShare)
{
    /// <summary>Team tag (CS2 wire values: 2 = T, 3 = CT).</summary>
    public string TeamLabel => Team switch
    {
        2 => "T",
        3 => "CT",
        _ => ""
    };

    /// <summary>CT before T before spectators, for grouped displays.</summary>
    public int TeamSort => Team switch
    {
        3 => 0,
        2 => 1,
        _ => 2
    };

    /// <summary>Seconds exposed, compact ("312.4 s").</summary>
    public string ExposedDisplay => FormatSeconds(ExposedSec);

    /// <summary>Seconds could-see, compact.</summary>
    public string CouldSeeDisplay => FormatSeconds(CouldSeeSec);

    /// <summary>Exposed share of sampled time ("23.4 %").</summary>
    public string ExposedShareDisplay => FormatShare(ExposedShare);

    /// <summary>Vision share of sampled time.</summary>
    public string VisionShareDisplay => FormatShare(VisionShare);

    private static string FormatSeconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    private static string FormatShare(double share) => (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";
}

/// <summary>
///     One cell: the raw boxed value (for sorting) plus its display string and, when built for a
///     catalogued column, the presentation metadata (width, alignment, emphasis).
/// </summary>
public sealed record StatCell(object? Raw, ColumnMeta? Meta = null)
{
    /// <summary>Invariant, compact rendering (doubles to 2 decimals; null → empty).</summary>
    public string Display => Raw switch
    {
        null => "",
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        bool b => b ? "✓" : "",
        _ => Convert.ToString(Raw, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>Cell width, matching the column header's tiered width.</summary>
    public double Width => Meta?.Width ?? 92;

    /// <summary>Numeric cells right-align so magnitudes scan vertically.</summary>
    public TextAlignment Alignment =>
        Meta is { Numeric: true } ? TextAlignment.Right : TextAlignment.Left;

    /// <summary>Flat accent for intrinsically good columns (clutches, aces), style class hook.</summary>
    public bool IsPositive => Meta?.Emphasis == Emphasis.Positive && Raw is not (null or 0 or 0.0);

    /// <summary>Flat accent for intrinsically bad columns (team/self damage), style class hook.</summary>
    public bool IsNegative => Meta?.Emphasis == Emphasis.Negative && Raw is not (null or 0 or 0.0);
}

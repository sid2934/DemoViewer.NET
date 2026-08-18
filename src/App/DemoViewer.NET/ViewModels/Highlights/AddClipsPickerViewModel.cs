#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     The cross-demo <b>Add clips</b> picker — the
///     reason multi-demo reels still work with the card grid gone. A <b>flat, virtualized highlight-ROW
///     list</b> spanning every cached demo, because the unit of work here is a clip, not a demo; that also
///     retires the chunked <c>CardRow</c> machinery, which only ever existed because <c>WrapPanel</c> has no
///     virtualizing counterpart.
///     <para>
///         <b>Where the card grid's orphaned filters landed.</b> Free text, players (with counts), highlight types and
///         maps — the same <see cref="PlayerFilterItem" /> / <see cref="HighlightTypeFilterItem" /> /
///         <see cref="MapFilterItem" /> item types the card grid used, re-pointed at highlight rows. They
///         were <em>discovery</em> affordances over a library-wide corpus, which is exactly what this list
///         is; putting them over the staged tray instead would have been machinery without a job.
///     </para>
///     <para>
///         <b>The row set is SNAPSHOTTED at open.</b> A backfill raises <c>DemoCacheStore.Changed</c>
///         every few seconds; re-projecting under an open picker would reset scroll and wipe the user's
///         multi-select mid-assembly. A picker is transient by construction, so the honest fix is a snapshot
///         plus a footer note when a scan is running — not the <c>SameProjection</c> stale-guard the
///         always-on card grid needed. <b>Staged flags are the exception</b> and are pushed live
///         (<see cref="SyncStagedFlags" />), because the tray and this list must never disagree about what is
///         already in the reel.
///     </para>
/// </summary>
public sealed partial class AddClipsPickerViewModel : ObservableObject
{
    private readonly List<AddClipsRowViewModel> _allRows;
    private readonly Action _close;
    private readonly Action? _rescanAll;
    private readonly Action<IReadOnlyList<HighlightSelection>> _stage;
    private readonly Action<HighlightKey> _unstage;

    // Filter re-application is O(rows); the constructor builds three filter lists and each one raises
    // PropertyChanged per item, so a naive hook would run the whole filter pass a few hundred times before
    // the picker is even visible.
    private bool _suppressApply = true;

    /// <summary>Free-text needle matched against map, player, demo file name and highlight title.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>A transient note shown in the footer (e.g. after <c>Rescan all</c> re-queues the library).</summary>
    [ObservableProperty]
    private string _statusNote = "";

    /// <summary>
    ///     Builds the picker over a snapshot of the highlights cache.
    /// </summary>
    /// <param name="records">Cache records that CARRY highlights (the index filters the rest out).</param>
    /// <param name="libraryRowCount">How many demos the cache knows about at all — the coverage denominator.</param>
    /// <param name="isStaged">O(1) staged test against the live tray.</param>
    /// <param name="stage">Stages a batch into the tray (a single <c>[ + ]</c> passes a one-item list).</param>
    /// <param name="unstage">Un-stages one highlight (the <c>[ ✓ ]</c> toggle-off path).</param>
    /// <param name="close">Dismisses the picker (the overlay is owned by the tab, not by this VM).</param>
    /// <param name="leadInSeconds">Lead-in used for the per-row <c>~Ns</c> estimate (snapshot of the config pane).</param>
    /// <param name="leadOutSeconds">Lead-out used for the per-row estimate.</param>
    /// <param name="dontCrossRoundStart">Whether the estimate floors the lead-in at the round start.</param>
    /// <param name="rescanAll">Mirrored <c>⟳ Rescan all</c>; null hides it (browser host / tests).</param>
    /// <param name="scanQueueDepth">Demos still queued for a scan — drives the honest "more are coming" note.</param>
    public AddClipsPickerViewModel(
        IReadOnlyList<DemoCacheRecord> records,
        int libraryRowCount,
        Func<HighlightKey, bool> isStaged,
        Action<IReadOnlyList<HighlightSelection>> stage,
        Action<HighlightKey> unstage,
        Action close,
        double leadInSeconds = 15,
        double leadOutSeconds = 5,
        bool dontCrossRoundStart = false,
        Action? rescanAll = null,
        int scanQueueDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(isStaged);
        _stage = stage;
        _unstage = unstage;
        _close = close;
        _rescanAll = rescanAll;
        ScanQueueDepth = scanQueueDepth;

        LibraryRowCount = libraryRowCount;

        List<AddClipsRowViewModel> built = [];
        foreach (DemoCacheRecord record in records.OrderByDescending(r => r.ModifiedTicks))
        {
            // COVERAGE DEFINITION (a subtlety, resolved deliberately): a demo appears here when it
            // has EVENTS, not when its ScanState is Indexed. The measured library has 346/348 rows Pending
            // yet 267 events present — a re-queued row keeps its previous harvest — so counting Indexed rows
            // would print "0 analysed demos" above a list of hundreds of visible highlights. A page that
            // contradicts what it is showing is the exact defect the earlier capture review caught.
            if (record.Highlights.Count == 0)
            {
                continue;
            }

            DemosWithHighlights++;
            foreach (CachedHighlightEvent highlight in record.Highlights)
            {
                built.Add(new AddClipsRowViewModel(record, highlight,
                    leadInSeconds, leadOutSeconds, dontCrossRoundStart, OnRowStageRequested));
            }
        }

        // Discovery order is Score DESCENDING — the picker surfaces the best firings first, matching the reel's
        // own ordering (CachedHighlightEvent.Score, higher = cooler). OrderByDescending is stable, so ties keep
        // the recency order the enumeration above already established. (The user-curated TRAY is never sorted —
        // curation lives in HighlightsTabViewModel._order and is left alone by design.)
        _allRows = [.. built.OrderByDescending(r => r.Score)];
        TotalHighlights = _allRows.Count;

        BuildPlayerFilters();
        BuildTypeFilters();
        BuildMapFilters();
        BuildKindFilters();

        foreach (AddClipsRowViewModel row in _allRows)
        {
            row.IsStaged = isStaged(row.Key);
            row.PropertyChanged += OnRowPropertyChanged;
        }

        _suppressApply = false;
        ApplyFilter();
    }

    /// <summary>The filtered, virtualized highlight rows (the picker's whole content).</summary>
    public ObservableCollection<AddClipsRowViewModel> Rows { get; } = [];

    /// <summary>Player multi-select, keyed steamId64, with library-wide counts. None checked = all players.</summary>
    public ObservableCollection<PlayerFilterItem> PlayerFilters { get; } = [];

    /// <summary>Highlight-type multi-select, keyed <c>{rulesetId}.{highlightId}</c>. None checked = all types.</summary>
    public ObservableCollection<HighlightTypeFilterItem> TypeFilters { get; } = [];

    /// <summary>Map multi-select (the Library's <see cref="MapFilterItem" />). None checked = all maps.</summary>
    public ObservableCollection<MapFilterItem> MapFilters { get; } = [];

    /// <summary>Highlight-KIND multi-select (skill / funny / lowlight). None checked = all kinds.</summary>
    public ObservableCollection<HighlightKindFilterItem> KindFilters { get; } = [];

    /// <summary>How many highlights exist across every demo with a usable harvest.</summary>
    public int TotalHighlights { get; }

    /// <summary>How many demos contribute at least one highlight (the coverage denominator that matters).</summary>
    public int DemosWithHighlights { get; private set; }

    /// <summary>How many rows the cache knows about at all (analysed or not) — the library-size context.</summary>
    public int LibraryRowCount { get; }

    /// <summary>Demos still queued for a scan when the picker opened.</summary>
    public int ScanQueueDepth { get; }

    /// <summary>Filter-button caption, e.g. "Players (2)".</summary>
    public string PlayerFilterSummary => Summary("Players", PlayerFilters.Count(p => p.IsSelected));

    /// <summary>Filter-button caption, e.g. "Types (1)".</summary>
    public string TypeFilterSummary => Summary("Types", TypeFilters.Count(t => t.IsSelected));

    /// <summary>Filter-button caption, e.g. "Maps".</summary>
    public string MapFilterSummary => Summary("Maps", MapFilters.Count(m => m.IsSelected));

    /// <summary>Filter-button caption, e.g. "Kind (1)".</summary>
    public string KindFilterSummary => Summary("Kind", KindFilters.Count(k => k.IsSelected));

    /// <summary>True when any filter is narrowing the list — drives the Clear affordance.</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText) || PlayerFilters.Any(p => p.IsSelected)
                                               || TypeFilters.Any(t => t.IsSelected)
                                               || MapFilters.Any(m => m.IsSelected)
                                               || KindFilters.Any(k => k.IsSelected);

    /// <summary>The filtered-out empty state: rows exist, but the filters exclude every one of them.</summary>
    public bool ShowNoFilterMatch => _allRows.Count > 0 && Rows.Count == 0;

    /// <summary>The library has no usable harvest at all — a different emptiness from "no filter match".</summary>
    public bool ShowNothingIndexed => _allRows.Count == 0;

    /// <summary>True while the list itself should render (mutually exclusive with both empty states).</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>How many rows are ticked for the bulk <c>Add N selected</c> action.</summary>
    public int PickedCount => _allRows.Count(r => r.IsPicked);

    /// <summary>Primary caption — always states the count, so an empty selection reads as "nothing yet".</summary>
    public string AddSelectedLabel => PickedCount > 0 ? $"Add {PickedCount} selected" : "Add selected";

    /// <summary>Gates the bulk add.</summary>
    public bool CanAddSelected => PickedCount > 0;

    /// <summary>Whether the mirrored <c>⟳ Rescan all</c> is available (desktop only).</summary>
    public bool CanRescan => _rescanAll is not null;

    /// <summary>
    ///     The footer's honest coverage line. Unfiltered it states the corpus; filtered it states the
    ///     narrowing, because "12 highlights" over a list of 3 rows is the same self-contradiction the
    ///     coverage definition above exists to avoid.
    /// </summary>
    public string CoverageLine
    {
        get
        {
            string corpus = $"{TotalHighlights} highlight{S(TotalHighlights)} across " +
                            $"{DemosWithHighlights} demo{S(DemosWithHighlights)}";
            return HasActiveFilters ? $"Showing {Rows.Count} of {corpus}" : corpus;
        }
    }

    /// <summary>
    ///     The coverage caveat. Careful: the wireframe's copy ("Only demos with full stats appear here") is
    ///     wrong under the chosen definition — a re-queued <c>Pending</c> row with a previous harvest DOES
    ///     appear — so the wording follows the definition rather than the wireframe.
    /// </summary>
    public string CoverageNote
    {
        get
        {
            string note = "Only demos that have been analysed for highlights appear here.";
            // The "of N" clause is emitted ONLY when the cache actually knows about more demos than it has
            // harvested. In a test or capture host (and before the first RefreshStaleness pass) the cache
            // holds nothing but the analysed rows, and "12 of 12" would read as a bug rather than context.
            if (LibraryRowCount > DemosWithHighlights)
            {
                note += $" {DemosWithHighlights} of {LibraryRowCount} cached demos have been.";
            }

            return note;
        }
    }

    /// <summary>True while a scan is still queued — the list will grow, and saying so prevents "it's broken".</summary>
    public bool ShowScanPendingNote => ScanQueueDepth > 0;

    /// <summary>Copy for <see cref="ShowScanPendingNote" />.</summary>
    public string ScanPendingNote =>
        $"{ScanQueueDepth} demo{S(ScanQueueDepth)} still queued for scanning — more highlights will appear " +
        "once they finish.";

    /// <summary>True while <see cref="StatusNote" /> carries anything.</summary>
    public bool HasStatusNote => !string.IsNullOrEmpty(StatusNote);

    /// <summary>
    ///     Re-reads the staged flag for every row against the live tray. Called from the tab's single tray
    ///     funnel (<c>PushTray</c>), so an un-stage performed in the TRAY flips this list's <c>[ ✓ ]</c> back
    ///     to <c>[ + ]</c> while it is open — the round-trip the plan requires.
    /// </summary>
    /// <param name="isStaged">O(1) staged test against the live tray.</param>
    public void SyncStagedFlags(Func<HighlightKey, bool> isStaged)
    {
        ArgumentNullException.ThrowIfNull(isStaged);
        foreach (AddClipsRowViewModel row in _allRows)
        {
            row.IsStaged = isStaged(row.Key);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnStatusNoteChanged(string value) => OnPropertyChanged(nameof(HasStatusNote));

    /// <summary>Stages every ticked row in ONE tray push, then clears the ticks.</summary>
    [RelayCommand(CanExecute = nameof(CanAddSelected))]
    private void AddSelected()
    {
        // Picks are deliberately NOT scoped to the current filter: a user who ticked three rows, changed the
        // player filter and pressed Add meant all three. Clearing on every filter change would silently drop
        // work the footer count says is still there.
        List<AddClipsRowViewModel> picked = [.. _allRows.Where(r => r.IsPicked && !r.IsStaged)];
        if (picked.Count > 0)
        {
            _stage([.. picked.Select(r => r.Selection)]);
        }

        foreach (AddClipsRowViewModel row in _allRows)
        {
            row.IsPicked = false;
        }

        StatusNote = picked.Count == 0 ? "" : $"Added {picked.Count} clip{S(picked.Count)} to the tray.";
    }

    /// <summary>Clears every filter (the filter bar's <c>Clear</c>, and the empty state's <c>Clear filters</c>).</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _suppressApply = true;
        SearchText = "";
        foreach (PlayerFilterItem p in PlayerFilters)
        {
            p.IsSelected = false;
        }

        foreach (HighlightTypeFilterItem t in TypeFilters)
        {
            t.IsSelected = false;
        }

        foreach (MapFilterItem m in MapFilters)
        {
            m.IsSelected = false;
        }

        foreach (HighlightKindFilterItem k in KindFilters)
        {
            k.IsSelected = false;
        }

        _suppressApply = false;
        RaiseFilterSummaries();
        ApplyFilter();
    }

    /// <summary>Mirrored whole-library rescan.</summary>
    [RelayCommand]
    private void RescanAll()
    {
        if (_rescanAll is null)
        {
            return;
        }

        _rescanAll();
        // The snapshot is now provably behind the cache, and saying nothing would let the user read an
        // unchanging list as "the rescan did nothing".
        StatusNote = "Re-scanning your library. Re-open this picker once it finishes to see new highlights.";
    }

    /// <summary>Dismisses the picker.</summary>
    [RelayCommand]
    private void Close() => _close();

    // Row [ + ] / [ ✓ ]: stage or un-stage exactly this highlight. The row does NOT mutate its own IsStaged —
    // the tray is the source of truth and pushes the flag back through SyncStagedFlags, so a rejected stage
    // (e.g. the demo vanished from the cache) can never leave the row lying about being in the reel.
    private void OnRowStageRequested(AddClipsRowViewModel row)
    {
        if (row.IsStaged)
        {
            _unstage(row.Key);
            return;
        }

        _stage([row.Selection]);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AddClipsRowViewModel.IsPicked))
        {
            return;
        }

        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(AddSelectedLabel));
        OnPropertyChanged(nameof(CanAddSelected));
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnFilterItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerFilterItem.IsSelected))
        {
            return;
        }

        RaiseFilterSummaries();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_suppressApply)
        {
            return;
        }

        HashSet<string> players = [.. PlayerFilters.Where(p => p.IsSelected).Select(p => p.SteamId64)];
        HashSet<string> types = [.. TypeFilters.Where(t => t.IsSelected).Select(t => t.TypeKey)];
        HashSet<string> maps = new(MapFilters.Where(m => m.IsSelected).Select(m => m.Display),
            StringComparer.OrdinalIgnoreCase);
        HashSet<HighlightKind> kinds = [.. KindFilters.Where(k => k.IsSelected).Select(k => k.Kind)];
        string needle = SearchText.Trim();

        List<AddClipsRowViewModel> next = [];
        foreach (AddClipsRowViewModel row in _allRows)
        {
            if (players.Count > 0 && !players.Contains(row.PlayerKey))
            {
                continue;
            }

            if (types.Count > 0 && !types.Contains(row.TypeKey))
            {
                continue;
            }

            if (maps.Count > 0 && !maps.Contains(row.MapDisplay))
            {
                continue;
            }

            if (kinds.Count > 0 && !kinds.Contains(row.Kind))
            {
                continue;
            }

            if (needle.Length > 0 && !row.Matches(needle))
            {
                continue;
            }

            next.Add(row);
        }

        // Same result → don't touch the collection. Rows are stable instances, so this is reference equality,
        // and it matters: a Clear + N Adds over a 240-row corpus tears down and re-realizes every virtualized
        // container. Typing a character that narrows nothing, or backspacing back to a set already shown, is
        // the common case — and it would otherwise reset the user's scroll position mid-search.
        if (next.Count == Rows.Count && !next.Where((r, i) => !ReferenceEquals(r, Rows[i])).Any())
        {
            OnPropertyChanged(nameof(HasActiveFilters));
            OnPropertyChanged(nameof(CoverageLine));
            return;
        }

        Rows.Clear();
        foreach (AddClipsRowViewModel row in next)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ShowNoFilterMatch));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CoverageLine));
    }

    private void RaiseFilterSummaries()
    {
        OnPropertyChanged(nameof(PlayerFilterSummary));
        OnPropertyChanged(nameof(TypeFilterSummary));
        OnPropertyChanged(nameof(MapFilterSummary));
        OnPropertyChanged(nameof(KindFilterSummary));
    }

    private void BuildPlayerFilters()
    {
        foreach ((string key, string name, int count) in _allRows
                     .GroupBy(r => r.PlayerKey)
                     .Select(g => (g.Key, g.First().PlayerName, g.Count()))
                     .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase))
        {
            PlayerFilterItem item = new(key, name, count);
            item.PropertyChanged += OnFilterItemChanged;
            PlayerFilters.Add(item);
        }
    }

    private void BuildTypeFilters()
    {
        foreach ((string key, int count) in _allRows
                     .GroupBy(r => r.TypeKey)
                     .Select(g => (g.Key, g.Count()))
                     .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            HighlightTypeFilterItem item = new(key, TypeDisplay(key), count);
            item.PropertyChanged += OnFilterItemChanged;
            TypeFilters.Add(item);
        }
    }

    private void BuildMapFilters()
    {
        foreach ((string display, string mapKey) in _allRows
                     .GroupBy(r => r.MapDisplay, StringComparer.OrdinalIgnoreCase)
                     .Select(g => (g.Key, g.First().MapKey))
                     .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            MapFilterItem item = new(display, mapKey);
            item.PropertyChanged += OnFilterItemChanged;
            MapFilters.Add(item);
        }
    }

    private void BuildKindFilters()
    {
        // Ordered by the enum (Highlight, Funny, Lowlight) so the chips read in editorial-track order rather
        // than by count. Only kinds actually present get a chip — a library with no lowlights shows no Lowlight
        // option rather than a dead "(0)" one.
        foreach ((HighlightKind kind, int count) in _allRows
                     .GroupBy(r => r.Kind)
                     .Select(g => (g.Key, g.Count()))
                     .OrderBy(x => x.Key))
        {
            HighlightKindFilterItem item = new(kind, KindDisplay(kind), count);
            item.PropertyChanged += OnFilterItemChanged;
            KindFilters.Add(item);
        }
    }

    private static string Summary(string label, int selected) => selected == 0 ? label : $"{label} ({selected})";

    private static string S(int n) => n == 1 ? "" : "s";

    // A friendly type label from the qualified key: prefer the highlight id (last segment). Verbatim from the
    // card grid, so a user's remembered chip labels survive the move.
    private static string TypeDisplay(string typeKey)
    {
        int dot = typeKey.LastIndexOf('.');
        string id = dot >= 0 && dot < typeKey.Length - 1 ? typeKey[(dot + 1)..] : typeKey;
        return id.Replace('_', ' ');
    }

    // Chip label for the editorial track. The enum names already read well, so this is a plain map — kept
    // explicit rather than ToString() so a future enum rename can't silently change a user-facing chip.
    private static string KindDisplay(HighlightKind kind) => kind switch
    {
        HighlightKind.Funny => "Funny",
        HighlightKind.Lowlight => "Lowlight",
        _ => "Highlight"
    };
}

/// <summary>
///     One highlight ROW in the Add-clips picker — the unit of work the list is built around. Carries
///     its own provenance (map accent + map + demo file + player + title + round + estimated window) because
///     a flat cross-demo list is unreadable without it.
///     <para>
///         <b>Both interaction states live on the VM, never on the container.</b> The list is virtualized,
///         and <c>VirtualizingStackPanel</c> recycles containers on scroll — multi-select riding container
///         state would silently evaporate the moment the user scrolled past their own picks.
///     </para>
/// </summary>
public sealed partial class AddClipsRowViewModel : ObservableObject
{
    private readonly Action<AddClipsRowViewModel> _stageRequested;

    /// <summary>True when this highlight is already in the tray (pushed by the tray, never self-set).</summary>
    [ObservableProperty]
    private bool _isStaged;

    /// <summary>True when ticked for the bulk <c>Add N selected</c> action.</summary>
    [ObservableProperty]
    private bool _isPicked;

    /// <summary>Builds one picker row and pre-computes everything the template binds.</summary>
    /// <param name="record">The owning demo's cache record (demo facts + roster).</param>
    /// <param name="highlight">The harvested highlight.</param>
    /// <param name="leadInSeconds">Lead-in for the window estimate.</param>
    /// <param name="leadOutSeconds">Lead-out for the window estimate.</param>
    /// <param name="dontCrossRoundStart">Whether the estimate floors the lead-in at the round start.</param>
    /// <param name="stageRequested">Raised by the row's <c>[ + ]</c> / <c>[ ✓ ]</c> button.</param>
    public AddClipsRowViewModel(
        DemoCacheRecord record,
        CachedHighlightEvent highlight,
        double leadInSeconds,
        double leadOutSeconds,
        bool dontCrossRoundStart,
        Action<AddClipsRowViewModel> stageRequested)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(highlight);
        _stageRequested = stageRequested;
        Selection = new HighlightSelection(record, highlight);

        MapDisplay = DemoEntry.PrettifyMap(record.Map);
        MapKey = record.Map ?? "";
        FileName = Path.GetFileName(record.Path);
        PlayerName = DisplayText.Sanitize(Selection.RawPlayerName);
        // Falls back to the raw name for the same reason the card grid did: a demo with no steamId join
        // would otherwise collapse every player into one filter bucket keyed "".
        PlayerKey = string.IsNullOrEmpty(Selection.SteamId64) ? Selection.RawPlayerName : Selection.SteamId64;
        TypeKey = highlight.TypeKey;
        Title = string.IsNullOrEmpty(highlight.RenderedTitle)
            ? highlight.HighlightId.Replace('_', ' ')
            : highlight.RenderedTitle;
        RoundDisplay = highlight.RoundNumber > 0 ? $"r{highlight.RoundNumber}" : "";

        // The SAME window maths the config pane uses, with the pane's CURRENT padding snapshotted at open —
        // so the "~20s" a user reads here is the duration they get, not a nominal lead-in + lead-out sum
        // that ignores the round-start floor and the demo-end clamp.
        int rate = record.TickRate > 0 ? record.TickRate : 64;
        int? roundStart = dontCrossRoundStart
            ? ClipWindows.RoundStartFor(record.Rounds.ToClipRounds(), highlight.Tick)
            : null;
        (long start, long end) = ClipWindows.Compute(highlight.Tick, roundStart, rate,
            leadInSeconds, leadOutSeconds, record.TickCount);
        DurationText = $"~{Math.Max(0, end - start) / (double)rate:0.#}s";
    }

    /// <summary>The staged-selection payload (cache row + highlight) the tray consumes verbatim.</summary>
    public HighlightSelection Selection { get; }

    /// <summary>This highlight's tray identity.</summary>
    public HighlightKey Key => Selection.Key;

    /// <summary>Prettified map name (also the map filter's key).</summary>
    public string MapDisplay { get; }

    /// <summary>Raw map name, for the accent dot — the same converter the Library cards use.</summary>
    public string MapKey { get; }

    /// <summary>Demo file name (provenance; the full path is the tooltip).</summary>
    public string FileName { get; }

    /// <summary>Full demo path — the row's tooltip, since two demos can share a file name.</summary>
    public string FilePath => Selection.Record.Path;

    /// <summary>Sanitized player name.</summary>
    public string PlayerName { get; }

    /// <summary>Player aggregation key (steamId64, or the raw name when the join is missing).</summary>
    public string PlayerKey { get; }

    /// <summary>Qualified <c>{rulesetId}.{highlightId}</c> (the type filter's key).</summary>
    public string TypeKey { get; }

    /// <summary>Authored ranking weight (0–100); the picker sorts rows by this descending.</summary>
    public int Score => Selection.Highlight.Score;

    /// <summary>Editorial track (skill / funny / lowlight); the Kind filter's key.</summary>
    public HighlightKind Kind => Selection.Highlight.Kind;

    /// <summary>The rendered highlight title.</summary>
    public string Title { get; }

    /// <summary>Round label ("r7"), empty for a warmup firing.</summary>
    public string RoundDisplay { get; }

    /// <summary>Estimated clip length under the config pane's current padding.</summary>
    public string DurationText { get; }

    /// <summary>The stage button's glyph — <c>+</c> to add, <c>✓</c> when already in the tray.</summary>
    public string StageGlyph => IsStaged ? "✓" : "+";

    /// <summary>The stage button's tooltip, which is where the toggle semantics are actually stated.</summary>
    public string StageHint => IsStaged ? "In the tray — click to remove" : "Add this clip to the tray";

    /// <summary>Free-text match over everything the row displays.</summary>
    /// <param name="needle">The trimmed search needle.</param>
    public bool Matches(string needle) =>
        MapDisplay.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || PlayerName.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Title.Contains(needle, StringComparison.OrdinalIgnoreCase);

    partial void OnIsStagedChanged(bool value)
    {
        OnPropertyChanged(nameof(StageGlyph));
        OnPropertyChanged(nameof(StageHint));
        OnPropertyChanged(nameof(CanPick));
        // A staged row cannot also be "pending add" — leaving a tick behind would make Add N selected count
        // clips that are already in the tray.
        if (value)
        {
            IsPicked = false;
        }
    }

    /// <summary>False once staged — ticking a row that is already in the tray has no meaning.</summary>
    public bool CanPick => !IsStaged;

    /// <summary>Toggles this row in or out of the tray.</summary>
    [RelayCommand]
    private void ToggleStage() => _stageRequested(this);
}

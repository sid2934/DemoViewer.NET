#region

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.ViewModels.Library;

/// <summary>
///     One recently-opened demo projected for the Library landing: the source
///     <see cref="RecentFile.Path" />, its <see cref="RecentFile.MapName" /> (may be <c>null</c>), a
///     display <see cref="FileName" />, whether the file still <see cref="Exists" /> (drives grey-out), and
///     when it was opened (<see cref="OpenedAtUtc" />, drives the relative-date label). Rebuilt from the
///     store on every change, so <see cref="Exists" /> is fresh at build time.
/// </summary>
public sealed record RecentFileItem(string Path, string? MapName, string FileName, bool Exists, DateTime OpenedAtUtc)
{
    /// <summary>Prettified map (e.g. "Mirage"), or "Unknown" when the map wasn't known at open time.</summary>
    public string MapDisplay => DemoEntry.PrettifyMap(MapName);

    /// <summary>Relative "opened" age (e.g. "2d ago"). Reuses the library's one relative-time formatter.</summary>
    public string DateDisplay => DemoEntry.RelativeTime(OpenedAtUtc.ToLocalTime());

    /// <summary>Second-line metadata for a recents row: "&lt;map&gt; · &lt;opened age&gt;".</summary>
    public string Meta => $"{MapDisplay} · {DateDisplay}";

    /// <summary>Dim a row whose file no longer exists — still clickable, and the click prunes it.</summary>
    public double RowOpacity => Exists ? 1.0 : 0.4;
}

/// <summary>How demos sort in the browser.</summary>
public enum LibrarySort
{
    Newest,
    Oldest,
    MapAz,
    NameAz,
    LargestFirst
}

/// <summary>
///     A selectable map in the multi-select map filter: its display name (matched against
///     <see cref="DemoEntry.MapDisplay" />), an accent key (a raw <c>MapName</c> so the colour dot matches the
///     card accent), and a checked state the view-model observes to re-apply the filter.
/// </summary>
public partial class MapFilterItem(string display, string mapKey) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Prettified map name shown in the checklist and matched against <see cref="DemoEntry.MapDisplay" />.</summary>
    public string Display { get; } = display;

    /// <summary>A raw map name (e.g. <c>de_dust2</c>) for the accent-colour dot — kept consistent with the card accent.</summary>
    public string MapKey { get; } = mapKey;
}

/// <summary>
///     One virtualization row of the card browser: up to <see cref="LibraryTabViewModel.CardColumns" />
///     consecutive entries of the filtered list. The card grid renders as a VERTICAL virtualized list of
///     these rows (WrapPanel has no virtualizing counterpart — a large library would otherwise realize
///     every card; see the chunked-rows pattern in the library perf review).
/// </summary>
public sealed record CardRow(IReadOnlyList<DemoEntry> Items);

/// <summary>
///     View-model for the demo-library landing tab. Wraps the <see cref="DemoLibraryService" /> indexer and
///     exposes a filtered/sorted view (<see cref="FilteredEntries" />) over its discovered demos, plus the
///     card/list toggle and the filter controls: free-text search (filename + map + players), a MULTI-select
///     map filter (<see cref="MapFilters" /> — none checked = all maps), a single-select player filter
///     (<see cref="AvailablePlayers" />), and sort. Opening a demo routes through the injected shell load
///     callback and switches to the Parser tab.
/// </summary>
public partial class LibraryTabViewModel : ObservableObject, IWorkspaceTabViewModel
{
    private const string AllPlayers = "All players";
    private readonly DemoLibraryService _library;
    private readonly Func<string, Task> _openDemo; // shell.LoadDemoFromPathAsync
    private readonly Func<Task>? _openFilePicker; // shell.OpenFileAsync (the shared Open-Demo funnel)
    private readonly Func<Task<IReadOnlyList<string>>> _pickFolders; // folder picker
    private readonly RecentFilesStore? _recentFiles; // recent-files store (null on designer / older tests)
    private readonly string? _sampleDemoPath; // bundled tour sample (null = none ships / designer / tests)

    [ObservableProperty]
    private bool _isCardView = true; // user default: card view

    /// <summary>
    ///     True while a file drag is hovering the Library surface — drives the highlighted full-surface
    ///     drop affordance. Toggled by the view's drag handlers (a view concern) but kept here as an
    ///     observable so the overlay is a plain reactive binding, and so a capture variant can render the
    ///     drag-over look headlessly (a real drag can't be synthesized off-display).
    /// </summary>
    [ObservableProperty]
    private bool _isDragOver;

    private bool _scannedOnce;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private DemoEntry? _selectedEntry;

    /// <summary>
    ///     Raised when the user selects a demo card WITHOUT opening it (single click / arrow key). The shell
    ///     answers by rendering that demo's cached record on Match Overview.
    ///     <para>
    ///         <b>This must never start work.</b> Selection is a browsing gesture — it reads the cache and
    ///         nothing else. Opening stays on double-click, where the multi-second parse is something the user
    ///         asked for; one heavy parse is machine-wide, so a preview that parsed would make arrow-keying
    ///         the grid worse than useless.
    ///     </para>
    /// </summary>
    public event Action<DemoEntry>? DemoPreviewRequested;

    partial void OnSelectedEntryChanged(DemoEntry? value)
    {
        if (value is not null)
        {
            DemoPreviewRequested?.Invoke(value);
        }
    }

    [ObservableProperty]
    private string _selectedPlayer = AllPlayers;

    [ObservableProperty]
    private LibrarySort _sort = LibrarySort.Newest;

    // Set while a bulk filter change (Clear) is in flight so per-control change handlers don't each re-apply.
    private bool _suppressApply;

    public LibraryTabViewModel(
        DemoLibraryService library,
        Func<string, Task> openDemo,
        Func<Task<IReadOnlyList<string>>> pickFolders,
        Func<Task>? openFilePicker = null,
        RecentFilesStore? recentFiles = null,
        string? sampleDemoPath = null)
    {
        _library = library;
        _openDemo = openDemo;
        _pickFolders = pickFolders;
        _openFilePicker = openFilePicker;
        _recentFiles = recentFiles;
        _sampleDemoPath = sampleDemoPath;

        _library.Entries.CollectionChanged += OnEntriesChanged;
        _library.Folders.CollectionChanged += OnFoldersChanged;
        _library.Changed += OnLibraryChanged;

        if (_recentFiles is not null)
        {
            _recentFiles.Changed += RefreshRecentFiles;
            RefreshRecentFiles();
        }

        RefreshMapFilters();
        RefreshAvailablePlayers();
        ApplyFilter();
    }

    /// <summary>
    ///     Recently-opened demos, most-recent-first, projected from the <see cref="RecentFilesStore" /> and
    ///     kept live (rebuilt on the store's Changed event). Empty when no store is injected. The landing
    ///     UI binds this to its recent-files strip.
    /// </summary>
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];

    /// <summary>True when there are any recents to show (drives the landing's recent-files section visibility).</summary>
    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>
    ///     True when the compact "Recent ▾" flyout belongs in the toolbar: recents exist AND the folder
    ///     browser is showing. In the empty/hero state the hero already lists recents in full, so the header
    ///     flyout is suppressed to avoid two recents surfaces on screen at once.
    /// </summary>
    public bool ShowHeaderRecents => HasRecentFiles && !HasNoFolders;

    /// <summary>Folders being scanned (bound to the folder chips).</summary>
    public ObservableCollection<string> Folders => _library.Folders;

    /// <summary>The visible, filtered + sorted demo list (a projection of the indexer's entries).</summary>
    public ObservableCollection<DemoEntry> FilteredEntries { get; } = [];

    /// <summary>
    ///     <see cref="FilteredEntries" /> chunked into rows of <see cref="CardColumns" /> for the
    ///     virtualized card grid. Rebuilt whenever the filtered list or the column count changes.
    /// </summary>
    public ObservableCollection<CardRow> CardRows { get; } = [];

    /// <summary>Cards per row — driven by the view from the measured viewport width. Never below 1.</summary>
    public int CardColumns { get; private set; } = 4;

    /// <summary>The multi-select map filter — one checkable item per distinct map. None checked = all maps.</summary>
    public ObservableCollection<MapFilterItem> MapFilters { get; } = [];

    /// <summary>Distinct player names across the library, for the player filter; index 0 is "All players".</summary>
    public ObservableCollection<string> AvailablePlayers { get; } = [AllPlayers];

    /// <summary>Two-way index binding for the sort ComboBox (its items mirror <see cref="LibrarySort" /> order).</summary>
    public int SortIndex
    {
        get => (int)Sort;
        set => Sort = (LibrarySort)value;
    }

    /// <summary>Inverse of <see cref="IsCardView" /> for the list-view toggle button.</summary>
    public bool IsListView
    {
        get => !IsCardView;
        set => IsCardView = !value;
    }

    /// <summary>Count summary for the header, e.g. "12 of 40 demos".</summary>
    public string CountSummary => FilteredEntries.Count == _library.Entries.Count
        ? $"{_library.Entries.Count} demos"
        : $"{FilteredEntries.Count} of {_library.Entries.Count} demos";

    /// <summary>True when no folders are configured yet (drives the empty-state prompt).</summary>
    public bool HasNoFolders => Folders.Count == 0;

    /// <summary>
    ///     True when folders ARE configured but the indexer found zero demos in them (v0.6.0). Without
    ///     this state a user who added an empty folder dropped from the landing hero straight onto a
    ///     blank card grid with no explanation.
    /// </summary>
    public bool HasFoldersButNoDemos => !HasNoFolders && _library.Entries.Count == 0;

    /// <summary>
    ///     True when demos exist but every one is filtered out (v0.6.0) — drives the "no demos match
    ///     your filters" empty state next to the existing clear-filters affordance.
    /// </summary>
    public bool HasRowsButAllFiltered => _library.Entries.Count > 0 && FilteredEntries.Count == 0;

    /// <summary>
    ///     True when a bundled sample demo resolved at construction (<c>TourDemoLocator</c> — the shell
    ///     injects the path; null on Browser/WASM, the designer, and older tests). Drives the hero's
    ///     "Try a sample match" CTA. Fixed at construction — the shipped asset doesn't move at runtime.
    /// </summary>
    public bool HasSampleDemo => _sampleDemoPath is not null;

    /// <summary>
    ///     True when file drag-drop is available (desktop). WASM/browser has no local-path drop, so the
    ///     landing's drop hint + overlay are suppressed there (graceful degradation). Get-only (fixed at
    ///     construction) — a stable per-host capability, not reactive.
    /// </summary>
    public bool CanDropFiles { get; } = !OperatingSystem.IsBrowser();

    /// <summary>Label for the map-filter flyout button — shows the checked count when the filter is active.</summary>
    public string MapFilterSummary
    {
        get
        {
            int n = MapFilters.Count(m => m.IsSelected);
            return n == 0 ? "Maps" : $"Maps ({n})";
        }
    }

    /// <summary>True when any filter (search / map / player) is narrowing the list — drives the Clear button.</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText) || SelectedPlayer != AllPlayers || MapFilters.Any(m => m.IsSelected);

    public void OnActivated(IModuleContext context)
    {
        // Kick the first scan when the tab is first seen (it's the default tab, so this runs at startup).
        if (!_scannedOnce && Folders.Count > 0)
        {
            _scannedOnce = true;
            _ = _library.RescanAsync();
        }
    }

    public void OnDeactivated()
    {
    }

    partial void OnIsCardViewChanged(bool value) => OnPropertyChanged(nameof(IsListView));

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        IReadOnlyList<string> picked = await _pickFolders();
        if (picked.Count > 0)
        {
            _scannedOnce = true;
            await _library.AddFoldersAsync(picked);
        }
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(string? folder)
    {
        if (!string.IsNullOrEmpty(folder))
        {
            await _library.RemoveFolderAsync(folder);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _scannedOnce = true;
        await _library.RescanAsync();
    }

    /// <summary>How many demos are waiting on a score re-derivation.</summary>
    public int ScoreRepairCount => _library.ScoreRepairPendingCount;

    /// <summary>Drives the toolbar action's visibility — absent entirely at zero, which is the normal case.</summary>
    public bool HasScoreRepairPending => ScoreRepairCount > 0;

    /// <summary>
    ///     Names the work rather than the mechanism. "Repair" alone reads as fixing something broken; these
    ///     demos are intact and only their cached score is missing.
    /// </summary>
    public string ScoreRepairLabel => ScoreRepairCount == 1
        ? "Re-derive 1 score"
        : $"Re-derive {ScoreRepairCount} scores";

    /// <summary>
    ///     The explicit half-score repair. Enlists the flagged demos for a full re-parse — potentially
    ///     hours of background work, which is precisely why it is a button and not a launch behaviour.
    /// </summary>
    [RelayCommand]
    private async Task RepairScoresAsync()
    {
        _scannedOnce = true;
        await _library.RepairPendingScoresAsync();
        RaiseScoreRepairState();
    }

    /// <summary>
    ///     Opens a specific library card/entry (the card double-click path) through the shared shell load
    ///     core. Deliberately does NOT switch tabs itself: the shared load funnel owns the landing —
    ///     Match Overview on a normal open, stay-put while the tutorial is touring (a pre-switch here is
    ///     exactly what used to yank the tour's spotlighted card-click onto the Parser tab). Renamed from
    ///     <c>OpenDemoCommand</c> so the name <c>OpenDemoCommand</c> can mean the file-picker CTA
    ///     the Library landing owns.
    /// </summary>
    [RelayCommand]
    private async Task OpenEntryAsync(DemoEntry? entry)
    {
        entry ??= SelectedEntry;
        if (entry is null)
        {
            return;
        }

        await _openDemo(entry.FilePath);
    }

    /// <summary>
    ///     The primary "Open Demo…" call-to-action: routes through the shell's shared file picker
    ///     (<c>OpenFileAsync</c> → <c>LoadDemoFromBytesAsync</c>) — the same funnel the toolbar and Parser
    ///     empty-state buttons use, so every open records exactly one recent. No-op when no picker is wired
    ///     (designer / older tests). Does NOT switch tabs unconditionally: the picker can be cancelled and a
    ///     <see cref="Func{Task}" /> can't report success, so a post-open navigation is left to the landing.
    /// </summary>
    [RelayCommand]
    private async Task OpenDemoAsync()
    {
        if (_openFilePicker is not null)
        {
            await _openFilePicker();
        }
    }

    /// <summary>
    ///     Opens the bundled sample demo (the hero's "Try a sample match" CTA — and the walkthrough
    ///     gateway's target when the library is empty). Routes through the SAME shared load core as every
    ///     other open (<c>_openDemo</c> → <c>LoadDemoFromPathAsync</c>: records a recent, the funnel lands
    ///     the tab, the tour's <c>NotifyDemoLoaded</c> resumes). No-op when no sample ships.
    /// </summary>
    [RelayCommand]
    private async Task OpenSampleAsync()
    {
        if (_sampleDemoPath is not null)
        {
            await _openDemo(_sampleDemoPath);
        }
    }

    /// <summary>
    ///     Opens a demo by absolute filesystem path — the drag-drop landing path. Routes through the SAME
    ///     shared load core the recents/browser use (<c>_openDemo</c> → <c>LoadDemoFromPathAsync</c>, which
    ///     records the recent); the funnel lands the tab (Match Overview). A null/empty or non-<c>.dem</c>
    ///     path is a no-op, so a stray drop of the wrong file type does nothing.
    /// </summary>
    [RelayCommand]
    private async Task OpenPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _openDemo(path);
    }

    /// <summary>
    ///     Opens a recently-opened demo through the shared shell load core; the funnel lands the tab
    ///     (Match Overview). A file that no longer exists is pruned from the store (which live-refreshes
    ///     <see cref="RecentFiles" />) rather than attempting a load.
    /// </summary>
    [RelayCommand]
    private async Task OpenRecentAsync(RecentFileItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!File.Exists(item.Path))
        {
            _recentFiles?.Remove(item.Path); // stale entry → prune (fires Changed → RefreshRecentFiles)
            return;
        }

        await _openDemo(item.Path);
    }

    // Rebuilds the RecentFiles projection from the store (most-recent-first), recomputing FileName + Exists.
    // Runs on the caller's thread: the store's Changed fires on the UI thread (RecordOpen after a UI-thread
    // open; Remove from a UI-thread command), and the ctor calls it inline before the tab is shown.
    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        if (_recentFiles is not null)
        {
            bool canStat = !OperatingSystem.IsBrowser();
            foreach (RecentFile r in _recentFiles.Items)
            {
                RecentFiles.Add(new RecentFileItem(
                    r.Path,
                    r.MapName,
                    Path.GetFileName(r.Path),
                    canStat && File.Exists(r.Path),
                    r.OpenedAtUtc));
            }
        }

        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(ShowHeaderRecents));
    }

    [RelayCommand]
    private void ClearFilters()
    {
        // Bulk change: mutate every control once, then apply a single filter pass (avoids N churns of
        // FilteredEntries as each map chip / the player box clears).
        _suppressApply = true;
        SearchText = "";
        SelectedPlayer = AllPlayers;
        foreach (MapFilterItem m in MapFilters)
        {
            m.IsSelected = false;
        }

        _suppressApply = false;
        OnPropertyChanged(nameof(MapFilterSummary));
        ApplyFilter();
    }

    // ── Filter / sort ─────────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedPlayerChanged(string value) => ApplyFilter();

    partial void OnSortChanged(LibrarySort value)
    {
        OnPropertyChanged(nameof(SortIndex));
        ApplyFilter();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshMapFilters();
        RefreshAvailablePlayers();
        ApplyFilter();
    }

    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoFolders));
        OnPropertyChanged(nameof(ShowHeaderRecents));
        RaiseEmptyStates();
    }

    private void OnLibraryChanged()
    {
        RefreshMapFilters();
        RefreshAvailablePlayers();
        RaiseScoreRepairState();
        ApplyFilter();
    }

    private void RaiseScoreRepairState()
    {
        OnPropertyChanged(nameof(ScoreRepairCount));
        OnPropertyChanged(nameof(HasScoreRepairPending));
        OnPropertyChanged(nameof(ScoreRepairLabel));
    }

    // A map chip was (un)checked → refresh the flyout label and re-filter (unless a bulk Clear is in flight).
    private void OnMapFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapFilterItem.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(MapFilterSummary));
        if (!_suppressApply)
        {
            ApplyFilter();
        }
    }

    // Rebuilds the map-filter checklist from the current entries, one item per distinct MapDisplay, carrying a
    // representative raw MapName for the accent dot. Preserves existing checks; rebuilds only when the map SET
    // changes (so toggling a chip doesn't churn the list). Unsubscribes old items to avoid handler leaks.
    private void RefreshMapFilters()
    {
        List<(string Display, string MapKey)> groups = _library.Entries
            .Where(e => e.MapDisplay is { Length: > 0 } && e.MapDisplay != "Unknown")
            .GroupBy(e => e.MapDisplay, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Display: g.Key, MapKey: g.Select(e => e.MapName).FirstOrDefault(m => m is { Length: > 0 }) ?? g.Key))
            .OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // No change to the map set → keep the current chips (and their checked state) untouched.
        if (groups.Count == MapFilters.Count &&
            groups.All(g => MapFilters.Any(f => string.Equals(f.Display, g.Display, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        HashSet<string> selected = MapFilters
            .Where(m => m.IsSelected)
            .Select(m => m.Display)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (MapFilterItem old in MapFilters)
        {
            old.PropertyChanged -= OnMapFilterChanged;
        }

        MapFilters.Clear();
        foreach ((string display, string mapKey) in groups)
        {
            MapFilterItem item = new(display, mapKey)
            {
                IsSelected = selected.Contains(display)
            };
            item.PropertyChanged += OnMapFilterChanged;
            MapFilters.Add(item);
        }

        OnPropertyChanged(nameof(MapFilterSummary));
    }

    // Rebuilds the player dropdown from every player seen across the library (distinct, sorted). Keeps the
    // current selection stable when it still exists; otherwise falls back to "All players".
    private void RefreshAvailablePlayers()
    {
        List<string> players = _library.Entries
            .SelectMany(e => e.Players)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Unchanged set (excluding the "All players" sentinel) → leave the ComboBox alone.
        if (players.Count == AvailablePlayers.Count - 1 && players.All(AvailablePlayers.Contains))
        {
            return;
        }

        string keep = SelectedPlayer;
        AvailablePlayers.Clear();
        AvailablePlayers.Add(AllPlayers);
        foreach (string p in players)
        {
            AvailablePlayers.Add(p);
        }

        SelectedPlayer = AvailablePlayers.Contains(keep) ? keep : AllPlayers;
    }

    private void ApplyFilter()
    {
        if (_suppressApply)
        {
            return;
        }

        IEnumerable<DemoEntry> q = _library.Entries;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string needle = SearchText.Trim();
            q = q.Where(e =>
                e.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (e.MapName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                e.Players.Any(p => p.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        // Multi-select map filter: none checked = all maps; else keep only the checked maps (by display name).
        HashSet<string> selectedMaps = MapFilters
            .Where(m => m.IsSelected)
            .Select(m => m.Display)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedMaps.Count > 0)
        {
            q = q.Where(e => selectedMaps.Contains(e.MapDisplay));
        }

        // Player filter: keep only demos the chosen player appears in.
        if (SelectedPlayer != AllPlayers)
        {
            q = q.Where(e => e.Players.Any(p => string.Equals(p, SelectedPlayer, StringComparison.OrdinalIgnoreCase)));
        }

        q = Sort switch
        {
            LibrarySort.Newest => q.OrderByDescending(e => e.Modified),
            LibrarySort.Oldest => q.OrderBy(e => e.Modified),
            LibrarySort.MapAz => q.OrderBy(e => e.MapDisplay, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.Modified),
            LibrarySort.NameAz => q.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase),
            LibrarySort.LargestFirst => q.OrderByDescending(e => e.FileSizeBytes),
            _ => q
        };

        List<DemoEntry> ordered = q.ToList();

        // Same membership + order → skip the wholesale rebuild. The Clear+Add storm regenerates
        // every item container (the panels are not virtualized), and the indexer nudges Changed
        // periodically during long scans where the projection is usually identical.
        if (ordered.Count == FilteredEntries.Count)
        {
            bool same = true;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(ordered[i], FilteredEntries[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                OnPropertyChanged(nameof(CountSummary));
                OnPropertyChanged(nameof(HasActiveFilters));
                RaiseEmptyStates();
                return;
            }
        }

        FilteredEntries.Clear();
        foreach (DemoEntry e in ordered)
        {
            FilteredEntries.Add(e);
        }

        RebuildCardRows();
        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(HasActiveFilters));
        RaiseEmptyStates();
    }

    // Both empty-state flags derive from (entries, filtered) — re-raised on every filter pass, and on
    // folder changes below (a removed last folder flips HasFoldersButNoDemos off in favor of the hero).
    private void RaiseEmptyStates()
    {
        OnPropertyChanged(nameof(HasFoldersButNoDemos));
        OnPropertyChanged(nameof(HasRowsButAllFiltered));
    }

    /// <summary>
    ///     Sets the card-grid column count from the measured viewport width (view code-behind calls
    ///     this on size change). Re-chunks the rows only when the count actually changes.
    /// </summary>
    public void SetCardColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == CardColumns)
        {
            return;
        }

        CardColumns = columns;
        RebuildCardRows();
    }

    private void RebuildCardRows()
    {
        CardRows.Clear();
        for (int i = 0; i < FilteredEntries.Count; i += CardColumns)
        {
            List<DemoEntry> row = new(CardColumns);
            for (int j = i; j < Math.Min(i + CardColumns, FilteredEntries.Count); j++)
            {
                row.Add(FilteredEntries[j]);
            }

            CardRows.Add(new CardRow(row));
        }
    }
}

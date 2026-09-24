#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>
///     The Situations tab: the round index status strip. "Indexed 240 of 277 demos · 12 stale ·
///     indexing match730_…", with Rebuild index and Retry failed. Every count comes from the index
///     rows and the loaded index, never from a sidecar read, so a refresh costs a dictionary pass.
///     <para>
///         Delegate-injected (the Highlights precedent): the VM owns no engine; it reads the
///         <see cref="ISituationIndex" />, the cache rows and drives the <see cref="RoundIndexEvaluator" />.
///         The Query Canvas (<see cref="Canvas" />) sits below the strip with the Result Cards under it,
///         whose Overlay all N stacks onto the canvas; the Watched Situations list (<see cref="Watched" />)
///         sits between the strip and the canvas; the Tolerance Slider is its own build item.
///     </para>
///     <para>
///         On the browser host there is no queue and no filesystem, so no library index exists; the
///         strip says so in the annotations panel's words rather than showing a count of zero that reads
///         as "nothing matched".
///     </para>
/// </summary>
public sealed partial class SituationsTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    private readonly DemoCacheStore _demoCache;
    private readonly RoundIndexEvaluator? _evaluator;
    private readonly ISituationIndex _index;

    // The session-only service built when the host passes none; the host owns one it passed.
    private readonly WatchedSituationsService? _ownedWatched;
    private readonly RoundIndexPlaceSources _sources;
    private readonly Func<RoundIndexTokenSource> _tokenSource;

    private bool _disposed;

    /// <summary>Rows whose last index attempt threw: the Retry failed population.</summary>
    [ObservableProperty]
    private int _failedCount;

    /// <summary>The demo believed to be indexing right now; null when nothing is in flight.</summary>
    [ObservableProperty]
    private string? _indexingName;

    /// <summary>Demos whose sidecar the query service has loaded.</summary>
    [ObservableProperty]
    private int _indexedCount;

    /// <summary>True while the coordinator has index work in flight.</summary>
    [ObservableProperty]
    private bool _isIndexing;

    /// <summary>Demos the library knows (every index row).</summary>
    [ObservableProperty]
    private int _libraryCount;

    /// <summary>Demos waiting for an index under the current settings.</summary>
    [ObservableProperty]
    private int _pendingCount;

    /// <summary>Loaded demos whose index was built under another fingerprint (still answering queries).</summary>
    [ObservableProperty]
    private int _staleCount;

    /// <summary>The one-line status: "Indexed 240 of 277 demos · 12 stale · indexing match730_…".</summary>
    [ObservableProperty]
    private string _statusLine = "";

    /// <summary>The token source line, and the per-map fallback note when the zones mode has no zones for a map.</summary>
    [ObservableProperty]
    private string _tokenSourceLine = "";

    /// <param name="index">The in-memory situation index.</param>
    /// <param name="evaluator">The index writer; null on a host without a queue (the browser).</param>
    /// <param name="demoCache">The index rows the counts derive from.</param>
    /// <param name="sources">The fingerprint in force per map, for the stale count and the fallback note.</param>
    /// <param name="tokenSource">The live <c>SituationsSettings.TokenSource</c>.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    /// <param name="canvas">The Query Canvas; built over the same index and zone source when null, retiring bundles through the dispatcher.</param>
    /// <param name="results">The Result Cards; built over the same cache, sidecar store and sources when null.</param>
    /// <param name="playback">The seek seam a card opens playback through; null on a host with no 2D tab.</param>
    /// <param name="sidecars">The sidecar store the cards read positions from; needed when <paramref name="results" /> is null.</param>
    /// <param name="teams">Team Identity, for the rail's opponent and our-side fields; null offers neither. Used when <paramref name="canvas" /> is null.</param>
    /// <param name="provenance">Demo Provenance Labels, for the rail's source field; null offers none. Used when <paramref name="canvas" /> is null.</param>
    /// <param name="watched">Watched Situations; a session-only service over the same index and services when null.</param>
    public SituationsTabViewModel(
        ISituationIndex index,
        RoundIndexEvaluator? evaluator,
        DemoCacheStore demoCache,
        RoundIndexPlaceSources sources,
        Func<RoundIndexTokenSource> tokenSource,
        bool? isBrowser = null,
        QueryCanvasViewModel? canvas = null,
        ResultCardsViewModel? results = null,
        Func<ISituationPlayback?>? playback = null,
        RoundIndexStore? sidecars = null,
        TeamIdentityService? teams = null,
        IDemoProvenanceSource? provenance = null,
        WatchedSituationsService? watched = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(tokenSource);
        _index = index;
        _evaluator = evaluator;
        _demoCache = demoCache;
        _sources = sources;
        _tokenSource = tokenSource;
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();

        // The canvas resolves a drop through the same zone source the builder mints tokens through, so
        // the place a click names and the place a row stores come from one vocabulary. Its filter rail
        // reads the two services the opponent, side and source fields join through.
        Canvas = canvas ?? new QueryCanvasViewModel(index, new QueryPlaceResolver(index, sources.Zones), demoCache,
            filters: new SearchFiltersViewModel(demoCache, teams, provenance));

        // The cards read the positions files the same store wrote, under the fingerprint in force for
        // the map; a set built without a sidecar store has nothing to draw and says so on every tile.
        // Their overlay is the canvas's own document, so the heatmap lands on the map the query was
        // drawn on.
        Results = results ?? new ResultCardsViewModel(demoCache, sidecars ?? new RoundIndexStore(null, demoCache),
            sources, playback ?? (() => null), overlay: Canvas.Overlay);
        Canvas.Searched += Results.Load;
        Canvas.PropertyChanged += OnCanvasPropertyChanged;

        // The saved list saves from and re-runs onto this canvas. A host that passes no service gets
        // a session-only one over the same index, the browser's own state.
        _ownedWatched = watched is null ? new WatchedSituationsService(null, index, demoCache, teams, provenance) : null;
        Watched = new WatchedSituationsViewModel(watched ?? _ownedWatched!, Canvas, teams);

        // Both sources matter: the cache raises on every stamp, the index on load and merge. Subscribed
        // for the VM's life rather than per activation so the strip is right the moment the tab opens.
        _demoCache.Changed += OnCacheChanged;
        _index.Changed += Refresh;
        Refresh();
    }

    /// <summary>True on the WASM head: no queue, no filesystem, no library index.</summary>
    public bool IsBrowser { get; }

    /// <summary>The Query Canvas below the strip.</summary>
    public QueryCanvasViewModel Canvas { get; }

    /// <summary>The filter rail, owned by the canvas: the same draft the count and the search read.</summary>
    public SearchFiltersViewModel Filters => Canvas.Filters;

    /// <summary>The Result Cards below the canvas: the last search's hits, and the walk over them.</summary>
    public ResultCardsViewModel Results { get; }

    /// <summary>The Watched Situations list between the strip and the canvas.</summary>
    public WatchedSituationsViewModel Watched { get; }

    /// <summary>The line the strip shows instead of counts on the browser host: the annotation panel's words.</summary>
    public const string BrowserNote = "session only: no library index in the browser";

    /// <summary>True until the startup load has finished; the strip says "indexing" rather than a count.</summary>
    public bool IsLoading => !_index.IsReady;

    public bool HasFailed => FailedCount > 0;

    public bool HasStale => StaleCount > 0;

    public bool HasIndexingName => !string.IsNullOrEmpty(IndexingName);

    /// <summary>The Rebuild and Retry actions need a queue; the browser has none.</summary>
    public bool CanRebuild => _evaluator is not null && !IsBrowser;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context) => Refresh();

    /// <inheritdoc />
    /// <remarks>Nothing per tick is subscribed, so there is nothing to drop; the counts keep tracking the cache.</remarks>
    public void OnDeactivated()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _demoCache.Changed -= OnCacheChanged;
        _index.Changed -= Refresh;
        Canvas.Searched -= Results.Load;
        Canvas.PropertyChanged -= OnCanvasPropertyChanged;
        Watched.Dispose();
        _ownedWatched?.Dispose();
        Canvas.Dispose();
    }

    /// <summary>
    ///     Marks every index stale and re-queues the library. The old rows keep answering until each
    ///     demo is rebuilt, which is why the strip shows a stale count rather than going blank. The
    ///     thumbnails go, though: every one is a picture of rows about to be replaced, and their keys
    ///     carry the fingerprint the rebuild leaves behind.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private void RebuildIndex()
    {
        Results.Cache.Clear();
        _evaluator?.RebuildAll();
    }

    // The canvas nulls its count the moment the query or the map changes; the cards describe the
    // old query and go with it, so a stale set never sits under a new one.
    private void OnCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryCanvasViewModel.ResultCount) && Canvas.ResultCount is null && Results.HasCards)
        {
            Results.Clear();
        }
    }

    /// <summary>Re-queues the failed rows only, at user priority. A user with three broken demos has not asked for a library rebuild.</summary>
    [RelayCommand(CanExecute = nameof(HasFailed))]
    private void RetryFailed() => _evaluator?.RetryFailed();

    // Library-wide counts move for any demo, so the changed path is accepted and ignored.
    private void OnCacheChanged(string? changedPath) => Refresh();

    /// <summary>Recomputes every count from the index rows and the loaded index.</summary>
    public void Refresh()
    {
        IReadOnlyList<DemoCacheIndexEntry> rows = _demoCache.Index;
        IReadOnlyList<string> pending = _evaluator?.PendingPaths() ?? [];

        LibraryCount = rows.Count;
        IndexedCount = _index.IndexedDemoCount;
        StaleCount = _index.StaleDemoCount;
        FailedCount = rows.Count(r => r.RoundIndexState == RoundIndexState.Failed);
        PendingCount = pending.Count;
        IsIndexing = _evaluator?.IsIndexing ?? false;
        // PendingPaths is newest-first, the order the queue drains in, so its head is the demo in flight.
        IndexingName = IsIndexing && pending.Count > 0 ? Path.GetFileName(pending[0]) : null;

        StatusLine = BuildStatusLine();
        TokenSourceLine = BuildTokenSourceLine(rows);

        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasStale));
        OnPropertyChanged(nameof(HasIndexingName));
        RetryFailedCommand.NotifyCanExecuteChanged();
        RebuildIndexCommand.NotifyCanExecuteChanged();
    }

    private string BuildStatusLine()
    {
        if (IsBrowser)
        {
            return BrowserNote;
        }

        if (IsLoading)
        {
            return "loading the index";
        }

        List<string> parts = [$"Indexed {IndexedCount} of {LibraryCount} demos"];
        if (StaleCount > 0)
        {
            parts.Add($"{StaleCount} stale");
        }

        if (PendingCount > 0)
        {
            parts.Add($"{PendingCount} queued");
        }

        if (FailedCount > 0)
        {
            parts.Add($"{FailedCount} failed");
        }

        if (HasIndexingName)
        {
            parts.Add($"indexing {IndexingName}");
        }
        else if (IsIndexing)
        {
            parts.Add("indexing");
        }

        return string.Join(" · ", parts);
    }

    // The zones mode is per map: a map without zones falls back to the pawn and the strip names it,
    // so a search result speaking Valve's names on that map is not a surprise.
    private string BuildTokenSourceLine(IReadOnlyList<DemoCacheIndexEntry> rows)
    {
        if (_tokenSource() != RoundIndexTokenSource.Zones)
        {
            return "Places from the pawn (Valve's names)";
        }

        List<string> fallback =
        [
            .. rows.Select(r => r.Map)
                .OfType<string>()
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(_sources.IsZoneFallback)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
        ];
        return fallback.Count == 0
            ? "Places from the zone set (your names); changing the source re-indexes the library"
            : $"Places from the zone set; no zones for {string.Join(", ", fallback)}, so those maps use the pawn";
    }
}

#region

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>
///     The Query Canvas: the map picker, the ten-slot token rail (five CT, five T), the query draft the
///     rail and the canvas both edit, the filter rail (<see cref="Filters" />), and the one Search
///     button, which reads "Search, N rounds" from the live count.
///     <para>
///         <b>The live count</b> (round-index.md §3.11) is <see cref="ISituationIndex.Count" /> over the
///         draft as it stands, run through <see cref="SituationLiveCount" /> off the UI thread on every
///         token, map, filter or index change, debounced, the newest request cancelling the pending one.
///         It is the same code path as the search with the materialisation skipped, so the number on
///         the button is the number of cards the search will show. The coverage beside it says how many
///         of the library's demos the count ran over, so a small number is never mistaken for a rare
///         situation.
///     </para>
///     <para>
///         The rail arms a slot; the canvas tool places it on the next press over the map, and the
///         drop resolves to a place through <see cref="IQueryPlaceResolver" />. The rail then shows the
///         place the slot resolved to, or says it resolved nothing, and the token can be moved or lifted
///         on the map or lifted from the rail. Every rail row is a projection of the document; the
///         document is the only state.
///     </para>
///     <para>
///         The maps on offer are the ones the library has rows for: a canvas over a map with no indexed
///         demo would snap to nothing. On the browser host there is no library index and no bundle
///         directory, so the picker is empty and the canvas says why.
///     </para>
///     <para>
///         <b>The tolerance slider</b> (round-index.md §3.7, §3.8) runs from exact to any place. With an
///         adjacency graph for the map it has four stops; without one the middle two would only repeat
///         Exact (the index collapses them), so it shows the two that differ, the plan's degraded form.
///         Every move re-asks the live count, and the count never falls as the slider loosens because
///         each stop's match implies the next one's. The graph's source is named beside the slider, so
///         a user can tell the zone graph from the one the index folded from its own transitions.
///     </para>
/// </summary>
public sealed partial class QueryCanvasViewModel : ViewModelBase, IDisposable
{
    private readonly SituationLiveCount _counter;
    private readonly DemoCacheStore _demoCache;
    private readonly ISituationIndex _index;
    private readonly Func<string, LoadedMapAsset?> _loadMapAsset;
    private readonly Action<Action> _retire;

    private static readonly SituationTolerance[] _allStops =
    [
        SituationTolerance.Exact, SituationTolerance.Adjacent, SituationTolerance.TwoHops, SituationTolerance.AnyPlace
    ];

    private static readonly SituationTolerance[] _degradedStops = [SituationTolerance.Exact, SituationTolerance.AnyPlace];

    private IPlaceAdjacency? _adjacency;
    private bool _disposed;

    /// <summary>The rail slot armed for the next press over the map; null when none.</summary>
    [ObservableProperty]
    private QueryRailSlotViewModel? _armedSlot;

    /// <summary>True while a count is pending or running; the button reads plain "Search" meanwhile.</summary>
    [ObservableProperty]
    private bool _isCounting;

    /// <summary>The count of the draft as it stands, or null while none is known (no map, index loading, a count in flight).</summary>
    [ObservableProperty]
    private int? _liveCount;

    /// <summary>The map the canvas shows and the query runs over; null before one is picked.</summary>
    [ObservableProperty]
    private string? _map;

    /// <summary>The map's bundle, or null when this host has none for it (the canvas then has no pane).</summary>
    [ObservableProperty]
    private LoadedMapAsset? _mapAsset;

    /// <summary>The last search's hit count, or null before a search or after the query changed.</summary>
    [ObservableProperty]
    private int? _resultCount;

    /// <summary>"12 rounds over 240 indexed demos", or what stopped the search.</summary>
    [ObservableProperty]
    private string _resultLine = "";

    /// <summary>What the rail says about the canvas: what to do next, or what the last drop resolved to.</summary>
    [ObservableProperty]
    private string _hintLine = "";

    /// <param name="index">The index the count runs against, and the place centroids the snap reads.</param>
    /// <param name="resolver">Turns a drop into a place.</param>
    /// <param name="demoCache">The index rows, for the map list.</param>
    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader in the app, a stub in a test.</param>
    /// <param name="retire">
    ///     Runs a replaced bundle's dispose after the host has rebound: a Background-priority dispatcher
    ///     post in the app, inline in a test.
    /// </param>
    /// <param name="filters">The filter rail; one over the cache alone, with no team or source fields, when null.</param>
    /// <param name="post">Marshals the live count's answer onto the UI thread; a dispatcher post in the app, inline in a test.</param>
    /// <param name="countDelay">The live count's debounce; <see cref="SituationLiveCount.DefaultDelay" /> when null, zero in a test.</param>
    public QueryCanvasViewModel(
        ISituationIndex index,
        IQueryPlaceResolver resolver,
        DemoCacheStore demoCache,
        Func<string, LoadedMapAsset?>? loadMapAsset = null,
        Action<Action>? retire = null,
        SearchFiltersViewModel? filters = null,
        Action<Action>? post = null,
        TimeSpan? countDelay = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(demoCache);
        _index = index;
        _demoCache = demoCache;
        _loadMapAsset = loadMapAsset ?? (map => MapAssetPipeline.TryLoad(map));
        _retire = retire ?? (dispose => Dispatcher.UIThread.Post(dispose, DispatcherPriority.Background));
        _counter = new SituationLiveCount(index, post ?? (action => Dispatcher.UIThread.Post(action)), countDelay);
        _counter.Counted += OnCounted;

        Document = new QueryCanvasDocument();
        Draft = new SituationQueryDraft(Document);
        Tool = new QueryTokenTool(Document, resolver);
        Filters = filters ?? new SearchFiltersViewModel(demoCache);
        Filters.Changed += OnFiltersChanged;

        for (int slot = 0; slot < QueryCanvasDocument.SlotsPerSide; slot++)
        {
            Slots.Add(new QueryRailSlotViewModel(this, QuerySide.Ct, slot));
        }

        for (int slot = 0; slot < QueryCanvasDocument.SlotsPerSide; slot++)
        {
            Slots.Add(new QueryRailSlotViewModel(this, QuerySide.T, slot));
        }

        Document.Changed += OnDocumentChanged;
        Tool.Dropped += OnDropped;
        _demoCache.Changed += OnCacheChanged;
        _index.Changed += OnIndexChanged;
        RefreshMaps();
        RefreshRail();
        RefreshAdjacency();
        HintLine = DefaultHint();
        RequestCount();
    }

    /// <summary>The ten slots. Shared with the layer and the tool.</summary>
    public QueryCanvasDocument Document { get; }

    /// <summary>
    ///     The Overlay View's points, drawn over the canvas by the heatmap layer the host mounts. The
    ///     Result Cards fill it from the hits' positions files; it empties with the map, since a
    ///     position on one map means nothing on another.
    /// </summary>
    public OverlayDocument Overlay { get; } = new();

    /// <summary>The query object model over the document.</summary>
    public SituationQueryDraft Draft { get; }

    /// <summary>The filter rail; its outputs are written into <see cref="Draft" /> on every change.</summary>
    public SearchFiltersViewModel Filters { get; }

    /// <summary>The live counter, for a test to await its pending run.</summary>
    internal SituationLiveCount Counter => _counter;

    /// <summary>"Search, 12 rounds" once the count is known; "Search" while it is not.</summary>
    public string SearchLabel => LiveCount is int count ? $"Search, {Rounds(count)}" : "Search";

    /// <summary>"over 240 of 277 demos" beside the count, "counting" while one is in flight, empty with no map.</summary>
    public string CoverageLine => LiveCount is not null
        ? $"over {_index.IndexedDemoCount} of {_demoCache.Index.Count} demos"
        : IsCounting ? "counting" : "";

    /// <summary>
    ///     How loosely the pairs match: the draft's tolerance, which the slider drives. Setting it re-asks
    ///     the live count and drops the last search's line, as a token move does.
    /// </summary>
    public SituationTolerance Tolerance
    {
        get => Draft.Tolerance;
        set
        {
            if (Draft.Tolerance == value)
            {
                return;
            }

            Draft.Tolerance = value;
            OnToleranceChanged();
            ResultCount = null;
            ResultLine = "";
            RequestCount();
        }
    }

    /// <summary>The slider's stops, exact first: all four with an adjacency graph, exact and any place without.</summary>
    public IReadOnlyList<SituationTolerance> ToleranceStops => _adjacency is null ? _degradedStops : _allStops;

    /// <summary>The slider's last stop index.</summary>
    public int ToleranceMaximum => ToleranceStops.Count - 1;

    /// <summary>
    ///     The slider's position, a stop index. A tolerance the stops do not offer (a watch saved at
    ///     Adjacent, loaded on a map with no graph) sits on Exact, which is what the index runs it as.
    /// </summary>
    public double ToleranceValue
    {
        get => Math.Max(0, IndexOfStop(Draft.Tolerance));
        set
        {
            int stop = Math.Clamp((int)Math.Round(value), 0, ToleranceMaximum);
            if (stop == IndexOfStop(Draft.Tolerance))
            {
                return;
            }

            Tolerance = ToleranceStops[stop];
        }
    }

    /// <summary>What the slider's stop means, as the user reads it.</summary>
    public string ToleranceLabel => EffectiveTolerance switch
    {
        SituationTolerance.Adjacent => "adjacent places, at least N",
        SituationTolerance.TwoHops => "two hops, at least N",
        SituationTolerance.AnyPlace => "any place, at least N alive",
        _ => "exact, exactly N"
    };

    /// <summary>"zones", "empirical", or null when the map has no graph.</summary>
    public string? AdjacencySource => _adjacency is null ? null : SourceKind(_adjacency.Source);

    /// <summary>The line beside the slider naming where "adjacent" comes from.</summary>
    public string AdjacencyLine => _adjacency is null
        ? Map is null ? "" : "no adjacency graph: exact or any place"
        : $"adjacency: {AdjacencySource}";

    /// <summary>The slider's tooltip: the stops' semantics and the graph's full source.</summary>
    public string ToleranceTip => _adjacency is null
        ? "Exact matches exactly N in each place; any place matches when the side has at least that many alive. "
          + "The middle stops need an adjacency graph, and this map has none yet."
        : "Exact matches exactly N in each place; the wider stops match at least N over the place and its "
          + $"neighbours, then their neighbours, then anywhere. Adjacency: {AdjacencySource} ({_adjacency.Source}).";

    // The tolerance the index actually runs: the middle stops collapse to Exact without a graph.
    private SituationTolerance EffectiveTolerance =>
        _adjacency is null && Draft.Tolerance is SituationTolerance.Adjacent or SituationTolerance.TwoHops
            ? SituationTolerance.Exact
            : Draft.Tolerance;

    /// <summary>The canvas tool the host registers on its router.</summary>
    public QueryTokenTool Tool { get; }

    /// <summary>The rail, CT row first then T, slot ascending.</summary>
    public ObservableCollection<QueryRailSlotViewModel> Slots { get; } = [];

    /// <summary>The CT row.</summary>
    public IEnumerable<QueryRailSlotViewModel> CtSlots => Slots.Where(s => s.Side == QuerySide.Ct);

    /// <summary>The T row.</summary>
    public IEnumerable<QueryRailSlotViewModel> TSlots => Slots.Where(s => s.Side == QuerySide.T);

    /// <summary>The maps the library has index rows for, sorted.</summary>
    public ObservableCollection<string> Maps { get; } = [];

    /// <summary>The map picker has something to pick.</summary>
    public bool HasMaps => Maps.Count > 0;

    /// <summary>A map is picked and this host has its bundle: the canvas has panes to drop on.</summary>
    public bool HasCanvas => Map is not null && MapAsset is not null;

    /// <summary>A map is picked and this host has no bundle for it: the canvas says so.</summary>
    public bool IsMissingBundle => Map is not null && MapAsset is null;

    /// <summary>The line beside a paneless canvas.</summary>
    public string MissingBundleNote =>
        Map is null ? "" : $"no map art for {Map} on this host, so there is nothing to drop a token on";

    /// <summary>The CT token as the index stores it: what a search matches against.</summary>
    public string CtToken => Draft.TokenFor(QuerySide.Ct);

    /// <summary>The T token as the index stores it.</summary>
    public string TToken => Draft.TokenFor(QuerySide.T);

    /// <summary>A search has something to run over.</summary>
    public bool CanSearch => Map is not null && _index.IsReady;

    /// <summary>Raised when the map or its bundle changes; the host rebinds on it.</summary>
    public event Action? MapChanged;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _counter.Counted -= OnCounted;
        _counter.Dispose();
        Filters.Changed -= OnFiltersChanged;
        Filters.Dispose();
        Document.Changed -= OnDocumentChanged;
        Tool.Dropped -= OnDropped;
        _demoCache.Changed -= OnCacheChanged;
        _index.Changed -= OnIndexChanged;
        if (MapAsset is { } asset)
        {
            MapAsset = null;
            _retire(asset.Dispose);
        }
    }

    /// <summary>Arms a slot for the next press over the map, or disarms it when it was the armed one.</summary>
    /// <param name="slot">The rail slot.</param>
    public void Arm(QueryRailSlotViewModel slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (ReferenceEquals(ArmedSlot, slot))
        {
            Disarm();
            return;
        }

        ArmedSlot = slot;
        Tool.Armed = (slot.Side, slot.Slot);
        HintLine = $"click the map to place {slot.Label}";
        RefreshRail();
    }

    /// <summary>Clears the armed slot.</summary>
    public void Disarm()
    {
        ArmedSlot = null;
        Tool.Armed = null;
        HintLine = DefaultHint();
        RefreshRail();
    }

    /// <summary>Lifts a slot's token off the map.</summary>
    /// <param name="slot">The rail slot.</param>
    public void Lift(QueryRailSlotViewModel slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (ReferenceEquals(ArmedSlot, slot))
        {
            Disarm();
        }

        Document.Lift(slot.Side, slot.Slot);
    }

    /// <summary>
    ///     Find Rounds Like This: replaces the rail with the snapshot's alive players, one token per
    ///     player at the spot they stood, on the floor that Z falls in, carrying the place the snapshot
    ///     minted. A player whose place is unknown is still put on the map, hollow, so the man-count is
    ///     visible; the query leaves it out, as it leaves out any unresolved drop.
    ///     <para>
    ///         The rail holds five per side. A side with more alive players than that (a custom game)
    ///         keeps its first five and the hint says so, rather than failing the whole snapshot.
    ///     </para>
    /// </summary>
    /// <param name="snapshot">The 2D scene's current tick, cut to alive players per side.</param>
    public void LoadSnapshot(SituationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Disarm();

        // Listed before it is picked: the ComboBox coerces a selection it cannot find to null, which
        // would write null back into Map and clear the document under the tokens about to land.
        EnsureListed(snapshot.Map);
        Map = snapshot.Map;

        // Setting the map only clears the document when the map changed; a snapshot over the map
        // already picked replaces the rail just the same.
        Document.Clear();

        // The token's floor key is the pane's band floor, the way a drop stores it. The bands are the
        // bundle's, the same list the host arranges panes from, so a token keyed here lands on the
        // pane its player was on. With no bundle there is no pane to miss, and Z itself is the key.
        MapSpaceFactory levels = new();
        levels.SetAuthoritativeFloors(MapAsset?.Floors);
        levels.Update(Scene2DFrame.Empty);
        MapSpace space = levels.Space;

        int placed = 0;
        int dropped = 0;
        foreach (QuerySide side in new[] { QuerySide.Ct, QuerySide.T })
        {
            IReadOnlyList<SituationSnapshotPlayer> players = snapshot.Players(side);
            for (int i = 0; i < players.Count; i++)
            {
                if (i >= QueryCanvasDocument.SlotsPerSide)
                {
                    dropped++;
                    continue;
                }

                SituationSnapshotPlayer player = players[i];
                double floor = space.LevelFor(player.WorldZ)?.ZMin ?? player.WorldZ;
                Document.Place(new QueryToken(side, i, player.WorldX, player.WorldY, MapSpace.QuantizeZ(floor),
                    player.Place));
                placed++;
            }
        }

        string overflow = dropped > 0 ? $"; {dropped} more than the rail holds, left off" : "";
        HintLine = $"tick {snapshot.Time.Tick} from 2D playback: {snapshot.Ct.Count} CT, {snapshot.T.Count} T, "
                   + $"{placed} placed{overflow}";
    }

    /// <summary>
    ///     Re-run (Watched Situations): puts a saved query back on the canvas as it was drawn, the same
    ///     tokens in the same slots at the same spots, the tolerance, and the rail's values through
    ///     <see cref="SearchFiltersViewModel.Apply" />. The hint names the watch; the caller searches.
    /// </summary>
    /// <param name="watch">The saved query.</param>
    public void Load(WatchedSituation watch)
    {
        ArgumentNullException.ThrowIfNull(watch);

        Disarm();

        // Listed before it is picked, for the reason LoadSnapshot gives: an unlisted selection is
        // coerced to null and would clear the document under the tokens.
        EnsureListed(watch.Map);
        Map = watch.Map;
        Document.Clear();
        foreach (WatchedToken token in watch.Tokens)
        {
            Document.Place(token.ToToken());
        }

        Draft.Tolerance = watch.Tolerance;
        OnToleranceChanged();
        Filters.Apply(watch.Filters);
        HintLine = $"watched situation loaded: {watch.Name}";
        RequestCount();
    }

    /// <summary>The last search's hits, in the index's order; the Result Cards load from here.</summary>
    public event Action<IReadOnlyList<SituationHit>>? Searched;

    /// <summary>
    ///     Runs the query against the index, shows the count and hands the hits to <see cref="Searched" />.
    ///     Synchronous: the query is microseconds, and the count is the hit list's length by construction.
    ///     An empty set is stated, never a blank page: the line says nothing matched and what to loosen.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void Search()
    {
        if (Map is null)
        {
            return;
        }

        IReadOnlyList<SituationHit> hits = _index.Query(Draft.ToQuery());
        int count = hits.Count;
        ResultCount = count;
        string scope = Draft.IsEmpty ? " (nothing placed: every indexed round)" : "";
        ResultLine = count == 0
            ? $"no rounds match over {_index.IndexedDemoCount} indexed demos{scope}; {LoosenHint()}"
            : $"{Rounds(count)} over {_index.IndexedDemoCount} indexed demos{scope}";
        Searched?.Invoke(hits);
    }

    private static string Rounds(int count) => count == 1 ? "1 round" : $"{count} rounds";

    // The seam's source string is "zones:<version>" or "index:<demoCount>" (round-index.md §3.8); the
    // slider names the kind, and the tooltip carries the rest.
    private static string SourceKind(string source) =>
        source.StartsWith("zones:", StringComparison.Ordinal) ? "zones" : "empirical";

    private int IndexOfStop(SituationTolerance tolerance)
    {
        IReadOnlyList<SituationTolerance> stops = ToleranceStops;
        for (int i = 0; i < stops.Count; i++)
        {
            if (stops[i] == tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnToleranceChanged()
    {
        OnPropertyChanged(nameof(Tolerance));
        OnPropertyChanged(nameof(ToleranceValue));
        OnPropertyChanged(nameof(ToleranceLabel));
    }

    // Re-asks the index for the map's graph: the map changed, or a merge moved the empirical one (a
    // first demo creates it) or the zones arrived. The draft's tolerance is left alone, so a watch
    // saved at Adjacent keeps its meaning when it is saved again; the slider shows the stop it runs as.
    private void RefreshAdjacency()
    {
        _adjacency = Map is null ? null : _index.Adjacency(Map);
        OnPropertyChanged(nameof(ToleranceStops));
        OnPropertyChanged(nameof(ToleranceMaximum));
        OnPropertyChanged(nameof(AdjacencySource));
        OnPropertyChanged(nameof(AdjacencyLine));
        OnPropertyChanged(nameof(ToleranceTip));
        OnToleranceChanged();
    }

    // What an empty set can be loosened by, in the order a user would try: a filter first, since it is
    // the cheaper change, then a token.
    private string LoosenHint() => Filters.IsActive
        ? Draft.IsEmpty ? "clear a filter" : "clear a filter or lift a token"
        : Draft.IsEmpty ? "the map has no indexed rounds under this query" : "lift a token";

    /// <summary>
    ///     Asks for a fresh count of the draft, or drops the pending one when there is nothing to count.
    ///     The old number goes at once: a count beside a query it does not describe reads as its answer.
    /// </summary>
    private void RequestCount()
    {
        LiveCount = null;
        if (Map is null || !_index.IsReady)
        {
            _counter.Cancel();
            IsCounting = false;
            return;
        }

        IsCounting = true;
        _counter.Request(Draft.ToQuery());
    }

    // The newest request's answer, on the post thread; older ones never reach here.
    private void OnCounted(SituationQuery query, int count)
    {
        if (_disposed)
        {
            return;
        }

        IsCounting = false;
        LiveCount = count;
    }

    partial void OnLiveCountChanged(int? value)
    {
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(CoverageLine));
    }

    partial void OnIsCountingChanged(bool value) => OnPropertyChanged(nameof(CoverageLine));

    // The rail's outputs live on the draft, the one object the search and the count both read.
    private void OnFiltersChanged()
    {
        Draft.Facts = Filters.ToFacts();
        Draft.Demos = Filters.ToDemos();
        ResultCount = null;
        ResultLine = "";
        RequestCount();
    }

    /// <summary>Empties every slot.</summary>
    [RelayCommand]
    private void ClearTokens()
    {
        Disarm();
        Document.Clear();
    }

    partial void OnMapChanged(string? value)
    {
        Disarm();
        Document.MapName = value ?? "";
        Overlay.Clear();

        // The old bundle is retired one dispatcher hop later, not here: the host rebinds on MapChanged
        // below, and the render thread may still be replaying a picture that references the old radar
        // images. The playback tab's ReplaceMapAsset takes the same hop for the same reason.
        LoadedMapAsset? previous = MapAsset;
        MapAsset = value is null ? null : _loadMapAsset(value);
        if (previous is not null)
        {
            _retire(previous.Dispose);
        }

        ResultCount = null;
        ResultLine = "";
        OnPropertyChanged(nameof(HasCanvas));
        OnPropertyChanged(nameof(IsMissingBundle));
        OnPropertyChanged(nameof(MissingBundleNote));
        SearchCommand.NotifyCanExecuteChanged();
        RefreshAdjacency();
        MapChanged?.Invoke();
        RequestCount();
    }

    private void OnDocumentChanged()
    {
        // The last count described a different query; a stale number beside a new query reads as an
        // answer to it.
        ResultCount = null;
        ResultLine = "";

        // The press consumed the armed slot; the rail's highlight follows the tool.
        if (Tool.Armed is null && ArmedSlot is not null)
        {
            ArmedSlot = null;
            HintLine = "drop it on the map";
        }

        RefreshRail();
        OnPropertyChanged(nameof(CtToken));
        OnPropertyChanged(nameof(TToken));
        RequestCount();
    }

    // The release's answer, after the document already carries the placed token.
    private void OnDropped(QueryPlaceHit? hit) => HintLine = hit is not null
        ? $"resolved to {hit.Place} ({hit.Source})"
        : "no place near that drop; the token is on the map but not in the query";

    // A row that arrived or left moves the coverage and, with a demo-level filter set, the demo set;
    // the count is re-asked either way, debounced, so a library scan does not run one per row.
    private void OnCacheChanged(string? changedPath)
    {
        RefreshMaps();
        if (Filters.HasDemoFilter)
        {
            Draft.Demos = Filters.ToDemos();
        }

        RequestCount();
    }

    private void OnIndexChanged()
    {
        RefreshMaps();
        RefreshAdjacency();
        SearchCommand.NotifyCanExecuteChanged();
        RequestCount();
    }

    // A snapshot can arrive over a demo the library has not indexed yet (the tab's own open, before
    // the evaluator reaches it). The picked map stays listed so the next cache refresh does not pull
    // the selection out from under the tokens.
    private void EnsureListed(string map)
    {
        if (Maps.Contains(map, StringComparer.Ordinal))
        {
            return;
        }

        int at = 0;
        while (at < Maps.Count && StringComparer.OrdinalIgnoreCase.Compare(Maps[at], map) < 0)
        {
            at++;
        }

        Maps.Insert(at, map);
        OnPropertyChanged(nameof(HasMaps));
    }

    private void RefreshMaps()
    {
        List<string> maps =
        [
            .. _demoCache.Index.Select(r => r.Map)
                .OfType<string>()
                .Where(m => m.Length > 0)
                .Concat(Map is { } picked ? [picked] : [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
        ];

        if (maps.SequenceEqual(Maps, StringComparer.Ordinal))
        {
            return;
        }

        Maps.Clear();
        foreach (string map in maps)
        {
            Maps.Add(map);
        }

        OnPropertyChanged(nameof(HasMaps));
        if (Map is null && maps.Count > 0)
        {
            Map = maps[0];
        }
    }

    private void RefreshRail()
    {
        foreach (QueryRailSlotViewModel slot in Slots)
        {
            slot.Refresh();
        }
    }

    private string DefaultHint() => Document.PlacedCount == 0
        ? "arm a slot on the rail, then click the map; drag a token to move it, right-click to lift it"
        : "drag a token to move it, right-click to lift it; empty map drags pan";
}

/// <summary>One rail slot: which side and slot, whether it is on the map, where, and whether it is armed.</summary>
public sealed partial class QueryRailSlotViewModel : ViewModelBase
{
    private readonly QueryCanvasViewModel _owner;

    /// <summary>True while this slot is the one the next press places.</summary>
    [ObservableProperty]
    private bool _isArmed;

    /// <summary>True while the slot's token is on the map, resolved or not.</summary>
    [ObservableProperty]
    private bool _isPlaced;

    /// <summary>The place the token resolved to; null off the map or unresolved.</summary>
    [ObservableProperty]
    private string? _place;

    /// <param name="owner">The canvas.</param>
    /// <param name="side">The rail row.</param>
    /// <param name="slot">The slot within the row.</param>
    public QueryRailSlotViewModel(QueryCanvasViewModel owner, QuerySide side, int slot)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        Side = side;
        Slot = slot;
    }

    /// <summary>The rail row.</summary>
    public QuerySide Side { get; }

    /// <summary>The slot within the row.</summary>
    public int Slot { get; }

    /// <summary>"CT 1" to "CT 5", "T 1" to "T 5".</summary>
    public string Label => $"{(Side == QuerySide.Ct ? "CT" : "T")} {Slot + 1}";

    /// <summary>The place, or what the slot is doing instead.</summary>
    public string Status => Place ?? (IsPlaced ? "no place" : IsArmed ? "click the map" : "");

    /// <summary>Arms or disarms this slot.</summary>
    [RelayCommand]
    private void Arm() => _owner.Arm(this);

    /// <summary>Takes this slot's token off the map.</summary>
    [RelayCommand(CanExecute = nameof(IsPlaced))]
    private void Lift() => _owner.Lift(this);

    /// <summary>Re-reads the document and the armed state.</summary>
    internal void Refresh()
    {
        QueryToken? token = _owner.Document.Get(Side, Slot);
        IsPlaced = token is not null;
        Place = token?.Place;
        IsArmed = ReferenceEquals(_owner.ArmedSlot, this);
        OnPropertyChanged(nameof(Status));
        LiftCommand.NotifyCanExecuteChanged();
    }
}

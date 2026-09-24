#region

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>
///     The Query Canvas: the map picker, the ten-slot token rail (five CT, five T), the query draft the
///     rail and the canvas both edit, and the one Search button, wired to
///     <see cref="ISituationIndex.Count" /> and a plain result count.
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
/// </summary>
public sealed partial class QueryCanvasViewModel : ViewModelBase, IDisposable
{
    private readonly DemoCacheStore _demoCache;
    private readonly ISituationIndex _index;
    private readonly Func<string, LoadedMapAsset?> _loadMapAsset;
    private readonly Action<Action> _retire;

    private bool _disposed;

    /// <summary>The rail slot armed for the next press over the map; null when none.</summary>
    [ObservableProperty]
    private QueryRailSlotViewModel? _armedSlot;

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
    public QueryCanvasViewModel(
        ISituationIndex index,
        IQueryPlaceResolver resolver,
        DemoCacheStore demoCache,
        Func<string, LoadedMapAsset?>? loadMapAsset = null,
        Action<Action>? retire = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(demoCache);
        _index = index;
        _demoCache = demoCache;
        _loadMapAsset = loadMapAsset ?? (map => MapAssetPipeline.TryLoad(map));
        _retire = retire ?? (dispose => Dispatcher.UIThread.Post(dispose, DispatcherPriority.Background));

        Document = new QueryCanvasDocument();
        Draft = new SituationQueryDraft(Document);
        Tool = new QueryTokenTool(Document, resolver);

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
        HintLine = DefaultHint();
    }

    /// <summary>The ten slots. Shared with the layer and the tool.</summary>
    public QueryCanvasDocument Document { get; }

    /// <summary>The query object model over the document.</summary>
    public SituationQueryDraft Draft { get; }

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

    /// <summary>Runs the query against the index and shows the count. Synchronous: the count is microseconds.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void Search()
    {
        if (Map is null)
        {
            return;
        }

        int count = _index.Count(Draft.ToQuery());
        ResultCount = count;
        string rounds = count == 1 ? "1 round" : $"{count} rounds";
        string scope = Draft.IsEmpty ? " (nothing placed: every indexed round)" : "";
        ResultLine = $"{rounds} over {_index.IndexedDemoCount} indexed demos{scope}";
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
        MapChanged?.Invoke();
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
    }

    // The release's answer, after the document already carries the placed token.
    private void OnDropped(QueryPlaceHit? hit) => HintLine = hit is not null
        ? $"resolved to {hit.Place} ({hit.Source})"
        : "no place near that drop; the token is on the map but not in the query";

    private void OnCacheChanged(string? changedPath) => RefreshMaps();

    private void OnIndexChanged()
    {
        RefreshMaps();
        SearchCommand.NotifyCanExecuteChanged();
    }

    private void RefreshMaps()
    {
        List<string> maps =
        [
            .. _demoCache.Index.Select(r => r.Map)
                .OfType<string>()
                .Where(m => m.Length > 0)
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

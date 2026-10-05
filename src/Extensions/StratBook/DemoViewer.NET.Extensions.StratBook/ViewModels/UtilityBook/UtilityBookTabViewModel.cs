#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core.Utility;
using GrenadeKind = DemoViewer.NET.Modules.UtilityBook.GrenadeKind;
using GrenadeTrailPoint = DemoViewer.NET.Playback2D.Core.GrenadeTrailPoint;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.ViewModels.UtilityBook;

/// <summary>A picker entry: the label shown and the value it stands for, null for "any".</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed record UtilityBookOption<T>(string Label, T? Value) where T : struct
{
    public override string ToString() => Label;
}

/// <summary>
///     The Utility Book: every grenade of the chosen map drawn where it goes off, grouped into landing
///     groups on the radar. Clicking a group shows the positions it was thrown from with a flight from each;
///     clicking a position opens its details (how often and in how many demos, the console line to copy, and
///     every throw from there, each one openable in 2D Playback).
///     <para>
///         By default only lineups seen twice or more are shown: one throw from a spot is a throw, two are a
///         lineup. The toggle brings the single throws back, and the map says how many it is hiding.
///     </para>
/// </summary>
public sealed partial class UtilityBookTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>Frame-clock ticks a watch starts before the release: two seconds at 64 ticks.</summary>
    public const int WatchLeadTicks = 128;

    /// <summary>The place picker's "no filter" entry.</summary>
    public const string AnyPlace = "any place";

    /// <summary>A lineup is a position thrown from at least this many times.</summary>
    public const int LineupMinThrows = 2;

    /// <summary>The console line shown when the throw's release angles were not read.</summary>
    public const string NoConsoleText = "release state unavailable";

    /// <summary>The line the panel shows on the browser host.</summary>
    public const string BrowserNote =
        "In the browser, grenades are indexed for the open demo only and this tab forgets them when it reloads.";

    private readonly Func<string, DateTime?> _demoDate;
    private readonly GrenadeIndex _index;
    private readonly Func<string, LoadedMapAsset?> _loadMapAsset;
    private readonly Action<Action> _retire;
    private readonly ISituationPlayback? _playback;
    private readonly Dictionary<string, LandingGroup> _groups = new(StringComparer.Ordinal);
    private readonly Action<Action> _background;
    private readonly Action<Action> _post;
    private bool _disposed;
    private bool _refreshing;
    private bool _shown = true;
    private int _refreshVersion;

    /// <summary>True when the card opens over the map's left edge, because the selected position is on the right.</summary>
    [ObservableProperty]
    private bool _cardOnLeft;

    [ObservableProperty]
    private LineupDetail? _detail;

    [ObservableProperty]
    private string _focusLine = "";

    [ObservableProperty]
    private string _footerLine = "";

    [ObservableProperty]
    private string _hiddenLine = "";

    [ObservableProperty]
    private UtilityBookOption<GrenadeKind>? _selectedKind;

    [ObservableProperty]
    private string? _selectedMap;

    [ObservableProperty]
    private string? _selectedPlace = AnyPlace;

    [ObservableProperty]
    private UtilityBookOption<int>? _selectedSide;

    [ObservableProperty]
    private bool _showSingleThrows;

    [ObservableProperty]
    private string _statusLine = "";

    private readonly string? _clipDirectory;
    private readonly string? _lockedMap;
    private readonly bool _ownsMapAsset;
    private (Guid Id, string? Technique)? _pendingReveal;
    private IReadOnlySet<GrenadeKind>? _queryKinds;
    private string? _focusedId;
    private string? _selectedLineupId;

    /// <param name="index">The grenade index.</param>
    /// <param name="playback">Opens a throw in 2D Playback; null when the host has no shell.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader when null, a stub in a test.</param>
    /// <param name="demoDate">A demo's date for the instance list; none shown when null.</param>
    /// <param name="retire">
    ///     Runs a replaced bundle's dispose after the host has rebound: a Background-priority dispatcher
    ///     post in the app, inline in a test.
    /// </param>
    /// <param name="clipDirectory">Where Lineup Clip Render writes its pairs; the card shows no clip when null.</param>
    /// <param name="background">Runs a refresh's index reads: the thread pool in the app, inline in a test.</param>
    /// <param name="post">Brings a refresh's result back: the UI dispatcher in the app, inline in a test.</param>
    /// <param name="lockedMap">
    ///     Shows this map and no other, even when it has no grenades: the Strat Book's lineup picker. Null for the
    ///     tab, which falls back to the first indexed map.
    /// </param>
    /// <param name="ownsMapAsset">
    ///     False when <paramref name="loadMapAsset" /> hands out a bundle another owner holds (the picker shares
    ///     the strat canvas's): it is then never disposed here.
    /// </param>
    public UtilityBookTabViewModel(GrenadeIndex index, ISituationPlayback? playback = null, bool? isBrowser = null,
        Func<string, LoadedMapAsset?>? loadMapAsset = null, Func<string, DateTime?>? demoDate = null,
        Action<Action>? retire = null, string? clipDirectory = null, Action<Action>? background = null,
        Action<Action>? post = null, string? lockedMap = null, bool ownsMapAsset = true)
    {
        _ownsMapAsset = ownsMapAsset;
        _lockedMap = lockedMap;
        _selectedMap = lockedMap;
        _background = background ?? (work => work());
        _post = post ?? (work => work());
        _clipDirectory = clipDirectory;
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _playback = playback;
        _loadMapAsset = loadMapAsset ?? (map => MapAssetPipeline.TryLoad(map));
        _retire = retire ?? (dispose => Dispatcher.UIThread.Post(dispose, DispatcherPriority.Background));
        _demoDate = demoDate ?? (_ => null);
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        Kinds =
        [
            new UtilityBookOption<GrenadeKind>("all grenades", null),
            .. Enum.GetValues<GrenadeKind>().Select(k => new UtilityBookOption<GrenadeKind>(k.ToString(), k))
        ];
        Sides =
        [
            new UtilityBookOption<int>("either side", null),
            new UtilityBookOption<int>("T", 2),
            new UtilityBookOption<int>("CT", 3)
        ];
        _selectedKind = Kinds.First(k => k.Value == GrenadeKind.Smoke);
        _selectedSide = Sides[0];
        _index.Changed += Refresh;
        Refresh();
    }

    /// <summary>True on the WASM head.</summary>
    public bool IsBrowser { get; }

    /// <summary>Whether <see cref="Dispose" /> ran. For tests of an owner that must dispose it.</summary>
    internal bool IsDisposed => _disposed;

    public ObservableCollection<string> Maps { get; } = [];

    public IReadOnlyList<UtilityBookOption<GrenadeKind>> Kinds { get; }

    public IReadOnlyList<UtilityBookOption<int>> Sides { get; }

    /// <summary><see cref="AnyPlace" /> first, then the landing places the selected map resolved to.</summary>
    public ObservableCollection<string> Places { get; } = [AnyPlace];

    /// <summary>What the map draws; the host binds its layer to it.</summary>
    public UtilityMapDocument Document { get; } = new();

    /// <summary>The selected map's baked bundle, or null when this host has none.</summary>
    public LoadedMapAsset? MapAsset { get; private set; }

    /// <summary>The map the document lies on.</summary>
    public string MapName => SelectedMap ?? "";

    /// <summary>True when the selected map has no baked bundle, so there is no radar to draw on.</summary>
    public bool HasNoMapArt => SelectedMap is not null && MapAsset is null;

    /// <summary>The landing groups on the map, most thrown first.</summary>
    public IReadOnlyList<LandingGroup> Groups => [.. _groups.Values.OrderByDescending(g => g.ThrowCount)];

    /// <summary>The focused landing group, or null.</summary>
    public LandingGroup? FocusedGroup => _focusedId is { } id ? _groups.GetValueOrDefault(id) : null;

    public bool HasFocus => FocusedGroup is not null;

    public bool HasDetail => Detail is not null;

    public bool HasGroups => _groups.Count > 0;

    /// <summary>Writes text to the clipboard; the view sets it, since the clipboard needs the visual tree.</summary>
    public Func<string, Task>? Clipboard { get; set; }

    /// <summary>
    ///     The kinds the query takes instead of <see cref="SelectedKind" />, or null to use it: the lineup picker
    ///     asks for a strat utility kind, which can be two grenade kinds (molotov and incendiary).
    /// </summary>
    public IReadOnlySet<GrenadeKind>? QueryKinds
    {
        get => _queryKinds;
        set
        {
            _queryKinds = value;
            _focusedId = null;
            _selectedLineupId = null;
            RefreshForUser();
        }
    }

    /// <summary>
    ///     Focuses the group holding the lineup <paramref name="lineupId" /> answers to (an alias id included) and
    ///     selects its position for <paramref name="technique" />, the most thrown one when null. Applied when the
    ///     next refresh lands; a lineup thrown from once turns the single throws on.
    /// </summary>
    /// <param name="lineupId">A stored lineup id.</param>
    /// <param name="technique">A technique key, or null.</param>
    public void Reveal(Guid lineupId, string? technique)
    {
        _pendingReveal = (lineupId, technique);
        RefreshForUser();
    }

    /// <summary>Raised when the bound map or its bundle changes, so the host rebinds the radar.</summary>
    public event Action? MapChanged;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        _shown = true;
        RefreshForUser();
    }

    // A click or a showing: the read goes to the front of the queue.
    private void RefreshForUser()
    {
        using (JobScope.UserAction())
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    public void OnDeactivated() => _shown = false;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _index.Changed -= Refresh;
        if (MapAsset is { } asset)
        {
            MapAsset = null;
            if (_ownsMapAsset)
            {
                _retire(asset.Dispose);
            }
        }
    }

    /// <summary>The query the pickers describe, or null with no map chosen.</summary>
    public GrenadeQuery? CurrentQuery() =>
        SelectedMap is not { Length: > 0 } map
            ? null
            : new GrenadeQuery(map,
                SelectedKind?.Value is { } kind ? new HashSet<GrenadeKind> { kind } : null,
                SelectedPlace is { } place && place != AnyPlace ? new HashSet<string>(StringComparer.Ordinal) { place } : null,
                SelectedSide?.Value);

    /// <summary>
    ///     Re-reads the maps and places and re-runs the query, keeping the focus and selection when they survive.
    ///     The index is read through the background delegate and the result applied through the post delegate;
    ///     a hidden section only notes that it is stale.
    /// </summary>
    public void Refresh()
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        if (!_shown)
        {
            return;
        }

        int version = ++_refreshVersion;
        string? map = SelectedMap;
        string? place = SelectedPlace;
        IReadOnlySet<GrenadeKind>? kinds = _queryKinds ?? (SelectedKind?.Value is { } k ? new HashSet<GrenadeKind> { k } : null);
        int? side = SelectedSide?.Value;
        _background(() =>
        {
            RefreshResult result;
            try
            {
                result = Compute(map, place, kinds, side);
            }
            catch (Exception ex) when (!JobScope.IsStop(ex))
            {
                ExceptionDispatchInfo failure = ExceptionDispatchInfo.Capture(ex);
                _post(failure.Throw);
                return;
            }

            _post(() =>
            {
                if (version == _refreshVersion && !_disposed)
                {
                    Apply(result);
                }
            });
        });
    }

    private sealed record RefreshResult(IReadOnlyList<string> Maps, string? Map, IReadOnlyList<string> Places, string Place,
        IReadOnlyList<GrenadeCluster> Clusters, bool Ready, int Demos, int Grenades);

    // Everything Refresh reads from the index. Takes the index's lock, so the app runs it off the UI thread.
    private RefreshResult Compute(string? selectedMap, string? selectedPlace, IReadOnlySet<GrenadeKind>? kinds, int? side)
    {
        IReadOnlyList<string> maps = _lockedMap is { } locked ? [locked] : _index.Maps();
        string? map = _lockedMap ?? (selectedMap is not null && maps.Contains(selectedMap) ? selectedMap : maps.Count > 0 ? maps[0] : null);
        JobScope.ThrowIfStopped();
        List<string> places = [AnyPlace, .. map is not null ? _index.LandingPlaces(map) : []];
        JobScope.ThrowIfStopped();
        string place = selectedPlace is not null && places.Contains(selectedPlace) ? selectedPlace : AnyPlace;
        IReadOnlyList<GrenadeCluster> clusters = map is not { Length: > 0 }
            ? []
            : _index.Query(new GrenadeQuery(map,
                kinds,
                place != AnyPlace ? new HashSet<string>(StringComparer.Ordinal) { place } : null,
                side));
        return new RefreshResult(maps, map, places, place, clusters, _index.IsReady, _index.DemoCount, _index.GrenadeCount);
    }

    private void Apply(RefreshResult result)
    {
        _refreshing = true;
        try
        {
            Sync(Maps, result.Maps);
            SelectedMap = result.Map;
            Sync(Places, result.Places);
            SelectedPlace = result.Place;
            RebindMap();
            // A lineup thrown from once is hidden by default; revealing one shows the singles.
            if (_pendingReveal is { } reveal && !ShowSingleThrows && NeedsSingles(result.Clusters, reveal.Id))
            {
                ShowSingleThrows = true;
            }

            BuildGroups(result.Clusters);
            if (_pendingReveal is { } pending)
            {
                _pendingReveal = null;
                ApplyReveal(pending.Id, pending.Technique);
            }

            StatusLine = StatusFor(result.Ready, result.Demos, result.Grenades, SelectedMap is not null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static bool NeedsSingles(IReadOnlyList<GrenadeCluster> clusters, Guid lineupId) =>
        Named([.. clusters.SelectMany(c => c.Lineups)], lineupId) is { } lineup && lineup.Throws.Count < LineupMinThrows;

    // The lineup whose own id this is, else one that absorbed it: LineupOriginSource.ByAnyId's precedence.
    private static GrenadeLineup? Named(IReadOnlyList<GrenadeLineup> lineups, Guid id) =>
        lineups.FirstOrDefault(l => l.Id == id) ?? lineups.FirstOrDefault(l => l.Answers(id));

    private void ApplyReveal(Guid lineupId, string? technique)
    {
        if (Named([.. _groups.Values.SelectMany(g => g.Lineups)], lineupId) is not { } lineup
            || _groups.Values.FirstOrDefault(g => g.Lineups.Contains(lineup)) is not { } group)
        {
            return;
        }

        _focusedId = group.Id;
        _selectedLineupId = GrenadeLineups.TechniqueFor(lineup, technique) is { } chosen ? PositionKey(lineup, chosen) : LineupKey(lineup);
        Project();
    }

    public void ClickLanding(string id)
    {
        _focusedId = string.Equals(_focusedId, id, StringComparison.Ordinal) ? null : id;
        _selectedLineupId = null;
        Project();
    }

    /// <summary>Focuses a group without toggling: the host's cycle through stacked icons.</summary>
    /// <param name="id">The group's id.</param>
    public void FocusLanding(string id)
    {
        _focusedId = id;
        _selectedLineupId = null;
        Project();
    }

    /// <summary>Selects a throw position without toggling: the host's cycle through stacked positions.</summary>
    /// <param name="lineupId">The lineup's id.</param>
    public void SelectThrow(string lineupId)
    {
        _selectedLineupId = lineupId;
        Project();
    }

    /// <summary>A throw position of the focused group was clicked: open its details, or close them when open.</summary>
    /// <param name="lineupId">The lineup's id.</param>
    public void ClickThrow(string lineupId)
    {
        _selectedLineupId = string.Equals(_selectedLineupId, lineupId, StringComparison.Ordinal) ? null : lineupId;
        Project();
    }

    /// <summary>Empty map clicked, or Escape: close the details, else leave the group.</summary>
    [RelayCommand]
    public void Back()
    {
        if (_selectedLineupId is not null)
        {
            _selectedLineupId = null;
        }
        else
        {
            _focusedId = null;
        }

        Project();
    }

    /// <summary>The status strip's line.</summary>
    /// <param name="ready">Whether the startup load finished.</param>
    /// <param name="demos">Demos with rows loaded.</param>
    /// <param name="grenades">Grenades across them.</param>
    /// <param name="hasMap">Whether any map has grenades.</param>
    public static string StatusFor(bool ready, int demos, int grenades, bool hasMap) =>
        !ready ? "Reading the grenade index…"
        : demos == 0 || !hasMap
            ? "No grenades indexed yet. Use Index grenades on Match Overview, or turn on background grenade indexing in Settings."
            : string.Create(CultureInfo.InvariantCulture,
                $"{grenades} {(grenades == 1 ? "grenade" : "grenades")} from {demos} {(demos == 1 ? "demo" : "demos")}");

    /// <summary>The baked icon key for a grenade kind.</summary>
    /// <param name="kind">What is thrown.</param>
    public static string IconKey(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Smoke => "equipment/smokegrenade",
        GrenadeKind.Flash => "equipment/flashbang",
        GrenadeKind.He => "equipment/hegrenade",
        GrenadeKind.Molotov => "equipment/molotov",
        GrenadeKind.Incendiary => "equipment/incgrenade",
        GrenadeKind.Decoy => "equipment/decoy",
        _ => ""
    };

    partial void OnSelectedMapChanged(string? value)
    {
        _focusedId = null;
        _selectedLineupId = null;
        RefreshForUser();
    }

    partial void OnSelectedKindChanged(UtilityBookOption<GrenadeKind>? value) => RefreshForUser();

    partial void OnSelectedPlaceChanged(string? value) => RefreshForUser();

    partial void OnSelectedSideChanged(UtilityBookOption<int>? value) => RefreshForUser();

    partial void OnShowSingleThrowsChanged(bool value) => RefreshForUser();

    partial void OnDetailChanged(LineupDetail? value) => OnPropertyChanged(nameof(HasDetail));

    // Every index change lands here, so the same map must not reload its bundle. A map with no art is
    // retried, since a bundle baked later should appear.
    private void RebindMap()
    {
        bool sameMap = string.Equals(_boundMap, SelectedMap, StringComparison.Ordinal);
        if (sameMap && (MapAsset is not null || SelectedMap is null))
        {
            return;
        }

        LoadedMapAsset? asset = SelectedMap is { } map ? _loadMapAsset(map) : null;
        if (sameMap && asset is null)
        {
            return;
        }

        LoadedMapAsset? previous = MapAsset;
        MapAsset = asset;
        _boundMap = SelectedMap;
        OnPropertyChanged(nameof(MapAsset));
        OnPropertyChanged(nameof(HasNoMapArt));
        MapChanged?.Invoke();

        // After the host rebound; the render thread may still hold a picture over the old images.
        if (_ownsMapAsset && previous is not null && !ReferenceEquals(previous, asset))
        {
            _retire(previous.Dispose);
        }
    }

    private string? _boundMap;

    // The groups the pickers and the lineup bar leave, each placed at the mean landing of the throws it
    // still shows, so filtering out single throws moves an icon onto the lineups that remain.
    private void BuildGroups(IReadOnlyList<GrenadeCluster> clusters)
    {
        _groups.Clear();
        int hiddenLineups = 0;
        int hiddenThrows = 0;
        {
            foreach (GrenadeCluster cluster in clusters)
            {
                List<GrenadeLineup> shown = [];
                foreach (GrenadeLineup lineup in cluster.Lineups)
                {
                    if (ShowSingleThrows || lineup.Throws.Count >= LineupMinThrows)
                    {
                        shown.Add(lineup);
                    }
                    else
                    {
                        hiddenLineups++;
                        hiddenThrows += lineup.Throws.Count;
                    }
                }

                if (shown.Count == 0)
                {
                    continue;
                }

                LandingGroup group = new(cluster, shown);
                _groups[group.Id] = group;
            }
        }

        if (_focusedId is not null && !_groups.ContainsKey(_focusedId))
        {
            _focusedId = null;
            _selectedLineupId = null;
        }

        HiddenLine = ShowSingleThrows || hiddenLineups == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"{hiddenLineups} {(hiddenLineups == 1 ? "position" : "positions")} thrown from only once ({hiddenThrows} {(hiddenThrows == 1 ? "throw" : "throws")}) {(hiddenLineups == 1 ? "is" : "are")} hidden");
        int throws = _groups.Values.Sum(g => g.ThrowCount);
        int lineups = _groups.Values.Sum(g => g.Lineups.Count);
        FooterLine = _groups.Count == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                  $"{_groups.Count} landing {(_groups.Count == 1 ? "spot" : "spots")}, {lineups} {(lineups == 1 ? "lineup" : "lineups")}, {throws} {(throws == 1 ? "throw" : "throws")}. ")
              + "Click an icon to see where it is thrown from; click a position for its details.";
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(Groups));
        Project();
    }

    // Everything the focus and selection decide: the document the map draws, the focus line and the card.
    private void Project()
    {
        LandingGroup? focused = FocusedGroup;
        ThrowPosition? selected = focused is null
            ? null
            : Positions(focused).FirstOrDefault(p => string.Equals(p.Key, _selectedLineupId, StringComparison.Ordinal));
        if (selected is null)
        {
            _selectedLineupId = null;
        }

        // Biggest last, so it draws on top and wins the hit test where icons overlap.
        List<UtilityLanding> landings =
        [
            .. _groups.Values.OrderBy(g => g.ThrowCount).ThenBy(g => g.Id, StringComparer.Ordinal)
                .Select(g => new UtilityLanding(g.Id, g.Landing.X, g.Landing.Y, g.Landing.Z, IconKey(g.Kind),
                    g.Kind.ToString()[0], g.ThrowCount, ReferenceEquals(g, focused)))
        ];
        List<UtilityThrow> throws = focused is null
            ? []
            :
            [
                .. Positions(focused).OrderBy(p => p.Throws.Count)
                    .Select(p => new UtilityThrow(p.Key, p.Origin.X, p.Origin.Y, p.Origin.Z, TeamOf(p.Throws), p.JumpThrow,
                        p.Key == selected?.Key, Flight(p, focused.Landing)))
            ];
        Document.Set(landings, throws);

        FocusLine = focused is null
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"{focused.Title}: {focused.ThrowCount} {(focused.ThrowCount == 1 ? "throw" : "throws")} from {focused.Lineups.Count} {(focused.Lineups.Count == 1 ? "lineup" : "lineups")}. Click a position for its details; Escape steps back.");
        Detail = selected is null
            ? null
            : new LineupDetail(focused!, selected.Lineup, _demoDate, WatchAsync, CopyAsync, selected.Technique, _clipDirectory);
        OnPropertyChanged(nameof(FocusedGroup));
        OnPropertyChanged(nameof(HasFocus));
    }

    /// <summary>The key the lineup's most thrown position is clicked by.</summary>
    /// <param name="lineup">The lineup.</param>
    public static string LineupKey(GrenadeLineup lineup) =>
        lineup.Techniques.Count > 0 ? PositionKey(lineup, lineup.Techniques[0]) : lineup.Id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The key one technique of a lineup is clicked by: the lineup's stable id and the technique.</summary>
    public static string PositionKey(GrenadeLineup lineup, LineupTechnique technique) =>
        lineup.Id.ToString("N", CultureInfo.InvariantCulture) + "/" + technique.Key;

    /// <summary>Every position a landing group draws: one per technique of each lineup.</summary>
    public static IEnumerable<ThrowPosition> Positions(LandingGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        foreach (GrenadeLineup lineup in group.Lineups)
        {
            if (lineup.Techniques.Count == 0)
            {
                yield return new ThrowPosition(LineupKey(lineup), lineup, null, lineup.Origin, lineup.JumpThrow, lineup.Throws,
                    lineup.Representative);
                continue;
            }

            foreach (LineupTechnique technique in lineup.Techniques)
            {
                yield return new ThrowPosition(PositionKey(lineup, technique), lineup, technique, technique.Origin, technique.JumpThrow,
                    technique.Throws, technique.Representative);
            }
        }
    }

    private static int TeamOf(IReadOnlyList<IndexedGrenade> throws) =>
        throws.GroupBy(t => t.Row.ThrowerTeam).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

    // The representative throw's recorded flight, else the stored one, else a straight line to the group's landing.
    private IReadOnlyList<GrenadeTrailPoint> Flight(ThrowPosition position, WorldPoint landing)
    {
        if ((position.Representative.Row.Trajectory is { Count: >= 2 } own ? own : _index.PathFor(position.Lineup, position.Technique))
            is { Count: >= 2 } path)
        {
            return [.. path.Select(p => new GrenadeTrailPoint(p.X, p.Y, p.Z))];
        }

        return [new GrenadeTrailPoint(position.Origin.X, position.Origin.Y, position.Origin.Z), new GrenadeTrailPoint(landing.X, landing.Y, landing.Z)];
    }

    private async Task WatchAsync(IndexedGrenade grenade)
    {
        if (_playback is null)
        {
            return;
        }

        await _playback.SeekAsync(grenade.Demo.Path, Math.Max(0, grenade.Row.ReleaseTick - WatchLeadTicks));
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            try
            {
                await clipboard(text);
            }
            catch (Exception)
            {
                // Clipboard writes are gated on some hosts; the line stays selectable in the card.
            }
        }
    }

    // Replaces a collection's contents only when they differ, so a bound picker keeps its selection.
    private static void Sync(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        if (target.SequenceEqual(values, StringComparer.Ordinal))
        {
            return;
        }

        target.Clear();
        foreach (string value in values)
        {
            target.Add(value);
        }
    }
}

/// <summary>One position drawn on the map: a lineup's technique, or the whole lineup under the grid grouping.</summary>
public sealed record ThrowPosition(
    string Key,
    GrenadeLineup Lineup,
    LineupTechnique? Technique,
    WorldPoint Origin,
    bool JumpThrow,
    IReadOnlyList<IndexedGrenade> Throws,
    IndexedGrenade Representative);

/// <summary>One landing group on the map: a cluster with the lineups the filters leave.</summary>
public sealed class LandingGroup
{
    /// <param name="cluster">The index's cluster.</param>
    /// <param name="lineups">Its lineups that are shown.</param>
    public LandingGroup(GrenadeCluster cluster, IReadOnlyList<GrenadeLineup> lineups)
    {
        Cluster = cluster;
        Lineups = lineups;
        List<IndexedGrenade> throws = [.. lineups.SelectMany(l => l.Throws)];
        Landing = new WorldPoint(throws.Average(t => t.Landing.X), throws.Average(t => t.Landing.Y), throws.Average(t => t.Landing.Z));
        // Not the landing cell: several landing groups can seed in one 256-unit cell.
        Id = string.Create(CultureInfo.InvariantCulture, $"{cluster.Kind}:{cluster.Lineups[0].Id:N}");
    }

    public GrenadeCluster Cluster { get; }

    public IReadOnlyList<GrenadeLineup> Lineups { get; }

    public string Id { get; }

    public GrenadeKind Kind => Cluster.Kind;

    /// <summary>Where the shown throws go off, on average.</summary>
    public WorldPoint Landing { get; }

    public int ThrowCount => Lineups.Sum(l => l.Throws.Count);

    public string Title => LineupClipPlanner.Title(Cluster);
}

/// <summary>The details card of one throw position: counts, the console line, and every throw from there.</summary>
public sealed partial class LineupDetail : ObservableObject
{
    private readonly Func<string, Task> _copy;

    [ObservableProperty]
    private string _copyStatus = "";

    /// <param name="group">The landing group.</param>
    /// <param name="lineup">The throw position.</param>
    /// <param name="demoDate">A demo's date, or null.</param>
    /// <param name="watch">Opens one throw in 2D Playback.</param>
    /// <param name="copy">Writes the console line to the clipboard.</param>
    /// <param name="technique">The position clicked, or null for the lineup as a whole.</param>
    /// <param name="clipDirectory">Where the position's clip pair lives; no clip when null.</param>
    public LineupDetail(LandingGroup group, GrenadeLineup lineup, Func<string, DateTime?> demoDate,
        Func<IndexedGrenade, Task> watch, Func<string, Task> copy, LineupTechnique? technique = null,
        string? clipDirectory = null)
    {
        Lineup = lineup;
        Technique = technique;
        _copy = copy;
        IndexedGrenade first = technique?.Representative ?? lineup.Representative;
        IReadOnlyList<IndexedGrenade> throws = technique?.Throws ?? lineup.Throws;
        string how = technique?.Label ?? (lineup.JumpThrow ? "jump-throw" : "standard throw");
        Title = group.Title;
        StyleLine = string.Create(CultureInfo.InvariantCulture,
            $"{first.Kind} · {how} · {first.Row.Movement} · {first.AirTimeSeconds:0.0}s air time");
        UsedLine = string.Create(CultureInfo.InvariantCulture,
            $"Used {lineup.Throws.Count} {(lineup.Throws.Count == 1 ? "time" : "times")} in {lineup.DemoCount} {(lineup.DemoCount == 1 ? "demo" : "demos")}");
        TechniquesLine = lineup.Techniques.Count > 1
            ? string.Join(" · ", lineup.Techniques.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Label} {t.Throws.Count}")))
            : "";
        if (clipDirectory is not null)
        {
            LineupTechnique? shown = technique ?? (lineup.Techniques.Count > 0 ? lineup.Techniques[0] : null);
            ClipPath = LineupClipPlanner.FinishedGif(clipDirectory, first.Map, first.Kind, lineup.Id, shown?.Key);
            ClipLine = ClipPath is not null
                ? ""
                : LineupClipPlanner.Plan(lineup, shown, Title, clipDirectory) is null
                    ? "no clip: a position gets one once it is thrown twice"
                    : "clip not rendered yet: it renders in the processing queue";
        }

        ConsoleText = GrenadeConsole.Format(first.Row) ?? UtilityBookTabViewModel.NoConsoleText;
        HasConsole = GrenadeConsole.Format(first.Row) is not null;
        foreach (IndexedGrenade grenade in throws
                     .OrderByDescending(t => demoDate(t.Demo.Path) ?? DateTime.MinValue)
                     .ThenBy(t => t.Demo.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Row.ReleaseTick))
        {
            Instances.Add(new LineupInstanceRow(grenade, demoDate(grenade.Demo.Path), new AsyncRelayCommand(() => watch(grenade))));
        }
    }

    public GrenadeLineup Lineup { get; }

    /// <summary>The position shown, or null for the whole lineup.</summary>
    public LineupTechnique? Technique { get; }

    /// <summary>Every way the lineup is thrown with its count, when there is more than one; else empty.</summary>
    public string TechniquesLine { get; }

    public bool HasTechniques => TechniquesLine.Length > 0;

    public string Title { get; }

    public string StyleLine { get; }

    /// <summary>"Used 7 times in 4 demos".</summary>
    public string UsedLine { get; }

    /// <summary>The setpos and setang line of the first throw, the one to copy.</summary>
    public string ConsoleText { get; }

    public bool HasConsole { get; }

    /// <summary>The position's finished clip GIF, or null while it has none.</summary>
    public string? ClipPath { get; }

    public bool HasClip => ClipPath is not null;

    /// <summary>Why there is no clip, or empty.</summary>
    public string ClipLine { get; } = "";

    public bool HasClipLine => ClipLine.Length > 0;

    /// <summary>Every throw from this position, newest demo first.</summary>
    public ObservableCollection<LineupInstanceRow> Instances { get; } = [];

    [RelayCommand]
    private async Task CopyConsole()
    {
        if (!HasConsole)
        {
            return;
        }

        await _copy(ConsoleText);
        CopyStatus = "copied";
    }
}

/// <summary>One throw from a lineup's position, as the details card lists it.</summary>
public sealed class LineupInstanceRow(IndexedGrenade grenade, DateTime? date, IAsyncRelayCommand watch)
{
    public IndexedGrenade Grenade { get; } = grenade;

    public string Date { get; } = date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    public string DemoName { get; } = Path.GetFileNameWithoutExtension(grenade.Demo.Path);

    /// <summary>Who threw it: the name at the time, sanitised; empty when the walk read no player.</summary>
    public string PlayerText { get; } = grenade.Row.ThrowerName is { Length: > 0 } name ? DisplayText.Sanitize(name) : "";

    public string RoundText { get; } = grenade.Row.RoundNumber > 0
        ? string.Create(CultureInfo.InvariantCulture, $"round {grenade.Row.RoundNumber}")
        : "";

    public string SideText { get; } = grenade.Row.ThrowerTeam switch
    {
        2 => "T",
        3 => "CT",
        _ => ""
    };

    public IAsyncRelayCommand WatchCommand { get; } = watch;
}

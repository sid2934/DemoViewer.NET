#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
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
    private readonly ISituationPlayback? _playback;
    private readonly Dictionary<string, LandingGroup> _groups = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _refreshing;

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

    private string? _focusedId;
    private string? _selectedLineupId;

    /// <param name="index">The grenade index.</param>
    /// <param name="playback">Opens a throw in 2D Playback; null when the host has no shell.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader when null, a stub in a test.</param>
    /// <param name="demoDate">A demo's date for the instance list; none shown when null.</param>
    public UtilityBookTabViewModel(GrenadeIndex index, ISituationPlayback? playback = null, bool? isBrowser = null,
        Func<string, LoadedMapAsset?>? loadMapAsset = null, Func<string, DateTime?>? demoDate = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _playback = playback;
        _loadMapAsset = loadMapAsset ?? (map => MapAssetPipeline.TryLoad(map));
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

    /// <summary>Raised when the bound map or its bundle changes, so the host rebinds the radar.</summary>
    public event Action? MapChanged;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context) => Refresh();

    /// <inheritdoc />
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
        _index.Changed -= Refresh;
    }

    /// <summary>The query the pickers describe, or null with no map chosen.</summary>
    public GrenadeQuery? CurrentQuery() =>
        SelectedMap is not { Length: > 0 } map
            ? null
            : new GrenadeQuery(map,
                SelectedKind?.Value is { } kind ? new HashSet<GrenadeKind> { kind } : null,
                SelectedPlace is { } place && place != AnyPlace ? new HashSet<string>(StringComparer.Ordinal) { place } : null,
                SelectedSide?.Value);

    /// <summary>Re-reads the maps and places and re-runs the query, keeping the focus and selection when they survive.</summary>
    public void Refresh()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            Sync(Maps, _index.Maps());
            if (SelectedMap is null || !Maps.Contains(SelectedMap))
            {
                SelectedMap = Maps.FirstOrDefault();
            }

            Sync(Places, [AnyPlace, .. SelectedMap is { } map ? _index.LandingPlaces(map) : []]);
            if (SelectedPlace is null || !Places.Contains(SelectedPlace))
            {
                SelectedPlace = AnyPlace;
            }

            RebindMap();
            BuildGroups();
            StatusLine = StatusFor(_index.IsReady, _index.DemoCount, _index.GrenadeCount, SelectedMap is not null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>A landing group was clicked: focus it, or unfocus when it already is.</summary>
    /// <param name="id">The group's id.</param>
    public void ClickLanding(string id)
    {
        _focusedId = string.Equals(_focusedId, id, StringComparison.Ordinal) ? null : id;
        _selectedLineupId = null;
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
        Refresh();
    }

    partial void OnSelectedKindChanged(UtilityBookOption<GrenadeKind>? value) => Refresh();

    partial void OnSelectedPlaceChanged(string? value) => Refresh();

    partial void OnSelectedSideChanged(UtilityBookOption<int>? value) => Refresh();

    partial void OnShowSingleThrowsChanged(bool value) => Refresh();

    partial void OnDetailChanged(LineupDetail? value) => OnPropertyChanged(nameof(HasDetail));

    private void RebindMap()
    {
        LoadedMapAsset? asset = SelectedMap is { } map ? _loadMapAsset(map) : null;
        if (ReferenceEquals(asset, MapAsset) && _boundMap == SelectedMap)
        {
            return;
        }

        MapAsset = asset;
        _boundMap = SelectedMap;
        OnPropertyChanged(nameof(MapAsset));
        OnPropertyChanged(nameof(HasNoMapArt));
        MapChanged?.Invoke();
    }

    private string? _boundMap;

    // The groups the pickers and the lineup bar leave, each placed at the mean landing of the throws it
    // still shows, so filtering out single throws moves an icon onto the lineups that remain.
    private void BuildGroups()
    {
        _groups.Clear();
        int hiddenLineups = 0;
        int hiddenThrows = 0;
        if (CurrentQuery() is { } query)
        {
            foreach (GrenadeCluster cluster in _index.Query(query))
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
        GrenadeLineup? selected = focused?.Lineups.FirstOrDefault(l => string.Equals(LineupKey(l), _selectedLineupId, StringComparison.Ordinal));
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
                .. focused.Lineups.OrderBy(l => l.Throws.Count)
                    .Select(l => new UtilityThrow(LineupKey(l), l.Origin.X, l.Origin.Y, l.Origin.Z, TeamOf(l), l.JumpThrow,
                        ReferenceEquals(l, selected), Flight(l, focused.Landing)))
            ];
        Document.Set(landings, throws);

        FocusLine = focused is null
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"{focused.Title}: {focused.ThrowCount} {(focused.ThrowCount == 1 ? "throw" : "throws")} from {focused.Lineups.Count} {(focused.Lineups.Count == 1 ? "position" : "positions")}. Click a position for its details; Escape steps back.");
        Detail = selected is null ? null : new LineupDetail(focused!, selected, _demoDate, WatchAsync, CopyAsync);
        OnPropertyChanged(nameof(FocusedGroup));
        OnPropertyChanged(nameof(HasFocus));
    }

    /// <summary>The key a throw position is clicked by: the lineup's stable id.</summary>
    /// <param name="lineup">The lineup.</param>
    public static string LineupKey(GrenadeLineup lineup) => lineup.Id.ToString("N", CultureInfo.InvariantCulture);

    private static int TeamOf(GrenadeLineup lineup) =>
        lineup.Throws.GroupBy(t => t.Row.ThrowerTeam).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

    // The first throw's recorded flight, else a straight line from the position to the group's landing.
    private static IReadOnlyList<GrenadeTrailPoint> Flight(GrenadeLineup lineup, WorldPoint landing)
    {
        IndexedGrenade first = lineup.Throws[0];
        if (first.Row.Trajectory is { Count: >= 2 } path)
        {
            return [.. path.Select(p => new GrenadeTrailPoint(p.X, p.Y, p.Z))];
        }

        return [new GrenadeTrailPoint(lineup.Origin.X, lineup.Origin.Y, lineup.Origin.Z), new GrenadeTrailPoint(landing.X, landing.Y, landing.Z)];
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
        Id = string.Create(CultureInfo.InvariantCulture, $"{cluster.Kind}:{cluster.Cell.X},{cluster.Cell.Y},{cluster.Cell.Z}");
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
    public LineupDetail(LandingGroup group, GrenadeLineup lineup, Func<string, DateTime?> demoDate,
        Func<IndexedGrenade, Task> watch, Func<string, Task> copy)
    {
        Lineup = lineup;
        _copy = copy;
        IndexedGrenade first = lineup.Throws[0];
        Title = group.Title;
        StyleLine = string.Create(CultureInfo.InvariantCulture,
            $"{first.Kind} · {(lineup.JumpThrow ? "jump-throw" : "standard throw")} · {first.Row.Movement} · {first.AirTimeSeconds:0.0}s air time");
        UsedLine = string.Create(CultureInfo.InvariantCulture,
            $"Used {lineup.Throws.Count} {(lineup.Throws.Count == 1 ? "time" : "times")} in {lineup.DemoCount} {(lineup.DemoCount == 1 ? "demo" : "demos")}");
        ConsoleText = GrenadeConsole.Format(first.Row) ?? UtilityBookTabViewModel.NoConsoleText;
        HasConsole = GrenadeConsole.Format(first.Row) is not null;
        foreach (IndexedGrenade grenade in lineup.Throws
                     .OrderByDescending(t => demoDate(t.Demo.Path) ?? DateTime.MinValue)
                     .ThenBy(t => t.Demo.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Row.ReleaseTick))
        {
            Instances.Add(new LineupInstanceRow(grenade, demoDate(grenade.Demo.Path), new AsyncRelayCommand(() => watch(grenade))));
        }
    }

    public GrenadeLineup Lineup { get; }

    public string Title { get; }

    public string StyleLine { get; }

    /// <summary>"Used 7 times in 4 demos".</summary>
    public string UsedLine { get; }

    /// <summary>The setpos and setang line of the first throw, the one to copy.</summary>
    public string ConsoleText { get; }

    public bool HasConsole { get; }

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

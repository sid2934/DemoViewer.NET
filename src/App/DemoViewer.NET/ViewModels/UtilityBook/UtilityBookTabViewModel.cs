#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.ViewModels.UtilityBook;

/// <summary>One entry of a filter picker: its label and the value it stands for (null for "any").</summary>
/// <typeparam name="T">The filter's value type.</typeparam>
/// <param name="Label">What the picker shows.</param>
/// <param name="Value">The filter value, or null for no filter.</param>
public sealed record UtilityBookOption<T>(string Label, T? Value) where T : struct
{
    public override string ToString() => Label;
}

/// <summary>One deduplicated throw position of a cluster, as the tab lists it.</summary>
public sealed class GrenadeLineupRow
{
    public GrenadeLineupRow(GrenadeLineup lineup, IAsyncRelayCommand watch)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        Lineup = lineup;
        WatchCommand = watch;
    }

    public GrenadeLineup Lineup { get; }

    /// <summary><c>from (x, y, z)</c> at whole units.</summary>
    public string OriginText => string.Create(CultureInfo.InvariantCulture,
        $"from ({Lineup.Origin.X:0}, {Lineup.Origin.Y:0}, {Lineup.Origin.Z:0})");

    /// <summary><c>6 throws in 4 demos, jump-throw</c>.</summary>
    public string DetailText
    {
        get
        {
            int throws = Lineup.Throws.Count;
            int demos = Lineup.DemoCount;
            string text = string.Create(CultureInfo.InvariantCulture,
                $"{throws} {(throws == 1 ? "throw" : "throws")} in {demos} {(demos == 1 ? "demo" : "demos")}");
            return Lineup.JumpThrow ? text + ", jump-throw" : text;
        }
    }

    /// <summary>Opens the first throw's demo a moment before the release in 2D Playback.</summary>
    public IAsyncRelayCommand WatchCommand { get; }
}

/// <summary>One landing cluster, as the tab lists it.</summary>
public sealed class GrenadeClusterRow
{
    public GrenadeClusterRow(GrenadeCluster cluster, IReadOnlyList<GrenadeLineupRow> lineups)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        Cluster = cluster;
        Lineups = lineups;
    }

    public GrenadeCluster Cluster { get; }

    public IReadOnlyList<GrenadeLineupRow> Lineups { get; }

    /// <summary><c>Smoke into CTSpawn</c>, or the landing point when no zone placed it.</summary>
    public string Title => Cluster.LandingPlace is { } place
        ? $"{Cluster.Kind} into {place}"
        : string.Create(CultureInfo.InvariantCulture,
            $"{Cluster.Kind} at ({Cluster.Landing.X:0}, {Cluster.Landing.Y:0}, {Cluster.Landing.Z:0})");

    /// <summary><c>14 throws from 5 positions</c>.</summary>
    public string Summary
    {
        get
        {
            int throws = Cluster.ThrowCount;
            int positions = Cluster.Lineups.Count;
            return string.Create(CultureInfo.InvariantCulture,
                $"{throws} {(throws == 1 ? "throw" : "throws")} from {positions} {(positions == 1 ? "position" : "positions")}");
        }
    }
}

/// <summary>
///     The Utility Book tab: the Grenade Index queried by map, kind, landing place and side, listed as
///     landing clusters with their deduplicated throw positions. Delegate-injected (the Highlights
///     precedent): the VM owns no clustering; it reads the <see cref="GrenadeIndex" /> and re-projects on
///     its <c>Changed</c> and on every filter change.
/// </summary>
public sealed partial class UtilityBookTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>Frame-clock ticks a watch starts before the release: two seconds at 64 ticks.</summary>
    public const int WatchLeadTicks = 128;

    /// <summary>The place picker's "no filter" entry.</summary>
    public const string AnyPlace = "any place";

    /// <summary>The line the panel shows on the browser host.</summary>
    public const string BrowserNote =
        "In the browser, grenades are indexed for the open demo only and this tab forgets them when it reloads.";

    private readonly GrenadeIndex _index;
    private readonly ISituationPlayback? _playback;
    private bool _disposed;
    private bool _refreshing;

    [ObservableProperty]
    private string _footerLine = "";

    [ObservableProperty]
    private UtilityBookOption<GrenadeKind>? _selectedKind;

    [ObservableProperty]
    private string? _selectedMap;

    [ObservableProperty]
    private string? _selectedPlace = AnyPlace;

    [ObservableProperty]
    private UtilityBookOption<int>? _selectedSide;

    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="index">The grenade index.</param>
    /// <param name="playback">Opens a throw in 2D Playback; null when the host has no shell.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    public UtilityBookTabViewModel(GrenadeIndex index, ISituationPlayback? playback = null, bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _playback = playback;
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

    public ObservableCollection<GrenadeClusterRow> Clusters { get; } = [];

    public bool HasClusters => Clusters.Count > 0;

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

    /// <summary>Re-reads the maps and places and re-runs the query.</summary>
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

            Clusters.Clear();
            if (CurrentQuery() is { } query)
            {
                foreach (GrenadeCluster cluster in _index.Query(query))
                {
                    Clusters.Add(new GrenadeClusterRow(cluster,
                    [
                        .. cluster.Lineups.Select(l => new GrenadeLineupRow(l,
                            new AsyncRelayCommand(() => WatchAsync(l.Throws[0]), () => _playback is not null)))
                    ]));
                }
            }

            OnPropertyChanged(nameof(HasClusters));
            StatusLine = StatusFor(_index.IsReady, _index.DemoCount, _index.GrenadeCount, SelectedMap is not null);
            int throws = Clusters.Sum(c => c.Cluster.ThrowCount);
            FooterLine = Clusters.Count == 0
                ? ""
                : string.Create(CultureInfo.InvariantCulture,
                    $"{Clusters.Count} landing {(Clusters.Count == 1 ? "spot" : "spots")}, {throws} {(throws == 1 ? "throw" : "throws")}; "
                    + $"a spot is a {GrenadeIndex.LandingCellSize:0}-unit square, and throws within {GrenadeIndex.OriginRounding:0} units count as one position");
        }
        finally
        {
            _refreshing = false;
        }
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

    partial void OnSelectedMapChanged(string? value) => Refresh();

    partial void OnSelectedKindChanged(UtilityBookOption<GrenadeKind>? value) => Refresh();

    partial void OnSelectedPlaceChanged(string? value) => Refresh();

    partial void OnSelectedSideChanged(UtilityBookOption<int>? value) => Refresh();

    private async Task WatchAsync(IndexedGrenade grenade)
    {
        if (_playback is null)
        {
            return;
        }

        await _playback.SeekAsync(grenade.Demo.Path, Math.Max(0, grenade.Row.ReleaseTick - WatchLeadTicks));
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

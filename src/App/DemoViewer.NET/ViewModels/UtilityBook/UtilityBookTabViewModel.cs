#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

/// <summary>
///     One deduplicated throw position of a cluster: a Lineup Card (plan.md §3, Phase 4). Prints the
///     CS2UTIL field set over the group's representative throw (oldest demo, then earliest release):
///     map, type, jump-throw flag, air time, movement, the <c>setpos</c>/<c>setang</c> string, and the
///     landing point, rendered on the radar when this host has the map's baked bundle and in words
///     otherwise. <see cref="ConsoleText" /> is what a card is copy-pasted into a console from.
/// </summary>
public sealed partial class GrenadeLineupRow : ViewModelBase
{
    /// <summary>The card's note when its map has no baked bundle on this host.</summary>
    public const string NoRadarNote = "no radar for this map";

    /// <summary><see cref="GrenadeConsole.Format" />'s own words for a throw whose release state was not read.</summary>
    public const string NoConsoleText = "release state unavailable";

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private string _thumbnailNote = "";

    public GrenadeLineupRow(GrenadeLineup lineup, IAsyncRelayCommand watch)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        ArgumentNullException.ThrowIfNull(watch);
        Lineup = lineup;
        WatchCommand = watch;
        Representative = lineup.Throws[0];
    }

    public GrenadeLineup Lineup { get; }

    /// <summary>The throw the card's fields are read from: the group's oldest demo, then earliest release.</summary>
    public IndexedGrenade Representative { get; }

    /// <summary>The map, as the demo header spells it.</summary>
    public string Map => Representative.Map;

    /// <summary>"Smoke", "Molotov" and so on: the CS2UTIL type field.</summary>
    public string TypeText => Representative.Kind.ToString();

    /// <summary>"Jump-throw" or "Standard throw": the CS2UTIL jump-throw flag.</summary>
    public string JumpThrowText => Lineup.JumpThrow ? "Jump-throw" : "Standard throw";

    /// <summary>"1.8s air time", from <see cref="IndexedGrenade.AirTimeSeconds" />.</summary>
    public string AirTimeText => string.Create(CultureInfo.InvariantCulture,
        $"{Representative.AirTimeSeconds:0.0}s air time");

    /// <summary>"Running", "Walking", "Stationary" or "Unknown": the CS2UTIL movement word.</summary>
    public string MovementText => Representative.Row.Movement.ToString();

    /// <summary>
    ///     The console line a card is copy-pasted from: <see cref="GrenadeConsole.Format" /> over
    ///     <see cref="Representative" />, or <see cref="NoConsoleText" /> when the release state was not
    ///     read (grenade-walk.md: never a confident wrong number).
    /// </summary>
    public string ConsoleText => GrenadeConsole.Format(Representative.Row) ?? NoConsoleText;

    /// <summary>"at (x, y, z)": the exact landing point in words, the radar's fallback.</summary>
    public string LandingText => string.Create(CultureInfo.InvariantCulture,
        $"at ({Representative.Landing.X:0}, {Representative.Landing.Y:0}, {Representative.Landing.Z:0})");

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

    /// <summary>A picture is on the card.</summary>
    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>The card has a note instead of a picture.</summary>
    public bool HasThumbnailNote => ThumbnailNote.Length > 0;

    /// <summary>Opens the first throw's demo a moment before the release in 2D Playback.</summary>
    public IAsyncRelayCommand WatchCommand { get; }

    /// <summary>Puts the rendered radar on the card, or the note that stands in for it. Called on the UI thread.</summary>
    /// <param name="bitmap">The decoded picture, or null when this host has no bundle for the map.</param>
    public void ApplyThumbnail(Bitmap? bitmap)
    {
        Thumbnail = bitmap;
        ThumbnailNote = bitmap is null ? NoRadarNote : "";
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasThumbnailNote));
    }
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
    public string Title => LineupClipPlanner.Title(Cluster);

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

    private readonly Func<byte[], Bitmap?> _decode;
    private readonly GrenadeIndex _index;
    private readonly ISituationPlayback? _playback;
    private readonly Action<Action> _post;
    private readonly Func<GrenadeLineupThumbnailRenderer> _renderer;
    private bool _disposed;
    private int _generation;
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
    /// <param name="renderer">Builds a batch's landing-point radar renderer; the pipeline's bundle loader when null.</param>
    /// <param name="post">UI-thread marshal for a rendered card; the dispatcher when null.</param>
    /// <param name="decode">PNG bytes to a bitmap; Avalonia's decoder when null, a stub in a test without a platform.</param>
    public UtilityBookTabViewModel(GrenadeIndex index, ISituationPlayback? playback = null, bool? isBrowser = null,
        Func<GrenadeLineupThumbnailRenderer>? renderer = null, Action<Action>? post = null,
        Func<byte[], Bitmap?>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _playback = playback;
        _renderer = renderer ?? (() => new GrenadeLineupThumbnailRenderer());
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _decode = decode ?? DecodePng;
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

    /// <summary>The last landing-point render batch's worker, so a test can await it instead of polling the cards.</summary>
    internal Task ThumbnailTask { get; private set; } = Task.CompletedTask;

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
        Interlocked.Increment(ref _generation); // stops a straggling render worker from posting after this
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
            List<GrenadeLineupRow> cards = [];
            if (CurrentQuery() is { } query)
            {
                foreach (GrenadeCluster cluster in _index.Query(query))
                {
                    List<GrenadeLineupRow> lineups =
                    [
                        .. cluster.Lineups.Select(l => new GrenadeLineupRow(l,
                            new AsyncRelayCommand(() => WatchAsync(l.Throws[0]), () => _playback is not null)))
                    ];
                    Clusters.Add(new GrenadeClusterRow(cluster, lineups));
                    cards.AddRange(lineups);
                }
            }

            FillThumbnails(cards);
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

    // Starts the batch that draws every visible card's landing-point radar, off the UI thread. A
    // generation behind the latest Refresh posts nothing, so a fast filter change never lands a
    // picture on a card an earlier query already replaced.
    private void FillThumbnails(List<GrenadeLineupRow> cards)
    {
        int generation = Interlocked.Increment(ref _generation);
        if (cards.Count == 0)
        {
            ThumbnailTask = Task.CompletedTask;
            return;
        }

        ThumbnailTask = Task.Run(() => Fill(generation, cards));
    }

    private void Fill(int generation, List<GrenadeLineupRow> cards)
    {
        using GrenadeLineupThumbnailRenderer renderer = _renderer();
        foreach (GrenadeLineupRow card in cards)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            byte[]? png = TryRender(renderer, card.Representative);
            Bitmap? bitmap = png is null ? null : _decode(png);
            _post(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    card.ApplyThumbnail(bitmap);
                }
            });
        }
    }

    private static byte[]? TryRender(GrenadeLineupThumbnailRenderer renderer, IndexedGrenade grenade)
    {
        try
        {
            return renderer.Render(grenade.Map, grenade.Landing, grenade.Row.ThrowerTeam);
        }
        catch (Exception)
        {
            return null; // a render that throws is a card with the landing point in words, never a dead one
        }
    }

    private static Bitmap? DecodePng(byte[] png)
    {
        try
        {
            using MemoryStream stream = new(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
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

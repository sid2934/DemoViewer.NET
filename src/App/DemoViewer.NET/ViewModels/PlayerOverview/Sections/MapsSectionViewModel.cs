#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The filter's window by map and by side. One row per map the window holds, most played first, each pooled by
///     <see cref="PlayerAggregate" /> over that map's demos alone, so the rows add up to the summary; a map with no
///     demo in the window has no row. Under the table, the window on each side: rounds and opening duels from the
///     <c>player_match</c> facet's side counts (<c>CTW/CTL/TW/TL</c>, <c>CTFK/CTFD/TFK/TFD</c>), and K/D, ADR and
///     KAST from the <c>player_round</c> facet's rounds summed by side, over the demos that have it.
/// </summary>
public sealed partial class MapsSectionViewModel : PlayerOverviewSection
{
    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _sideNote = "";

    [ObservableProperty]
    private IReadOnlyList<MapRowItem> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<SideSplitItem> _sides = [];

    /// <inheritdoc />
    public override string Id => "maps";

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Player is null || context.Window.Count == 0)
        {
            IsVisible = false;
            Rows = [];
            Sides = [];
            CountText = "";
            SideNote = "";
            return;
        }

        Rows = Build(context.Window);
        PlayerAggregate window = PlayerAggregate.Of(context.Window);
        Sides = [new SideSplitItem("CT", window, "CTW", "CTL", "CTFK", "CTFD"), new SideSplitItem("T", window, "TW", "TL", "TFK", "TFD")];
        SideNote = NoteOf(window);
        CountText = Rows.Count == 1 ? "1 map" : string.Create(CultureInfo.InvariantCulture, $"{Rows.Count} maps");
        IsVisible = true;
    }

    /// <summary>One row per map of <paramref name="demos" />, most demos first, then by name.</summary>
    /// <param name="demos">The window's demos.</param>
    internal static IReadOnlyList<MapRowItem> Build(IReadOnlyList<PlayerDemo> demos) =>
    [
        .. demos.GroupBy(d => d.Match.Row.Map ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new MapRowItem(g.Key, PlayerAggregate.Of([.. g])))
            .OrderByDescending(r => r.Pool.Matches)
            .ThenBy(r => r.MapText, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>What the section says when some matches with stats have no rounds by side yet; empty when all do.</summary>
    /// <param name="window">The window, pooled.</param>
    internal static string NoteOf(PlayerAggregate window) =>
        window.WithSides >= window.WithStats
            ? ""
            : window.WithSides == 0
                ? "K/D, ADR and KAST by side need per-round stats; the library is computing them."
                : string.Create(CultureInfo.InvariantCulture,
                    $"K/D, ADR and KAST by side cover {window.WithSides} of {window.WithStats} matches; the library is computing the rest.");

    internal static string Format(double? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "–";

    // "W-L (n%)" from two summed counts; a dash when the window has neither.
    internal static string Split(PlayerAggregate pool, string won, string lost)
    {
        if (pool[won] is not { } w || pool[lost] is not { } l || w + l == 0)
        {
            return "–";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{w:0}-{l:0} ({100 * w / (w + l):0}%)");
    }
}

/// <summary>One map in the window, every cell formatted.</summary>
public sealed class MapRowItem
{
    /// <param name="map">The map's name as the index holds it; empty when the demo has none.</param>
    /// <param name="pool">The subject's demos on the map, pooled.</param>
    public MapRowItem(string map, PlayerAggregate pool)
    {
        Map = map;
        Pool = pool ?? throw new ArgumentNullException(nameof(pool));
        MapText = map.Length == 0 ? "Unknown map" : DemoEntry.PrettifyMap(map);
        MatchesText = pool.Matches.ToString(CultureInfo.InvariantCulture);
        RecordText = pool.WithStats == 0
            ? "–"
            : pool.Ties > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{pool.Wins}-{pool.Losses}-{pool.Ties}")
                : string.Create(CultureInfo.InvariantCulture, $"{pool.Wins}-{pool.Losses}");
        WinRateText = MapsSectionViewModel.Format(SummarySectionViewModel.WinRate(pool), "0'%'");
        KdText = MapsSectionViewModel.Format(pool["KD"], "0.00");
        AdrText = MapsSectionViewModel.Format(pool["ADR"], "0.0");
        HltvText = MapsSectionViewModel.Format(pool["HLTV"], "0.00");
        CtText = MapsSectionViewModel.Split(pool, "CTW", "CTL");
        TText = MapsSectionViewModel.Split(pool, "TW", "TL");
    }

    /// <summary>The map as the index holds it.</summary>
    public string Map { get; }

    /// <summary>The subject's demos on the map, pooled.</summary>
    public PlayerAggregate Pool { get; }

    /// <summary>The map's display name.</summary>
    public string MapText { get; }

    /// <summary>Demos on the map, with stats or not.</summary>
    public string MatchesText { get; }

    /// <summary>Wins-losses, with ties when there are any; a dash without stats.</summary>
    public string RecordText { get; }

    /// <summary>Matches won of the matches with stats.</summary>
    public string WinRateText { get; }

    /// <summary>Kills over deaths.</summary>
    public string KdText { get; }

    /// <summary>Damage per round.</summary>
    public string AdrText { get; }

    /// <summary>HLTV 2.0, weighted by rounds.</summary>
    public string HltvText { get; }

    /// <summary>Rounds won and lost on CT, with the share won.</summary>
    public string CtText { get; }

    /// <summary>Rounds won and lost on T, with the share won.</summary>
    public string TText { get; }
}

/// <summary>One side's rounds and opening duels over the window, formatted.</summary>
public sealed class SideSplitItem
{
    /// <param name="side">"CT" or "T".</param>
    /// <param name="pool">The window, pooled.</param>
    /// <param name="won">The side's rounds-won column.</param>
    /// <param name="lost">The side's rounds-lost column.</param>
    /// <param name="openingKills">The side's opening-kill column.</param>
    /// <param name="openingDeaths">The side's opening-death column.</param>
    public SideSplitItem(string side, PlayerAggregate pool, string won, string lost, string openingKills, string openingDeaths)
    {
        ArgumentNullException.ThrowIfNull(pool);
        Side = side;
        RoundsText = MapsSectionViewModel.Split(pool, won, lost);
        OpeningText = MapsSectionViewModel.Split(pool, openingKills, openingDeaths);
        int cell = side == "CT" ? PlayerRoundFacet.Ct : PlayerRoundFacet.T;
        KdText = MapsSectionViewModel.Format(pool[PlayerRoundFacet.Column(cell, "KD")], "0.00");
        AdrText = MapsSectionViewModel.Format(pool[PlayerRoundFacet.Column(cell, "ADR")], "0.0");
        KastText = MapsSectionViewModel.Format(pool[PlayerRoundFacet.Column(cell, "KAST%")], "0.0'%'");
    }

    /// <summary>"CT" or "T".</summary>
    public string Side { get; }

    /// <summary>Rounds won-lost on the side, with the share won.</summary>
    public string RoundsText { get; }

    /// <summary>Opening kills-deaths on the side, with the share won.</summary>
    public string OpeningText { get; }

    /// <summary>Kills over deaths on the side; a dash without per-round stats.</summary>
    public string KdText { get; }

    /// <summary>Damage per round played on the side; a dash without per-round stats.</summary>
    public string AdrText { get; }

    /// <summary>The share of the side's rounds with a kill, an assist, survival or a trade; a dash without per-round stats.</summary>
    public string KastText { get; }
}

#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The filter bar and the headline tiles over the filter's window: matches, win rate, K/D, ADR, KAST%, HS%,
///     HLTV, KPR, opening duel win rate, clutches and multi-kill rounds, each pooled by its
///     <see cref="ColumnRules" /> rule. A filtered tile also shows the all-time value. Colour comes from the
///     catalogue's fixed benchmarks (Absolute bands): there are no peers across a career. No sight tile: the facet
///     carries no line-of-sight column.
/// </summary>
public sealed partial class SummarySectionViewModel : PlayerOverviewSection
{
    private const string None = "–";

    [ObservableProperty]
    private PlayerAggregate? _allTime;

    [ObservableProperty]
    private PlayerAggregate? _inWindow;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private IReadOnlyList<SummaryTileItem> _tiles = [];

    /// <param name="filter">The tab's filter bar, shown here.</param>
    public SummarySectionViewModel(PlayerOverviewFilterViewModel filter)
    {
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
    }

    /// <inheritdoc />
    public override string Id => "summary";

    /// <summary>The filter bar.</summary>
    public PlayerOverviewFilterViewModel Filter { get; }

    /// <summary>True when there is a line under the tiles.</summary>
    public bool HasNote => Note.Length > 0;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Player is null)
        {
            IsVisible = false;
            Tiles = [];
            AllTime = InWindow = null;
            Note = "";
            return;
        }

        PlayerAggregate all = PlayerAggregate.Of(context.Demos);
        PlayerAggregate window = context.IsFiltered ? PlayerAggregate.Of(context.Window) : all;
        AllTime = all;
        InWindow = window;
        Tiles = Build(window, context.IsFiltered ? all : null);
        Note = NoteFor(context, window);
        IsVisible = true;
    }

    // The window's tiles; with an all-time pool beside, each tile's caption carries the all-time value.
    private static SummaryTileItem[] Build(PlayerAggregate window, PlayerAggregate? all) =>
    [
        new("Matches", "Matches", Count(window.Matches), window.Matches, null,
            all is null ? Record(window) : $"all time {Count(all.Matches)}",
            "Demos in the window, with stats or not. The record below counts the demos with stats."),
        Tile("Win%", "Win rate", WinRate(window), WinRate(all), "0'%'", StatScale.Absolute(0, 100, 45, 55),
            "Matches won of the matches with stats; a tie counts as played, not won."),
        Catalogued("KD", "K/D", window, all, "0.00"),
        Catalogued("ADR", "ADR", window, all, "0.0"),
        Catalogued("KAST%", "KAST", window, all, "0.0'%'"),
        Catalogued("HS%", "HS%", window, all, "0'%'"),
        Catalogued("HLTV", "Rating", window, all, "0.00"),
        Catalogued("KPR", "KPR", window, all, "0.00"),
        Catalogued("Duel%", "Opening duels", window, all, "0'%'"),
        Tile("Clutch", "Clutches won", window["Clutch"], all?["Clutch"], "0", null, ColumnCatalogue.Resolve("Clutch").Tooltip),
        Tile("MultiKills", "Multi-kills", MultiKills(window), MultiKills(all), "0", null,
            "Rounds with two or more kills: 2K, 3K, 4K and aces")
    ];

    private static SummaryTileItem Catalogued(string key, string label, PlayerAggregate window, PlayerAggregate? all, string format) =>
        Tile(key, label, window[key], all?[key], format, AbsoluteScale(key, window), ColumnCatalogue.Resolve(key).Tooltip);

    private static SummaryTileItem Tile(string key, string label, double? value, double? allTime, string format, StatScale? scale, string tooltip) =>
        new(key, label, Format(value, format), value, value is null ? null : scale,
            allTime is { } a ? "all time " + Format(a, format) : "", tooltip);

    /// <summary>
    ///     The catalogue's benchmark band for <paramref name="key" /> as a fixed scale, or null when the column has
    ///     none or the window has too few attempts to judge (the opening-duel gate).
    /// </summary>
    internal static StatScale? AbsoluteScale(string key, PlayerAggregate window)
    {
        if (ColumnCatalogue.Resolve(key).Scale is not { } spec)
        {
            return null;
        }

        if (spec.ColourGateColumns is { Count: > 0 } gate && gate.Sum(c => window[c] ?? 0) < spec.ColourGateMinimum)
        {
            return null;
        }

        return spec.ColourMin is { } min && spec.ColourMax is { } max
            ? StatScale.Absolute(min, max, spec.NeutralLow, spec.NeutralHigh, spec.Polarity)
            : spec.Polarity == StatPolarity.Neutral && key.EndsWith('%')
                ? StatScale.Absolute(0, 100, polarity: StatPolarity.Neutral)
                : null;
    }

    private static double? WinRate(PlayerAggregate? pool) =>
        pool is { } p && p.Wins + p.Losses + p.Ties is var decided and > 0 ? 100.0 * p.Wins / decided : null;

    private static double? MultiKills(PlayerAggregate? pool)
    {
        if (pool?["2K"] is not { } two)
        {
            return null;
        }

        return two + (pool["3K"] ?? 0) + (pool["4K"] ?? 0) + (pool["5K"] ?? 0);
    }

    private static string Record(PlayerAggregate pool) =>
        pool.WithStats == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{pool.Wins}-{pool.Losses}-{pool.Ties}");

    private static string NoteFor(PlayerOverviewContext context, PlayerAggregate window)
    {
        if (context.Window.Count == 0)
        {
            return context.IsFiltered ? "No match passes the filter." : "";
        }

        int missing = window.Matches - window.WithStats;
        if (missing == 0)
        {
            return "";
        }

        string demos = missing == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{missing} matches");
        return context.IsReadingStats
            ? "Reading stats; the tiles fill in as they arrive."
            : $"{demos} without stats {(missing == 1 ? "is" : "are")} left out of the tiles.";
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(double? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : None;
}

/// <summary>One summary tile, formatted.</summary>
/// <param name="Key">The catalogue key the tile reads, or the tile's own id ("Matches", "Win%", "MultiKills").</param>
/// <param name="Label">The caption under the number.</param>
/// <param name="Text">The window's value, formatted; an en dash when no demo in the window has stats.</param>
/// <param name="Value">The window's value, for the colour; null with no stats.</param>
/// <param name="Scale">The fixed band the value is judged against; null for a count or too thin a sample.</param>
/// <param name="Caption">The all-time value when the window is narrowed, else empty (the match record on Matches).</param>
/// <param name="Tooltip">What the tile measures.</param>
public sealed record SummaryTileItem(string Key, string Label, string Text, double? Value, StatScale? Scale, string Caption, string Tooltip);

#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The aim board over the filter's window: reaction times, crosshair placement and the after-contact hit rates,
///     which need the map's line-of-sight geometry, beside the accuracy columns every demo carries. A sight column
///     pools over the demos whose facet was written with geometry (<see cref="PlayerAggregate.WithSight" />) and reads
///     "no data" when there is none, never 0: the background visit has no geometry, and an open of the demo with its
///     map's collision bake is what adds it. Each value shows the population behind it, from the catalogue's
///     denominator. Labels and tooltips come from <see cref="ColumnCatalogue" />, Absolute bands only.
/// </summary>
public sealed partial class AimQualitySectionViewModel : PlayerOverviewSection
{
    /// <summary>What a sight column reads when no demo in the window has line-of-sight data.</summary>
    public const string NoData = "no data";

    private const string None = "–";

    /// <summary>Every block, top to bottom: its title and its columns.</summary>
    internal static readonly IReadOnlyList<(string Title, IReadOnlyList<(string Key, string Format)> Entries)> All =
    [
        ("Reaction", [("TTS", "0' ms'"), ("TTD", "0' ms'"), ("AimRx", "0' ms'"), ("TTK", "0' ms'")]),
        ("Crosshair", [("Preaim", "0.0'°'"), ("XPlace", "0.0'°'"), ("FlickErr", "+0.0'°';-0.0'°';0.0'°'")]),
        ("After contact", [("FB%", "0.0'%'"), ("SAcc%", "0.0'%'"), ("SprayAcc%", "0.0'%'")]),
        ("Accuracy", [("Acc%", "0.0'%'"), ("HSAcc%", "0.0'%'"), ("HSDmg%", "0.0'%'"), ("CS%", "0.0'%'"), ("CSAll%", "0.0'%'"), ("Linear%", "0.0'%'"), ("Spray", "0.00'°'")])
    ];

    [ObservableProperty]
    private PlayerAggregate? _aggregate;

    [ObservableProperty]
    private IReadOnlyList<CategoryItem> _blocks = [];

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private string _scopeText = "";

    /// <inheritdoc />
    public override string Id => "aim-quality";

    /// <inheritdoc />
    public override PlayerOverviewPageKind Page => PlayerOverviewPageKind.Stats;

    /// <summary>True when there is a line under the blocks.</summary>
    public bool HasNote => Note.Length > 0;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PlayerAggregate? window = context.Player is null ? null : PlayerAggregate.Of(context.Window);
        if (window is not { WithStats: > 0 })
        {
            IsVisible = false;
            Blocks = [];
            Aggregate = null;
            ScopeText = Note = "";
            return;
        }

        IReadOnlyList<PlayerDemo> pooled = [.. context.Window.Where(d => d.Stats is not null)];
        Aggregate = window;
        Blocks = [.. All.Select(b => new CategoryItem(b.Title, [.. b.Entries.Select(e => Build(window, pooled, e.Key, e.Format))]))];
        ScopeText = window.WithSight == 0
            ? "no line-of-sight data"
            : string.Create(CultureInfo.InvariantCulture, $"line of sight from {Matches(window.WithSight)}");
        Note = NoteFor(window);
        IsVisible = true;
    }

    private static CategoryStatItem Build(PlayerAggregate window, IReadOnlyList<PlayerDemo> pooled, string key, string format)
    {
        ColumnMeta meta = ColumnCatalogue.Resolve(key);
        double? value = window[key];
        string text = value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : meta.RequiresVisibility ? NoData : None;
        return new CategoryStatItem(key, meta.Display, text, value,
            value is null || OverNothing(pooled, meta) ? null : SummarySectionViewModel.AbsoluteScale(key, window), Population(window, meta, value),
            meta.Tooltip);
    }

    // "after 412 contacts": the catalogue's denominator of the column, pooled over the same demos as the value.
    private static string Population(PlayerAggregate window, ColumnMeta meta, double? value)
    {
        if (value is null || meta.Denominator is not { } over || window[over.Key] is not { } count)
        {
            return "";
        }

        string noun = count == 1 && over.Noun.EndsWith('s') ? over.Noun[..^1] : over.Noun;
        return string.Create(CultureInfo.InvariantCulture, $"{over.Preposition} {count:0} {noun}");
    }

    // A rate over a zero denominator pools to 0, as the engine divides; it measured nothing, so it takes no colour.
    private static bool OverNothing(IReadOnlyList<PlayerDemo> pooled, ColumnMeta meta) =>
        ColumnRules.For(meta.Key) is ColumnRule.RatioOfSums ratio
        && pooled.Where(d => d.HasSight || !meta.RequiresVisibility).Sum(d => ratio.Denominator.Sum(c => d.Stats!.GetValueOrDefault(c))) == 0;

    private static string NoteFor(PlayerAggregate window)
    {
        int without = window.WithStats - window.WithSight;
        if (without == 0)
        {
            return "";
        }

        return window.WithSight == 0
            ? "None of these matches has line-of-sight data, so reaction, crosshair and after-contact columns read no data. "
              + "The background scan has no map geometry; opening a match on a map with a collision bake adds it."
            : $"{Matches(without)} without line-of-sight data {(without == 1 ? "is" : "are")} left out of the reaction, crosshair and after-contact columns. "
              + "Opening a match on a map with a collision bake adds it.";
    }

    private static string Matches(int count) =>
        count == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{count} matches");
}

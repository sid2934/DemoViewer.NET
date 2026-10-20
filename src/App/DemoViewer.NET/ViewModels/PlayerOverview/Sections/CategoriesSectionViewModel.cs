#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The catalogue's stat groups over the filter's window: opening duels, trades, clutches, utility, weapons,
///     multi-kills and economy, each column pooled by its <see cref="ColumnRules" /> rule, with a rate beside a
///     count where one reads better across a career (per round, per match, or a share). Labels, tooltips and
///     colour bands come from <see cref="ColumnCatalogue" />, Absolute bands only. Aim quality is left out: its
///     columns need line-of-sight geometry the background run does not have, and the footer says so.
/// </summary>
public sealed partial class CategoriesSectionViewModel : PlayerOverviewSection
{
    private const string None = "–";

    /// <summary>Why aim quality is missing, under the categories.</summary>
    public const string SightNote =
        "Aim quality (crosshair placement, time to damage and the rest) is on the Stats tab only: it needs line-of-sight geometry, which the background run that computes these stats does not have.";

    private static readonly Rate PerRound = new([ColumnRules.Rounds], 1, "0.00", "per round");
    private static readonly Rate PerMatch = new([], 1, "0.00", "per match");
    private static readonly Rate OfKills = new(["TotalK"], 100, "0", "% of kills");
    private static readonly Rate OfDeaths = new(["TotalD"], 100, "0", "% of deaths");
    private static readonly Rate OfRounds = new([ColumnRules.Rounds], 100, "0", "% of rounds");
    private static readonly Rate PerFlash = new(["Flash"], 1, "0.00", "per flash");

    /// <summary>Every category, top to bottom: its title, the catalogue group it mirrors, and its columns.</summary>
    internal static readonly IReadOnlyList<Category> All =
    [
        new("Opening duels", [StatGroup.OpeningDuels],
        [
            new("TotalFK", "0", PerRound), new("TotalFD", "0", PerRound), new("FK±", "+0;-0;0"), new("Duel%", "0'%'"),
            new("CTFK", "0"), new("CTFD", "0"), new("TFK", "0"), new("TFD", "0")
        ]),
        new("Trades", [], [new("TrdK", "0", PerRound), new("TradedD", "0", OfDeaths)]),
        new("Clutches", [], [new("Clutch", "0", PerMatch)]),
        new("Utility", [StatGroup.Utility],
        [
            new("Flash", "0", PerRound), new("EFlash", "0", PerFlash), new("AvgBlind", "0.00"), new("FlashAst", "0", PerRound),
            new("HE", "0", PerRound), new("Smokes", "0", PerRound), new("Molly", "0", PerRound)
        ]),
        new("Weapons", [StatGroup.Weapons],
        [
            new("Rifle", "0", OfKills), new("AWP", "0", OfKills), new("Pistol", "0", OfKills), new("SMG", "0", OfKills),
            new("Knife", "0", OfKills), new("DeagleHSRnds", "0", PerMatch)
        ]),
        new("Multi-kill", [StatGroup.MultiKill],
            [new("2K", "0", PerMatch), new("3K", "0", PerMatch), new("4K", "0", PerMatch), new("5K", "0", PerMatch)]),
        new("Economy", [StatGroup.Economy], [new("Equip", "'$'#,0"), new("Armor", "0", OfRounds)])
    ];

    [ObservableProperty]
    private PlayerAggregate? _aggregate;

    [ObservableProperty]
    private IReadOnlyList<CategoryItem> _categories = [];

    [ObservableProperty]
    private string _scopeText = "";

    /// <inheritdoc />
    public override string Id => "categories";

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PlayerAggregate? window = context.Player is null ? null : PlayerAggregate.Of(context.Window);
        if (window is not { WithStats: > 0 })
        {
            IsVisible = false;
            Categories = [];
            Aggregate = null;
            ScopeText = "";
            return;
        }

        IReadOnlyList<IReadOnlyDictionary<string, double>> rows = [.. context.Window.Select(d => d.Stats).OfType<IReadOnlyDictionary<string, double>>()];
        Aggregate = window;
        Categories = [.. All.Select(c => c.Build(window, rows))];
        double rounds = rows.Sum(r => r.GetValueOrDefault(ColumnRules.Rounds));
        string matches = window.WithStats == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{window.WithStats} matches");
        ScopeText = string.Create(CultureInfo.InvariantCulture, $"{matches} · {rounds:0} rounds");
        IsVisible = true;
    }

    /// <summary>One category: a title, the catalogue groups it shows in full, and its columns in order.</summary>
    /// <param name="Title">The heading.</param>
    /// <param name="Groups">The catalogue groups whose every pooled column the category lists; empty for a pick.</param>
    /// <param name="Entries">The columns shown.</param>
    internal sealed record Category(string Title, IReadOnlyList<StatGroup> Groups, IReadOnlyList<Entry> Entries)
    {
        public CategoryItem Build(PlayerAggregate window, IReadOnlyList<IReadOnlyDictionary<string, double>> rows) =>
            new(Title, [.. Entries.Select(c => c.Build(window, rows))]);
    }

    /// <summary>One column of a category, the format of its value and the rate shown beside it.</summary>
    internal sealed record Entry(string Key, string Format, Rate? Rate = null)
    {
        public CategoryStatItem Build(PlayerAggregate window, IReadOnlyList<IReadOnlyDictionary<string, double>> rows)
        {
            ColumnMeta meta = ColumnCatalogue.Resolve(Key);
            double? value = window[Key];
            string detail = value is { } v && Rate?.Of(v, rows) is { } rate ? Rate.Text(rate) : "";
            return new CategoryStatItem(Key, meta.Display, Text(value), value,
                value is null || OverNothing(rows) ? null : SummarySectionViewModel.AbsoluteScale(Key, window), detail, meta.Tooltip);
        }

        // A rate over a zero denominator pools to 0, as the engine divides; it measured nothing, so it takes no colour.
        private bool OverNothing(IReadOnlyList<IReadOnlyDictionary<string, double>> rows) =>
            ColumnRules.For(Key) is ColumnRule.RatioOfSums ratio && rows.Sum(r => ratio.Denominator.Sum(c => r.GetValueOrDefault(c))) == 0;

        private string Text(double? value) => value is { } v ? v.ToString(Format, CultureInfo.InvariantCulture) : None;
    }

    /// <summary>
    ///     A count over a pooled denominator: the summed <paramref name="Over" /> columns, or the matches with stats
    ///     when empty. Null over a zero denominator, so a rate is never shown for nothing.
    /// </summary>
    internal sealed record Rate(IReadOnlyList<string> Over, double Scale, string Format, string Unit)
    {
        public double? Of(double value, IReadOnlyList<IReadOnlyDictionary<string, double>> rows)
        {
            double over = Over.Count == 0 ? rows.Count : rows.Sum(r => Over.Sum(c => r.GetValueOrDefault(c)));
            return over == 0 ? null : value / over * Scale;
        }

        public string Text(double rate) =>
            Unit.StartsWith('%')
                ? rate.ToString(Format, CultureInfo.InvariantCulture) + Unit
                : rate.ToString(Format, CultureInfo.InvariantCulture) + " " + Unit;
    }
}

/// <summary>One category, built.</summary>
/// <param name="Title">"Opening duels".</param>
/// <param name="Stats">Its columns, in order.</param>
public sealed record CategoryItem(string Title, IReadOnlyList<CategoryStatItem> Stats);

/// <summary>One pooled column of a category, formatted.</summary>
/// <param name="Key">The catalogue key.</param>
/// <param name="Label">The catalogue's display name.</param>
/// <param name="Text">The window's value, formatted; an en dash when it does not pool.</param>
/// <param name="Value">The window's value, for the colour.</param>
/// <param name="Scale">The catalogue's fixed band, or null for a count or too thin a sample.</param>
/// <param name="Detail">The rate beside the value ("0.12 per round", "34% of kills"), or empty.</param>
/// <param name="Tooltip">What the column measures.</param>
public sealed record CategoryStatItem(string Key, string Label, string Text, double? Value, StatScale? Scale, string Detail, string Tooltip)
{
    /// <summary>True when there is a rate to show.</summary>
    public bool HasDetail => Detail.Length > 0;
}

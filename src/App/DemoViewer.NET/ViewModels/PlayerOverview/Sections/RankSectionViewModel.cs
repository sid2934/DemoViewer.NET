#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.GameIcons;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject's rank over the filter's window, as a bar per ranked match: Premier's CS Rating, and the
///     Competitive Skill Group whichever map it was on (the map is in each bar's tooltip, and each map's latest is on
///     the Maps and sides table). The latest standing of each sits on the name card above the tabs
///     (<see cref="Latest" />). The rows live on the demos' records, which the tab reads off the UI thread through
///     <see cref="PlayerRankCache" />; a window with no ranked demo has no section.
/// </summary>
public sealed partial class RankSectionViewModel : PlayerOverviewSection
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private string _loadText = "";

    [ObservableProperty]
    private IReadOnlyList<RankChartItem> _charts = [];

    /// <inheritdoc />
    public override string Id => "rank";

    /// <inheritdoc />
    public override SectionSlot Slot => SectionSlot.Rail;

    /// <summary>True while the records are being read.</summary>
    public bool IsLoading => LoadText.Length > 0;

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        LoadText = context.IsReadingRanks ? "Reading ranks" : "";
        Charts = context.Player is not null && context.Ranks is { } ranks ? Build([.. context.Window.Select(d => d.Match)], ranks) : [];
        IsVisible = Charts.Count > 0;
    }

    /// <summary>One chart per rank system of <paramref name="matches" />: Premier, then Competitive, then Wingman.</summary>
    /// <param name="matches">The demos to chart, newest match first.</param>
    /// <param name="ranks">The subject's rank rows.</param>
    internal static IReadOnlyList<RankChartItem> Build(IReadOnlyList<PlayerMatch> matches, PlayerRanks ranks)
    {
        Dictionary<int, List<(PlayerMatch Match, MatchRankRow Rank)>> systems = [];
        foreach (PlayerMatch match in matches)
        {
            if (ranks.For(match.Row) is not { } rank)
            {
                continue;
            }

            if (!systems.TryGetValue(rank.RankType, out List<(PlayerMatch, MatchRankRow)>? list))
            {
                systems[rank.RankType] = list = [];
            }

            list.Add((match, rank));
        }

        return [.. systems.OrderBy(s => Order(s.Key)).Select(s => new RankChartItem(s.Key, s.Value))];

        static int Order(int rankType) => rankType switch
        {
            RankDisplay.Premier => 0,
            RankDisplay.Competitive => 1,
            _ => 2
        };
    }

    /// <summary>
    ///     The name card's standings: the latest Premier rating and the latest Competitive Skill Group, each from the
    ///     newest match of its system, over every demo of the subject.
    /// </summary>
    /// <param name="matches">The subject's demos, newest match first.</param>
    /// <param name="ranks">The subject's rank rows.</param>
    internal static IReadOnlyList<RankCardItem> Latest(IEnumerable<PlayerMatch> matches, PlayerRanks ranks)
    {
        RankCardItem? premier = null;
        RankCardItem? competitive = null;
        foreach (PlayerMatch match in matches)
        {
            switch (ranks.For(match.Row))
            {
                case { RankType: RankDisplay.Premier } rank when premier is null:
                    premier = new RankCardItem(match, rank);
                    break;
                case { RankType: RankDisplay.Competitive } rank when competitive is null:
                    competitive = new RankCardItem(match, rank);
                    break;
            }

            if (premier is not null && competitive is not null)
            {
                break;
            }
        }

        return [.. new[] { premier, competitive }.OfType<RankCardItem>()];
    }

    /// <summary>
    ///     Each map's latest Competitive Skill Group: the subject's newest Competitive row on the map, ranked or not.
    ///     A map the subject never played Competitive on is absent.
    /// </summary>
    /// <param name="matches">The subject's demos, newest match first.</param>
    /// <param name="ranks">The subject's rank rows.</param>
    internal static Dictionary<string, MapRankItem> LatestByMap(IEnumerable<PlayerMatch> matches, PlayerRanks ranks)
    {
        Dictionary<string, MapRankItem> latest = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlayerMatch match in matches)
        {
            string map = match.Row.Map ?? "";
            if (ranks.For(match.Row) is { RankType: RankDisplay.Competitive } rank && !latest.ContainsKey(map))
            {
                latest[map] = new MapRankItem(match, rank);
            }
        }

        return latest;
    }
}

/// <summary>One rank system over the window: a bar per ranked match, oldest on the left.</summary>
public sealed class RankChartItem
{
    // How far below the lowest rating a Premier chart's floor sits, so the shortest bar still shows.
    private const int PremierFloorMargin = 500;

    /// <param name="rankType">The rank system.</param>
    /// <param name="rows">The system's demos, newest match first.</param>
    internal RankChartItem(int rankType, IReadOnlyList<(PlayerMatch Match, MatchRankRow Rank)> rows)
    {
        RankType = rankType;
        Title = RankDisplay.SystemName(rankType);

        List<double> values = [];
        List<string> tips = [];
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            (PlayerMatch match, MatchRankRow rank) = rows[i];
            if (RankDisplay.After(rank) is not { } value)
            {
                continue;
            }

            values.Add(value);
            tips.Add($"{RankDisplay.DateOf(match)} · {DemoEntry.PrettifyMap(match.Row.Map)} · {RankDisplay.Describe(rank)}");
        }

        Values = values;
        PointTips = tips;
        if (rankType == RankDisplay.Premier)
        {
            // A rating's bars would all look alike from zero; the floor sits under the lowest, the ceiling at the top.
            Minimum = values.Count > 0 ? Math.Max(0, Math.Floor((values.Min() - PremierFloorMargin) / 1000) * 1000) : 0;
            Maximum = null;
        }
        else
        {
            // Skill Groups run 1-18: a fixed scale, so a short window does not read as a climb.
            Minimum = 0;
            Maximum = 18;
        }

        string count = rows.Count == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{rows.Count} matches");
        if (values.Count == 0)
        {
            SummaryText = $"{count} · unranked";
            return;
        }

        string latest = RankDisplay.Value(rankType, (int) values[^1]);
        string peak = RankDisplay.Value(rankType, (int) values.Max());
        string net = values.Count > 1 && rankType == RankDisplay.Premier
            ? " · " + (values[^1] - values[0] >= 0 ? "+" : "") + (values[^1] - values[0]).ToString("N0", CultureInfo.InvariantCulture)
            : "";
        SummaryText = $"{count} · latest {latest} · peak {peak}{net}";
    }

    /// <summary>The rank system: 11 Premier, 12 Competitive, 7 Wingman.</summary>
    public int RankType { get; }

    /// <summary>"Premier", "Competitive" or "Wingman".</summary>
    public string Title { get; }

    /// <summary>"12 matches · latest 14,350 · peak 15,010 · +350": the net change is Premier's alone.</summary>
    public string SummaryText { get; }

    /// <summary>The rank after each ranked match, oldest first.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>One line per <see cref="Values" /> entry: when, where and what moved.</summary>
    public IReadOnlyList<string> PointTips { get; }

    /// <summary>The chart's floor.</summary>
    public double Minimum { get; }

    /// <summary>The chart's ceiling; null fits the tallest bar.</summary>
    public double? Maximum { get; }

    /// <summary>True when there is a bar to draw.</summary>
    public bool HasBars => Values.Count > 0;
}

/// <summary>A standing on the name card: a badge, its value, and the match it comes from.</summary>
public sealed class RankCardItem
{
    /// <param name="match">The newest match of the system.</param>
    /// <param name="rank">The subject's rank row in it.</param>
    internal RankCardItem(PlayerMatch match, MatchRankRow rank)
    {
        int? value = RankDisplay.After(rank);
        RankType = rank.RankType;
        Title = RankDisplay.SystemName(rank.RankType).ToUpperInvariant();
        BadgeKey = RankDisplay.BadgeKey(rank.RankType, value, rank.Wins);
        ValueText = value is { } v ? RankDisplay.Value(rank.RankType, v) : "Unranked";
        ChangeText = RankDisplay.Change(rank) ?? "";
        IsUp = rank.RankChange > 0;
        IsDown = rank.RankChange < 0;
        Tooltip = $"{RankDisplay.Describe(rank)} · {RankDisplay.DateOf(match)} · {DemoEntry.PrettifyMap(match.Row.Map)}";
    }

    /// <summary>The rank system.</summary>
    public int RankType { get; }

    /// <summary>"PREMIER" or "COMPETITIVE".</summary>
    public string Title { get; }

    /// <summary>The badge's icon key; Premier's is pre-tinted for its tier.</summary>
    public string? BadgeKey { get; }

    /// <summary>A CS Rating ("14,402"), a Skill Group's name, or "Unranked".</summary>
    public string ValueText { get; }

    /// <summary>The latest match's change ("+189"); empty when it is not known.</summary>
    public string ChangeText { get; }

    /// <summary>True when the change raised the rank.</summary>
    public bool IsUp { get; }

    /// <summary>True when the change lowered it.</summary>
    public bool IsDown { get; }

    /// <summary>The match's rank line, when and where.</summary>
    public string Tooltip { get; }
}

/// <summary>A map's latest Competitive Skill Group, for the Maps and sides table.</summary>
public sealed class MapRankItem
{
    internal MapRankItem(PlayerMatch match, MatchRankRow rank)
    {
        int? value = RankDisplay.After(rank);
        BadgeKey = RankDisplay.BadgeKey(RankDisplay.Competitive, value, rank.Wins);
        string name = value is { } v ? RankDisplay.Value(RankDisplay.Competitive, v) : "unranked";
        Tooltip = $"Latest Competitive rank on the map: {name} ({RankDisplay.DateOf(match)})";
    }

    /// <summary>The Skill Group badge's icon key.</summary>
    public string? BadgeKey { get; }

    /// <summary>"Latest Competitive rank on the map: Gold Nova II (2026-10-12)".</summary>
    public string Tooltip { get; }
}

/// <summary>How the Player Overview words a rank: numbers for Premier, Skill Group names for the rest.</summary>
internal static class RankDisplay
{
    public const int Wingman = 7;
    public const int Premier = 11;
    public const int Competitive = 12;

    // Placement: an account owes this many wins before CS2 shows a Skill Group.
    private const int PlacementWins = 10;

    public static bool IsPerMap(int rankType) => rankType != Premier;

    public static string SystemName(int rankType) => rankType switch
    {
        Premier => "Premier",
        Competitive => "Competitive",
        Wingman => "Wingman",
        _ => string.Create(CultureInfo.InvariantCulture, $"Rank type {rankType}")
    };

    /// <summary>The rank after the match when known, else the one going into it; null when unranked.</summary>
    public static int? After(MatchRankRow rank) => rank.RankNew ?? rank.RankOld;

    public static string Value(int rankType, int rank) =>
        rankType == Premier
            ? rank.ToString("N0", CultureInfo.InvariantCulture)
            : IconCatalogue.RankName(rank) is { Length: > 0 } name
                ? name
                : rank.ToString(CultureInfo.InvariantCulture);

    public static string? BadgeKey(int rankType, int? rank, int? wins) => (rankType switch
    {
        Premier => rank is { } r ? IconCatalogue.PremierEmblem(r) : IconCatalogue.PremierUnranked(),
        Wingman => rank is { } r ? IconCatalogue.WingmanRank(r) : IconCatalogue.UnrankedBadge(wingman: true),
        _ => rank is { } r
            ? IconCatalogue.CompetitiveRank(r)
            : wins < PlacementWins
                ? IconCatalogue.NeedsWinsBadge()
                : IconCatalogue.UnrankedBadge()
    })?.Key;

    /// <summary>"+189", "-12" or "0"; null when the change is not known.</summary>
    public static string? Change(MatchRankRow? rank) =>
        rank?.RankChange is { } change
            ? change > 0 ? string.Create(CultureInfo.InvariantCulture, $"+{change}") : change.ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>"Premier 14,213 → 14,402 (+189)", "Competitive Gold Nova II", "Premier unranked".</summary>
    public static string Describe(MatchRankRow rank)
    {
        string system = SystemName(rank.RankType);
        if (rank.IsUnranked)
        {
            return system + " unranked";
        }

        string before = rank.RankOld is { } old ? Value(rank.RankType, old) : "unranked";
        if (rank.RankNew is not { } after)
        {
            return $"{system} {before}";
        }

        string change = Change(rank) is { } c ? $" ({c})" : "";
        string source = rank.RankSource == RankSource.Predicted ? ", predicted" : "";
        return rank.RankOld == rank.RankNew && rank.RankChange is null or 0
            ? $"{system} {Value(rank.RankType, after)}{source}"
            : $"{system} {before} → {Value(rank.RankType, after)}{change}{source}";
    }

    public static string DateOf(PlayerMatch match)
    {
        long ticks = match.TimeTicks;
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return "unknown date";
        }

        DateTime time = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
        return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

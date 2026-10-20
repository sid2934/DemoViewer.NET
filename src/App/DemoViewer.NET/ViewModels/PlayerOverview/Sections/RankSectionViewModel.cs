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
///     Where the subject stands: their latest Premier CS Rating and the highest Competitive Skill Group they have
///     held on any map. Both read every demo of the subject, not the filter's window, since a standing does not
///     narrow with a filter. The rows live on the demos' records, which the tab reads off the UI thread through
///     <see cref="PlayerRankCache" />; a subject with neither has no section. Each map's latest Skill Group sits on
///     the Maps and sides table.
/// </summary>
public sealed partial class RankSectionViewModel : PlayerOverviewSection
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private string _loadText = "";

    [ObservableProperty]
    private IReadOnlyList<RankCardItem> _cards = [];

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
        Cards = context.Player is not null && context.Ranks is { } ranks ? Build([.. context.Demos.Select(d => d.Match)], ranks) : [];
        IsVisible = Cards.Count > 0;
    }

    /// <summary>The latest Premier rating and the best Competitive Skill Group of <paramref name="matches" />, each when there is one.</summary>
    /// <param name="matches">The subject's demos, newest match first.</param>
    /// <param name="ranks">The subject's rank rows.</param>
    internal static IReadOnlyList<RankCardItem> Build(IReadOnlyList<PlayerMatch> matches, PlayerRanks ranks)
    {
        (PlayerMatch Match, MatchRankRow Rank)? premier = null;
        (PlayerMatch Match, MatchRankRow Rank)? competitive = null;
        (PlayerMatch Match, MatchRankRow Rank, int Value)? best = null;
        foreach (PlayerMatch match in matches)
        {
            if (ranks.For(match.Row) is not { } rank)
            {
                continue;
            }

            if (rank.RankType == RankDisplay.Premier)
            {
                premier ??= (match, rank);
            }
            else if (rank.RankType == RankDisplay.Competitive)
            {
                competitive ??= (match, rank);
                // Newest first, so a tie keeps the latest match that reached it.
                if (RankDisplay.After(rank) is { } value && (best is null || value > best.Value.Value))
                {
                    best = (match, rank, value);
                }
            }
        }

        List<RankCardItem> cards = [];
        if (premier is { } p)
        {
            cards.Add(RankCardItem.Latest(p.Match, p.Rank));
        }

        if (best is { } b)
        {
            cards.Add(RankCardItem.Highest(b.Match, b.Rank, b.Value));
        }
        else if (competitive is { } c)
        {
            // Only placement or unranked rows: say so rather than leave Competitive out.
            cards.Add(RankCardItem.Latest(c.Match, c.Rank));
        }

        return cards;
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

/// <summary>One standing on the rank card: a badge, its value, and the match it comes from.</summary>
public sealed class RankCardItem
{
    private RankCardItem(string title, int rankType, int? value, MatchRankRow rank, PlayerMatch match, bool showChange)
    {
        Title = title;
        BadgeKey = RankDisplay.BadgeKey(rankType, value, rank.Wins);
        ValueText = value is { } v ? RankDisplay.Value(rankType, v) : "Unranked";
        ChangeText = showChange ? RankDisplay.Change(rank) ?? "" : "";
        IsUp = showChange && rank.RankChange > 0;
        IsDown = showChange && rank.RankChange < 0;
        CaptionText = $"{RankDisplay.DateOf(match)} · {DemoEntry.PrettifyMap(match.Row.Map)}";
        Tooltip = RankDisplay.Describe(rank);
    }

    /// <summary>The newest row of a system, with the change it brought.</summary>
    internal static RankCardItem Latest(PlayerMatch match, MatchRankRow rank) =>
        new(rank.RankType == RankDisplay.Premier ? "Premier rating" : "Competitive", rank.RankType, RankDisplay.After(rank), rank, match, true);

    /// <summary>The best Competitive Skill Group, at the match that reached it.</summary>
    internal static RankCardItem Highest(PlayerMatch match, MatchRankRow rank, int value) =>
        new("Highest Competitive", RankDisplay.Competitive, value, rank, match, false);

    /// <summary>"Premier rating", "Highest Competitive", or "Competitive" for an account still placing.</summary>
    public string Title { get; }

    /// <summary>The badge's icon key; Premier's is pre-tinted for its tier.</summary>
    public string? BadgeKey { get; }

    /// <summary>A CS Rating ("14,402"), a Skill Group's name, or "Unranked".</summary>
    public string ValueText { get; }

    /// <summary>The latest match's change ("+189"); empty for a peak or when it is not known.</summary>
    public string ChangeText { get; }

    /// <summary>True when the change raised the rank.</summary>
    public bool IsUp { get; }

    /// <summary>True when the change lowered it.</summary>
    public bool IsDown { get; }

    /// <summary>When and where: "2026-10-12 · Inferno".</summary>
    public string CaptionText { get; }

    /// <summary>The match's rank line, worded as the history's rank column words it.</summary>
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

        DateTime time = match.Row.MatchTimeUtcTicks is null
            ? new DateTime(ticks, DateTimeKind.Local)
            : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
        return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

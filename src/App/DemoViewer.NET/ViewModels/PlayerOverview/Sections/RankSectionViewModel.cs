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
///     The subject's rank over its demos, one track per rank system: Premier as one CS Rating, and Competitive and
///     Wingman per map, because CS2 keeps their Skill Groups per map. Each track shows the latest badge and a line
///     of the rank after every match in the filter's window, oldest on the left. The rows live on the demos'
///     records, which the tab reads off the UI thread through <see cref="PlayerRankCache" />; a window with no
///     ranked demo has no section.
/// </summary>
public sealed partial class RankSectionViewModel : PlayerOverviewSection
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private string _loadText = "";

    [ObservableProperty]
    private IReadOnlyList<RankTrackItem> _tracks = [];

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
        Tracks = context.Player is not null && context.Ranks is { } ranks ? Build([.. context.Window.Select(d => d.Match)], ranks) : [];
        IsVisible = Tracks.Count > 0;
    }

    /// <summary>The tracks of <paramref name="matches" />: Premier first, then the one played most recently first.</summary>
    /// <param name="matches">The demos to chart, newest match first.</param>
    /// <param name="ranks">The subject's rank rows.</param>
    internal static IReadOnlyList<RankTrackItem> Build(IReadOnlyList<PlayerMatch> matches, PlayerRanks ranks)
    {
        // Newest first, so a track's first entry is its latest match.
        Dictionary<(int Type, string Map), List<(PlayerMatch Match, MatchRankRow Rank)>> tracks = [];
        List<(int Type, string Map)> order = [];
        foreach (PlayerMatch match in matches)
        {
            if (ranks.For(match.Row) is not { } rank)
            {
                continue;
            }

            (int, string) key = (rank.RankType, RankDisplay.IsPerMap(rank.RankType) ? match.Row.Map ?? "" : "");
            if (!tracks.TryGetValue(key, out List<(PlayerMatch, MatchRankRow)>? list))
            {
                tracks[key] = list = [];
                order.Add(key);
            }

            list.Add((match, rank));
        }

        return [.. order.OrderBy(k => k.Type == RankDisplay.Premier ? 0 : 1).Select(k => new RankTrackItem(k.Type, k.Map, tracks[k]))];
    }
}

/// <summary>One rank system (and map, for a per-map one) over the subject's demos.</summary>
public sealed class RankTrackItem
{
    /// <param name="rankType">The rank system.</param>
    /// <param name="map">The map for a per-map system, else empty.</param>
    /// <param name="rows">The track's demos, newest match first.</param>
    internal RankTrackItem(int rankType, string map, IReadOnlyList<(PlayerMatch Match, MatchRankRow Rank)> rows)
    {
        RankType = rankType;
        Title = map.Length > 0 ? $"{RankDisplay.SystemName(rankType)} · {DemoEntry.PrettifyMap(map)}" : RankDisplay.SystemName(rankType);

        MatchRankRow latest = rows[0].Rank;
        int? now = RankDisplay.After(latest);
        BadgeKey = RankDisplay.BadgeKey(rankType, now, latest.Wins);
        LatestText = now is { } n ? RankDisplay.Value(rankType, n) : "Unranked";
        LatestChangeText = RankDisplay.Change(latest) ?? "";
        IsUp = latest.RankChange > 0;
        IsDown = latest.RankChange < 0;

        List<double> values = [];
        List<string> tips = [];
        int? peak = null;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            (PlayerMatch match, MatchRankRow rank) = rows[i];
            if (RankDisplay.After(rank) is not { } value)
            {
                continue;
            }

            values.Add(value);
            tips.Add($"{RankDisplay.DateOf(match)} · {DemoEntry.PrettifyMap(match.Row.Map)} · {RankDisplay.Describe(rank)}");
            peak = peak is { } p ? Math.Max(p, value) : value;
        }

        Values = values;
        PointTips = tips;
        string count = rows.Count == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{rows.Count} matches");
        SummaryText = peak is { } best ? $"{count} · peak {RankDisplay.Value(rankType, best)}" : count;
    }

    /// <summary>The rank system: 11 Premier, 12 Competitive, 7 Wingman.</summary>
    public int RankType { get; }

    /// <summary>"Premier", or "Competitive · Mirage".</summary>
    public string Title { get; }

    /// <summary>The latest badge's icon key; Premier's is pre-tinted for its tier.</summary>
    public string? BadgeKey { get; }

    /// <summary>The latest rank: a CS Rating ("14,402"), a Skill Group's name, or "Unranked".</summary>
    public string LatestText { get; }

    /// <summary>The latest match's change ("+189"), or empty when it is not known.</summary>
    public string LatestChangeText { get; }

    /// <summary>True when the latest match raised the rank.</summary>
    public bool IsUp { get; }

    /// <summary>True when the latest match lowered it.</summary>
    public bool IsDown { get; }

    /// <summary>The rank after each ranked match, oldest first.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>One line per <see cref="Values" /> entry: when, where and what moved.</summary>
    public IReadOnlyList<string> PointTips { get; }

    /// <summary>True when there are two points to draw a line through.</summary>
    public bool HasLine => Values.Count > 1;

    /// <summary>"12 matches · peak 15,010".</summary>
    public string SummaryText { get; }
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

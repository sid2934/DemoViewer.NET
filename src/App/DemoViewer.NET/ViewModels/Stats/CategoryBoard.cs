#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using DemoViewer.NET.Controls.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>
///     The shape a category's page takes. Not every group of numbers wants to be a table.
/// </summary>
public enum CategoryLayout
{
    /// <summary>One column per stat. The default, and right for heterogeneous columns.</summary>
    Table,

    /// <summary>
    ///     One stacked bar per player: what they used, and how much of it. For groups that are a
    ///     composition rather than a list, where a table makes the reader do the division.
    /// </summary>
    Composition,

    /// <summary>
    ///     Won against lost about a break-even line. For groups that are one contest counted twice,
    ///     where the table spends four columns saying it.
    /// </summary>
    Diverging,

    /// <summary>
    ///     One mark per event. For counts small enough that a numeric column is mostly whitespace and
    ///     zero looks like one.
    /// </summary>
    Pips
}

/// <summary>What every non-table board row carries, so one section type can hold any of them.</summary>
public interface IBoardRow
{
    /// <summary>Player display name.</summary>
    string PlayerName { get; }

    /// <summary>CS2 wire team value: 2 = T, 3 = CT.</summary>
    int Team { get; }

    /// <summary>Side-colour hook.</summary>
    bool IsCt { get; }
}

/// <summary>One player's composition bar: the mix, the volume, and the lobby's largest volume.</summary>
public sealed record CompositionRow(
    string PlayerName,
    int Team,
    IReadOnlyList<StatSegment> Segments,
    double Total,
    double MaxTotal,
    string Detail,
    bool IsUtility) : IBoardRow
{
    /// <summary>Picks the weapon slot palette. Two bools, because a style class binds to one.</summary>
    public bool IsWeapons => !IsUtility;

    /// <inheritdoc />
    public bool IsCt => Team == TeamBadge.TeamCt;

    /// <summary>Volume as text, beside the bar.</summary>
    public string TotalText => Total.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One player's duel record: lost, won, and the rate, with a volume warning.</summary>
public sealed record DuelRow(
    string PlayerName,
    int Team,
    double Lost,
    double Won,
    double Extent,
    string NetText,
    string RateText,
    bool LowVolume) : IBoardRow
{
    /// <inheritdoc />
    public bool IsCt => Team == TeamBadge.TeamCt;

    /// <summary>Total duels taken, which is the fact a bare rate hides.</summary>
    public string VolumeText => string.Create(CultureInfo.InvariantCulture, $"{Lost + Won:0} duels");
}

/// <summary>One labelled group of marks within a <see cref="PipsRow" />.</summary>
/// <param name="Label">Column heading, e.g. "3K" or "Plants".</param>
/// <param name="Count">How many marks.</param>
/// <param name="Accent">
///     Style class for the strip: empty, <c>good</c> or <c>rare</c>. A class rather than a brush,
///     because a view model has no business holding a colour.
/// </param>
public sealed record PipGroup(string Label, int Count, string Accent)
{
    /// <summary>Style-class hook for the ordinary-good tier.</summary>
    public bool IsGood => Accent == "good";

    /// <summary>Style-class hook for the rarest tier (an ace, a defuse).</summary>
    public bool IsRare => Accent == "rare";
}

/// <summary>One player's row of pip groups.</summary>
public sealed record PipsRow(string PlayerName, int Team, IReadOnlyList<PipGroup> Groups) : IBoardRow
{
    /// <inheritdoc />
    public bool IsCt => Team == TeamBadge.TeamCt;
}

/// <summary>
///     A team's block on a non-table category page: the same header the scoreboard uses, over whichever
///     row shape the category asked for.
/// </summary>
public sealed record BoardSection(
    string TeamLabel,
    int Team,
    TeamOutcome Outcome,
    string ScoreText,
    IReadOnlyList<IBoardRow> Rows);

/// <summary>
///     Projects the game table into the non-table category boards.
///     <para>
///         Kept out of <see cref="StatsTabViewModel" /> because it is a family of small independent
///         projections rather than tab state, and because each one is really a claim about what a group
///         of columns MEANS. Those claims are easier to argue with when they are side by side.
///     </para>
/// </summary>
public static class CategoryBoard
{
    /// <summary>Duels below this count cannot support a rate, so the rate is shown as unreliable.</summary>
    private const double DuelVolumeGate = 8;

    /// <summary>The utility mix, in throw order, paired with its palette slot.</summary>
    private static readonly (string Column, string Label, int Slot)[] _utility =
    [
        ("Flash", "flashes", 0),
        ("Smokes", "smokes", 1),
        ("HE", "HEs", 2),
        ("Molly", "molotovs", 3)
    ];

    /// <summary>Kills by weapon class.</summary>
    private static readonly (string Column, string Label, int Slot)[] _weapons =
    [
        ("Rifle", "rifle", 0),
        ("AWP", "AWP", 1),
        ("SMG", "SMG", 2),
        ("Pistol", "pistol", 3),
        ("Knife", "knife", 4)
    ];

    private static readonly (string Column, string Label, string Accent)[] _multiKill =
    [
        ("2K", "2K", ""),
        ("3K", "3K", "good"),
        ("4K", "4K", "good"),
        ("5K", "ACE", "rare")
    ];

    private static readonly (string Column, string Label, string Accent)[] _objectives =
    [
        ("Plants", "plants", ""),
        ("Defuses", "defuses", "good")
    ];

    /// <summary>Which shape a category asks for. Anything unlisted stays a table.</summary>
    public static CategoryLayout LayoutFor(StatGroup group) => group switch
    {
        StatGroup.Utility => CategoryLayout.Composition,
        StatGroup.Weapons => CategoryLayout.Composition,
        StatGroup.OpeningDuels => CategoryLayout.Diverging,
        StatGroup.MultiKill => CategoryLayout.Pips,
        StatGroup.Objectives => CategoryLayout.Pips,
        _ => CategoryLayout.Table
    };

    /// <summary>
    ///     The colour key for a composition board: one equal-width segment per part, carrying the label
    ///     and the palette slot.
    ///     <para>
    ///         Built as segments rather than as its own legend type so the key reads its colours from the
    ///         same slot palette the bars do. A legend that can disagree with the thing it explains is
    ///         worse than none.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<StatSegment> Legend(IReadOnlyList<string> availableColumns, StatGroup group)
    {
        ArgumentNullException.ThrowIfNull(availableColumns);

        if (LayoutFor(group) != CategoryLayout.Composition)
        {
            return [];
        }

        HashSet<string> have = new(availableColumns, StringComparer.Ordinal);
        (string Column, string Label, int Slot)[] parts = group == StatGroup.Utility ? _utility : _weapons;
        return parts
            .Where(p => have.Contains(p.Column))
            .Select(p => new StatSegment(1, null, p.Label, p.Slot))
            .ToList();
    }

    /// <summary>
    ///     Builds the team-sectioned board for a category, or an empty list when the category is a
    ///     table or the columns it needs are not in this evaluation's output.
    /// </summary>
    public static IReadOnlyList<BoardSection> Build(
        IReadOnlyList<MetricRow> rows,
        IReadOnlyList<string> availableColumns,
        StatGroup group,
        IReadOnlyDictionary<int, int?> scoreBySort,
        Func<int, TeamOutcome> outcomeFor)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(availableColumns);
        ArgumentNullException.ThrowIfNull(outcomeFor);

        CategoryLayout layout = LayoutFor(group);
        if (layout == CategoryLayout.Table || rows.Count == 0)
        {
            return [];
        }

        HashSet<string> have = new(availableColumns, StringComparer.Ordinal);

        List<IBoardRow> built = layout switch
        {
            CategoryLayout.Composition => Composition(rows, have,
                group == StatGroup.Utility ? _utility : _weapons,
                group == StatGroup.Utility),
            CategoryLayout.Diverging => Duels(rows, have),
            CategoryLayout.Pips => Pips(rows, have,
                group == StatGroup.MultiKill ? _multiKill : _objectives),
            _ => []
        };

        return built.Count == 0 ? [] : Section(built, scoreBySort, outcomeFor);
    }

    private static List<BoardSection> Section(
        IReadOnlyList<IBoardRow> rows,
        IReadOnlyDictionary<int, int?> scoreBySort,
        Func<int, TeamOutcome> outcomeFor)
    {
        List<BoardSection> sections = [];
        foreach (IGrouping<int, IBoardRow> group in rows
                     .GroupBy(r => r.Team switch { TeamBadge.TeamCt => 0, TeamBadge.TeamT => 1, _ => 2 })
                     .OrderBy(g => g.Key))
        {
            string label = group.Key switch
            {
                0 => "ENDED CT",
                1 => "ENDED T",
                _ => "SPECTATORS"
            };
            int team = group.Key switch
            {
                0 => TeamBadge.TeamCt,
                1 => TeamBadge.TeamT,
                _ => 0
            };
            string score = scoreBySort.GetValueOrDefault(group.Key) is { } s
                ? s.ToString(CultureInfo.InvariantCulture)
                : "";
            sections.Add(new BoardSection(label, team, outcomeFor(group.Key), score, group.ToList()));
        }

        return sections;
    }

    private static List<IBoardRow> Composition(
        IReadOnlyList<MetricRow> rows,
        HashSet<string> have,
        (string Column, string Label, int Slot)[] parts,
        bool isUtility)
    {
        (string Column, string Label, int Slot)[] present = parts.Where(p => have.Contains(p.Column)).ToArray();
        if (present.Length == 0)
        {
            return [];
        }

        // One shared maximum across the lobby, so bar length is volume and the bars can be compared.
        double maxTotal = rows.Max(r => present.Sum(p => Num(r, p.Column)));

        List<IBoardRow> built = [];
        foreach (MetricRow row in rows)
        {
            List<StatSegment> segments = [];
            double total = 0;
            foreach ((string column, string label, int slot) in present)
            {
                double v = Num(row, column);
                total += v;
                if (v > 0)
                {
                    segments.Add(new StatSegment(v, null, v.ToString("0", CultureInfo.InvariantCulture), slot));
                }
            }

            built.Add(new CompositionRow(Name(row), TeamOf(row), segments, total, maxTotal,
                isUtility ? UtilityDetail(row, have) : WeaponDetail(present, row), isUtility));
        }

        return built.OrderByDescending(r => ((CompositionRow)r).Total).ToList();
    }

    private static string UtilityDetail(MetricRow row, HashSet<string> have)
    {
        List<string> parts = [];
        if (have.Contains("AvgBlind"))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Num(row, "AvgBlind"):0.0}s blind"));
        }

        if (have.Contains("EFlash"))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Num(row, "EFlash"):0} flashed"));
        }

        return string.Join("  ", parts);
    }

    private static string WeaponDetail((string Column, string Label, int Slot)[] present, MetricRow row)
    {
        (string Column, string Label, int Slot) top = default;
        double best = 0;
        foreach ((string column, string label, int slot) in present)
        {
            double v = Num(row, column);
            if (v > best)
            {
                best = v;
                top = (column, label, slot);
            }
        }

        return best > 0
            ? string.Create(CultureInfo.InvariantCulture, $"mostly {top.Label}  ({best:0})")
            : "";
    }

    private static List<IBoardRow> Duels(IReadOnlyList<MetricRow> rows, HashSet<string> have)
    {
        if (!have.Contains("TotalFK") || !have.Contains("TotalFD"))
        {
            return [];
        }

        // A shared half-scale, so one player's arms are comparable with another's.
        double extent = Math.Max(1, rows.Max(r => Math.Max(Num(r, "TotalFK"), Num(r, "TotalFD"))));

        List<IBoardRow> built = [];
        foreach (MetricRow row in rows)
        {
            double won = Num(row, "TotalFK");
            double lost = Num(row, "TotalFD");
            double attempts = won + lost;
            double net = won - lost;

            built.Add(new DuelRow(Name(row), TeamOf(row), lost, won, extent,
                string.Create(CultureInfo.InvariantCulture, $"{net:+0;-0;0}"),
                attempts > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{won / attempts:P0}")
                    : "",
                attempts < DuelVolumeGate));
        }

        return built.OrderByDescending(r => ((DuelRow)r).Won - ((DuelRow)r).Lost).ToList();
    }

    private static List<IBoardRow> Pips(
        IReadOnlyList<MetricRow> rows,
        HashSet<string> have,
        (string Column, string Label, string Accent)[] parts)
    {
        (string Column, string Label, string Accent)[] present =
            parts.Where(p => have.Contains(p.Column)).ToArray();
        if (present.Length == 0)
        {
            return [];
        }

        List<IBoardRow> built = [];
        foreach (MetricRow row in rows)
        {
            List<PipGroup> groups = present
                .Select(p => new PipGroup(p.Label, (int)Math.Round(Num(row, p.Column)), p.Accent))
                .ToList();
            built.Add(new PipsRow(Name(row), TeamOf(row), groups));
        }

        // Busiest first: a page of pips is only worth reading top-down if the top has the most marks.
        return built.OrderByDescending(r => ((PipsRow)r).Groups.Sum(g => g.Count)).ToList();
    }

    private static string Name(MetricRow row) =>
        row.Dimensions.GetValueOrDefault("player_name")?.ToString() ?? "?";

    private static int TeamOf(MetricRow row) =>
        row.Dimensions.GetValueOrDefault("team") is { } t
            ? Convert.ToInt32(t, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>Reads one numeric value, treating anything non-numeric as absent rather than throwing.</summary>
    private static double Num(MetricRow row, string column) =>
        new StatCell(row.Values.GetValueOrDefault(column)).Numeric ?? 0;
}

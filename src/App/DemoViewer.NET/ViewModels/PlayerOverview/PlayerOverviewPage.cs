#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview;

/// <summary>
///     One inner tab of the Player Overview: the sections whose <see cref="PlayerOverviewSection.Page" /> names it,
///     split by <see cref="PlayerOverviewSection.Slot" />. The top sections span the page; under them the primary
///     column and the rail sit side by side from <see cref="RailBreakpoint" /> and stack below it, and under those the
///     bottom sections sit two abreast, or stack with them. The page view
///     binds the grid ints here, the Match Overview split, so there is one subtree at every width.
/// </summary>
public sealed partial class PlayerOverviewPage : ObservableObject
{
    // The card's padding, its title row and the column header around a page-long list.
    private const double ListChrome = 120;
    private const double MinListHeight = 320;

    private readonly string _title;

    /// <summary>The page shows.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>True below <see cref="RailBreakpoint" />, or with nothing in the rail: one column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimarySpan), nameof(RailColumn), nameof(RailRow), nameof(RailSpan), nameof(BottomColumns))]
    private bool _isStacked = true;

    /// <summary>
    ///     The height a page-long list may take: the page's visible height less the card around it, so a long list
    ///     fills the page and scrolls inside itself rather than growing the page.
    /// </summary>
    [ObservableProperty]
    private double _listMaxHeight = 520;

    /// <summary>The number shown beside the title, or null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int? _count;

    /// <param name="kind">Which page.</param>
    /// <param name="sections">Every section of the tab; the page keeps its own.</param>
    /// <param name="primaryWeight">The primary column's share beside the rail.</param>
    /// <param name="railWeight">The rail's share beside the primary column.</param>
    /// <param name="railBreakpoint">The page width from which the rail sits beside the primary column.</param>
    public PlayerOverviewPage(PlayerOverviewPageKind kind, IEnumerable<PlayerOverviewSection> sections,
        double primaryWeight = 2, double railWeight = 1, double railBreakpoint = 1100)
    {
        Kind = kind;
        _title = kind.ToString();
        PrimaryWeight = primaryWeight;
        RailWeight = railWeight;
        RailBreakpoint = railBreakpoint;
        PlayerOverviewSection[] own = [.. sections.Where(s => !s.IsPersistent && s.Page == kind)];
        Top = [.. own.Where(s => s.Slot == SectionSlot.Top)];
        Primary = [.. own.Where(s => s.Slot == SectionSlot.Primary)];
        Rail = [.. own.Where(s => s.Slot == SectionSlot.Rail)];
        Bottom = [.. own.Where(s => s.Slot == SectionSlot.Bottom)];
    }

    /// <summary>Which page.</summary>
    public PlayerOverviewPageKind Kind { get; }

    /// <summary>The tab's render its sections last took; behind the tab's, the page is out of date.</summary>
    internal int RenderedFor { get; set; } = -1;

    /// <summary>The strip caption, with the count when there is one.</summary>
    public string Header => Count is { } count
        ? string.Create(CultureInfo.InvariantCulture, $"{_title} · {count}")
        : _title;

    /// <summary>The sections across the page, in page order.</summary>
    public IReadOnlyList<PlayerOverviewSection> Top { get; }

    /// <summary>The sections of the primary column, in page order.</summary>
    public IReadOnlyList<PlayerOverviewSection> Primary { get; }

    /// <summary>The sections of the rail, in page order.</summary>
    public IReadOnlyList<PlayerOverviewSection> Rail { get; }

    /// <summary>The sections under the columns, in page order.</summary>
    public IReadOnlyList<PlayerOverviewSection> Bottom { get; }

    /// <summary>Every section on the page, in page order.</summary>
    public IEnumerable<PlayerOverviewSection> Sections => Top.Concat(Primary).Concat(Rail).Concat(Bottom);

    /// <summary>The primary column's star weight.</summary>
    public double PrimaryWeight { get; }

    /// <summary>The rail's star weight.</summary>
    public double RailWeight { get; }

    /// <summary>The page width from which the rail sits beside the primary column.</summary>
    public double RailBreakpoint { get; }

    /// <summary>The primary column's span: the whole grid when stacked.</summary>
    public int PrimarySpan => IsStacked ? 3 : 1;

    /// <summary>The rail's grid column.</summary>
    public int RailColumn => IsStacked ? 0 : 2;

    /// <summary>The rail's grid row: under the primary column when stacked.</summary>
    public int RailRow => IsStacked ? 1 : 0;

    /// <summary>The rail's span.</summary>
    public int RailSpan => IsStacked ? 3 : 1;

    /// <summary>How many bottom sections sit abreast: two beside each other while the columns do, else one.</summary>
    public int BottomColumns => IsStacked || Bottom.Count < 2 ? 1 : 2;

    /// <summary>Sizes page-long lists for a page <paramref name="height" /> tall; zero (not measured yet) keeps the last.</summary>
    public void SetHeight(double height)
    {
        if (height > 0)
        {
            ListMaxHeight = Math.Max(MinListHeight, height - ListChrome);
        }
    }

    /// <summary>Lays the page out for <paramref name="width" />; zero (not measured yet) stacks.</summary>
    public void SetWidth(double width) => IsStacked = (Rail.Count == 0 && Bottom.Count < 2) || width < RailBreakpoint;
}

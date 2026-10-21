namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The Player Overview's sections, grouped by inner tab in strip order and top to bottom within each. A new
///     section is one line here plus its own two files; its <see cref="PlayerOverviewSection.Page" /> and
///     <see cref="PlayerOverviewSection.Slot" /> place it.
/// </summary>
public static class PlayerOverviewSections
{
    /// <summary>Builds one of each section, in page order.</summary>
    /// <param name="services">What the sections can reach.</param>
    /// <param name="filter">The tab's filter bar, whose window the summary tiles follow.</param>
    public static IReadOnlyList<PlayerOverviewSection> Create(PlayerOverviewServices services, PlayerOverviewFilterViewModel filter) =>
    [
        new HeaderSectionViewModel(services),

        new SummarySectionViewModel(filter),
        new TrendsSectionViewModel(),
        new RankSectionViewModel(),

        new HistorySectionViewModel(services),
        new MapsSectionViewModel(),
        new TeammatesSectionViewModel(services),
        new HeadToHeadSectionViewModel(services),

        new CategoriesSectionViewModel(),
        new AimQualitySectionViewModel(),
        new CompareSectionViewModel(services),
        new BenchmarksSectionViewModel(services),

        new PersonalBestsSectionViewModel(services),
        new HighlightsSectionViewModel(services)
    ];
}

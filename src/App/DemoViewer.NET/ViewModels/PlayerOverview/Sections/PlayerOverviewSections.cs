namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>The Player Overview's sections, top to bottom. A new section is one line here plus its own two files.</summary>
public static class PlayerOverviewSections
{
    /// <summary>Builds one of each section, in page order.</summary>
    /// <param name="services">What the sections can reach.</param>
    /// <param name="filter">The tab's filter bar, which the summary shows above the panels it filters.</param>
    public static IReadOnlyList<PlayerOverviewSection> Create(PlayerOverviewServices services, PlayerOverviewFilterViewModel filter) =>
    [
        new HeaderSectionViewModel(services),
        new SummarySectionViewModel(filter),
        new HistorySectionViewModel(services),
        new MapsSectionViewModel(),
        new TrendsSectionViewModel(),
        new RankSectionViewModel(),
        new CategoriesSectionViewModel(),
        new PersonalBestsSectionViewModel(services),
        new HighlightsSectionViewModel(services),

        new TeammatesSectionViewModel(services),
        new HeadToHeadSectionViewModel(services)

        // Then compare, benchmarks, aim quality.
    ];
}

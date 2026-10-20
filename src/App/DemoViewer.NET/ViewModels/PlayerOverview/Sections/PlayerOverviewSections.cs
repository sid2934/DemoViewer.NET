namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>The Player Overview's sections, top to bottom. A new section is one line here plus its own two files.</summary>
public static class PlayerOverviewSections
{
    /// <summary>Builds one of each section, in page order.</summary>
    /// <param name="services">What the sections can reach.</param>
    public static IReadOnlyList<PlayerOverviewSection> Create(PlayerOverviewServices services) =>
    [
        new HeaderSectionViewModel(services),

        // Summary and filters go here, above the history they filter.
        new HistorySectionViewModel(services),

        // Later items, in this order: per-map, trends, rank history, stat categories, personal bests.
        new HighlightsSectionViewModel(services),

        // Teammates goes here.
        new HeadToHeadSectionViewModel(services)

        // Then compare, benchmarks, aim quality.
    ];
}

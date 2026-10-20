namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>The Player Overview's sections, top to bottom. A new section is one line here plus its own two files.</summary>
public static class PlayerOverviewSections
{
    /// <summary>Builds one of each section, in page order.</summary>
    /// <param name="services">What the sections can reach.</param>
    public static IReadOnlyList<PlayerOverviewSection> Create(PlayerOverviewServices services) =>
    [
        new HeaderSectionViewModel(services)

        // Later items, in this order: summary and filters, per-map, trends, rank history, stat categories,
        // personal bests, highlights, teammates, head-to-head, compare, benchmarks, aim quality.
    ];
}

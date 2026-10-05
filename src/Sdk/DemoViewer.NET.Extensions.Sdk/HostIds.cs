namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>Ids of host tabs, features and passes an extension can name.</summary>
public static class HostIds
{
    /// <summary>
    ///     The Library tab. Also a hub: a section naming it as its <c>HostId</c> joins the Library's view
    ///     switch beside the demo browser.
    /// </summary>
    public const string LibraryTab = "builtin.library";

    /// <summary>The Strat Book tab, a hub: a section naming it as its <c>HostId</c> joins the Strat Book rail.</summary>
    public const string StratBookHub = "stratbook.hub";

    /// <summary>The Match Overview tab.</summary>
    public const string MatchOverviewTab = "builtin.matchoverview";

    /// <summary>The Stats tab.</summary>
    public const string StatsTab = "builtin.stats";

    /// <summary>The 2D Playback tab.</summary>
    public const string Playback2DTab = "playback2d.viewport";

    /// <summary>The 2D Playback tab's feature: the parent for sub-features docked in it.</summary>
    public const string Playback2DFeature = "tab.playback2d";

    /// <summary>The Library tab's feature.</summary>
    public const string LibraryFeature = "tab.library";

    /// <summary>The Match Overview tab's feature.</summary>
    public const string MatchOverviewFeature = "tab.matchoverview";

    /// <summary>The library indexer's pass: name it in <c>after</c> to run on a demo once its cache record is written.</summary>
    public const string LibraryPass = "library";

    /// <summary>
    ///     The Round Facts pass: name it in <c>after</c> to run on a demo once its per-round, per-side rows
    ///     are written.
    /// </summary>
    public const string RoundFactsPass = "roundfacts";

    /// <summary>
    ///     The analysis facts pass: name it in <c>after</c> to run on a demo once the tables of every ruleset the
    ///     host runs beside the highlights are written (<see cref="IAnalysisFacts" />).
    /// </summary>
    public const string FactsPass = "facts";
}

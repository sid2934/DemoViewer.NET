#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Views.Analysis;
using DemoViewer.NET.Views.Diagnostics;
using DemoViewer.NET.Views.EntityTracking;
using DemoViewer.NET.Views.Library;
using DemoViewer.NET.Views.MatchOverview;
using DemoViewer.NET.Views.Parser;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     First-party module that contributes the four existing shell tabs as descriptors, so the
///     shell has exactly ONE code path for all tabs. The built-in
///     VMs stay constructed + wired in <c>MainViewModel</c>; this module's descriptors just REFERENCE
///     the already-built instances: no callback re-plumbing.
///     <para>
///         <b>DataContext model</b> (verified codebase constraint): three of the
///         four built-in views (Parser / Entity Tracking / Analysis) declare
///         <c>x:DataType="shell:MainViewModel"</c> and bind <c>{Binding TabVM.X}</c> THROUGH the shell,
///         so their descriptors' <c>DataContext</c> is the shell itself. The Diagnostics view binds
///         against its own VM, so its descriptor's <c>DataContext</c> is the Diagnostics VM.
///     </para>
/// </summary>
public sealed class BuiltInTabsModule : IWorkspaceModule
{
    private readonly object _diagnosticsViewModel;
    private readonly object _libraryViewModel;
    private readonly object? _matchOverviewViewModel;
    private readonly object _shell;
    private readonly object? _statsViewModel;

    /// <param name="shell">The shell (MainViewModel): DataContext for the three shell-routed views.</param>
    /// <param name="diagnosticsViewModel">The Diagnostics VM: DataContext for the Diagnostics view.</param>
    /// <param name="libraryViewModel">The Library VM: DataContext for the demo-browser landing tab.</param>
    /// <param name="statsViewModel">The Stats VM: DataContext for the scoreboard tab; null omits the tab.</param>
    /// <param name="matchOverviewViewModel">The Match Overview VM: DataContext for the landing tab; null omits it.</param>
    public BuiltInTabsModule(object shell, object diagnosticsViewModel, object libraryViewModel,
        object? statsViewModel = null, object? matchOverviewViewModel = null)
    {
        _shell = shell;
        _diagnosticsViewModel = diagnosticsViewModel;
        _libraryViewModel = libraryViewModel;
        _statsViewModel = statsViewModel;
        _matchOverviewViewModel = matchOverviewViewModel;
    }

    public string Id => "net.demoviewer.builtin";
    public string DisplayName => "Built-in Tabs";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        // Library is the landing tab: Order -1 sorts it before Parser so it's selected on startup.
        // Uses ViewModelFactory (not DataContext) so the VM receives OnActivated → its first folder scan.
        yield return new WorkspaceTabDescriptor
        {
            TabId = "builtin.library",
            Header = "Library",
            Order = -1,
            ViewModelFactory = () => (IWorkspaceTabViewModel)_libraryViewModel,
            ViewFactory = () => new LibraryTabView()
        };

        // Match Overview: the demo landing page. Order 0 + yielded before Parser (a stable ThenBy(Order) keeps
        // yield order for ties) so it sits right after Library and is the tab the shell switches to on open.
        if (_matchOverviewViewModel is not null)
        {
            yield return new WorkspaceTabDescriptor
            {
                TabId = "builtin.matchoverview",
                Header = "Match Overview",
                Order = 0,
                DataContext = _matchOverviewViewModel,
                ViewFactory = () => new MatchOverviewTabView()
            };
        }

        yield return new WorkspaceTabDescriptor
        {
            TabId = "builtin.parser",
            Header = "Parser",
            Order = 0,
            DataContext = _shell,
            ViewFactory = () => new ParserTabView()
        };

        yield return new WorkspaceTabDescriptor
        {
            TabId = "builtin.entity",
            Header = "Entity Tracking",
            Order = 1,
            DataContext = _shell,
            ViewFactory = () => new EntityTrackingTabView()
        };

        // Stats: the user-facing scoreboard (release plan P1-3.1). Sits before the developer-
        // oriented Analysis Engine tab: the dual-audience split (D4) keeps the graph debugger
        // untouched and gives the player/analyst persona a surface of their own.
        if (_statsViewModel is not null)
        {
            yield return new WorkspaceTabDescriptor
            {
                TabId = "builtin.stats",
                Header = "Stats",
                Order = 2,
                DataContext = _statsViewModel,
                ViewFactory = () => new StatsTabView()
            };
        }

        yield return new WorkspaceTabDescriptor
        {
            TabId = "builtin.analysis",
            Header = "Analysis Engine",
            Order = 3,
            DataContext = _shell,
            ViewFactory = () => new AnalysisTabView()
        };

        yield return new WorkspaceTabDescriptor
        {
            TabId = "builtin.diagnostics",
            Header = "Diagnostics",
            Order = 3,
            Placement = TabPlacement.Diagnostics,
            DataContext = _diagnosticsViewModel,
            ViewFactory = () => new DiagnosticsTabView()
        };
    }
}

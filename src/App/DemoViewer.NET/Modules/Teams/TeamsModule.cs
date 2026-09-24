#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Teams;
using DemoViewer.NET.Views.Teams;

#endregion

namespace DemoViewer.NET.Modules.Teams;

/// <summary>
///     The Teams tab module: Team Identity's one home. Contributes one Main-strip tab
///     (<c>"teams.browser"</c>, after Situations) whose VM lists the teams clustering found, the rosters
///     and members behind each, the demos per team, and the actions that make team truth the user's:
///     rename, set as us, merge, split, start roster, hide, not a team, recompute, and the me accounts.
///     <para>
///         <b>The ids are persisted keys.</b> <c>TabId "teams.browser"</c> and the feature id
///         <c>"tab.teams"</c> key the user's per-tab session state and feature overrides; the header
///         "Teams" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>. The VM is delegate-injected (the Highlights precedent):
///         the composition root supplies the service and the cache; the module references no shell.
///     </para>
/// </summary>
public sealed class TeamsModule : IWorkspaceModule
{
    private readonly Func<TeamsTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public TeamsModule(Func<TeamsTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.teams";
    public string DisplayName => "Teams";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = "teams.browser",
            Header = "Teams",
            Order = 5, // after Situations (4)
            Placement = TabPlacement.Main,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new TeamsTabView()
        };
    }
}

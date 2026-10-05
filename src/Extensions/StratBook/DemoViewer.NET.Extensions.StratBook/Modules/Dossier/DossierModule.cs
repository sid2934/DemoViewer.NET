#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Dossier;
using DemoViewer.NET.Views.Dossier;

#endregion

namespace DemoViewer.NET.Modules.Dossier;

/// <summary>
///     The Opponent Dossier module: the Dossier section of the Strat Book tab's rail (<see cref="BrowserTabId" />,
///     last on it) keyed by a Team Identity team: the Map Pool Record (the demo-derivable substitute for the veto
///     model), Setup Heatmaps By Buy, Opening Tendencies, Post-Plant And Retake, Situational Behaviour,
///     Period Diff, and the editable long form with its one-pager export.
///     <para>
///         <b>The ids are persisted keys.</b> <see cref="BrowserTabId" /> and <see cref="TabFeatureId" />
///         key the user's per-tab session state and <c>Features:Overrides:{id}</c>; the header
///         "Dossier" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>. The VM is delegate-injected (the Teams precedent): the
///         composition root supplies Team Identity, the cache and the veto store; the module references
///         no shell.
///     </para>
/// </summary>
public sealed class DossierModule : IWorkspaceModule
{
    /// <summary>The tab's feature id. A persisted key; never renamed. On by default for every category.</summary>
    public const string TabFeatureId = "tab.dossier";

    /// <summary>The section's tab id; its descriptor declares <see cref="TabFeatureId" /> directly.</summary>
    public const string BrowserTabId = "dossier.browser";

    private readonly Func<DossierTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public DossierModule(Func<DossierTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.dossier";
    public string DisplayName => "Dossier";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = BrowserTabId,
            Header = "Dossier",
            Order = 5, // after Review (4)
            HostId = HostIds.StratBookHub,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new DossierTabView()
        };
    }
}

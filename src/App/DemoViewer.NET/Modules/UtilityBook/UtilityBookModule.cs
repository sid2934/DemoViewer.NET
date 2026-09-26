#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.UtilityBook;
using DemoViewer.NET.Views.UtilityBook;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The Utility Book module: one Main-strip tab (<see cref="BrowserTabId" />, after the Strat Book) over
///     the <see cref="GrenadeIndex" />: every grenade in the indexed demos, clustered by where it landed,
///     searchable by map, kind, landing place and side. Lineup Cards join this tab when they land.
///     <para>
///         <b>The ids are persisted keys.</b> The module id, <see cref="BrowserTabId" /> and
///         <see cref="TabFeatureId" /> key the user's per-tab session state and <c>Features:Overrides:{id}</c>;
///         the header "Utility Book" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>. The VM is delegate-injected (the Highlights precedent):
///         the composition root supplies the index and the playback seam; the module references no shell.
///     </para>
/// </summary>
public sealed class UtilityBookModule : IWorkspaceModule
{
    /// <summary>The tab's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.utilitybook";

    /// <summary>The tab's id, mapped to <see cref="TabFeatureId" /> by the shell's tab gate.</summary>
    public const string BrowserTabId = "utilitybook.browser";

    private readonly Func<UtilityBookTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public UtilityBookModule(Func<UtilityBookTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.utilitybook";
    public string DisplayName => "Utility Book";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = BrowserTabId,
            Header = "Utility Book",
            Order = 9, // after the Strat Book (8)
            Placement = TabPlacement.Main,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new UtilityBookTabView()
        };
    }
}

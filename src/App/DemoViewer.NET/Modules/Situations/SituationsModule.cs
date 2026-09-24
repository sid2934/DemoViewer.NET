#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.Views.Situations;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The Situations tab module: Situation Search over the round index. Contributes one Main-strip
///     tab (<c>"situations.search"</c>, after Reels) whose VM owns the index status strip; the Query
///     Canvas, Result Cards, Overlay View and the Tolerance Slider are their own build items and plug
///     into this tab.
///     <para>
///         <b>The ids are persisted keys.</b> <c>TabId "situations.search"</c> and the feature id
///         <c>"tab.situations"</c> key the user's per-tab session state and feature overrides; the
///         header "Situations" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>, so activation drives the VM's lifecycle. The VM is
///         delegate-injected (the Highlights precedent): the composition root supplies the index, the
///         evaluator and the cache directly; the module references no shell.
///     </para>
/// </summary>
public sealed class SituationsModule : IWorkspaceModule
{
    private readonly Func<SituationsTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public SituationsModule(Func<SituationsTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.situations";
    public string DisplayName => "Situations";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = "situations.search",
            Header = "Situations",
            Order = 4, // after Reels (3)
            Placement = TabPlacement.Main,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new SituationsTabView()
        };
    }
}

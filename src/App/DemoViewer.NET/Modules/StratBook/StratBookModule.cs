#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.Modules.StratBook;

/// <summary>
///     The Strat Book module (strat-model.md §3.11): one Main-strip tab (<see cref="BrowserTabId" />, after the
///     Matrix) holding the book selector, the map and side filters, the strat list and the strat editor. The Step
///     Authoring canvas, the record, history and callouts panes join this tab as their items land; a strat has no
///     demo, so none of it lives in 2D Playback.
///     <para>
///         <b>The ids are persisted keys.</b> The module id, <see cref="BrowserTabId" /> and
///         <see cref="TabFeatureId" /> key the user's per-tab session state and <c>Features:Overrides:{id}</c>;
///         the header "Strat Book" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and retained), never
///         <c>DataContext</c>, with the VM delegate-injected at the composition root, the Highlights precedent; the
///         module references no shell. The shell reaches the open strat at exit through <see cref="Shutdown" />,
///         which builds nothing: a tab never opened has nothing to commit.
///     </para>
/// </summary>
public sealed class StratBookModule : IWorkspaceModule
{
    /// <summary>The tab's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.stratbook";

    /// <summary>The tab's id, mapped to <see cref="TabFeatureId" /> by the shell's tab gate.</summary>
    public const string BrowserTabId = "stratbook.browser";

    private readonly Func<StratBookTabViewModel> _viewModelFactory;
    private StratBookTabViewModel? _viewModel;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public StratBookModule(Func<StratBookTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.stratbook";
    public string DisplayName => "Strat Book";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = BrowserTabId,
            Header = "Strat Book",
            Order = 8, // after the Matrix (7)
            Placement = TabPlacement.Main,
            ViewModelFactory = () => _viewModel ??= _viewModelFactory(),
            ViewFactory = () => new StratBookTabView()
        };
    }

    /// <summary>
    ///     Commits the open strat and writes the strat index: shutdown is one of the commit triggers (§3.8). A no-op
    ///     when the tab was never built.
    /// </summary>
    public void Shutdown() => _viewModel?.Shutdown();
}

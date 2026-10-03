#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.Views.Situations;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The Situations module: Situation Search over the round index. Contributes the Situations section of
///     the Strat Book tab's rail (<c>"situations.search"</c>, after Strats) whose VM owns the index status
///     strip; the Query Canvas (with its tolerance slider), Result Cards and Overlay View plug into it.
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
///     <para>
///         <b>The badge.</b> Watched Situations' "N new" sits on the rail item through
///         <see cref="WorkspaceTabDescriptor.Badge" />. Subscribed on first activation, not at
///         registration: this module is contributed regardless of the pack's gate, so resolving the
///         service here would build the situation index and Team Identity on every launch. The accessor
///         costs nothing once activation happens, because the tab VM's own construction already built it.
///     </para>
/// </summary>
public sealed class SituationsModule : IWorkspaceModule
{
    private readonly Func<SituationsTabViewModel> _viewModelFactory;
    private readonly Func<WatchedSituationsService>? _watched;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    /// <param name="watched">Watched Situations, for the badge; called only after first activation. Null shows none.</param>
    public SituationsModule(Func<SituationsTabViewModel> viewModelFactory, Func<WatchedSituationsService>? watched = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _watched = watched;
    }

    /// <summary>"3 new", or null when nothing is new.</summary>
    /// <param name="newCount">New hits over every watch.</param>
    public static string? BadgeFor(int newCount) => newCount > 0 ? $"{newCount} new" : null;

    public string Id => "net.demoviewer.situations";
    public string DisplayName => "Situations";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        // ViewModelFactory is init-only, so the closure below cannot assign it after construction; it
        // reaches the descriptor through this ref, assigned once construction completes.
        WorkspaceTabDescriptor? tabRef = null;
        bool subscribed = false;

        WorkspaceTabDescriptor tab = new()
        {
            TabId = "situations.search",
            Header = "Situations",
            Order = 1, // after Strats (0)
            Placement = TabPlacement.StratBook,
            ViewFactory = () => new SituationsTabView(),
            ViewModelFactory = () =>
            {
                SituationsTabViewModel vm = _viewModelFactory();

                // Guarded so a re-activation (the VM is retained) never double-subscribes. The service
                // outlives the tab (both are container singletons), so there is nothing to unsubscribe.
                if (!subscribed && _watched is { } watchedAccessor)
                {
                    subscribed = true;
                    WatchedSituationsService watched = watchedAccessor();
                    tabRef!.Badge = BadgeFor(watched.NewCount);
                    watched.Changed += () => tabRef!.Badge = BadgeFor(watched.NewCount);
                }

                return vm;
            }
        };
        tabRef = tab;

        yield return tab;
    }
}

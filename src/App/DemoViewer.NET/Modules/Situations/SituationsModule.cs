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
///     <para>
///         <b>The badge.</b> Watched Situations' "N new" sits on the tab header through
///         <see cref="WorkspaceTabDescriptor.Badge" />, driven by the service rather than the VM: the
///         VM is built on first activation, and the badge has to show before the tab is ever opened.
///     </para>
/// </summary>
public sealed class SituationsModule : IWorkspaceModule
{
    private readonly Func<SituationsTabViewModel> _viewModelFactory;
    private readonly WatchedSituationsService? _watched;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    /// <param name="watched">Watched Situations, for the badge; null shows none.</param>
    public SituationsModule(Func<SituationsTabViewModel> viewModelFactory, WatchedSituationsService? watched = null)
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
        WorkspaceTabDescriptor tab = new()
        {
            TabId = "situations.search",
            Header = "Situations",
            Order = 4, // after Reels (3)
            Placement = TabPlacement.Main,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new SituationsTabView()
        };

        // The service outlives the tab (both are container singletons), so the subscription is for the
        // descriptor's life and needs no unsubscribe.
        if (_watched is { } watched)
        {
            tab.Badge = BadgeFor(watched.NewCount);
            watched.Changed += () => tab.Badge = BadgeFor(watched.NewCount);
        }

        yield return tab;
    }
}

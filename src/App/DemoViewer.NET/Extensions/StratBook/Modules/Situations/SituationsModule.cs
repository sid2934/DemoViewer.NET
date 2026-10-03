#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.Views.Situations;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The Situations module: Situation Search over the round index. Contributes the Situations section of
///     the Strat Book tab's rail (<c>"situations.search"</c>, after Strats) whose VM owns the index status
///     strip; the Query Canvas (with its tolerance slider), Result Cards and Overlay View plug into it.
///     <para>
///         <b>The ids are persisted keys.</b> <c>TabId "situations.search"</c> and <see cref="TabFeatureId" />
///         key the user's per-tab session state and feature overrides; the header "Situations" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>, so activation drives the VM's lifecycle. The VM is
///         delegate-injected (the Highlights precedent): the composition root supplies the index, the
///         evaluator and the cache directly; the module references no shell.
///     </para>
///     <para>
///         <b>The badge.</b> Watched Situations' "N new" sits on the rail item through
///         <see cref="WorkspaceTabDescriptor.Badge" />, driven by the service rather than the VM: the
///         VM is built on first activation, and the badge has to show before the tab is ever opened.
///         <see cref="StratBookPack.Contribute" /> resolves the service only while <see cref="TabFeatureId" />
///         is on, so a pack-off launch never builds it; a live toggle (the gate's own <c>Changed</c>)
///         clears a stale count going off and recomputes going on, the same shape item 1 gave
///         <c>SuggestedInboxModule</c>.
///     </para>
/// </summary>
public sealed class SituationsModule : IWorkspaceModule
{
    /// <summary>The section's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.situations";

    private readonly Func<SituationsTabViewModel> _viewModelFactory;
    private readonly WatchedSituationsService? _watched;
    private readonly Func<bool> _enabled;
    private readonly IFeatureGate? _gate;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    /// <param name="watched">Watched Situations, for the badge; null when the pack was off at composition.</param>
    /// <param name="enabled">
    ///     This section's own <see cref="TabFeatureId" /> gate, which already cascades off with the pack;
    ///     null resolves <see cref="IFeatureGate" /> from <see cref="App.Services" /> live, failing CLOSED
    ///     (not the usual fail-open default) since this is a pack-owned id: <see cref="StratBookPack.Contribute" />
    ///     always passes its own delegate, so the fallback here only matters when nothing has resolved.
    /// </param>
    /// <param name="gate">
    ///     The same gate as <paramref name="enabled" />, kept separately only for its <c>Changed</c> event:
    ///     a live toggle clears the badge going off and recomputes it going on, instead of leaving the last
    ///     value stale until the next unrelated <c>watched.Changed</c>. Null skips that push.
    /// </param>
    public SituationsModule(Func<SituationsTabViewModel> viewModelFactory, WatchedSituationsService? watched = null,
        Func<bool>? enabled = null, IFeatureGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _watched = watched;
        _gate = gate;
        _enabled = enabled ?? (() => App.Services?.GetService<IFeatureGate>()?.IsEnabled(TabFeatureId) ?? false);
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
            Order = 1, // after Strats (0)
            Placement = TabPlacement.StratBook,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new SituationsTabView()
        };

        if (_watched is { } watched)
        {
            if (_enabled())
            {
                tab.Badge = BadgeFor(watched.NewCount);
            }

            // Read live: a toggle mid-session stops this recompute without a restart.
            watched.Changed += () =>
            {
                if (_enabled())
                {
                    tab.Badge = BadgeFor(watched.NewCount);
                }
            };

            // The gate's own Changed, not just watched.Changed: going off clears a stale count rather than
            // leaving it until the next unrelated service write; going on recomputes without waiting for one.
            if (_gate is { } gate)
            {
                gate.Changed += (_, _) => tab.Badge = _enabled() ? BadgeFor(watched.NewCount) : null;
            }
        }

        yield return tab;
    }
}

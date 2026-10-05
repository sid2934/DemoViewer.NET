#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.Views.Review;

#endregion

namespace DemoViewer.NET.Modules.Review;

/// <summary>
///     The Review Queue module: the Review section of the Strat Book tab's rail (<c>"review.queue"</c>,
///     after Utility) over the shared
///     <see cref="ReviewQueue" /> that the Reels tray, Result Cards, a Matrix cell and a
///     pick at the playhead all send clips to.
///     <para>
///         <b>The ids are persisted keys.</b> <c>TabId "review.queue"</c> and <see cref="TabFeatureId" />
///         key the user's per-tab session state and feature overrides; the header "Review" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>. The VM is delegate-injected (the Highlights precedent):
///         the composition root supplies the queue and the seek seam; the module references no shell.
///     </para>
///     <para>
///         <b>The badge</b> is the count of clips nobody has marked reviewed, driven by the queue rather than
///         the VM, so clips sent from another surface show on the rail item before the Review section is ever
///         opened. <see cref="StratBookPack.Contribute" /> resolves the queue only while <see cref="TabFeatureId" />
///         is on; a live toggle (the gate's own <c>Changed</c>) clears a stale count going off and recomputes
///         going on, the same shape <c>SuggestedInboxModule</c> uses.
///     </para>
/// </summary>
public sealed class ReviewQueueModule : IWorkspaceModule
{
    /// <summary>The tab id. A persisted key; never renamed.</summary>
    public const string TabId = "review.queue";

    /// <summary>The tab's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.review";

    private readonly ReviewQueue? _queue;
    private readonly Func<ReviewQueueTabViewModel> _viewModelFactory;
    private readonly Func<bool> _enabled;
    private readonly IFeatureGate? _gate;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    /// <param name="queue">The shared queue, for the badge; null when the pack was off at composition.</param>
    /// <param name="enabled">
    ///     This section's own <see cref="TabFeatureId" /> gate, which already cascades off with the pack.
    /// </param>
    /// <param name="gate">
    ///     The same gate as <paramref name="enabled" />, kept separately only for its <c>Changed</c> event:
    ///     a live toggle clears the badge going off and recomputes it going on, instead of leaving the last
    ///     value stale until the next unrelated <c>queue.Changed</c>. Null skips that push.
    /// </param>
    public ReviewQueueModule(Func<ReviewQueueTabViewModel> viewModelFactory, Func<bool> enabled, ReviewQueue? queue = null,
        IFeatureGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _queue = queue;
        _gate = gate;
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
    }

    /// <summary>"12", or null with nothing to review.</summary>
    /// <param name="clipCount">Unreviewed clips in the queue.</param>
    public static string? BadgeFor(int clipCount) => clipCount > 0 ? clipCount.ToString(CultureInfo.InvariantCulture) : null;

    public string Id => "net.demoviewer.review";
    public string DisplayName => "Review";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        WorkspaceTabDescriptor tab = new()
        {
            TabId = TabId,
            Header = "Review",
            Order = 4, // after Utility (3)
            HostId = HostIds.StratBookHub,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new ReviewQueueTabView()
        };

        if (_queue is { } queue)
        {
            if (_enabled())
            {
                tab.Badge = BadgeFor(queue.UnreviewedCount);
            }

            // Read live: a toggle mid-session stops this recompute without a restart.
            queue.Changed += () =>
            {
                if (_enabled())
                {
                    tab.Badge = BadgeFor(queue.UnreviewedCount);
                }
            };

            // The gate's own Changed, not just queue.Changed: going off clears a stale count rather than
            // leaving it until the next unrelated queue write; going on recomputes without waiting for one.
            if (_gate is { } gate)
            {
                gate.Changed += (_, _) => tab.Badge = _enabled() ? BadgeFor(queue.UnreviewedCount) : null;
            }
        }

        yield return tab;
    }
}

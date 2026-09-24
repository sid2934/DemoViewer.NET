#region

using System.Globalization;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.Views.Review;

#endregion

namespace DemoViewer.NET.Modules.Review;

/// <summary>
///     The Review Queue module: one Main-strip tab (<c>"review.queue"</c>, after Teams) over the shared
///     <see cref="ReviewQueue" /> that the Reels tray, Result Cards, a Matrix cell and a
///     pick at the playhead all send clips to.
///     <para>
///         <b>The ids are persisted keys.</b> <c>TabId "review.queue"</c> and the feature id
///         <c>"tab.review"</c> key the user's per-tab session state and feature overrides; the header
///         "Review" is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>. The VM is delegate-injected (the Highlights precedent):
///         the composition root supplies the queue and the seek seam; the module references no shell.
///     </para>
///     <para>
///         <b>The badge</b> is the clip count, driven by the queue rather than the VM, so clips sent
///         from another tab show on the header before the Review tab is ever opened.
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

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    /// <param name="queue">The shared queue, for the badge; null shows none.</param>
    public ReviewQueueModule(Func<ReviewQueueTabViewModel> viewModelFactory, ReviewQueue? queue = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _queue = queue;
    }

    /// <summary>"12", or null with nothing queued.</summary>
    /// <param name="clipCount">Clips in the queue.</param>
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
            Order = 6, // after Teams (5)
            Placement = TabPlacement.Main,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new ReviewQueueTabView()
        };

        // The queue outlives the tab (both are container singletons), so the subscription is for the
        // descriptor's life and needs no unsubscribe.
        if (_queue is { } queue)
        {
            tab.Badge = BadgeFor(queue.ClipCount);
            queue.Changed += () => tab.Badge = BadgeFor(queue.ClipCount);
        }

        yield return tab;
    }
}

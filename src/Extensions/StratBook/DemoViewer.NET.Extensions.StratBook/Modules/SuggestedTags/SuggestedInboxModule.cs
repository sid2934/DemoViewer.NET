#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Extensions.StratBook.ViewModels.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Views.SuggestedTags;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>
///     The Suggested section of the Strat Book's rail: every demo's tag suggestions in one inbox. The ids are
///     persisted keys (the per-tab session state and the feature override); the header is display text. The badge
///     is the library's pending count from the proposals' stamps, so it needs no file read.
/// </summary>
public sealed class SuggestedInboxModule : IWorkspaceModule
{
    /// <summary>The section's tab id. A persisted key; never renamed.</summary>
    public const string TabId = "suggested.inbox";

    /// <summary>The section's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.suggested";

    private readonly ProposalStore? _proposals;
    private readonly Func<bool> _enabled;
    private readonly IExtensionFeatures? _gate;
    private readonly Func<SuggestedInboxViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the section's VM on first activation.</param>
    /// <param name="proposals">The proposals, whose stamps carry the badge's count; null shows none.</param>
    /// <param name="enabled">
    ///     This section's own <see cref="TabFeatureId" /> gate, which already cascades off with the pack.
    /// </param>
    /// <param name="gate">
    ///     The same gate as <paramref name="enabled" />, kept separately only for its <c>Changed</c> event:
    ///     a live toggle clears the badge going off and recomputes it going on, instead of leaving the last
    ///     value stale until the next unrelated <c>proposals.Changed</c>. Null skips that push and keeps the
    ///     poll-on-read behaviour.
    /// </param>
    public SuggestedInboxModule(Func<SuggestedInboxViewModel> viewModelFactory, Func<bool> enabled, ProposalStore? proposals = null,
        IExtensionFeatures? gate = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _proposals = proposals;
        _gate = gate;
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
    }

    public string Id => "net.demoviewer.suggested";
    public string DisplayName => "Suggested";
    public Version ContractVersion => new(1, 0, 0);

    /// <summary>"12", or null with nothing new.</summary>
    public static string? BadgeFor(int pending) => pending > 0 ? pending.ToString(CultureInfo.InvariantCulture) : null;

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        WorkspaceTabDescriptor tab = new()
        {
            TabId = TabId,
            Header = "Suggested",
            Order = 6, // after Dossier (5)
            HostId = HostIds.StratBookHub,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new SuggestedInboxView()
        };

        if (_proposals is { } proposals)
        {
            if (_enabled())
            {
                tab.Badge = BadgeFor(proposals.PendingTotal());
            }

            // Read live: a toggle mid-session stops this recompute without a restart.
            proposals.Changed += _ =>
            {
                if (_enabled())
                {
                    tab.Badge = BadgeFor(proposals.PendingTotal());
                }
            };

            // The gate's own Changed, not just proposals.Changed: going off clears a stale count rather than
            // leaving it until the next unrelated stamp; going on recomputes without waiting for one.
            if (_gate is { } gate)
            {
                gate.Changed += () => tab.Badge = _enabled() ? BadgeFor(proposals.PendingTotal()) : null;
            }
        }

        yield return tab;
    }
}

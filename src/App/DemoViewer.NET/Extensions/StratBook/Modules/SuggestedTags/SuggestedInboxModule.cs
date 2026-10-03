#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.SuggestedTags;
using DemoViewer.NET.Views.SuggestedTags;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The Suggested section of the Strat Book's rail: every demo's tag suggestions in one inbox. The ids are
///     persisted keys (the per-tab session state and the feature override); the header is display text. The badge
///     is the library's pending count from the demo index, so it needs no file read.
/// </summary>
public sealed class SuggestedInboxModule : IWorkspaceModule
{
    /// <summary>The section's tab id. A persisted key; never renamed.</summary>
    public const string TabId = "suggested.inbox";

    /// <summary>The section's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.suggested";

    private readonly DemoCacheStore? _cache;
    private readonly Func<bool> _enabled;
    private readonly IFeatureGate? _gate;
    private readonly Func<SuggestedInboxViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the section's VM on first activation.</param>
    /// <param name="cache">The demo index, for the badge; null shows none.</param>
    /// <param name="enabled">
    ///     This section's own <see cref="TabFeatureId" /> gate, which already cascades off with the pack
    ///     (its ParentId is the pack directly); null resolves <see cref="IFeatureGate" /> from
    ///     <see cref="App.Services" /> live, failing CLOSED (not the usual fail-open default) since this is
    ///     a pack-owned id: StratBookPack.Contribute always passes its own delegate, so the fallback here
    ///     only matters when nothing has resolved.
    /// </param>
    /// <param name="gate">
    ///     The same gate as <paramref name="enabled" />, kept separately only for its <c>Changed</c> event:
    ///     a live toggle clears the badge going off and recomputes it going on, instead of leaving the last
    ///     value stale until the next unrelated <c>cache.Changed</c>. Null skips that push and keeps the
    ///     poll-on-read behaviour.
    /// </param>
    public SuggestedInboxModule(Func<SuggestedInboxViewModel> viewModelFactory, DemoCacheStore? cache = null, Func<bool>? enabled = null,
        IFeatureGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _cache = cache;
        _gate = gate;
        _enabled = enabled ?? (() => App.Services?.GetService<IFeatureGate>()?.IsEnabled(TabFeatureId) ?? false);
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
            Placement = TabPlacement.StratBook,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new SuggestedInboxView()
        };

        if (_cache is { } cache)
        {
            if (_enabled())
            {
                tab.Badge = BadgeFor(cache.Index.Sum(e => e.SuggestionCount()));
            }

            // Read live: a toggle mid-session stops this recompute without a restart.
            cache.Changed += _ =>
            {
                if (_enabled())
                {
                    tab.Badge = BadgeFor(cache.Index.Sum(e => e.SuggestionCount()));
                }
            };

            // The gate's own Changed, not just cache.Changed: going off clears a stale count rather than
            // leaving it until the next unrelated cache write; going on recomputes without waiting for one.
            if (_gate is { } gate)
            {
                gate.Changed += (_, _) => tab.Badge = _enabled() ? BadgeFor(cache.Index.Sum(e => e.SuggestionCount())) : null;
            }
        }

        yield return tab;
    }
}

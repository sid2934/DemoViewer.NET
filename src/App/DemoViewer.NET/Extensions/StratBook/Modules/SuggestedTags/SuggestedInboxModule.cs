#region

using System.Globalization;
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
    private readonly Func<SuggestedInboxViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the section's VM on first activation.</param>
    /// <param name="cache">The demo index, for the badge; null shows none.</param>
    /// <param name="enabled">
    ///     This section's own <see cref="TabFeatureId" /> gate, which already cascades off with the pack
    ///     (its ParentId is the pack directly); null resolves <see cref="IFeatureGate" /> from
    ///     <see cref="App.Services" /> live.
    /// </param>
    public SuggestedInboxModule(Func<SuggestedInboxViewModel> viewModelFactory, DemoCacheStore? cache = null, Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
        _cache = cache;
        _enabled = enabled ?? (() => App.Services?.GetService<IFeatureGate>()?.IsEnabled(TabFeatureId) ?? true);
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
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new SuggestedInboxView()
        };

        if (_cache is { } cache)
        {
            if (_enabled())
            {
                tab.Badge = BadgeFor(cache.Index.Sum(e => e.SuggestionCount));
            }

            // Read live: a toggle mid-session stops this recompute without a restart.
            cache.Changed += _ =>
            {
                if (_enabled())
                {
                    tab.Badge = BadgeFor(cache.Index.Sum(e => e.SuggestionCount));
                }
            };
        }

        yield return tab;
    }
}

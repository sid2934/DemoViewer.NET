#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Features;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Every pack's contributions, collected once: <see cref="IExtension.Contribute" /> runs per pack when
///     this is first resolved, and every consumer (the module registry, the merged rules build) reads the
///     same collection. A container singleton so no consumer triggers a second <c>Contribute</c>.
/// </summary>
internal sealed class PackContributionSet
{
    /// <param name="packs">The packs, in registration order.</param>
    /// <param name="sp">The composition root each pack resolves its services from.</param>
    public PackContributionSet(IReadOnlyList<IExtension> packs, IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(sp);
        List<PackContributions> collected = new(packs.Count);
        foreach (IExtension pack in packs)
        {
            PackContributions contributions = new(pack, () => sp.GetExtensionContext(pack.Id));
            try
            {
                pack.Contribute(contributions, sp);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One extension failing its Contribute must not stop the app: it contributes nothing.
                AppLog.OperationFailed(Log, "contribute extension " + pack.Id, ex);
                contributions = new PackContributions(pack, () => sp.GetExtensionContext(pack.Id));
            }

            collected.Add(contributions);
        }

        Packs = collected;
    }

    /// <summary>One entry per pack, in pack order.</summary>
    public IReadOnlyList<PackContributions> Packs { get; }

    private static Microsoft.Extensions.Logging.ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ShellCategory);

    /// <summary>Every pack's host tabs, in pack order then contribution order. The shell builds its strip from this.</summary>
    public IReadOnlyList<HostTabContribution> HostTabs => [.. Packs.SelectMany(p => p.HostTabs)];

    /// <summary>Every pack's settings pages, in pack order then contribution order. Settings renders them under Extensions.</summary>
    public IReadOnlyList<SettingsPageContribution> SettingsPages => [.. Packs.SelectMany(p => p.SettingsPages)];

    /// <summary>Every pack's status-chip slots, in pack order then contribution order. The shell fills its chip strip from this.</summary>
    public IReadOnlyList<StatusChipContribution> StatusChips => [.. Packs.SelectMany(p => p.StatusChips)];

    /// <summary>Every pack's re-index estimate, in pack order. Settings watches only the first for the toggle notice.</summary>
    public IReadOnlyList<IReindexEstimate> ReindexEstimates => [.. Packs.SelectMany(p => p.ReindexEstimates)];

    /// <summary>Every pack's Library filter/badge contributions, in pack order. The Library hosts these generically.</summary>
    public IReadOnlyList<ILibraryContribution> LibraryContributions => [.. Packs.SelectMany(p => p.LibraryContributions)];

    /// <summary>Every pack's store and cache paths, in pack order then contribution order.</summary>
    public IReadOnlyList<StoreDescriptor> Stores => [.. Packs.SelectMany(p => p.Stores)];

    /// <summary>Every extension's Match Overview actions, in extension order.</summary>
    public IReadOnlyList<GatedDemoAction> DemoActions => [.. Packs.SelectMany(p => p.DemoActions)];

    /// <summary>Every pack's "delete extension data" action, in pack order. One row per entry in Settings.</summary>
    public IReadOnlyList<IExtensionDataRemoval> DataRemovals => [.. Packs.Select(p => p.DataRemovalContribution).OfType<IExtensionDataRemoval>()];

    /// <summary>
    ///     Every pack-owned ruleset with its owner's live gate answer. A null gate reads every pack as on,
    ///     the designer and unit-test path; a pack id the gate does not know resolves off, never on.
    /// </summary>
    public IReadOnlyList<GatedRuleset> GatedRulesets(IFeatureGate? gate) =>
    [
        .. Packs.SelectMany(p => p.Rulesets.Select(r =>
            new GatedRuleset(r.RulesetId, () => gate?.IsEnabled(p.Pack.FeatureId) ?? true)))
    ];
}

#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

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
        ExtensionFaults faults = sp.GetService<ExtensionFaults>() ?? ExtensionFaults.For(packs, static a => a());
        _collecting++;
        try
        {
            Collect(packs, sp, faults, collected);
        }
        finally
        {
            _collecting--;
        }

        Packs = collected;
        _sp = sp;
    }

    // Per thread: the container serializes a construction on another thread, so only a read from inside a
    // pack's Contribute on this thread would build a second set.
    [ThreadStatic] private static int _collecting;

    /// <summary>True while a set is collecting the packs' contributions on this thread.</summary>
    internal static bool Collecting => _collecting > 0;

    private static void Collect(IReadOnlyList<IExtension> packs, IServiceProvider sp, ExtensionFaults faults, List<PackContributions> collected)
    {
        foreach (IExtension pack in packs)
        {
            ExtensionGuard guard = faults.GuardFor(pack);
            PackContributions contributions = new(pack, () => sp.GetExtensionContext(pack.Id), UiThreadMarshal.Run, guard);

            // A pack whose registration failed has no services to contribute from.
            if (faults.StartupFailed(pack.Id))
            {
                collected.Add(contributions);
                continue;
            }

            try
            {
                pack.Contribute(contributions, sp);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One extension failing its Contribute must not stop the app: it contributes nothing.
                AppLog.OperationFailed(Log, "contribute extension " + pack.Id, ex);
                guard.Report("contribute", ex);
                contributions = new PackContributions(pack, () => sp.GetExtensionContext(pack.Id), UiThreadMarshal.Run, guard);
            }

            collected.Add(contributions);
        }
    }

    private readonly IServiceProvider _sp;

    /// <summary>One entry per pack, in pack order.</summary>
    public IReadOnlyList<PackContributions> Packs { get; }

    private static Microsoft.Extensions.Logging.ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ShellCategory);

    /// <summary>Every pack's rulesets, in pack order then contribution order. The merged rules read them as their middle layer.</summary>
    public IReadOnlyList<ContributedRuleset> Rulesets => [.. Packs.SelectMany(p => p.Rulesets)];

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

    /// <summary>Every extension's "delete extension data" action, in pack order. One row per entry in Settings.</summary>
    public IReadOnlyList<IExtensionDataRemoval> DataRemovals => [.. Packs.Select(p => new HostDataRemoval(p, _sp))];
}

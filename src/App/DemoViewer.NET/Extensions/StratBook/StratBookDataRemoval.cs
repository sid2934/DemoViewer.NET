#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's "delete extension data" action (item 24), built with the real <see cref="IServiceProvider" />
///     <see cref="StratBookPack.Contribute" /> receives so it can turn the pack off and resolve its own
///     stores directly, never through the <c>App.Services</c> locator.
/// </summary>
internal sealed class StratBookDataRemoval(IServiceProvider sp) : IPackDataRemoval
{
    // Every id StratBookPack.Contribute passes to contributions.Evaluator: the facets this pack's stamps
    // carry on DemoCacheRecord.PackStamps, kept in one place so the two cannot drift apart.
    internal static readonly string[] FacetIds =
    [
        RoundFactsEvaluator.EvaluatorId, RoundIndexEvaluator.EvaluatorId,
        SuggestedTagsService.EvaluatorId, GrenadeIndexEvaluator.EvaluatorId
    ];

    /// <inheritdoc />
    public string PackFeatureId => StratBookPack.PackFeatureId;

    /// <inheritdoc />
    public Task<PackDataInventory> InventoryAsync() =>
        Remover().InventoryAsync(StratBookStores.All, PackFeatureId, "Strat Book: count extension data");

    /// <inheritdoc />
    /// <remarks>
    ///     If the pack is on, writes the gate override off (the same write the master switch row makes) and
    ///     waits for <see cref="PackSwitch.Pending" />, which <see cref="Features.FeatureGate.RaiseChanged" />
    ///     updates inline on this call because the write runs on the UI thread: by the time the override
    ///     write returns, <see cref="PackSwitch" /> has already queued the release, so the wait is for a
    ///     real task, never a stale one. Only once that release has run does this delete the files, so
    ///     nothing the release still owns (a lineup flush, the signature cache) is deleted out from under it.
    /// </remarks>
    public async Task<PackDataRemovalResult> DeleteAsync()
    {
        IFeatureGate? gate = sp.GetService<IFeatureGate>();
        if (gate?.IsEnabled(PackFeatureId) == true)
        {
            sp.GetRequiredService<SettingsService>().Write(s => s.Features.Overrides[PackFeatureId] = false);
            if (sp.GetService<PackSwitch>() is { } packSwitch)
            {
                await packSwitch.Pending.ConfigureAwait(false);
            }
        }

        PackDataRemovalResult result = await Remover()
            .DeleteAsync(StratBookCache.PackId, StratBookStores.All, FacetIds, PackFeatureId, "Strat Book: delete extension data")
            .ConfigureAwait(false);
        if (result.Ran)
        {
            ReloadLiveStores();
        }

        return result;
    }

    private PackDataRemover Remover() =>
        new(sp.GetRequiredService<DemoCacheStore>(), AppPaths.ConfigRoot, AppPaths.DemoCacheDir, sp.GetService<IDemoProcessingQueue>());

    // None of these are released on disable (architecture doc §8: the pack's small user-truth stores are
    // "not released" because they are cheap and the user's own). Without this, re-enabling in the same
    // session would show what the delete just removed, and the next edit would save it back.
    private void ReloadLiveStores()
    {
        sp.GetService<StratStore>()?.RebuildIndexFromDisk();
        sp.GetService<TagStore>()?.RebuildIndexFromDisk();
        sp.GetService<TagPaletteStore>()?.Reload();
        sp.GetService<DossierNotesStore>()?.Reload();
        sp.GetService<VetoHistoryStore>()?.Reload();
        sp.GetService<WatchedSituationsService>()?.Reload();
        sp.GetService<StratMiningService>()?.ResetState();
        sp.GetService<ProfileStore>()?.Reload();
    }
}

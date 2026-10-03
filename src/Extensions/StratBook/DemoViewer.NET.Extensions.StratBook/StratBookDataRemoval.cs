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
internal sealed class StratBookDataRemoval : IPackDataRemoval
{
    // Every id StratBookPack.Contribute passes to contributions.Evaluator: the facets this pack's stamps
    // carry on DemoCacheRecord.PackStamps, kept in one place so the two cannot drift apart.
    internal static readonly string[] FacetIds =
    [
        RoundFactsEvaluator.EvaluatorId, RoundIndexEvaluator.EvaluatorId,
        SuggestedTagsService.EvaluatorId, GrenadeIndexEvaluator.EvaluatorId
    ];

    private readonly IServiceProvider _sp;
    private readonly Action? _afterReleaseForTests;

    public StratBookDataRemoval(IServiceProvider sp) : this(sp, null)
    {
    }

    /// <param name="sp">The real composition root, captured at <see cref="StratBookPack.Contribute" /> time.</param>
    /// <param name="afterReleaseForTests">
    ///     Test seam only: runs synchronously right after the release wait resolves, before the re-check and
    ///     the remover call, so a test can flip the gate back on in exactly the window the blocker closes.
    /// </param>
    internal StratBookDataRemoval(IServiceProvider sp, Action? afterReleaseForTests)
    {
        _sp = sp;
        _afterReleaseForTests = afterReleaseForTests;
    }

    /// <inheritdoc />
    public string PackFeatureId => StratBookPack.PackFeatureId;

    /// <inheritdoc />
    public Task<PackDataInventory> InventoryAsync() =>
        Remover().InventoryAsync(StratBookStores.All, PackFeatureId, "Strat Book: count extension data");

    /// <inheritdoc />
    /// <remarks>
    ///     If the pack is on, writes the gate override off (the same write the master switch row makes) and
    ///     waits for <see cref="PackSwitch.Pending" />, which <c>FeatureGate.RaiseChanged</c>
    ///     updates inline on this call because the write runs on the UI thread: by the time the override
    ///     write returns, <see cref="PackSwitch" /> has already queued the release, so the wait is for a
    ///     real task, never a stale one. Only once that release has run does this delete the files, so
    ///     nothing the release still owns (a lineup flush, the signature cache) is deleted out from under it.
    ///     <para>
    ///         That wait alone is not enough: a re-enable landing between the wait resolving and the delete
    ///         actually running (its own job may sit behind the re-enable's attach on the same serial) would
    ///         otherwise run the delete against a fully live pack. Two checks close it: the gate is read
    ///         again right here, before the remover is even called, and <see cref="PackDataRemover.DeleteAsync" />
    ///         takes a <c>stillOff</c> predicate it evaluates a second time inside the queued job itself,
    ///         right before any file is touched.
    ///     </para>
    /// </remarks>
    public async Task<PackDataRemovalResult> DeleteAsync()
    {
        IFeatureGate? gate = _sp.GetService<IFeatureGate>();
        if (gate?.IsEnabled(PackFeatureId) == true)
        {
            _sp.GetRequiredService<SettingsService>().Write(s => s.Features.Overrides[PackFeatureId] = false);
            if (_sp.GetService<PackSwitch>() is { } packSwitch)
            {
                await packSwitch.Pending.ConfigureAwait(false);
            }
        }

        _afterReleaseForTests?.Invoke();

        if (gate?.IsEnabled(PackFeatureId) == true)
        {
            return PackDataRemovalResult.NotRun;
        }

        PackDataRemovalResult result = await Remover()
            .DeleteAsync(StratBookCache.PackId, StratBookStores.All, FacetIds, PackFeatureId, "Strat Book: delete extension data",
                stillOff: () => gate?.IsEnabled(PackFeatureId) != true)
            .ConfigureAwait(false);
        if (result.Ran)
        {
            ReloadLiveStores();
        }

        return result;
    }

    private PackDataRemover Remover() =>
        new(_sp.GetRequiredService<DemoCacheStore>(), AppPaths.ConfigRoot, AppPaths.DemoCacheDir, _sp.GetService<IDemoProcessingQueue>());

    // None of these are released on disable (architecture doc §8: the pack's small user-truth stores are
    // "not released" because they are cheap and the user's own). Without this, re-enabling in the same
    // session would show what the delete just removed, and the next edit would save it back.
    private void ReloadLiveStores()
    {
        _sp.GetService<StratStore>()?.RebuildIndexFromDisk();
        _sp.GetService<TagStore>()?.RebuildIndexFromDisk();
        _sp.GetService<TagPaletteStore>()?.Reload();
        _sp.GetService<DossierNotesStore>()?.Reload();
        _sp.GetService<VetoHistoryStore>()?.Reload();
        _sp.GetService<WatchedSituationsService>()?.Reload();
        _sp.GetService<StratMiningService>()?.ResetState();
        _sp.GetService<ProfileStore>()?.Reload();
    }
}

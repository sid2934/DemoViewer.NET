#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     "Delete extension data" for one extension. The extension is switched off first and its release awaited,
///     so nothing it still holds is deleted from under it; the gate is read again before the delete is queued
///     and once more inside the queued job, so a switch back on in between cancels the delete untouched.
///     Then it deletes the extension's own folders (config and cache, the per-demo data included), the
///     stores it declared, its payload, its passes' stamps on the demo cache records and the analysis facts
///     of its rulesets, or runs the extension's own removal when it contributed one. Afterwards the per-demo
///     index is forgotten and the extension's after-delete callbacks run on the UI thread.
/// </summary>
internal sealed class HostDataRemoval : IExtensionDataRemoval
{
    private readonly Action? _afterReleaseForTests;
    private readonly PackContributions _pack;
    private readonly IServiceProvider _sp;

    /// <param name="pack">The extension's contributions.</param>
    /// <param name="sp">The composition root.</param>
    /// <param name="afterReleaseForTests">Runs right after the release wait, before the re-check: a test's way into that window.</param>
    public HostDataRemoval(PackContributions pack, IServiceProvider sp, Action? afterReleaseForTests = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(sp);
        _pack = pack;
        _sp = sp;
        _afterReleaseForTests = afterReleaseForTests;
    }

    /// <inheritdoc />
    public string FeatureId => _pack.DataRemovalContribution?.FeatureId ?? _pack.Pack.FeatureId;

    /// <summary>The extension's own folders, then what it declared.</summary>
    public IReadOnlyList<StoreDescriptor> Stores
    {
        get
        {
            string folder = ExtensionFolders.DataDirectoryName + "/" + ExtensionFolders.SafeName(_pack.Pack.Id);
            return
            [
                new StoreDescriptor("extension-files", "Extension files", StoreRoot.Config, [folder], IsUserWork: true),
                new StoreDescriptor("extension-cache", "Extension cache", StoreRoot.Cache, [folder], IsUserWork: false),
                .. _pack.Rulesets.Select(r => new StoreDescriptor(StampedFacts.StampId(r.RulesetId), "Analysis facts of " + r.RulesetId,
                    StoreRoot.Cache, ["demos/*" + StampedFacts.RulesetSuffix(r.RulesetId)], IsUserWork: false)),
                .. _pack.Stores
            ];
        }
    }

    /// <inheritdoc />
    public Task<ExtensionDataInventory> InventoryAsync() =>
        _pack.DataRemovalContribution is { } custom
            ? custom.InventoryAsync()
            : Remover().InventoryAsync(Stores, _pack.Pack.FeatureId, Label + ": count extension data");

    /// <inheritdoc />
    public async Task<ExtensionDataRemovalResult> DeleteAsync()
    {
        IFeatureGate? gate = _sp.GetService<IFeatureGate>();
        string featureId = _pack.Pack.FeatureId;
        if (gate?.IsEnabled(featureId) == true)
        {
            _sp.GetRequiredService<SettingsService>().Write(s => s.Features.Overrides[featureId] = false);
            if (_sp.GetService<PackSwitch>() is { } packSwitch)
            {
                await packSwitch.Pending.ConfigureAwait(true);
            }
        }

        _afterReleaseForTests?.Invoke();
        if (gate?.IsEnabled(featureId) == true)
        {
            return ExtensionDataRemovalResult.NotRun;
        }

        ExtensionDataRemovalResult result = _pack.DataRemovalContribution is { } custom
            ? await custom.DeleteAsync().ConfigureAwait(true)
            : await Remover().DeleteAsync(_pack.Pack.Id, Stores,
                [.. _pack.Passes.Select(p => p.Id), .. _pack.Rulesets.Select(r => StampedFacts.StampId(r.RulesetId))], featureId,
                Label + ": delete extension data", () => gate?.IsEnabled(featureId) != true).ConfigureAwait(true);
        if (result.Ran)
        {
            (_pack.Context.Data as ExtensionDemoDataStore)?.Reset();
            _pack.RaiseDataDeleted();
        }

        return result;
    }

    // The extension's own name, as its master switch shows it.
    private string Label =>
        _pack.Guard.Run("feature list", () => _pack.Pack.Features.FirstOrDefault(f => f.Id == _pack.Pack.FeatureId)?.Label, null)
        ?? _pack.Pack.Id;

    private PackDataRemover Remover() =>
        new(_sp.GetRequiredService<DemoCacheStore>(), AppPaths.ConfigRoot, AppPaths.DemoCacheDir, _pack.Context.Jobs);
}

#region

using System.Text;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     "Delete extension data" without a removal of the extension's own: the extension's folders (its per-demo
///     data included) and its declared stores go, the per-demo index forgets them, and the extension hears
///     about it afterwards. Another extension's folders are untouched.
/// </summary>
[NotInParallel]
public class HostDataRemovalTests
{
    [Test]
    public async Task TheHostsDelete_TakesTheExtensionsFoldersAndStores_AndNothingOfAnotherExtension()
    {
        string id = "dev.example.remove." + Guid.NewGuid().ToString("N")[..8];
        string otherId = id + ".other";
        string configRoot = AppPaths.ConfigRoot!;
        string cacheRoot = AppPaths.DemoCacheDir!;
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        ExtensionContext context = new(pack, sp);
        sp.GetRequiredService<DemoCacheStore>().Update("/demos/a.dem", 1, 1, r => r.Sha256 = "aaaa");

        await context.Storage.WriteAtomicAsync(StoreRoot.Config, "notes.json", Encoding.UTF8.GetBytes("{}"));
        await context.Storage.WriteAtomicAsync(StoreRoot.Cache, "index.bin", new byte[] { 1 });
        context.Data.Write("/demos/a.dem", new DemoDataWrite("rounds", 1, "fp", new byte[] { 1, 2 }));
        string declared = Path.Combine(configRoot, id + "-legacy.json");
        await File.WriteAllTextAsync(declared, "{}");
        string otherFolder = Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName, otherId);
        Directory.CreateDirectory(otherFolder);
        await File.WriteAllTextAsync(Path.Combine(otherFolder, "keep.json"), "{}");

        PackContributions contributions = new(pack, () => context);
        contributions.Store(new StoreDescriptor("legacy", "Legacy", StoreRoot.Config, [id + "-legacy.json"], IsUserWork: true));
        int deleted = 0;
        contributions.DataDeleted(() => deleted++);
        HostDataRemoval removal = new(contributions, sp);

        try
        {
            ExtensionDataInventory before = await removal.InventoryAsync();
            await Assert.That(before.Items.Sum(i => i.FileCount)).IsEqualTo(5)
                .Because("the config file, the cache file, the data file, the data index and the declared store");

            ExtensionDataRemovalResult result = await removal.DeleteAsync();

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(configRoot, ExtensionFolders.DataDirectoryName, id))).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName, id))).IsFalse();
                await Assert.That(File.Exists(declared)).IsFalse();
                await Assert.That(context.Data.Stamp("/demos/a.dem", "rounds")).IsNull().Because("the index was forgotten with the files");
                await Assert.That(File.Exists(Path.Combine(otherFolder, "keep.json"))).IsTrue();
                await Assert.That(deleted).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(otherFolder, true);
        }
    }

    [Test]
    public async Task TheHostsDelete_ListsTheFactsOfTheExtensionsRulesets()
    {
        string id = "dev.example.remove." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp));
        contributions.Ruleset(new RulesetContribution("kills", () => new MemoryStream()));

        StoreDescriptor? facts = new HostDataRemoval(contributions, sp).Stores
            .FirstOrDefault(s => s.Id == "facts:" + RulesetContribution.QualifiedId(id, "kills"));

        await Assert.That(facts).IsNotNull();
        await Assert.That(facts!.Paths).IsEquivalentTo([$"demos/*.facts.{RulesetContribution.QualifiedId(id, "kills")}.json.gz"]);
    }

    [Test]
    public async Task ARulesetTheManifestDoesNotList_IsDroppedAndReported()
    {
        string id = "dev.example.unlisted." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp), guard: guard);

        contributions.Ruleset(new RulesetContribution("deaths", () => new MemoryStream()));
        contributions.Ruleset(new RulesetContribution("kills", () => new MemoryStream()));

        using (Assert.Multiple())
        {
            await Assert.That(contributions.Rulesets.Select(r => r.RulesetId))
                .IsEquivalentTo([RulesetContribution.QualifiedId(id, "kills")]);
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
        }
    }

    private sealed class Pack(string id) : IExtension, IManifestSource
    {
        public ExtensionManifest Manifest { get; } = ExtensionManifest.Parse($$"""
            {
              "id": "{{id}}", "name": "Remove", "version": "1.0.0", "assembly": "Remove.dll", "entryType": "Remove.Pack",
              "requiresHost": "^1.0", "requiresCs2DemoKit": "0.13.0-beta0001", "rulesets": ["kills"]
            }
            """);

        public string Id => id;
        public string FeatureId => "pack." + id;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

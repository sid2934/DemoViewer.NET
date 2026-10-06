#region

using System.Reflection;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     What keeps one extension out of another's stores and out of the host's: a context resolves only for its
///     own code, Register cannot replace a registration it does not own, a declared store cannot name another
///     owner's files, and a third-party build against the app's own assembly does not load.
/// </summary>
public class ExtensionIsolationTests
{
    [Test]
    public async Task OneExtensionsCode_AskingForAnothersContext_IsRefused()
    {
        StubExtension stub = new("pack.isolation");
        StratBookPack stratBook = new();
        ServiceCollection services = new();
        IExtensionContext stubContext = DispatchProxy.Create<IExtensionContext, NullProxy>();
        services.AddKeyedSingleton(stub.Id, stubContext);
        using ServiceProvider provider = services.BuildServiceProvider();
        ExtensionContextAccess access = new(provider, [stub, stratBook]);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            access.Resolve(stub.Id, typeof(StratBookPack).Assembly));

        using (Assert.Multiple())
        {
            await Assert.That(refused.Message).Contains(stratBook.Id);
            await Assert.That(access.Resolve(stub.Id, typeof(StubExtension).Assembly)).IsSameReferenceAs(stubContext)
                .Because("the extension's own code resolves its context");
            await Assert.That(access.Resolve(stub.Id, typeof(App).Assembly)).IsSameReferenceAs(stubContext)
                .Because("the app's own code is no extension");
        }
    }

    [Test]
    public async Task Register_ThatAddsAContextOrAnotherLifecycleOrAHostService_IsRefused()
    {
        StubExtension pack = new("pack.isolation");
        ServiceDescriptor[] existing =
        [
            ServiceDescriptor.Singleton<IFormatProvider>(System.Globalization.CultureInfo.InvariantCulture),
            ServiceDescriptor.KeyedSingleton<IExtensionContext>("other.id", (_, _) => null!)
        ];

        using (Assert.Multiple())
        {
            await Assert.That(App.RegisterOverride(pack,
                [ServiceDescriptor.KeyedSingleton<IExtensionContext>("other.id", (_, _) => null!)], existing)).IsNotNull();
            await Assert.That(App.RegisterOverride(pack,
                [ServiceDescriptor.KeyedSingleton<IExtensionLifecycle>("other.id", (_, _) => null!)], existing)).IsNotNull();
            await Assert.That(App.RegisterOverride(pack,
                [ServiceDescriptor.Singleton<IFormatProvider>(System.Globalization.CultureInfo.CurrentCulture)], existing)).IsNotNull();
            await Assert.That(App.RegisterOverride(pack,
                [
                    ServiceDescriptor.KeyedSingleton<IExtensionLifecycle>(pack.Id, (_, _) => null!),
                    ServiceDescriptor.Singleton<StubExtension>(pack)
                ], existing)).IsNull().Because("its own lifecycle and its own types are its to register");
        }
    }

    [Test]
    [Arguments("dev.example.store", StoreRoot.Config, "dev.example.store.json", true)]
    [Arguments("dev.example.store", StoreRoot.Cache, "dev.example.store-old/index.bin", true)]
    [Arguments("dev.example.store", StoreRoot.Config, "extension-settings", false)]
    [Arguments("dev.example.store", StoreRoot.Config, "extension-data", false)]
    [Arguments("dev.example.store", StoreRoot.Config, "strats", false)]
    [Arguments("dev.example.store", StoreRoot.Cache, "demos", false)]
    [Arguments("dev.example.store", StoreRoot.Config, "dev.example.storex.json", false)]
    [Arguments("dev.example.store", StoreRoot.Config, "../dev.example.store", false)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Config, "strats", true)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Cache, "demos/*.grenades.json.gz", true)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Config, "extension-settings", false)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Config, "Extension-Data/x", false)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Cache, "demos", false)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Cache, "demos/*.json.gz", false)]
    [Arguments("net.demoviewer.pack.x", StoreRoot.Cache, "demos/*", false)]
    public async Task ADeclaredStore_MayNameOnlyItsOwnersFiles(string packId, StoreRoot root, string path, bool accepted)
    {
        string? refusal = PackContributions.StoreRefusal(packId, new StoreDescriptor("s", "S", root, [path], IsUserWork: false));
        await Assert.That(refusal is null).IsEqualTo(accepted);
    }

    [Test]
    public async Task TheHostsDelete_ListsTheExtensionsSettingsFile()
    {
        StubExtension pack = new("pack.isolation");
        PackContributions contributions = new(pack, () => null!);
        HostDataRemoval removal = new(contributions, new ServiceCollection().BuildServiceProvider());

        StoreDescriptor? settings = removal.Stores.FirstOrDefault(s => s.Id == "extension-settings");

        await Assert.That(settings?.Paths).IsEquivalentTo([ExtensionSettingsStore.DirectoryName + "/" + pack.Id + ".json"]);
    }

    [Test]
    public async Task AThirdPartyBuildAgainstTheAppsAssembly_IsRefused()
    {
        ExtensionManifest manifest = ExtensionManifest.Parse("""
            {
              "id": "dev.example.app-reference",
              "name": "App reference",
              "version": "1.0.0",
              "assembly": "AppReference.dll",
              "entryType": "AppReference.Pack",
              "requiresHost": "^1.0",
              "requiresCs2DemoKit": "0.13.0-beta0001"
            }
            """);
        ExtensionCandidate candidate = new(Path.GetTempPath(), manifest);

        using (Assert.Multiple())
        {
            await Assert.That(ExternalExtensions.CheckAppReference(candidate, typeof(ExtensionIsolationTests).Assembly)?.Failure)
                .IsEqualTo(LoadFailure.ReferencesApp);
            await Assert.That(ExternalExtensions.CheckAppReference(candidate, typeof(IExtension).Assembly)).IsNull()
                .Because("the SDK is the contract an extension builds against");
        }
    }

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}

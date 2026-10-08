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
        services.AddKeyedSingleton(ExtensionContextAccess.KeyFor(stub.Id), stubContext);
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
            await Assert.That(provider.GetKeyedService<IExtensionContext>(stub.Id)).IsNull()
                .Because("a lookup by the plain id skips the owner check, so it finds nothing");
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
    [Arguments("settings.json", StoreRoot.Config, "settings.json", false)]
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

    [Test]
    [NotInParallel]
    public async Task APassThatTakesACorePassesId_FailsItsExtensionsStartup_NotTheApps()
    {
        TakesTheFactsPass pack = new();
        await HeadlessSession.RunOnUi(async () =>
        {
            ServiceCollection services = App.ComposeServices(new Services.DesktopWindowService(() => null), [pack]);
            await using ServiceProvider provider = services.BuildServiceProvider();
            ExtensionFaults faults = provider.GetRequiredService<ExtensionFaults>();

            provider.GetRequiredService<Services.DemoProcessing.DemoScheduler>().ValidatePasses();

            await Assert.That(faults.StartupFailed(pack.Id)).IsTrue();
        });
    }

    private sealed class TakesTheFactsPass : IExtension
    {
        public string Id => "dev.example.facts-thief";
        public string FeatureId => "dev.example.facts-thief";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services) =>
            contributions.Pass(HostIds.FactsPass, () => new NoPass());
    }

    private sealed class NoPass : IExtensionPass
    {
        public string Id => HostIds.FactsPass;

        public DemoInterest Interest(string demoPath) => DemoInterest.No;

        public void Run(IPassContext context)
        {
        }
    }

    [Test]
    public async Task AContributionGatedOnAFeatureItDoesNotOwn_FollowsTheExtensionsSwitch_AndIsReported()
    {
        OwnsOneFeature pack = new();
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        PackContributions contributions = new(pack, () => null!, guard: guard);

        contributions.DemoAction(new DemoAction("dev.example.owner.core", "Core", "", _ => true, _ => { }, "playback2d"));
        contributions.DemoAction(new DemoAction("dev.example.owner.own", "Own", "", _ => true, _ => { }, OwnsOneFeature.Sub));
        contributions.DemoAction(new DemoAction("dev.example.owner.none", "None", "", _ => true, _ => { }));

        using (Assert.Multiple())
        {
            await Assert.That(contributions.DemoActions.Select(a => a.FeatureId))
                .IsEquivalentTo([pack.FeatureId, OwnsOneFeature.Sub, pack.FeatureId], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1)
                .Because("only the contribution naming another owner's feature is reported");
        }
    }

    [Test]
    public async Task AThirdPartyIdWithoutItsExtensionsPrefix_IsLeftOut()
    {
        OwnsOneFeature pack = new();
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        PackContributions contributions = new(pack, () => null!, guard: guard);

        contributions.DemoAction(new DemoAction("hello.greet", "Greet", "", _ => true, _ => { }));
        contributions.HubTab(new HubTabContribution("hello.hub", "Hub", 50, "HUB"));
        contributions.DemoAction(new DemoAction("dev.example.owner.greet", "Greet", "", _ => true, _ => { }));

        using (Assert.Multiple())
        {
            await Assert.That(contributions.DemoActions.Select(a => a.Action.Id)).IsEquivalentTo(["dev.example.owner.greet"]);
            await Assert.That(contributions.HubTabs).IsEmpty();
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(2);
        }
    }

    private sealed class OwnsOneFeature : IExtension
    {
        public const string Sub = "dev.example.owner.sub";
        public string Id => "dev.example.owner";
        public string FeatureId => "dev.example.owner";

        public IEnumerable<ExtensionFeature> Features =>
        [
            new(FeatureId, ExtensionFeatureKind.Extension, "Owner", "", null, AudienceDefaults.Everyone),
            new(Sub, ExtensionFeatureKind.SubFeature, "Sub", "", FeatureId, AudienceDefaults.Everyone)
        ];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}

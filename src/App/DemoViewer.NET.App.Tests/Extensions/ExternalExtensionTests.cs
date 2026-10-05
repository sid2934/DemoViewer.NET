#region

using System.Runtime.Loader;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Third-party extensions end to end, over the sample under samples/Extensions/HelloExtension: built against the
///     SDK alone, unsigned, installed in a temp extensions folder.
/// </summary>
[NotInParallel]
public class ExternalExtensionTests
{
    private const string HelloId = "dev.example.hello";

    private static string SampleOutput()
    {
        string configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "HelloExtension", configuration));
    }

    // <root>/extensions/<id>/<version>/ holding the sample's build output.
    private static string Install(string configRoot, string id = HelloId, string version = "1.0.0")
    {
        string target = Path.Combine(configRoot, ExtensionLoader.ExtensionsDirectoryName, id, version);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(SampleOutput()))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "dv-ext-" + Guid.NewGuid().ToString("N"));

    private static ExternalResolution Resolve(string root, bool allowUnverified, IReadOnlyList<IExtension>? shipped = null) =>
        ExternalExtensions.Resolve(root, shipped ?? [], ExtensionHost.Current, new ExternalTrust(PublisherKeys.Current, allowUnverified));

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    [Test]
    public async Task TheSample_IsBuiltAgainstTheSdkAlone()
    {
        string sample = Path.Combine(SampleOutput(), "HelloExtension.dll");
        string[] references =
        [
            .. System.Reflection.Assembly.LoadFile(sample).GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        await Assert.That(references).IsEquivalentTo(["DemoViewer.NET.Extensions.Sdk", "DemoViewer.NET.Modules.Abstractions"],
            TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    [Test]
    public async Task AnUnsignedExtension_IsRefusedByDefault_NamingTheSetting()
    {
        string root = NewRoot();
        try
        {
            Install(root);
            ExternalResolution result = Resolve(root, allowUnverified: false);

            using (Assert.Multiple())
            {
                await Assert.That(result.Loaded).IsEmpty();
                LoadOutcome refused = result.Rejected.Single();
                await Assert.That(refused.Failure).IsEqualTo(LoadFailure.Unverified);
                await Assert.That(refused.External).IsTrue();
                await Assert.That(refused.UserMessage).Contains(ExternalExtensions.AllowUnverifiedLabel);
                await Assert.That(refused.UserMessage).StartsWith("Hello 1.0.0 was not loaded");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task WithUnverifiedAllowed_TheExtensionLoads_InItsOwnContext_SharingTheSdk()
    {
        string root = NewRoot();
        try
        {
            string installed = Install(root);
            ExternalResolution result = Resolve(root, allowUnverified: true);

            PackStatus status = result.Loaded.Single();
            AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(status.Pack.GetType().Assembly);
            using (Assert.Multiple())
            {
                await Assert.That(result.Rejected).IsEmpty();
                await Assert.That(status.Pack.Id).IsEqualTo(HelloId);
                await Assert.That(status.IsCompatible).IsTrue();
                await Assert.That(status.Source).IsEqualTo(new PackSource.External(installed, false));
                await Assert.That(context).IsTypeOf<ExternalLoadContext>();
                await Assert.That(status.Pack.GetType().GetInterfaces()).Contains(typeof(IExtension))
                    .Because("the SDK resolves from the app, so the extension implements the host's own IExtension");
                await Assert.That(FeatureCatalog.Build([status.Pack]).Select(d => d.Id)).Contains("pack.hello");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task AnIdInTheReservedPrefix_IsRefused_EvenWithUnverifiedAllowed()
    {
        string root = NewRoot();
        try
        {
            string installed = Install(root, "net.demoviewer.hello");
            string manifest = Path.Combine(installed, ExtensionManifest.FileName);
            await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace(HelloId, "net.demoviewer.hello", StringComparison.Ordinal));

            ExternalResolution result = Resolve(root, allowUnverified: true);

            await Assert.That(result.Loaded).IsEmpty();
            await Assert.That(result.Rejected.Single().Failure).IsEqualTo(LoadFailure.ReservedId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task ABrokenSignature_IsRefused_EvenWithUnverifiedAllowed()
    {
        string root = NewRoot();
        try
        {
            string installed = Install(root);
            await File.WriteAllTextAsync(Path.Combine(installed, ExtensionSignature.FileName), "{ \"keyId\": \"x\" }");

            ExternalResolution result = Resolve(root, allowUnverified: true);

            await Assert.That(result.Loaded).IsEmpty();
            await Assert.That(result.Rejected.Single().Failure).IsEqualTo(LoadFailure.Untrusted);
            await Assert.That(ExternalExtensions.Classify(installed, PublisherKeys.Current).Trust).IsEqualTo(ExternalExtensions.Trust.Invalid);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task AFeatureIdAlreadyTaken_IsAConflict_NotACrash()
    {
        string root = NewRoot();
        try
        {
            Install(root);
            ExternalResolution result = Resolve(root, allowUnverified: true, [new TakenFeatureId()]);

            await Assert.That(result.Loaded).IsEmpty();
            await Assert.That(result.Rejected.Single().Failure).IsEqualTo(LoadFailure.Conflicts);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task SafeMode_LoadsNothing_TheShippedExtensionIncluded()
    {
        string root = NewRoot();
        try
        {
            Install(root);
            ExtensionStartupResult result = ExtensionStartup.Resolve(root,
                [ShippedPack.BesideApp(StratBookPackId, static () => new global::DemoViewer.NET.Extensions.StratBook.StratBookPack(), ["round_facts"])],
                ExtensionHost.Current, TrustPolicy.Nothing, PublisherKeys.Current, allowUnverified: true, safeMode: true);

            await Assert.That(result.Statuses).IsEmpty();
            await Assert.That(result.ExternalRejected).IsEmpty();
            await Assert.That(result.ClaimedRulesets).IsEquivalentTo(["round_facts"])
                .Because("the shipped extension's ruleset stays claimed, so safe mode leaves the highlights fingerprint alone");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task ANormalLaunch_LoadsTheShippedExtension_ThenTheAllowedThirdPartyOne()
    {
        string root = NewRoot();
        try
        {
            Install(root);
            ExtensionStartupResult result = ExtensionStartup.Resolve(root,
                [ShippedPack.BesideApp(StratBookPackId, static () => new global::DemoViewer.NET.Extensions.StratBook.StratBookPack())],
                ExtensionHost.Current, TrustPolicy.Nothing, PublisherKeys.Current, allowUnverified: true, safeMode: false);

            await Assert.That(result.Statuses.Select(s => s.Pack.Id)).IsEquivalentTo([StratBookPackId, HelloId]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task TheSetting_ReadsFromTheSettingsFile_AndAnythingUnreadableIsOff()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(file, "{ \"Extensions\": { \"AllowUnverified\": true } }");
            bool on = ExtensionStartup.ReadAllowUnverified(file);
            await File.WriteAllTextAsync(file, "{ \"extensions\": { \"allowUnverified\": true } }");
            bool caseInsensitive = ExtensionStartup.ReadAllowUnverified(file);
            await File.WriteAllTextAsync(file, "{ not json");
            bool broken = ExtensionStartup.ReadAllowUnverified(file);

            using (Assert.Multiple())
            {
                await Assert.That(on).IsTrue();
                await Assert.That(caseInsensitive).IsTrue();
                await Assert.That(broken).IsFalse();
                await Assert.That(ExtensionStartup.ReadAllowUnverified(Path.Combine(root, "missing.json"))).IsFalse();
                await Assert.That(new Configuration.AppSettings().Extensions.AllowUnverified).IsFalse()
                    .Because("off by default, whatever the user category");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Loaded in its own context, the sample still contributes through the host's own types: its tab module
    // builds a view, and its Match Overview action reaches the composition.
    [Test]
    public async Task ALoadedExtension_Contributes_ItsTabAndItsDemoAction()
    {
        string root = NewRoot();
        string? previous = Environment.GetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar);
        try
        {
            Install(root);
            IExtension hello = Resolve(root, allowUnverified: true).Loaded.Single().Pack;
            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, root);

            await HeadlessSession.RunOnUi(async () =>
            {
                Microsoft.Extensions.DependencyInjection.ServiceCollection services =
                    App.ComposeServices(new Services.DesktopWindowService(() => null), [hello]);
                using Microsoft.Extensions.DependencyInjection.ServiceProvider provider =
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
                PackContributions contributed = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<PackContributionSet>(provider).Packs.Single();

                Modules.Abstractions.WorkspaceTabDescriptor tab =
                    contributed.Modules.Single(m => m.Id == "dev.example.hello.tabs").CreateTabs(new NoHost()).Single();
                Avalonia.Controls.Control view = tab.ViewFactory();

                using (Assert.Multiple())
                {
                    await Assert.That(tab.TabId).IsEqualTo("hello.tab");
                    await Assert.That(tab.FeatureId).IsEqualTo("tab.hello");
                    await Assert.That(view).IsNotNull();
                    await Assert.That(contributed.DemoActions.Single().Action.Id).IsEqualTo("hello.greet");
                    await Assert.That(contributed.DemoActions.Single().FeatureId).IsEqualTo("pack.hello")
                        .Because("an action with no feature of its own shows under the extension's master switch");
                    await Assert.That(contributed.PlaybackContributions.Single()).IsTypeOf<SdkPlaybackContribution>();
                    await Assert.That(contributed.HubTabs.Single().Id).IsEqualTo("hello.hub");
                    await Assert.That(contributed.StatusChips.Single().FeatureId).IsEqualTo("pack.hello");
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, previous);
            Cleanup(root);
        }
    }

    // The sample joins the one keymap as a third-party extension: its prefixed command takes a free chord
    // beside the shipped extension's, the key's id runs its toolbar button, and with the extension off the
    // chord resolves to nothing and the id reaches no one.
    [Test]
    [NotInParallel]
    public async Task TheSamplesCommand_JoinsTheKeymap_RunsItsButton_AndGoesWhenTheExtensionIsOff()
    {
        const string where = HelloId + ".where";
        string root = NewRoot();
        string? previous = Environment.GetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar);
        try
        {
            Install(root);
            IExtension hello = Resolve(root, allowUnverified: true).Loaded.Single().Pack;

            CommandRegistry registry = CommandRegistry.Build([new global::DemoViewer.NET.Extensions.StratBook.StratBookPack(), hello]);
            using (Assert.Multiple())
            {
                await Assert.That(registry.Conflicts).IsEmpty();
                await Assert.That(Modules.Playback2D.Playback2DKeymap.FindConflicts(registry.EffectiveBindings,
                    Modules.Playback2D.Playback2DKeymap.ReservedGestures(true))).IsEmpty();
                await Assert.That(registry.ActionIds).Contains(where);
                await Assert.That(registry.TryResolve(Avalonia.Input.Key.H, Avalonia.Input.KeyModifiers.Shift, "playback2d",
                    _ => true, out CommandDescriptor? on)).IsTrue();
                await Assert.That(on!.Id).IsEqualTo(where);
                await Assert.That(registry.TryResolve(Avalonia.Input.Key.H, Avalonia.Input.KeyModifiers.Shift, "playback2d",
                    id => id != "pack.hello", out _)).IsFalse();
            }

            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, root);
            await HeadlessSession.RunOnUi(async () =>
            {
                Microsoft.Extensions.DependencyInjection.ServiceCollection services =
                    App.ComposeServices(new Services.DesktopWindowService(() => null), [hello]);
                using Microsoft.Extensions.DependencyInjection.ServiceProvider provider =
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
                PackContributions contributed = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<PackContributionSet>(provider).Packs.Single();

                SwitchableGate gate = new();
                PlaybackContributionHost host = new([(hello, contributed.PlaybackContributions)], gate);
                (Modules.Playback2D.Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab(contributions: host);

                await Assert.That(vm.ExecuteAction(where)).IsTrue().Because("the id runs the button that names it");

                gate.Set("pack.hello", false);
                await Assert.That(vm.ExecuteAction(where)).IsFalse().Because("off, nothing of the extension answers");

                vm.Dispose();
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, previous);
            Cleanup(root);
        }
    }

    private sealed class SwitchableGate : Features.IFeatureGate
    {
        private readonly HashSet<string> _off = [];

        public Configuration.UserCategory Category => Configuration.UserCategory.Developer;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => !_off.Contains(featureId);

        public event EventHandler? Changed;

        public void Set(string featureId, bool on)
        {
            if (on)
            {
                _off.Remove(featureId);
            }
            else
            {
                _off.Add(featureId);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // The loader's probe registers into a scratch container and passes; the real container's Register then
    // throws partway. The app still composes, the half-run registration is not in it, and the extension does
    // not start this session.
    [Test]
    [NotInParallel]
    public async Task ARegisterThatThrowsOnlyOnTheRealContainer_LeavesTheAppComposing_WithoutTheExtension()
    {
        SecondRegisterThrows pack = new();
        Microsoft.Extensions.DependencyInjection.ServiceCollection probe = new();
        pack.Register(probe);
        await Assert.That(probe.Count).IsEqualTo(1).Because("the probe's call succeeds");

        await HeadlessSession.RunOnUi(async () =>
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollection services =
                App.ComposeServices(new Services.DesktopWindowService(() => null), [pack]);
            using Microsoft.Extensions.DependencyInjection.ServiceProvider provider =
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            ExtensionFaults faults = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<ExtensionFaults>(provider);
            PackContributions contributed = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<PackContributionSet>(provider).Packs.Single();

            using (Assert.Multiple())
            {
                await Assert.That(services.Any(d => d.ServiceType == typeof(SecondRegisterThrows.Partial))).IsFalse()
                    .Because("nothing from the failed Register reached the container");
                await Assert.That(faults.StartupFailed(pack.Id)).IsTrue();
                await Assert.That(faults.StateOf(pack.FeatureId).Suspended).IsTrue();
                await Assert.That(pack.Contributed).IsFalse().Because("an extension with no services contributes nothing");
                await Assert.That(contributed.Modules).IsEmpty();
            }
        });
    }

    // A pack's Register sees the host's registrations, so its TryAdd of a host service adds nothing, and a
    // RemoveAll of one does not reach the container.
    [Test]
    public async Task APacksRegister_CannotReplaceOrRemoveAHostService()
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services =
            App.ComposeServices(new Services.DesktopWindowService(() => null), [new ReachesForHostServices()]);

        Microsoft.Extensions.DependencyInjection.ServiceDescriptor[] gates =
            [.. services.Where(d => d.ServiceType == typeof(Features.IFeatureGate))];
        using (Assert.Multiple())
        {
            await Assert.That(gates.Length).IsEqualTo(1);
            await Assert.That(gates[0].ImplementationType).IsEqualTo(typeof(Features.FeatureGate));
            await Assert.That(services.Count(d => d.ServiceType == typeof(Theming.ThemeRegistry))).IsEqualTo(1)
                .Because("RemoveAll inside Register only touched the pack's copy");
            await Assert.That(services.Any(d => d.ServiceType == typeof(ReachesForHostServices.Own))).IsTrue();
        }
    }

    private sealed class ReachesForHostServices : IExtension
    {
        public sealed class Own;

        public string Id => "dev.example.reaching";
        public string FeatureId => "pack.reaching";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .TryAddSingleton<Features.IFeatureGate, FakeGateForRegister>(services);
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .RemoveAll<Theming.ThemeRegistry>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Own>(services);
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    private sealed class FakeGateForRegister : Features.IFeatureGate
    {
        public Configuration.UserCategory Category => Configuration.UserCategory.Developer;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class SecondRegisterThrows : IExtension
    {
        private int _registers;

        public sealed class Partial;

        public string Id => "dev.example.flaky";
        public string FeatureId => "pack.flaky";
        public IEnumerable<ExtensionFeature> Features => [];
        public bool Contributed { get; private set; }

        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Partial>(services);
            if (++_registers > 1)
            {
                throw new InvalidOperationException("only on the real container");
            }
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services) => Contributed = true;
    }

    private sealed class NoHost : Modules.Abstractions.IModuleHost
    {
        public Modules.Abstractions.IModuleContext Context => null!;

        public bool HasCapability(string capability) => true;

        public void Log(Modules.Abstractions.ModuleLogLevel level, string message)
        {
        }
    }

    private const string StratBookPackId = "net.demoviewer.pack.stratbook";

    private sealed class TakenFeatureId : IExtension, IManifestSource
    {
        public string Id => "dev.example.other";
        public string FeatureId => "pack.hello";
        public ExtensionManifest Manifest => FakeManifests.For(Id);
        public IEnumerable<ExtensionFeature> Features =>
            [new("pack.hello", ExtensionFeatureKind.Extension, "Other", "d", null, AudienceDefaults.Everyone)];

        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

/// <summary>The developer opt-in never loads an unsigned copy unless the user also allowed unverified extensions.</summary>
public class LaunchTrustPolicyTests
{
    [Test]
    public async Task TheEnvironmentOptIn_CountsOnlyWithTheSetting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ExtensionManifest manifest = FakeManifests.For("net.demoviewer.pack.fake");
            bool settingOff = TrustPolicy.ForLaunch(false, static _ => "1").Judge(dir, manifest).Trusted;
            bool settingOn = TrustPolicy.ForLaunch(true, static _ => "1").Judge(dir, manifest).Trusted;
            bool settingOnNoOptIn = TrustPolicy.ForLaunch(true, static _ => null).Judge(dir, manifest).Trusted;

            using (Assert.Multiple())
            {
                await Assert.That(settingOff).IsFalse();
                await Assert.That(settingOn).IsTrue();
                await Assert.That(settingOnNoOptIn).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

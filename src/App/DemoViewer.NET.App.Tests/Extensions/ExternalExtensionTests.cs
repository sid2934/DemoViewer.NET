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
                [ShippedPack.BesideApp(StratBookPackId, static () => new global::DemoViewer.NET.Extensions.StratBook.StratBookPack())],
                ExtensionHost.Current, TrustPolicy.Nothing, PublisherKeys.Current, allowUnverified: true, safeMode: true);

            await Assert.That(result.Statuses).IsEmpty();
            await Assert.That(result.ExternalRejected).IsEmpty();
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

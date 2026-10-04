#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="PackCompatibility.Check" /> over a fixed host: each reason in check order, the compatible
///     case, and the user-facing message each reason produces. <see cref="PackStatus.Evaluate(IExtension, ExtensionHostInfo)" />
///     adds the manifest-level failures a check never sees: a getter that throws, and an id that is not the pack's.
/// </summary>
public class PackCompatibilityTests
{
    private static readonly ExtensionHostInfo _host = new(
        SemVersion.Parse("1.0.0"), SemVersion.Parse("0.6.2"), SemVersion.Parse("0.13.0-beta0001"));

    [Test]
    public async Task Compatible_WhenEveryRangeHolds()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "X", "1.0.0", "^1.0", "0.13.0-beta0001", "0.6.0");
        PackCompatibility result = PackCompatibility.Check(m, _host);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCompatible).IsTrue();
            await Assert.That(result).IsEqualTo(PackCompatibility.Compatible.Instance);
            await Assert.That(result.Describe(m, "x")).IsNull();
        }
    }

    [Test]
    public async Task HostContractMismatch_WhenTheContractRangeExcludesThisApp()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.2.0", "^2.0", "*");
        PackCompatibility result = PackCompatibility.Check(m, _host);
        using (Assert.Multiple())
        {
            await Assert.That(result is PackCompatibility.HostContractMismatch { Actual.Major: 1 }).IsTrue();
            await Assert.That(result.IsCompatible).IsFalse();
            await Assert.That(result.Describe(m, "x")).IsEqualTo("Strat Book 1.2.0 needs app contract ^2.0; this app provides 1.0.0");
        }
    }

    [Test]
    public async Task Cs2DemoKitMismatch_WhenTheKitRangeExcludesThisBuild()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.0.0", "^1.0", "0.14.0");
        PackCompatibility result = PackCompatibility.Check(m, _host);
        using (Assert.Multiple())
        {
            await Assert.That(result is PackCompatibility.Cs2DemoKitMismatch).IsTrue();
            await Assert.That(result.Describe(m, "x")).IsEqualTo("Strat Book 1.0.0 needs CS2DemoKit 0.14.0; this app ships 0.13.0-beta0001");
        }
    }

    [Test]
    public async Task AppTooOld_WhenTheFloorIsAboveThisRelease_AndNotOnAnUnstampedBuild()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.0.0", "^1.0", "*", "0.7.0");
        using (Assert.Multiple())
        {
            PackCompatibility result = PackCompatibility.Check(m, _host);
            await Assert.That(result is PackCompatibility.AppTooOld).IsTrue();
            await Assert.That(result.Describe(m, "x")).IsEqualTo("Strat Book 1.0.0 needs app 0.7.0 or newer; this is 0.6.2");

            ExtensionHostInfo unstamped = _host with { AppVersion = null };
            await Assert.That(PackCompatibility.Check(m, unstamped).IsCompatible).IsTrue()
                .Because("a build with no version stamped is a developer build, not an old release");
        }
    }

    [Test]
    public async Task ChecksRunInOrder_ContractBeforeKitBeforeApp()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "X", "1.0.0", "^9.0", "9.9.9", "9.0.0");
        await Assert.That(PackCompatibility.Check(m, _host) is PackCompatibility.HostContractMismatch).IsTrue();
        ExtensionManifest kitAndApp = m with { RequiresHost = VersionRange.Any };
        await Assert.That(PackCompatibility.Check(kitAndApp, _host) is PackCompatibility.Cs2DemoKitMismatch).IsTrue();
    }

    [Test]
    public async Task Evaluate_ManifestInvalid_WhenTheGetterThrows_OrTheIdIsNotThePacks()
    {
        ThrowingPack throwing = new();
        PackStatus status = PackStatus.Evaluate(throwing, _host);
        using (Assert.Multiple())
        {
            await Assert.That(status.IsCompatible).IsFalse();
            await Assert.That(status.Manifest).IsNull();
            await Assert.That(status.Compatibility is PackCompatibility.ManifestInvalid { Reason: "no resource" }).IsTrue();
            await Assert.That(status.Problem).IsEqualTo("net.demoviewer.pack.throwing has an invalid manifest: no resource")
                .Because("with no manifest the pack id names the extension");
        }

        MismatchedIdPack mismatched = new();
        PackStatus idStatus = PackStatus.Evaluate(mismatched, _host);
        using (Assert.Multiple())
        {
            await Assert.That(idStatus.IsCompatible).IsFalse();
            await Assert.That(idStatus.Manifest).IsNotNull().Because("the manifest parsed; it just names another pack");
            await Assert.That(idStatus.Compatibility is PackCompatibility.ManifestInvalid).IsTrue();
            await Assert.That(idStatus.Problem).Contains("is not the pack id");
        }
    }

    // A manifest whose range has an empty alternative does not parse, so the pack reads as ManifestInvalid
    // rather than as one that matches every host.
    [Test]
    public async Task Evaluate_ManifestInvalid_WhenARangeHasAnEmptyAlternative()
    {
        ParsingPack pack = new("""
            {
              "id": "net.demoviewer.pack.parsing",
              "name": "Parsing",
              "version": "1.0.0",
              "assembly": "Parsing.dll",
              "entryType": "Parsing.Pack",
              "requiresHost": "^1.0 || ",
              "requiresCs2DemoKit": "*"
            }
            """);
        PackStatus status = PackStatus.Evaluate(pack, _host);
        using (Assert.Multiple())
        {
            await Assert.That(status.IsCompatible).IsFalse();
            await Assert.That(status.Compatibility is PackCompatibility.ManifestInvalid).IsTrue();
            await Assert.That(status.Problem).Contains("requiresHost");
        }
    }

    [Test]
    public async Task Evaluate_KeepsDeclarationOrder_AndEveryVerdict()
    {
        IExtension good = new ManifestPack("net.demoviewer.pack.good", FakeManifests.For("net.demoviewer.pack.good"));
        IExtension bad = new ManifestPack("net.demoviewer.pack.bad",
            FakeManifests.For("net.demoviewer.pack.bad", "Bad", "3.0.0", "^3.0", "*"));
        IReadOnlyList<PackStatus> statuses = PackStatus.Evaluate([bad, good], _host);
        using (Assert.Multiple())
        {
            await Assert.That(statuses.Select(s => s.Pack.Id)).IsEquivalentTo(["net.demoviewer.pack.bad", "net.demoviewer.pack.good"]);
            await Assert.That(statuses[0].IsCompatible).IsFalse();
            await Assert.That(statuses[1].IsCompatible).IsTrue();
            await Assert.That(statuses[1].Problem).IsNull();
        }
    }

    private sealed class ThrowingPack : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.pack.throwing";
        public string FeatureId => "pack.throwing";
        public ExtensionManifest Manifest => throw new ExtensionManifestException("no resource");
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    // Parses its manifest on every read, the way a real pack reads its embedded copy.
    private sealed class ParsingPack(string json) : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.pack.parsing";
        public string FeatureId => "pack.parsing";
        public ExtensionManifest Manifest => ExtensionManifest.Parse(json);
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    private sealed class MismatchedIdPack : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.pack.actual";
        public string FeatureId => "pack.actual";
        public ExtensionManifest Manifest => FakeManifests.For("net.demoviewer.pack.other");
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    internal sealed class ManifestPack(string id, ExtensionManifest manifest) : IExtension, IManifestSource
    {
        public string Id => id;
        public string FeatureId => "pack." + id.Split('.')[^1];
        public ExtensionManifest Manifest => manifest;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

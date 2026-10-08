#region

using System.Xml.Linq;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionHost" />: the CS2DemoKit version it reads from the referenced assembly equals
///     the <c>Directory.Packages.props</c> pin, so a package bump cannot leave the two apart; the contract
///     version is the extension SDK's, 1.1 or later; the app version is <see cref="AppVersionInfo" />'s.
/// </summary>
public class ExtensionHostTests
{
    [Test]
    public async Task Cs2DemoKitVersion_EqualsTheDirectoryPackagesPropsPin()
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");
        XDocument props = XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props"));
        string pin = props.Descendants("PackageVersion")
            .Single(e => (string?)e.Attribute("Include") == "CS2DemoKit.Analysis")
            .Attribute("Version")!.Value;

        using (Assert.Multiple())
        {
            await Assert.That(ExtensionHost.Cs2DemoKitVersion.ToString()).IsEqualTo(pin)
                .Because("the host reads the referenced assembly; the pin is what it was restored from");
            await Assert.That(VersionRange.Parse(pin).Satisfies(ExtensionHost.Cs2DemoKitVersion)).IsTrue()
                .Because("an exact requiresCs2DemoKit on the pin must accept this build");
        }
    }

    // The fallback path a trimmed build without the informational attribute takes: the assembly version,
    // three components, no prerelease, so an exact prerelease pin then fails on purpose.
    [Test]
    public async Task ResolveVersion_PrefersTheInformationalVersion_AndFallsBackToTheAssemblyVersion()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ExtensionHost.ResolveVersion("0.13.0.1-beta0001+9f1e3e3b4a", new Version(0, 13, 0, 1)).ToString())
                .IsEqualTo("0.13.0-beta0001");
            await Assert.That(ExtensionHost.ResolveVersion(null, new Version(0, 13, 0, 1)).ToString()).IsEqualTo("0.13.0");
            await Assert.That(ExtensionHost.ResolveVersion("not a version", new Version(1, 2, 3)).ToString()).IsEqualTo("1.2.3");
            await Assert.That(ExtensionHost.ResolveVersion(null, new Version(1, 2)).ToString()).IsEqualTo("1.2.0")
                .Because("a two-part Version reports Build as -1");
            await Assert.That(ExtensionHost.ResolveVersion(null, null).ToString()).IsEqualTo("0.0.0");
            await Assert.That(VersionRange.Parse("0.13.0-beta0001").Satisfies(ExtensionHost.ResolveVersion(null, new Version(0, 13, 0, 1)))).IsFalse()
                .Because("the fallback cannot carry the prerelease, so an exact pin on it does not match");
        }
    }

    [Test]
    public async Task ContractVersion_IsTheSdksMajor_AndCurrentCarriesAllThree()
    {
        using (Assert.Multiple())
        {
            // 1.1 and up: 1.0.0 is what an app from before the SDK reports for its internal contract.
            await Assert.That(ExtensionHost.ContractVersion).IsGreaterThanOrEqualTo(new SemVersion(1, 1, 0));
            await Assert.That(ExtensionHost.ContractVersion.Major).IsEqualTo(typeof(IExtension).Assembly.GetName().Version!.Major);
            await Assert.That(ExtensionHost.ContractVersion.IsPrerelease).IsFalse();
            await Assert.That(SemVersion.TryParse(ExtensionHost.ContractVersion.ToString(), out _)).IsTrue();
            await Assert.That(ExtensionHost.Current.ContractVersion).IsEqualTo(ExtensionHost.ContractVersion);
            await Assert.That(ExtensionHost.Current.AppVersion).IsEqualTo(ExtensionHost.AppVersion);
            await Assert.That(ExtensionHost.Current.Cs2DemoKitVersion).IsEqualTo(ExtensionHost.Cs2DemoKitVersion);
        }
    }

    [Test]
    public async Task AppVersion_IsTheNormalizedReleaseVersion()
    {
        string? release = AppVersionInfo.CurrentReleaseVersion;
        if (release is null)
        {
            await Assert.That(ExtensionHost.AppVersion).IsNull().Because("an unstamped build has no app version");
            return;
        }

        await Assert.That(ExtensionHost.AppVersion).IsEqualTo(SemVersion.Parse(release));
    }
}

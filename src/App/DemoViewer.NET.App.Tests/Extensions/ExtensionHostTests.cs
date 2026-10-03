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
///     version is the documented 1.x; the app version is <see cref="AppVersionInfo" />'s.
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

    [Test]
    public async Task ContractVersion_IsOneDotX_AndCurrentCarriesAllThree()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ExtensionHost.ContractVersion.Major).IsEqualTo(1);
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

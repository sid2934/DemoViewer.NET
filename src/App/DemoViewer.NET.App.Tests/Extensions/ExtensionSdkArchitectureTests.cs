#region

using System.Reflection;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The SDK packages are what third parties build against, so they may reference nothing of the app's.</summary>
public class ExtensionSdkArchitectureTests
{
    [Test]
    public async Task TheSdk_ReferencesOnlyTheModuleAbstractionsAndTheScene_OfThisRepo()
    {
        string[] ours =
        [
            .. typeof(IExtension).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEquivalentTo(["DemoViewer.NET.Modules.Abstractions", "DemoViewer.NET.Playback2D.Scene"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheUiKit_ReferencesOnlyTheSdkTheIconCatalogueAndTheScene_OfThisRepo()
    {
        string[] ours =
        [
            .. typeof(DisplayText).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        string[] allowed =
        [
            "DemoViewer.NET.Extensions.Sdk", "DemoViewer.NET.Modules.Abstractions", "DemoViewer.NET.GameIcons",
            "DemoViewer.NET.Playback2D.Scene"
        ];

        await Assert.That(ours.Except(allowed)).IsEmpty();
    }

    [Test]
    public async Task TheSdkFamily_CarriesOneVersion()
    {
        // The package version, not the assembly version: assemblyVersion.precision is "major", so every 1.x
        // assembly stamps 1.0.0.0 and a drift below the major is invisible there. The family ships in
        // lockstep, and Directory.Build.targets pins each intra-family dependency at exactly the sibling's
        // own computed version, so a drift ships as a set with no one version to ask for, under an sdk/v tag
        // that names the SDK's alone.
        //
        // GameIcons is not published but belongs here anyway: it reads the SDK's version.json through
        // GitVersionBaseDirectory, so it is on the same line whether or not it is packed.
        string sdk = ReleaseVersion(typeof(IExtension).Assembly);

        using (Assert.Multiple())
        {
            await Assert.That(ReleaseVersion(typeof(Modules.Abstractions.IModuleContext).Assembly)).IsEqualTo(sdk);
            await Assert.That(ReleaseVersion(typeof(DisplayText).Assembly)).IsEqualTo(sdk);
            await Assert.That(ReleaseVersion(typeof(Playback2D.Scene2DFrame).Assembly)).IsEqualTo(sdk);
            await Assert.That(ReleaseVersion(typeof(GameIcons.IconCatalogue).Assembly)).IsEqualTo(sdk);
        }
    }

    [Test]
    public async Task TheSceneContract_ReferencesNothingOfThisRepo()
    {
        string[] ours =
        [
            .. typeof(Playback2D.Scene2DFrame).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal) || n.StartsWith("Avalonia", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEmpty();
    }

    [Test]
    public async Task TheModuleAbstractions_ReferenceNothingOfThisRepo()
    {
        string[] ours =
        [
            .. typeof(Modules.Abstractions.IModuleContext).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal) || n.StartsWith("Avalonia", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEmpty();
    }

    [Test]
    public async Task TheSdkAssemblyVersion_IsMajorOnly_SoEveryMinorBindsToTheSameExtensions()
    {
        Version version = typeof(IExtension).Assembly.GetName().Version!;

        await Assert.That(version.Minor + version.Build + version.Revision).IsEqualTo(0);
    }

    // Nerdbank.GitVersioning writes the release version, then '+' and the commit id, into the informational
    // version. The NuGet package version is this plus a -g<commit> suffix off a release ref, which is the same
    // suffix for every assembly built from one commit, so the part before the '+' is what can drift.
    private static string ReleaseVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}

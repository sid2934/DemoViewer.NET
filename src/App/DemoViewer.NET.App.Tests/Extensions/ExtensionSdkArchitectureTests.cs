namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The SDK packages are what third parties build against, so they may reference nothing of the app's.</summary>
public class ExtensionSdkArchitectureTests
{
    [Test]
    public async Task TheSdk_ReferencesOnlyTheModuleAbstractions_OfThisRepo()
    {
        string[] ours =
        [
            .. typeof(IExtension).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEquivalentTo(["DemoViewer.NET.Modules.Abstractions"]);
    }

    [Test]
    public async Task TheUiKit_ReferencesOnlyTheSdkAndTheIconCatalogue_OfThisRepo()
    {
        string[] ours =
        [
            .. typeof(DisplayText).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        string[] allowed = ["DemoViewer.NET.Extensions.Sdk", "DemoViewer.NET.Modules.Abstractions", "DemoViewer.NET.GameIcons"];

        await Assert.That(ours.Except(allowed)).IsEmpty();
    }

    [Test]
    public async Task TheUiKitIconAndSceneAssemblyVersions_AreTheSdks()
    {
        Version sdk = typeof(IExtension).Assembly.GetName().Version!;

        await Assert.That(typeof(DisplayText).Assembly.GetName().Version).IsEqualTo(sdk);
        await Assert.That(typeof(GameIcons.IconCatalogue).Assembly.GetName().Version).IsEqualTo(sdk);
        await Assert.That(typeof(Playback2D.Core.Scene2DFrame).Assembly.GetName().Version).IsEqualTo(sdk);
    }

    [Test]
    public async Task TheSceneContract_ReferencesNothingOfThisRepo()
    {
        string[] ours =
        [
            .. typeof(Playback2D.Core.Scene2DFrame).Assembly.GetReferencedAssemblies()
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
}

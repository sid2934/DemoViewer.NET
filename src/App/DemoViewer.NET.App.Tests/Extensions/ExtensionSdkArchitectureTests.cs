namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The SDK is what third parties build against, so it may reference nothing of the app's.</summary>
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

#region

using System.Reflection;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book builds its views on the SDK's UI kit, as a third-party extension would. The app's
///     <see cref="ViewModelBase" /> stays reachable through the first-party grants, so the compiler alone does
///     not keep it out.
/// </summary>
public class StratBookUiKitTests
{
    private static readonly Assembly Pack = typeof(StratBookPack).Assembly;

    [Test]
    public async Task NoStratBookType_DerivesFromTheAppsViewModelBase()
    {
        string[] offenders = [.. Pack.GetTypes().Where(t => t.IsSubclassOf(typeof(ViewModelBase))).Select(t => t.FullName!)];

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task ItsViewModels_ResolveThroughTheSdkMarker()
    {
        Type[] viewModels = [.. Pack.GetTypes().Where(t => typeof(IExtensionViewModel).IsAssignableFrom(t))];

        using (Assert.Multiple())
        {
            await Assert.That(viewModels.Length).IsGreaterThanOrEqualTo(20);
            await Assert.That(viewModels.All(t => t.IsSubclassOf(typeof(ExtensionViewModel)))).IsTrue();
            await Assert.That(Pack.GetReferencedAssemblies().Select(a => a.Name!)).Contains("DemoViewer.NET.Extensions.Sdk.Ui");
        }
    }
}

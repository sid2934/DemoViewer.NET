#region

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.ViewModels;
using ThirdPartyFake;
using ThirdPartyFake.ViewModels;
using ThirdPartyFake.Views;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension built against the SDK and its UI kit alone, in its own root namespace, gets its views found by
///     naming: the view locator accepts the SDK marker and searches the view model's own assembly, which is not a
///     compiled-in pack.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ThirdPartyViewResolutionTests
{
    private static string FakeOutput()
    {
        string configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "ThirdPartyFake", configuration));
    }

    [Test]
    public async Task TheFake_IsBuiltAgainstTheSdkAndUiKitAlone_InItsOwnNamespace()
    {
        Assembly fake = typeof(FakeExtension).Assembly;
        string[] ours =
        [
            .. fake.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];
        string[] allowed = ["DemoViewer.NET.Extensions.Sdk", "DemoViewer.NET.Extensions.Sdk.Ui", "DemoViewer.NET.Modules.Abstractions"];
        Type[] ownTypes =
        [
            .. fake.GetExportedTypes().Where(t => typeof(IExtension).IsAssignableFrom(t)
                                                  || typeof(IExtensionViewModel).IsAssignableFrom(t)
                                                  || typeof(Control).IsAssignableFrom(t))
        ];

        using (Assert.Multiple())
        {
            await Assert.That(ours.Except(allowed)).IsEmpty();
            await Assert.That(ours).Contains("DemoViewer.NET.Extensions.Sdk.Ui");
            await Assert.That(ownTypes.Length).IsEqualTo(5);
            await Assert.That(ownTypes.All(t => t.Namespace!.StartsWith("ThirdPartyFake", StringComparison.Ordinal))).IsTrue();
            await Assert.That(FeaturePacks.Compatible.Any(p => p.GetType().Assembly == fake)).IsFalse()
                .Because("the view must be found through the view model's own assembly, not a pack search");
        }
    }

    [Test]
    public async Task TheLocator_MatchesTheMarker_AndBuildsTheViewFromTheViewModelsAssembly()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ViewLocator locator = new();
            FakeDetailViewModel detail = new();

            using (Assert.Multiple())
            {
                await Assert.That(locator.Match(detail)).IsTrue();
                await Assert.That(locator.Build(detail)).IsTypeOf<FakeDetailView>();
                await Assert.That(locator.Match(new StatusChipViewModel())).IsFalse()
                    .Because("an observable without the marker is not the locator's to render");
            }
        });
    }

    [Test]
    public async Task ABareContentControl_RendersTheTab_AndItsNestedDetail_WithTheHostLook()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeTabViewModel vm = new();
            Window window = new() { Width = 640, Height = 480, Content = new ContentControl { Content = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Control[] visuals = [.. window.GetVisualDescendants().OfType<Control>()];
            FakeTabView? tab = visuals.OfType<FakeTabView>().SingleOrDefault();
            using (Assert.Multiple())
            {
                await Assert.That(tab).IsNotNull();
                await Assert.That(visuals.OfType<FakeDetailView>().Count()).IsEqualTo(1);
                await Assert.That(visuals.OfType<StatusChip>().Count()).IsEqualTo(1);
                await Assert.That(visuals.OfType<TextBlock>().Any(t => t.Text == "Fake · ready")).IsTrue();
                await Assert.That(visuals.OfType<TextBlock>().Any(t => t.Text?.StartsWith("Not Found", StringComparison.Ordinal) == true)).IsFalse();
                await Assert.That(tab!.Background).IsNotNull().Because("PanelBg resolves through a published token constant");
            }

            window.Close();
        });
    }

    [Test]
    public async Task LoadedThroughTheExternalPath_ItsViewModels_StillResolveToItsViews()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-ext-" + Guid.NewGuid().ToString("N"));
        try
        {
            string target = Path.Combine(root, ExtensionLoader.ExtensionsDirectoryName, FakeExtension.ExtensionId, "1.0.0");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(FakeOutput()))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }

            ExternalResolution result = ExternalExtensions.Resolve(root, [], ExtensionHost.Current,
                new ExternalTrust(PublisherKeys.Current, true));
            Assembly loaded = result.Loaded.Single().Pack.GetType().Assembly;
            object detail = Activator.CreateInstance(loaded.GetType(typeof(FakeDetailViewModel).FullName!, throwOnError: true)!)!;

            await Assert.That(loaded).IsNotEqualTo(typeof(FakeExtension).Assembly).Because("the external copy loads in its own context");
            await HeadlessSession.RunOnUi(async () =>
            {
                ViewLocator locator = new();
                Control? view = locator.Build(detail);

                using (Assert.Multiple())
                {
                    await Assert.That(locator.Match(detail)).IsTrue();
                    await Assert.That(view?.GetType().Assembly).IsEqualTo(loaded);
                    await Assert.That(view?.GetType().FullName).IsEqualTo(typeof(FakeDetailView).FullName);
                }
            });
        }
        finally
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
    }
}

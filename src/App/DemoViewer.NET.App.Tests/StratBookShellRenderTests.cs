#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using TabPlacement = DemoViewer.NET.Modules.Abstractions.TabPlacement;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Headless render smoke for the Strat Book shell: the whole MainView with the Strat Book tab selected
///     (the strip, the rail and a section's view) and the Library with its Teams view up. Fake sections
///     carry a labelled view so the frame shows where each one lands; the PNGs under the artifact
///     directory are the review surface.
/// </summary>
[Category("Integration")]
public class StratBookShellRenderTests
{
    [Test]
    public async Task MainView_RendersTheStratBookRail_AndTheLibraryTeamsView()
    {
        int railInk = 0, teamsInk = 0;

        await HeadlessSession.RunOnUi(() =>
        {
            ModuleRegistry registry = new();
            registry.Register(new LabelledSectionsModule());
            MainViewModel vm = new(null, registry, TestLibraries.Empty());
            vm.RestoreSession();
            try
            {
                MainView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 1280,
                    Height = 800,
                    Content = view
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.TrySelectTab("situations.search");
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                if (window.CaptureRenderedFrame() is { } rail)
                {
                    rail.Save(Path.Combine(HeadlessSession.ArtifactDir, "stratbook-rail.png"), new PngBitmapEncoderOptions());
                    railInk = NonBackground(rail);
                }

                vm.TrySelectTab("teams.browser");
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                if (window.CaptureRenderedFrame() is { } teams)
                {
                    teams.Save(Path.Combine(HeadlessSession.ArtifactDir, "library-teams.png"), new PngBitmapEncoderOptions());
                    teamsInk = NonBackground(teams);
                }

                window.Close();
            }
            finally
            {
                vm.Dispose();
            }

            return Task.CompletedTask;
        });

        Console.WriteLine($"[stratbook-shell] railInk={railInk} teamsInk={teamsInk}");
        using (Assert.Multiple())
        {
            await Assert.That(railInk).IsGreaterThan(500);
            await Assert.That(teamsInk).IsGreaterThan(500);
        }
    }

    private static int NonBackground(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i] > 40 || buffer[i + 1] > 40 || buffer[i + 2] > 40)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class LabelledSectionsModule : IWorkspaceModule
    {
        public string Id => "net.demoviewer.test.labelledsections";
        public string DisplayName => "Sections";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            yield return Section("stratbook.browser", "Strats", 0, TabPlacement.StratBook, null);
            yield return Section("situations.search", "Situations", 1, TabPlacement.StratBook, "3 new");
            yield return Section("tagger.matrix", "Tags", 2, TabPlacement.StratBook, null);
            yield return Section("utilitybook.browser", "Utility", 3, TabPlacement.StratBook, null);
            yield return Section("review.queue", "Review", 4, TabPlacement.StratBook, "12");
            yield return Section("dossier.browser", "Dossier", 5, TabPlacement.StratBook, null);
            yield return Section("teams.browser", "Teams", 0, TabPlacement.Library, null);
        }

        private static WorkspaceTabDescriptor Section(string id, string header, int order, TabPlacement placement, string? badge) =>
            new()
            {
                TabId = id,
                Header = header,
                Order = order,
                Placement = placement,
                Badge = badge,
                ViewModelFactory = () => new PlaceholderTabViewModel(),
                ViewFactory = () => new Border
                {
                    Padding = new Thickness(24),
                    Child = new TextBlock
                    {
                        Text = $"{header} section view",
                        Foreground = Brushes.Gray,
                        FontSize = 20,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
    }
}

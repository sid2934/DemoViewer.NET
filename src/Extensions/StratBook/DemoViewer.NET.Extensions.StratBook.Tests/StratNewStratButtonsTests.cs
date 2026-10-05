#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     New strat and its template menu, clicked with the pointer in the real shell built from the real composition
///     root, on a fresh config: "All maps" and no demo, the state the app starts in.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class StratNewStratButtonsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewStrat_WithNoMap_OpensTheMapFilter_AndThePickMakesTheStrat(bool listCollapsed) =>
        await InShell(async (window, vm) =>
        {
            vm.Layout.IsListCollapsed = listCollapsed;
            Pump();
            SplitButton newStrat = Named<SplitButton>(window, "NewStratButton");
            ComboBox maps = Named<ComboBox>(window, "MapFilter");
            TextBlock hint = Named<TextBlock>(window, "NewStratHintText");

            Click(window, newStrat, new Point(8, newStrat.Bounds.Height / 2));
            await Settle();
            using (Assert.Multiple())
            {
                await Assert.That(hint.IsEffectivelyVisible).IsTrue().Because("the click says why nothing was made yet");
                await Assert.That(maps.IsDropDownOpen).IsTrue().Because("the click asks for the map");
                await Assert.That(vm.Strats.Count).IsEqualTo(0);
            }

            PickMap(window, maps, "de_mirage");
            await Settle();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document?.Map).IsEqualTo("de_mirage").Because("the pick finishes the create");
                await Assert.That(vm.Session.Document?.Name).IsEqualTo("New strat");
                await Assert.That(hint.IsEffectivelyVisible).IsFalse();
                await Assert.That(vm.CanApplyTemplate).IsTrue();
            }
        });

    [Test]
    public async Task ATemplate_WithNoMap_WaitsForTheMap_ThenUsesTheTemplate() =>
        await InShell(async (window, vm) =>
        {
            SplitButton newStrat = Named<SplitButton>(window, "NewStratButton");
            ComboBox maps = Named<ComboBox>(window, "MapFilter");

            Click(window, newStrat, new Point(newStrat.Bounds.Width - 6, newStrat.Bounds.Height / 2));
            await Settle();
            MenuFlyout menu = (MenuFlyout)newStrat.Flyout!;
            await Assert.That(menu.IsOpen).IsTrue();
            MenuItem item = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Default (T)"));
            Click(window, item, new Point(8, item.Bounds.Height / 2));
            await Settle();
            await Assert.That(maps.IsDropDownOpen).IsTrue();

            PickMap(window, maps, "de_nuke");
            await Settle();
            StratTemplate template = StratTemplates.Find("default")!;
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document?.Map).IsEqualTo("de_nuke");
                await Assert.That(vm.Session.Document?.Type).IsEqualTo(template.Type);
                await Assert.That(vm.Session.Document?.Name).IsEqualTo(template.Name);
            }
        });

    [Test]
    public async Task ADismissedMapChoice_DropsTheWaitingCreate() =>
        await InShell(async (window, vm) =>
        {
            SplitButton newStrat = Named<SplitButton>(window, "NewStratButton");
            ComboBox maps = Named<ComboBox>(window, "MapFilter");
            TextBlock hint = Named<TextBlock>(window, "NewStratHintText");

            Click(window, newStrat, new Point(8, newStrat.Bounds.Height / 2));
            await Settle();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Settle();
            await Assert.That(maps.IsDropDownOpen).IsFalse();

            vm.SelectedMap = "de_mirage";
            await Settle();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Strats.Count).IsEqualTo(0).Because("a later map pick is only a filter");
                await Assert.That(hint.IsEffectivelyVisible).IsFalse();
            }
        });

    private static async Task InShell(Func<Window, StratBookTabViewModel, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvnewstrat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                Window? window = null;
                try
                {
                    MainViewModel main = provider.GetRequiredService<MainViewModel>();
                    main.RestoreSession();
                    window = new Window { Width = 1600, Height = 1000, Content = new MainView { DataContext = main } };
                    window.Show();
                    main.TrySelectTab("stratbook.browser");
                    Pump();
                    StratBookTabViewModel vm = provider.GetRequiredService<StratBookTabViewModel>();
                    await Assert.That(vm.SelectedMap).IsEqualTo(StratBookTabViewModel.AllMaps);
                    await body(window, vm);
                }
                finally
                {
                    window?.Close();
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window window, Visual target, Point local)
    {
        Point at = target.TranslatePoint(local, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    private static void PickMap(Window window, ComboBox maps, string map)
    {
        int index = maps.Items.IndexOf(map);
        maps.ScrollIntoView(index);
        Pump();
        Control item = maps.ContainerFromIndex(index)!;
        Click(window, item, new Point(8, item.Bounds.Height / 2));
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // The spawn read for a cold map lands on the thread pool, then posts back.
    private static async Task Settle()
    {
        for (int i = 0; i < 25; i++)
        {
            Pump();
            await Task.Delay(20);
        }
    }
}

#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Module-framework gates: the four built-in tabs run through the
///     ItemsSource-driven registry, inactive-content unload still holds, and
///     the host player-join reaches a registered module end-to-end with only the active module
///     receiving pushes.
/// </summary>
[NotInParallel]
public class ModuleFrameworkPhase3Tests
{
    [Test]
    public async Task Registry_ProducesBuiltInTabs_InOrder()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());

            // The four built-in tabs are present as descriptors.
            string[] ids = vm.Tabs.Select(t => t.TabId).ToArray();
            await Assert.That(ids).Contains("builtin.parser");
            await Assert.That(ids).Contains("builtin.entity");
            await Assert.That(ids).Contains("builtin.analysis");
            await Assert.That(ids).Contains("builtin.diagnostics");
        });

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            // Library is the landing tab (Order -1), then Match Overview (Order 0, yielded first), then the rest
            // of the main tabs, then the diagnostics-group tab.
            await Assert.That(vm.Tabs[0].TabId).IsEqualTo("builtin.library");
            await Assert.That(vm.Tabs[1].TabId).IsEqualTo("builtin.matchoverview");
            await Assert.That(vm.Tabs[2].TabId).IsEqualTo("builtin.parser");
            await Assert.That(vm.Tabs[3].TabId).IsEqualTo("builtin.entity");
            await Assert.That(vm.Tabs[4].TabId).IsEqualTo("builtin.stats");
            await Assert.That(vm.Tabs[5].TabId).IsEqualTo("builtin.analysis");
            // The initial tab is activated (the first-tab edge case).
            await Assert.That(vm.SelectedTab).IsEqualTo(vm.Tabs[0]);
            await Assert.That(vm.Tabs[0].IsActive).IsTrue();
        });
    }

    [Test]
    public async Task TabControl_UnloadsInactiveContent_OnlySelectedRealized()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());

            Window window = new()
            {
                Width = 1280,
                Height = 720,
                Content = new MainView
                {
                    DataContext = vm
                }
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Only the selected descriptor has a realized View; the others are unloaded (single content
            // presenter). This is the headless proof per the project's UI rule.
            WorkspaceTabDescriptor selected = vm.SelectedTab!;
            await Assert.That(selected.ActiveContent).IsNotNull();
            await Assert.That(selected.ActiveContent).IsAssignableTo<Control>();
        });

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            Window window = new()
            {
                Width = 1280,
                Height = 720,
                Content = new MainView
                {
                    DataContext = vm
                }
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Exactly one descriptor is realized at a time.
            await Assert.That(vm.Tabs.Count(t => t.ActiveContent is not null)).IsEqualTo(1);

            // Switch tabs: the old View is dropped, the new one realized — still exactly one.
            WorkspaceTabDescriptor first = vm.SelectedTab!;
            vm.SelectedTab = vm.Tabs[1];
            Dispatcher.UIThread.RunJobs();

            await Assert.That(first.ActiveContent).IsNull(); // old dropped (unloaded)
            await Assert.That(vm.Tabs[1].ActiveContent).IsNotNull(); // new realized
            await Assert.That(vm.Tabs.Count(t => t.ActiveContent is not null)).IsEqualTo(1);
        });
    }

    [Test]
    public async Task PlaceholderModule_HostPlayerJoin_AndActiveOnlyPushes()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            // Compose the shell with the placeholder module registered (as the desktop host does).
            ModuleRegistry registry = new();
            registry.Register(new PlaceholderModule());
            MainViewModel vm = new(null, registry, TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);

            WorkspaceTabDescriptor sandbox = vm.Tabs.First(t => t.TabId == "placeholder.sandbox");

            // Establish the authoritative tracker at a mid-match frame (players spread on the map).
            int mid = vm.Frames.Count / 2;
            vm.Navigation.SeekToFrame(mid);
            await WaitUntil(() => vm.Playback.AuthoritativeTracker?.CurrentFrameIndex == mid);

            // Activate the sandbox. Its OnActivated pulls context.CurrentPlayers — proving the host
            // player-join (PawnLookup reverse m_hController + PositionUtil) is reachable end-to-end.
            vm.SelectedTab = sandbox;
            Dispatcher.UIThread.RunJobs();
            PlaceholderTabViewModel sandboxVm = (PlaceholderTabViewModel)sandbox.TabViewModel!;
            await Assert.That(sandboxVm.Status).Contains("players joined");
            // A live mid-match frame has joined players (the status carries the count).
            await Assert.That(sandboxVm.Status).DoesNotContain("0 players joined");

            // Deactivate the sandbox: while inactive, playing produces ZERO pushes to it.
            vm.SelectedTab = vm.Tabs.First(t => t.TabId == "builtin.parser");
            Dispatcher.UIThread.RunJobs();
            int pushesAfterDeactivate = sandboxVm.PushCount;

            vm.Playback.Play();
            await PumpFor(400);
            vm.Playback.Pause();

            await Assert.That(sandboxVm.PushCount).IsEqualTo(pushesAfterDeactivate);

            // Re-activate the sandbox and play — it DOES receive coalesced pushes (active-only work).
            vm.SelectedTab = sandbox;
            Dispatcher.UIThread.RunJobs();
            int before = sandboxVm.PushCount;
            vm.Playback.Play();
            await PumpFor(400);
            vm.Playback.Pause();

            await Assert.That(sandboxVm.PushCount).IsGreaterThan(before);
        });
    }

    private static async Task PumpFor(int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 25)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}

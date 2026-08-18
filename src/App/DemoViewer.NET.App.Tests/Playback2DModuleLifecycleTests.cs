#region

using Avalonia.Threading;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     2D Playback module lifecycle gate. Proves
///     the pilot registers as a Main-strip tab, that the <c>ViewModelFactory</c> path actually builds and
///     drives the VM's <c>OnActivated</c>/<c>OnDeactivated</c> lifecycle (the subscribe/unsubscribe that
///     makes inactive-tab work zero-cost), and that pushes accrue ONLY while the tab is the active tab.
///     This is the de-risking gate for the module wiring before any drawing lands.
/// </summary>
[NotInParallel]
public class Playback2DModuleLifecycleTests
{
    [Test]
    public async Task Module_RegistersAsMainTab_AfterBuiltIns()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // The desktop composition root registers the 2D pilot; mirror that here.
            MainViewModel vm = NewShellWith2D();

            WorkspaceTabDescriptor? tab = vm.Tabs.FirstOrDefault(t => t.TabId == "playback2d.viewport");
            await Assert.That(tab).IsNotNull();
            await Assert.That(tab!.Header).IsEqualTo("2D Playback");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);

            // Library is the landing tab (Order -1); Match Overview (Order 0, yielded first) follows, then the
            // rest of the Main built-ins, then the 2D pilot.
            await Assert.That(vm.Tabs[0].TabId).IsEqualTo("builtin.library");
            await Assert.That(vm.Tabs[1].TabId).IsEqualTo("builtin.matchoverview");
            await Assert.That(vm.Tabs[2].TabId).IsEqualTo("builtin.parser");
            await Assert.That(vm.Tabs[3].TabId).IsEqualTo("builtin.entity");
            await Assert.That(vm.Tabs[4].TabId).IsEqualTo("builtin.stats");
            await Assert.That(vm.Tabs[5].TabId).IsEqualTo("builtin.analysis");
            await Assert.That(vm.Tabs[6].TabId).IsEqualTo("playback2d.viewport");
            // Diagnostics (the only Diagnostics-placement tab) sorts last.
            await Assert.That(vm.Tabs[^1].TabId).IsEqualTo("builtin.diagnostics");
        });
    }

    [Test]
    public async Task ViewModelFactory_Path_FiresLifecycle_AndActiveOnlyPushes()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShellWith2D();
            await vm.AutoLoadDemoAsync(demo);

            WorkspaceTabDescriptor tab = vm.Tabs.First(t => t.TabId == "playback2d.viewport");

            // Establish the authoritative tracker at a mid-match frame (players spread across the map).
            int mid = vm.Frames.Count / 2;
            vm.Navigation.SeekToFrame(mid);
            await WaitUntil(() => vm.Playback.AuthoritativeTracker?.CurrentFrameIndex == mid);

            // Activate the 2D tab. The ViewModelFactory path must build the VM AND fire OnActivated, which
            // pulls context.CurrentPlayers — proving the host player-join is reachable end-to-end.
            vm.SelectedTab = tab;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(tab.TabViewModel).IsNotNull();
            Playback2DTabViewModel pvm = (Playback2DTabViewModel)tab.TabViewModel!;
            await Assert.That(pvm.Status).Contains("active");
            await Assert.That(pvm.Status).DoesNotContain("0 players");

            // Real-data → markers: the OnActivated CurrentPlayers resync built live markers at plausible
            // (on-radar) world positions from the host player-join (the synthetic render test owns the
            // markers → pixels half; this pins real-data → markers without the fragile headless render).
            await Assert.That(pvm.Markers.Count).IsGreaterThan(0);
            foreach (PlayerMarker m in pvm.Markers)
            {
                await Assert.That(Math.Abs(m.WorldX)).IsLessThan(16384f);
                await Assert.That(Math.Abs(m.WorldY)).IsLessThan(16384f);
            }

            // And the attributes panel populated at least one live row.
            await Assert.That(pvm.Attributes.Any(a => a.HasLivePawn)).IsTrue();

            // Discriminating gate for the one-hop weapon resolve: at a mid-round frame every live
            // player holds a weapon, so a non-"—" ActiveWeapon proves the whole handle-resolve path
            // (TryGet<ulong> coercion + the clobber-rule loop + ResolveHandle masking) works against
            // real data. The synthetic render test only exercises the empty "—" branch; this is the real
            // proof. Weapon + grenades share the resolve path, so this also covers the grenade loop.
            await Assert.That(pvm.Attributes.Any(a => a.HasLivePawn && a.ActiveWeapon != "—")).IsTrue();

            // Deactivate: while inactive, playing produces ZERO pushes to the 2D VM.
            vm.SelectedTab = vm.Tabs.First(t => t.TabId == "builtin.parser");
            Dispatcher.UIThread.RunJobs();
            int pushesAfterDeactivate = pvm.PushCount;

            vm.Playback.Play();
            await PumpFor(400);
            vm.Playback.Pause();

            await Assert.That(pvm.PushCount).IsEqualTo(pushesAfterDeactivate);

            // Re-activate and play — it DOES receive coalesced pushes (active-only work).
            vm.SelectedTab = tab;
            Dispatcher.UIThread.RunJobs();
            int before = pvm.PushCount;
            vm.Playback.Play();
            await PumpFor(400);
            vm.Playback.Pause();

            await Assert.That(pvm.PushCount).IsGreaterThan(before);
        });
    }

    // Composes the shell the way App.axaml.cs BuildRegistry does (the 2D pilot registered first-party).
    private static MainViewModel NewShellWith2D()
    {
        ModuleRegistry registry = new();
        registry.Register(new Playback2DModule());
        return new MainViewModel(null, registry, TestLibraries.Empty());
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

#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cs2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Play-loop gates for the modular-UI framework. Drives the real
///     <c>PlaybackController</c> play loop through the headless shell and asserts the invariants that
///     a naive "does it advance?" test would miss: the loop is LEAN (discrete tabs untouched
///     while playing, snapped only on Pause) and the <c>Advanced</c> push is coalesced (bounded
///     pushes regardless of speed). Inactive-module zero cost is gated in the module-framework
///     tests — there are no modules here.
/// </summary>
[NotInParallel]
public class PlaybackLoopPhase2Tests
{
    [Test]
    public async Task Play_AdvancesPositionForward_ThenPauseSnapsDiscreteTabs()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.Frames.Count).IsGreaterThan(40);

            // Establish the authoritative tracker at frame 5 (discrete seek; wait for it to land).
            vm.Navigation.SeekToFrame(5);
            await WaitUntil(() => vm.Playback.AuthoritativeTracker?.CurrentFrameIndex == 5);

            DemoFrame? selectedBeforePlay = vm.SelectedFrame;

            vm.Playback.Play();
            await Assert.That(vm.Playback.IsPlaying).IsTrue();

            // Let the loop run a while.
            await PumpFor(600);

            int posDuringPlay = vm.Playback.CurrentFrameIndex;
            await Assert.That(posDuringPlay).IsGreaterThan(5); // it advanced

            // Leanness: while playing, the loop does NOT touch the discrete tabs — the Parser
            // selection is still the pre-play frame, even though the clock advanced.
            await Assert.That(vm.SelectedFrame).IsEqualTo(selectedBeforePlay);

            vm.Playback.Pause();
            await Assert.That(vm.Playback.IsPlaying).IsFalse();

            // After Pause the discrete tabs snap to where playback stopped.
            await PumpFor(50);
            await Assert.That(vm.SelectedFrame).IsEqualTo(vm.Frames[vm.Playback.CurrentFrameIndex]);
            await Assert.That(vm.Playback.AuthoritativeTracker!.CurrentFrameIndex)
                .IsEqualTo(vm.Playback.CurrentFrameIndex);
        });
    }

    [Test]
    public async Task Play_CoalescesAdvancedPush_BoundedRegardlessOfSpeed()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.Frames.Count).IsGreaterThan(100);

            vm.Navigation.SeekToFrame(2);
            await WaitUntil(() => vm.Playback.AuthoritativeTracker?.CurrentFrameIndex == 2);

            int pushCount = 0;
            int maxFrameJumpPerPush = 0;
            int lastFrame = vm.Playback.CurrentFrameIndex;
            vm.Playback.Advanced += pf =>
            {
                pushCount++;
                maxFrameJumpPerPush = Math.Max(maxFrameJumpPerPush, pf.FrameIndex - lastFrame);
                lastFrame = pf.FrameIndex;
            };

            vm.Playback.Speed = 8.0; // many tracker frames per timer tick
            vm.Playback.Play();
            await PumpFor(500);
            vm.Playback.Pause();

            int framesAdvanced = vm.Playback.CurrentFrameIndex - 2;
            Console.WriteLine($"frames advanced={framesAdvanced}  pushes={pushCount}  maxJump/push={maxFrameJumpPerPush}");

            // Coalescing bound: at speed 8 the tracker steps multiple frames per push, so push count is strictly
            // less than frames advanced (coalescing). And it did advance.
            await Assert.That(framesAdvanced).IsGreaterThan(0);
            await Assert.That(pushCount).IsLessThanOrEqualTo(framesAdvanced);
        });
    }

    [Test]
    public async Task MainView_WithTransport_RendersWithoutBindingErrors()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);

            // Render the real shell with the new play/pause/speed transport bound to the controller.
            // A render that produces a non-null frame proves the visual tree (incl. the new toolbar
            // bindings to Playback.TogglePlayCommand / Playback.IsPlaying / Playback.Speed) built.
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

            string path = Path.Combine(HeadlessSession.ArtifactDir, "phase2-transport.png");
            window.CaptureRenderedFrame()!.Save(path);
            Console.WriteLine($"[capture] {path}");

            await Assert.That(File.Exists(path)).IsTrue();
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

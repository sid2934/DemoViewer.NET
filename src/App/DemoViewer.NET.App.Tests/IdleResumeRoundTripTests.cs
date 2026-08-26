#region

using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     End-to-end coverage of the idle capture → close → resume → restore cycle against a REAL demo (skips
///     when none is available, via <see cref="DemoTestHelper.RequireDemo()" />). This is the functional heart
///     the pure-logic <see cref="IdleControllerTests" /> can't reach: it proves the demo is actually released
///     on idle-entry and that reopening restores the captured playback frame + active tab. Two sequential
///     heavy loads (open, then the resume re-parse), so it is <see cref="NotInParallelAttribute" /> and heavy.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class IdleResumeRoundTripTests
{
    [Test]
    public async Task Idle_CapturesClosesAndRestores_PlaybackFrameAndTab()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());

            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.HasFile).IsTrue().Because("the demo must load before we can idle-close it");

            // Seek to a non-trivial frame and note the active tab — this is what resume must restore.
            int targetFrame = Math.Min(120, vm.Playback.TotalFrames - 1);
            vm.Playback.SeekToFrame(targetFrame);
            string? tabId = vm.SelectedTab?.TabId;
            await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(targetFrame);

            // Enter idle: captures resume state, then closes the demo (releases its memory).
            await vm.EnterIdleModeAsync();
            await Assert.That(vm.IsIdle).IsTrue();
            await Assert.That(vm.HasFile).IsFalse().Because("idle-entry closes the open demo to conserve RAM");

            // Resume: reopens the same demo and restores the captured frame + tab.
            await vm.ResumeFromIdleAsync();
            await Assert.That(vm.IsIdle).IsFalse();
            await Assert.That(vm.HasFile).IsTrue().Because("resume reopens the captured demo");
            await Assert.That(vm.Playback.CurrentFrameIndex)
                .IsEqualTo(targetFrame)
                .Because("resume must land back on the frame playback sat at when idle engaged");
            await Assert.That(vm.SelectedTab?.TabId).IsEqualTo(tabId);
        });
    }
}

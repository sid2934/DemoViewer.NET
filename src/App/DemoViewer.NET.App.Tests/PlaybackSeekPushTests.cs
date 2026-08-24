#region

using Avalonia.Threading;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A DISCRETE seek (nav bar / frame box / prev-next / semantic nav) must push the module-facing
///     <c>Advanced</c> event so module viewports (the 2D playback) update on navigation — not only during
///     the play loop. The push is a coalesced, Render-priority dispatch, so the test pumps the dispatcher
///     (RunJobs) before asserting — a synchronous assert would see nothing (posted-but-never-pumped).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class PlaybackSeekPushTests
{
    [Test]
    public async Task DiscreteSeek_PushesAdvanced_AtNewFrame_AndRapidSeeksLandLatest()
    {
        IReadOnlyList<DemoFrame> frames = LoadFrames();

        await HeadlessSession.RunOnUi(async () =>
        {
            PlaybackController controller = NewWiredController(frames);
            List<int> pushed = new();
            controller.Advanced += pf => pushed.Add(pf.FrameIndex);

            // (a) a single discrete seek pushes Advanced at the NEW frame.
            int target = frames.Count / 2;
            controller.SeekToFrame(target);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(controller.CurrentFrameIndex).IsEqualTo(target);
            await Assert.That(pushed.Contains(target)).IsTrue();

            // (b) rapid prev→prev→prev then one pump: the coalesced push lands on the LATEST frame, never
            // a stale one (proves a queued push reads current position, not the frame it was queued at).
            pushed.Clear();
            controller.SeekToFrame(target - 1);
            controller.SeekToFrame(target - 2);
            controller.SeekToFrame(target - 3);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(controller.CurrentFrameIndex).IsEqualTo(target - 3);
            await Assert.That(pushed.Count > 0 && pushed[^1] == target - 3).IsTrue();
        });
    }

    [Test]
    public async Task StepForward_PushesAdvanced()
    {
        IReadOnlyList<DemoFrame> frames = LoadFrames();

        await HeadlessSession.RunOnUi(async () =>
        {
            PlaybackController controller = NewWiredController(frames);

            int start = frames.Count / 2;
            controller.SeekToFrame(start); // establish the authoritative tracker at `start`
            Dispatcher.UIThread.RunJobs();

            List<int> pushed = new();
            controller.Advanced += pf => pushed.Add(pf.FrameIndex);

            controller.StepForward(); // a paused frame-step must push too (in-place or re-seek path)
            Dispatcher.UIThread.RunJobs();

            await Assert.That(controller.CurrentFrameIndex).IsEqualTo(start + 1);
            await Assert.That(pushed.Contains(start + 1)).IsTrue();
        });
    }

    private static IReadOnlyList<DemoFrame> LoadFrames()
    {
        string path = DemoTestHelper.RequireDemo();
        return DemoTestHelper.GetOrParse(path).Frames;
    }

    // A controller wired the way the shell wires it, but with a SYNCHRONOUS ApplySeek (build tracker →
    // PublishTracker) so the test is deterministic. The fix lives in PublishTracker / StepForward, so the
    // synchronous stub exercises exactly the controller push contract.
    private static PlaybackController NewWiredController(IReadOnlyList<DemoFrame> frames)
    {
        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        controller.ApplySeek = idx =>
        {
            EntityTracker t = new();
            t.ReplayToIndex(idx, frames);
            controller.PublishTracker(t);
        };
        return controller;
    }
}

#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Scrubbing, asserted on the recording context's <c>RequestSeekToFrame</c> log rather than on pixels:
///     the requirement is that every scrub lands on the SHARED clock, because that is what LiveSync observes
///     and what keeps every other tab in step.
/// </summary>
[NotInParallel]
public class Playback2DTimelineScrubTests
{
    [Test]
    public async Task PointerPressOnScrubBar_RequestsSeekToProportionalFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            Panel bar = Playback2DTimelineHarness.ScrubBar(Playback2DTimelineHarness.Timeline(view));

            double width = bar.Bounds.Width;
            await Assert.That(width).IsGreaterThan(100);
            await Assert.That(vm.Timeline.PixelWidth).IsEqualTo(width);

            Point mid = Playback2DTimelineHarness.ToWindow(bar, window, width / 2, bar.Bounds.Height / 2);
            window.MouseDown(mid, MouseButton.Left);
            window.MouseUp(mid, MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(ctx.SeekFrames.Count).IsGreaterThanOrEqualTo(1);

            int expected = vm.Timeline.FrameIndexAt(width / 2);
            Console.WriteLine($"[scrub] width={width:F0} requested={ctx.SeekFrames[0]} expected={expected}");
            await Assert.That(ctx.SeekFrames[0]).IsEqualTo(expected);
            await Assert.That(ctx.SeekFrames[0]).IsBetween(450, 550);
        });
    }

    [Test]
    public async Task HoverOverScrubBar_ShowsTheTargetFrameInTheFooter()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);
            Panel bar = Playback2DTimelineHarness.ScrubBar(timeline);

            double width = bar.Bounds.Width;
            Point at = Playback2DTimelineHarness.ToWindow(bar, window, width / 4, bar.Bounds.Height / 2);
            window.MouseMove(at);
            Playback2DTimelineHarness.Pump();

            int expected = vm.Timeline.FrameIndexAt(width / 4);
            TextBlock readout = timeline.FindControl<TextBlock>("HoverReadout")
                                ?? throw new InvalidOperationException("hover readout not found");

            Console.WriteLine($"[hover] text=\"{readout.Text}\" expected={expected}");

            // A hover readout the VM computes but the footer never shows is the same as no hover readout.
            await Assert.That(vm.Timeline.HoverText)
                .Contains(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Assert.That(readout.Text).IsEqualTo(vm.Timeline.HoverText);

            // Hover is a read: it must never move the clock.
            await Assert.That(ctx.SeekFrames.Count).IsEqualTo(0);
        });
    }

    [Test]
    public async Task PointerDragAcrossScrubBar_PushesMonotonicallyIncreasingFrames()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            Panel bar = Playback2DTimelineHarness.ScrubBar(Playback2DTimelineHarness.Timeline(view));

            double width = bar.Bounds.Width;
            double y = bar.Bounds.Height / 2;

            window.MouseDown(Playback2DTimelineHarness.ToWindow(bar, window, width * 0.1, y), MouseButton.Left);
            double[] fractions = [0.3, 0.5, 0.7, 0.9];
            foreach (double fraction in fractions)
            {
                window.MouseMove(Playback2DTimelineHarness.ToWindow(bar, window, width * fraction, y));
            }

            window.MouseUp(Playback2DTimelineHarness.ToWindow(bar, window, width * 0.9, y), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[scrub-drag] frames={string.Join(",", ctx.SeekFrames)}");
            await Assert.That(ctx.SeekFrames.Count).IsGreaterThanOrEqualTo(5);

            for (int i = 1; i < ctx.SeekFrames.Count; i++)
            {
                await Assert.That(ctx.SeekFrames[i]).IsGreaterThanOrEqualTo(ctx.SeekFrames[i - 1]);
            }

            await Assert.That(ctx.SeekFrames[^1]).IsGreaterThan(ctx.SeekFrames[0]);
        });
    }

    [Test]
    public async Task ClickOnRoundBand_SeeksToBandStart()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            ItemsControl rounds = Playback2DTimelineHarness.RoundsBand(Playback2DTimelineHarness.Timeline(view));

            await Assert.That(vm.Timeline.Bands.Count).IsGreaterThanOrEqualTo(2);
            TimelineBandViewModel band = vm.Timeline.Bands[1];

            // Deliberately click PAST the band's first pixel: the band seeks to its own start frame, not to
            // whatever frame the cursor's x maps to.
            Point inside = Playback2DTimelineHarness.ToWindow(rounds, window,
                band.X + Math.Min(20, band.Width / 2), rounds.Bounds.Height / 2);
            window.MouseDown(inside, MouseButton.Left);
            window.MouseUp(inside, MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[band-click] band=[{band.StartFrameIndex}..{band.EndFrameIndex}] "
                              + $"x={band.X:F0} w={band.Width:F0} seeks={string.Join(",", ctx.SeekFrames)}");

            await Assert.That(ctx.SeekFrames).Contains(band.StartFrameIndex);
        });
    }
}

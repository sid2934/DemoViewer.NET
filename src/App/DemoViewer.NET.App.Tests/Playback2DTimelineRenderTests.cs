#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Render probes for the docked timeline: it draws SOMETHING where it is supposed to, and nothing at all
///     when its feature gate is off. Non-blank probes rather than goldens — the golden corpus starts at B0 on
///     the CPU surface provider.
/// </summary>
[NotInParallel]
public class Playback2DTimelineRenderTests
{
    [Test]
    public async Task Timeline_RendersNonBlank_WithRoundsAndKills()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(200, 400);

            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);

            await Assert.That(timeline.IsVisible).IsTrue();
            await Assert.That(timeline.Bounds.Height).IsGreaterThan(0);
            await Assert.That(vm.Timeline.Bands.Count).IsGreaterThan(0);
            await Assert.That(vm.Timeline.Markers.Count).IsGreaterThan(0);

            Playback2DTimelineHarness.Pump(4);
            WriteableBitmap? bmp = window.CaptureRenderedFrame();
            await Assert.That(bmp).IsNotNull();

            Point origin = Playback2DTimelineHarness.ToWindow(timeline, window, 0, 0);
            int nonBg = ScanBand(bmp!, (int)origin.Y, (int)(origin.Y + timeline.Bounds.Height));

            string path = Path.Combine(HeadlessSession.ArtifactDir, "playback2d-timeline.png");
            bmp!.Save(path);
            Console.WriteLine($"[timeline-render] rows={origin.Y}..{origin.Y + timeline.Bounds.Height} "
                              + $"nonBg={nonBg} bands={vm.Timeline.Bands.Count} "
                              + $"markers={vm.Timeline.Markers.Count} -> {path}");

            await Assert.That(nonBg).IsGreaterThan(100);
        });
    }

    [Test]
    public async Task Timeline_HiddenWhenFeatureGateOff()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);
            Playback2DViewport viewport = Playback2DTimelineHarness.Viewport(view);
            await Assert.That(timeline.IsVisible).IsTrue();

            double viewportWithTimeline = viewport.Bounds.Height;
            double timelineHeight = timeline.Bounds.Height;
            await Assert.That(timelineHeight).IsGreaterThan(0);

            ctx.Gate!.SetEnabled("playback2d.timeline", false);
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.IsTimelineEnabled).IsFalse();
            await Assert.That(vm.Timeline.IsVisible).IsFalse();
            await Assert.That(timeline.IsVisible).IsFalse();

            // Auto-sized row: an off gate must leave no layout HOLE — the viewport takes the space back.
            Console.WriteLine($"[timeline-gate] viewport {viewportWithTimeline:F0} -> {viewport.Bounds.Height:F0}"
                              + $" (timeline was {timelineHeight:F0})");
            await Assert.That(viewport.Bounds.Height).IsEqualTo(viewportWithTimeline + timelineHeight);
        });
    }

    [Test]
    public async Task Timeline_ShowsNoTrackToggleForAnEventTheDemoLacks()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Timelines.Remove("bomb_planted");
            vm.OnDeactivated();
            vm.OnActivated(ctx);
            Playback2DTimelineHarness.Show(vm);

            await Assert.That(vm.Timeline.Tracks.Single(t => t.Id == "bomb").IsAvailable).IsFalse();
            await Assert.That(vm.Timeline.Tracks.Single(t => t.Id == "kill").IsAvailable).IsTrue();
            await Assert.That(vm.Timeline.Tracks.Single(t => t.Id == "round").IsAvailable).IsTrue();
        });
    }

    // Counts pixels in a horizontal band that differ from the viewport background — the same probe shape
    // Playback2DCameraModeTests uses, restricted to the timeline's own rows.
    private static int ScanBand(WriteableBitmap bmp, int top, int bottom)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int first = Math.Clamp(top, 0, size.Height);
        int last = Math.Clamp(bottom, 0, size.Height);

        int nonBg = 0;
        for (int y = first; y < last; y++)
        {
            for (int x = 0; x < size.Width; x++)
            {
                int i = (y * size.Width + x) * 4;
                if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 0)
                {
                    nonBg++;
                }
            }
        }

        return nonBg;
    }
}

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
            BandScan scan = Scan(bmp!, new PixelRect(
                (int)origin.X, (int)origin.Y,
                (int)timeline.Bounds.Width, (int)timeline.Bounds.Height));

            string path = Path.Combine(HeadlessSession.ArtifactDir, "playback2d-timeline.png");
            bmp!.Save(path);
            Console.WriteLine($"[timeline-render] rect={origin.X},{origin.Y} "
                              + $"{timeline.Bounds.Width:F0}x{timeline.Bounds.Height:F0} "
                              + $"area={scan.Area} fill=#{scan.Fill:X8} x{scan.FillCount} "
                              + $"ink={scan.Ink} colours={scan.DistinctColours} "
                              + $"anyChannelNonZero={scan.AnyChannelNonZero} "
                              + $"bands={vm.Timeline.Bands.Count} markers={vm.Timeline.Markers.Count} "
                              + $"-> {path}");

            // D6 G-6: this used to assert `nonBg > 100`, where nonBg counted pixels with ANY non-zero
            // channel. Pb2dPanelBg is #1A1E24 — every channel non-zero — so the opaque panel fill alone
            // satisfied it several thousand times over, on a completely empty timeline. The line below
            // records that the old metric is still trivially true, so the reason this case was rewritten
            // cannot be lost by someone restoring it.
            await Assert.That(scan.AnyChannelNonZero).IsGreaterThan(scan.Area * 9 / 10)
                .Because("the panel fill is opaque and non-black, which is precisely why counting "
                         + "non-black pixels measured nothing");

            // The fill is found rather than hard-coded, so a re-themed panel does not re-baseline this.
            await Assert.That(scan.FillCount).IsGreaterThan(scan.Area / 4)
                .Because("the most common colour in the rect must BE the panel fill; if it is not, this "
                         + "probe is measuring the wrong rectangle and everything below is noise");

            // Ink is what is drawn ON the fill: round bands, kill and bomb glyphs, tick labels, playhead.
            await Assert.That(scan.Ink).IsGreaterThan(100)
                .Because("an empty timeline is a rect of one colour — that is the state this case exists "
                         + "to fail on");
            await Assert.That(scan.DistinctColours).IsGreaterThan(4)
                .Because("bands, markers and text are several colours; one flat wash over the fill would "
                         + "clear the Ink floor while still being nothing a user could read");
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

    /// <summary>What one rectangle of the captured frame actually contains.</summary>
    /// <param name="Area">Pixels examined.</param>
    /// <param name="Fill">The most common colour — for a panel, its background.</param>
    /// <param name="FillCount">How many pixels are that colour.</param>
    /// <param name="Ink">Pixels that are NOT the fill: everything drawn on top of it.</param>
    /// <param name="DistinctColours">Distinct colours present, fill included.</param>
    /// <param name="AnyChannelNonZero">The superseded metric, kept so the assertions can show it is vacuous.</param>
    private readonly record struct BandScan(
        int Area, uint Fill, int FillCount, int Ink, int DistinctColours, int AnyChannelNonZero);

    // The timeline's OWN rectangle, not a full-width row band: at those rows the window also holds the
    // splitter and the roster panel, whose pixels are not evidence about the timeline.
    private static BandScan Scan(WriteableBitmap bmp, PixelRect rect)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int x0 = Math.Clamp(rect.X, 0, size.Width);
        int x1 = Math.Clamp(rect.X + rect.Width, 0, size.Width);
        int y0 = Math.Clamp(rect.Y, 0, size.Height);
        int y1 = Math.Clamp(rect.Y + rect.Height, 0, size.Height);

        Dictionary<uint, int> histogram = [];
        int area = 0;
        int anyChannelNonZero = 0;

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * size.Width + x) * 4;
                uint colour = (uint)(buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16)
                                     | (buffer[i + 3] << 24));
                histogram[colour] = histogram.GetValueOrDefault(colour) + 1;
                area++;
                if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 0)
                {
                    anyChannelNonZero++;
                }
            }
        }

        if (area == 0)
        {
            return new BandScan(0, 0, 0, 0, 0, 0);
        }

        (uint fill, int fillCount) = histogram.MaxBy(e => e.Value);
        return new BandScan(area, fill, fillCount, area - fillCount, histogram.Count, anyChannelNonZero);
    }
}

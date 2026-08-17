#region

using Cs2VideoGenerator.Core.Models;
using DemoViewer.NET.Services.LiveSync;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     The reel path's tick-space boundary. A
///     <see cref="ReelClip" /> window is FRAME CLOCK — the packaged clip planner leaves it there, so
///     every clamp (round floor, demo-end, reach-back) happens in one clock — and the
///     <c>TickOffset</c> shim converts it into CS2 demo-tick space exactly ONCE, here at emission.
///     Both emission paths (the real capture compilation and the dry-run walk) go through
///     <c>ReelJobService.Cs2Range</c>, which is what stops the two from drifting apart: a dry run
///     that validated a different range than the render is a silently wrong validation.
/// </summary>
[Category("Unit")]
public class ReelClipTickSpaceTests
{
    private static ReelClip Clip(long start, long end) =>
        new("/d/a.dem", "sha", 76561198000000001, "s1mple", start, end, 64, "double kill");

    private static ReelRequest Request(params ReelClip[] clips) =>
        new(clips, "/out", "reel", "mp4", 60, true, false, 20, null, false, true);

    [Test]
    public async Task Cs2Range_IdentityShim_LeavesTheFrameClockWindowUntouched()
    {
        (int start, int end) = ReelJobService.Cs2Range(Clip(5000, 6000), 0);

        await Assert.That(start).IsEqualTo(5000);
        await Assert.That(end).IsEqualTo(6000);
    }

    [Test]
    public async Task Cs2Range_AppliesTheOffsetToBothEnds()
    {
        (int start, int end) = ReelJobService.Cs2Range(Clip(5000, 6000), 32);

        await Assert.That(start).IsEqualTo(5032);
        await Assert.That(end).IsEqualTo(6032);
    }

    [Test]
    public async Task BuildCompilation_StampsCs2DemoTicks_OnceEach()
    {
        // The clip that reaches CSVG carries frame-clock + offset — exactly once. A dialog that had
        // pre-applied the offset (the pre-extraction shape) would double it here.
        Cs2Compilation compilation = ReelJobService.BuildCompilation(
            Request(Clip(500, 900), Clip(1500, 1900)), 1920, 1080, 32);

        await Assert.That(compilation.Clips[0].StartTick).IsEqualTo(532);
        await Assert.That(compilation.Clips[0].EndTick).IsEqualTo(932);
        await Assert.That(compilation.Clips[1].StartTick).IsEqualTo(1532);
        await Assert.That(compilation.Clips[1].EndTick).IsEqualTo(1932);
    }

    [Test]
    public async Task BuildCompilation_WithoutAnOffset_EmitsTheFrameClockWindow()
    {
        Cs2Compilation compilation = ReelJobService.BuildCompilation(Request(Clip(500, 900)), 1920, 1080, 0);

        await Assert.That(compilation.Clips[0].StartTick).IsEqualTo(500);
        await Assert.That(compilation.Clips[0].EndTick).IsEqualTo(900);
    }
}

#region

using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Cadence-invariant gate for the play loop: playback rate (frames/second) must be LINEAR in
///     <c>Speed</c>. A prior bug paced the <c>DispatcherTimer</c> at <c>TickRate×Speed</c> AND stepped
///     ~Speed frames per fire, double-applying Speed (frames/sec = <c>TickRate×Speed²</c>), so 0.5× played
///     at quarter-speed and 2× at quadruple, with only 1× correct. <c>EffectiveFramesPerSecond</c> mirrors
///     the loop's two real factors (timer rate × per-fire step); these assertions FAIL under the quadratic
///     bug and PASS once the timer is paced at a fixed rate.
/// </summary>
public class PlaybackControllerSpeedTests
{
    [Test]
    public async Task PlaybackRate_IsLinearInSpeed_NotQuadratic()
    {
        PlaybackController c = new();
        c.LoadDemo(Array.Empty<DemoFrame>(), 64);

        c.Speed = 1.0;
        double f1 = c.EffectiveFramesPerSecond();
        c.Speed = 0.5;
        double fHalf = c.EffectiveFramesPerSecond();
        c.Speed = 2.0;
        double fDouble = c.EffectiveFramesPerSecond();
        c.Speed = 0.25;
        double fQuarter = c.EffectiveFramesPerSecond();

        // 1× of a 64-tick demo is real-time = 64 frames/sec.
        await Assert.That(Math.Abs(f1 - 64.0)).IsLessThan(1e-6);

        // Linear in Speed. The quadratic bug instead gave 0.5×→16, 2×→256, 0.25×→4.
        await Assert.That(Math.Abs(fHalf - 32.0)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(fDouble - 128.0)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(fQuarter - 16.0)).IsLessThan(1e-6);

        // The ratios are the property the bug violated (half the speed → half the rate, exactly).
        await Assert.That(Math.Abs(fHalf / f1 - 0.5)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(fDouble / f1 - 2.0)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(fQuarter / f1 - 0.25)).IsLessThan(1e-9);
    }

    [Test]
    public async Task PlaybackRate_EqualsTickRate_AtOneX_ForAnyTickRate()
    {
        // 1× advances one demo-frame per server tick → frames/sec == tick rate, for any tick rate.
        foreach (int tickRate in new[]
                 {
                     32, 64, 128
                 })
        {
            PlaybackController c = new();
            c.LoadDemo(Array.Empty<DemoFrame>(), tickRate);
            c.Speed = 1.0;
            await Assert.That(Math.Abs(c.EffectiveFramesPerSecond() - tickRate)).IsLessThan(1e-6);
        }
    }
}

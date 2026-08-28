#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The hysteresis formula and its dwell, driven entirely by injected <c>dt</c> — no demo, no clock,
///     no window. Every constant here is justified against CS2 physics or <c>FloorSplitter</c>'s own
///     arithmetic in the B3 plan's "Hysteresis sizing" section; these are the assertions that stop a
///     retune from silently reintroducing stair dither.
/// </summary>
public class LevelHysteresisTests
{
    private const double Dt = 1.0 / 64;

    [Test]
    public async Task SpatialBand_ClampsToMin_OnThinBands()
    {
        MapSpace space = Two(0, 64, 64, 128);
        double band = LevelHysteresis.SpatialBand(space.Levels[0], space.Levels[1],
            LevelHysteresisOptions.Default);

        // 0.25 × 64 = 16, below the 32u boundary-jitter floor.
        await Assert.That(band).IsEqualTo(32);
    }

    [Test]
    public async Task SpatialBand_ClampsToMax_OnWideBands()
    {
        MapSpace space = Two(-10000, 0, 0, 10000);
        double band = LevelHysteresis.SpatialBand(space.Levels[0], space.Levels[1],
            LevelHysteresisOptions.Default);

        await Assert.That(band).IsEqualTo(128);
    }

    /// <summary>
    ///     CS2 jump velocity 301 u/s under <c>sv_gravity 800</c> peaks at 301²/(2·800) ≈ 56.6u. At the
    ///     128u cap a jump, a step-up (18u) and a crouch (≈18u) are all geometrically incapable of
    ///     changing a floor.
    /// </summary>
    [Test]
    public async Task JumpApex_56u_DoesNotSwitch_AtMaxBand()
    {
        MapSpace space = Two(-10000, 0, 0, 10000);
        LevelHysteresis hysteresis = new();
        SceneTime time = Time(Dt);

        MapLevelId lower = space.Levels[0].Id;
        hysteresis.Update(in time, -20, space);
        await Assert.That(hysteresis.Current).IsEqualTo(lower);

        // A whole jump's worth of scene time spent at the apex, above the boundary.
        for (int i = 0; i < 64; i++)
        {
            hysteresis.Update(in time, 56.6, space);
        }

        await Assert.That(hysteresis.Current).IsEqualTo(lower);
    }

    [Test]
    public async Task Dither_AcrossBoundary_DoesNotSwitch()
    {
        MapSpace space = Two(-640, 0, 0, 640);
        LevelHysteresis hysteresis = new();
        SceneTime time = Time(Dt);

        MapLevelId lower = space.Levels[0].Id;
        hysteresis.Update(in time, -100, space);

        // Two seconds of scene time oscillating ±10u across the boundary.
        for (int i = 0; i < 128; i++)
        {
            hysteresis.Update(in time, i % 2 == 0 ? 10 : -10, space);
        }

        await Assert.That(hysteresis.Current).IsEqualTo(lower);
    }

    [Test]
    public async Task SustainedCrossing_SwitchesAfter_0_35s()
    {
        MapSpace space = Two(-640, 0, 0, 640);
        LevelHysteresis hysteresis = new();
        SceneTime time = Time(Dt);

        MapLevelId lower = space.Levels[0].Id;
        MapLevelId upper = space.Levels[1].Id;
        hysteresis.Update(in time, -300, space);
        await Assert.That(hysteresis.Current).IsEqualTo(lower);

        // Well clear of the 128u band, held for 0.34 s: not yet.
        Advance(hysteresis, space, 400, 0.34, Dt);
        await Assert.That(hysteresis.Current).IsEqualTo(lower);

        Advance(hysteresis, space, 400, 0.04, Dt);
        await Assert.That(hysteresis.Current).IsEqualTo(upper);
    }

    [Test]
    public async Task Discontinuity_SwitchesImmediately()
    {
        MapSpace space = Two(-640, 0, 0, 640);
        LevelHysteresis hysteresis = new();
        SceneTime settled = Time(Dt);
        hysteresis.Update(in settled, -300, space);

        SceneTime seek = Time(Dt) with
        {
            IsDiscontinuity = true
        };
        hysteresis.Update(in seek, 400, space);

        await Assert.That(hysteresis.Current).IsEqualTo(space.Levels[1].Id);
        await Assert.That(hysteresis.PendingSeconds).IsEqualTo(0);
    }

    /// <summary>
    ///     The dwell is scene time, not frames: a 30 fps export and a 144 fps interactive session must
    ///     switch at the same moment of the demo (design §5.1).
    /// </summary>
    [Test]
    public async Task Dwell_IsFrameRateIndependent()
    {
        foreach (double dt in new[]
                 {
                     1.0 / 30, 1.0 / 144
                 })
        {
            MapSpace space = Two(-640, 0, 0, 640);
            LevelHysteresis hysteresis = new();
            SceneTime time = Time(dt);
            hysteresis.Update(in time, -300, space);

            Advance(hysteresis, space, 400, 0.30, dt);
            await Assert.That(hysteresis.Current).IsEqualTo(space.Levels[0].Id);

            Advance(hysteresis, space, 400, 0.10, dt);
            await Assert.That(hysteresis.Current).IsEqualTo(space.Levels[1].Id);
        }
    }

    /// <summary>
    ///     The sticky overload is the spatial half on its own — no dwell, because a marker must never lag
    ///     its own level. Here it holds the previous answer inside the band and yields outside it.
    /// </summary>
    [Test]
    public async Task StickyOverload_HoldsInsideTheBand_AndYieldsOutsideIt()
    {
        MapSpace space = Two(-640, 0, 0, 640);
        MapLevelId lower = space.Levels[0].Id;

        await Assert.That(space.LevelFor(100, lower)!.Id).IsEqualTo(lower);
        await Assert.That(space.LevelFor(200, lower)!.Id).IsEqualTo(space.Levels[1].Id);
        await Assert.That(space.LevelFor(100, null)!.Id).IsEqualTo(space.Levels[1].Id);
    }

    /// <summary>
    ///     Plan risk R4's mitigation is "all four constants live in <c>LevelHysteresisOptions</c>, so
    ///     retuning is a one-line change with no API break". <c>Default</c> is a get-only static, so the
    ///     only way to retune is to <i>pass</i> an options instance — which therefore has to reach the
    ///     spatial band, not just the dwell.
    /// </summary>
    [Test]
    public async Task Options_ReachTheSpatialBand_NotJustTheDwell()
    {
        MapSpace space = Two(-640, 0, 0, 640);
        MapLevelId lower = space.Levels[0].Id;
        MapLevelId upper = space.Levels[1].Id;

        // No band and no dwell: the chooser must track the raw boundary exactly.
        LevelHysteresis loose = new(new LevelHysteresisOptions(0, 0, 0, 0));
        SceneTime time = Time(Dt);

        loose.Update(in time, -300, space);
        await Assert.That(loose.Current).IsEqualTo(lower);

        loose.Update(in time, 10, space);
        await Assert.That(loose.Current).IsEqualTo(upper)
            .Because("a zero band and zero dwell must not still hold the old level for 128u");

        // And the default band DOES hold at the same Z, so the case above is the options talking.
        LevelHysteresis tuned = new();
        tuned.Update(in time, -300, space);
        tuned.Update(in time, 10, space);
        await Assert.That(tuned.Current).IsEqualTo(lower);
    }

    private static void Advance(LevelHysteresis hysteresis, MapSpace space, double z, double seconds,
        double dt)
    {
        SceneTime time = Time(dt);
        int steps = (int)Math.Round(seconds / dt);
        for (int i = 0; i < steps; i++)
        {
            hysteresis.Update(in time, z, space);
        }
    }

    private static SceneTime Time(double dt) => new(0, 0, 0, dt, false);

    private static MapSpace Two(double aMin, double aMax, double bMin, double bMax)
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(aMin, aMax), new FloorSlice(bMin, bMax)]);
        return space;
    }
}

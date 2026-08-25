#region

using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure-math gates for the per-slice camera: the render-frame lerp toward a target, settle
///     detection, and the manual-override carry. No Avalonia / no render — the smooth-mode motion is
///     verified here so the headless render tests only need to confirm a mode renders (not that it converges
///     through the render loop). The step factor is an explicit interpolation value (never wall-clock), so
///     "halfway moves between" and "N steps converge" are deterministic assertions.
/// </summary>
public class SliceCameraTests
{
    private static ViewportTransform Fit(double cx, double cy, double half) =>
        ViewportTransform.Fit(800, 600, cx - half, cy - half, cx + half, cy + half);

    [Test]
    public async Task StepToward_HalfFactor_MovesHalfwayBetweenCentres()
    {
        SliceCamera cam = new(Fit(0, 0, 1000));
        ViewportTransform target = Fit(1000, 400, 1000);

        SliceCamera stepped = cam.StepToward(target, 0.5);

        // Centre moves halfway toward the target on a 0.5 factor.
        await Assert.That(Math.Abs(stepped.Current.CenterX - 500)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(stepped.Current.CenterY - 200)).IsLessThan(1e-6);
    }

    [Test]
    public async Task StepToward_FactorOne_LandsExactlyOnTarget()
    {
        SliceCamera cam = new(Fit(-500, 700, 1500));
        ViewportTransform target = Fit(900, -300, 600);

        SliceCamera stepped = cam.StepToward(target, 1.0);

        await Assert.That(Math.Abs(stepped.Current.CenterX - target.CenterX)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(stepped.Current.CenterY - target.CenterY)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(stepped.Current.EffectiveScale - target.EffectiveScale)).IsLessThan(1e-6);
    }

    [Test]
    public async Task StepToward_FactorZero_IsNoOp()
    {
        SliceCamera cam = new(Fit(0, 0, 1000));
        ViewportTransform target = Fit(5000, 5000, 50);

        SliceCamera stepped = cam.StepToward(target, 0.0);

        await Assert.That(stepped.Current.CenterX).IsEqualTo(cam.Current.CenterX);
        await Assert.That(stepped.Current.CenterY).IsEqualTo(cam.Current.CenterY);
        await Assert.That(stepped.Current.EffectiveScale).IsEqualTo(cam.Current.EffectiveScale);
    }

    [Test]
    public async Task StepToward_RepeatedSteps_ConvergeWithinEpsilon()
    {
        SliceCamera cam = new(Fit(0, 0, 2000));
        ViewportTransform target = Fit(1200, -800, 500);

        // ~90 steps of a typical per-frame factor (~0.11 ≈ 1-e^(-7/60)) converge to sub-pixel.
        for (int i = 0; i < 90; i++)
        {
            cam = cam.StepToward(target, 0.11);
        }

        await Assert.That(cam.IsSettledAt(target)).IsTrue();
    }

    [Test]
    public async Task IsSettledAt_FarFromTarget_IsFalse()
    {
        SliceCamera cam = new(Fit(0, 0, 1000));
        ViewportTransform target = Fit(3000, 3000, 1000);
        await Assert.That(cam.IsSettledAt(target)).IsFalse();
    }

    [Test]
    public async Task StepToward_PreservesManualOverrideFlag()
    {
        SliceCamera cam = new(Fit(0, 0, 1000))
        {
            ManualOverride = true
        };
        SliceCamera stepped = cam.StepToward(Fit(100, 100, 1000), 0.5);
        await Assert.That(stepped.ManualOverride).IsTrue();
    }

    [Test]
    public async Task StepToward_LerpsZoomTowardTarget()
    {
        // A manually-zoomed-in camera (zoom 4) relaxing toward a Fit target (zoom 1) lerps the zoom down.
        ViewportTransform zoomedIn = ViewportTransform.Fit(800, 600, -1000, -1000, 1000, 1000)
            .ZoomAbout(400, 300, 4.0);
        SliceCamera cam = new(zoomedIn);
        ViewportTransform target = ViewportTransform.Fit(800, 600, -1000, -1000, 1000, 1000); // zoom 1

        SliceCamera stepped = cam.StepToward(target, 0.5);

        await Assert.That(stepped.Current.Zoom).IsLessThan(zoomedIn.Zoom);
        await Assert.That(stepped.Current.Zoom).IsGreaterThan(target.Zoom);
    }
}

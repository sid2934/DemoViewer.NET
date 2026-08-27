#region

using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The port of the App's <c>Playback2DInterpolationTests</c> onto <see cref="MarkerSmoother" />,
///     case for case — plus the discontinuity snap this suite adds.
///     <para>
///         The four original cases are the behaviour a viewer notices immediately when it breaks: a dot
///         that flies in from the origin on its first frame, a dot that steps instead of gliding, a dot
///         that streaks across the map on a seek, and a re-joining player who glides in from where they
///         disconnected. They no longer need a UI thread to assert.
///     </para>
/// </summary>
public class MarkerSmoothingTests
{
    private const double Dt = 1.0 / 60;

    [Test]
    public async Task FirstAppearance_SnapsToTheSampledSpot()
    {
        MarkerSmoother smoother = new();
        bool moving = smoother.Advance([Marker(0, 1200f, -800f)], Dt);

        (float X, float Y)? p = smoother.Position(0);
        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Value.X).IsEqualTo(1200f);
        await Assert.That(p.Value.Y).IsEqualTo(-800f);
        await Assert.That(moving).IsFalse(); // nothing to animate on the seeding frame
    }

    [Test]
    public async Task SmallMove_GlidesPartwayThenConverges()
    {
        MarkerSmoother smoother = new();
        smoother.Advance([Marker(0, 100f, 0f)], Dt);

        bool moving = smoother.Advance([Marker(0, 140f, 0f)], Dt);
        (float X, float Y) stepped = smoother.Position(0)!.Value;

        await Assert.That(moving).IsTrue();
        await Assert.That(stepped.X).IsGreaterThan(100f);
        await Assert.That(stepped.X).IsLessThan(140f);

        for (int i = 0; i < 240; i++)
        {
            smoother.Advance([Marker(0, 140f, 0f)], Dt);
        }

        (float X, float Y) settled = smoother.Position(0)!.Value;
        await Assert.That(Math.Abs(settled.X - 140f)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(settled.Y)).IsLessThan(0.5f);
    }

    [Test]
    public async Task TeleportJump_SnapsImmediately()
    {
        MarkerSmoother smoother = new();
        smoother.Advance([Marker(0, 0f, 0f)], Dt);

        bool moving = smoother.Advance([Marker(0, 4000f, -3000f)], Dt);
        (float X, float Y) p = smoother.Position(0)!.Value;

        await Assert.That(p.X).IsEqualTo(4000f);
        await Assert.That(p.Y).IsEqualTo(-3000f);
        await Assert.That(moving).IsFalse(); // an instant snap, not an animated glide
    }

    [Test]
    public async Task DepartedSlot_IsPruned_SoARejoinDoesNotGlideFromAStaleSpot()
    {
        MarkerSmoother smoother = new();
        smoother.Advance([Marker(0, 500f, 500f), Marker(1, -500f, -500f)], Dt);
        await Assert.That(smoother.Position(1)).IsNotNull();

        smoother.Advance([Marker(0, 500f, 500f)], Dt);
        await Assert.That(smoother.Position(1)).IsNull();

        smoother.Advance([Marker(0, 500f, 500f), Marker(1, 2000f, 2000f)], Dt);
        (float X, float Y) rejoined = smoother.Position(1)!.Value;
        await Assert.That(rejoined.X).IsEqualTo(2000f);
        await Assert.That(rejoined.Y).IsEqualTo(2000f);
    }

    /// <summary>
    ///     The distance rule already catches a big seek, but a SHORT one moves a player
    ///     less than the teleport threshold — and gliding across that gap draws motion that never
    ///     happened. The distance rule stays: it is what the ported cases above pin.
    /// </summary>
    [Test]
    public async Task Discontinuity_SnapsEveryTrackedSlot_EvenBelowTheTeleportThreshold()
    {
        MarkerSmoother smoother = new();
        smoother.Advance([Marker(0, 0f, 0f), Marker(1, 100f, 100f)], Dt);

        // 40 units: well under the 250-unit teleport threshold, so without the flag both would glide.
        bool moving = smoother.Advance([Marker(0, 40f, 0f), Marker(1, 140f, 100f)], Dt, true);

        await Assert.That(smoother.Position(0)!.Value.X).IsEqualTo(40f);
        await Assert.That(smoother.Position(1)!.Value.X).IsEqualTo(140f);
        await Assert.That(moving).IsFalse();
    }

    /// <summary>
    ///     <b>One owner, and it is the marker layer.</b> Two advances in one frame step every dot twice
    ///     and it glides at double speed — which is what an earlier draft did, by de-duplicating on
    ///     <c>(frame, time)</c> so either the marker layer or the vision layer could drive it. A constant
    ///     frame delta (exactly what a headless render timer produces) then made every call after the
    ///     first a no-op that handed back a stale "still moving", and the self-terminating render loop
    ///     never terminated. This pins the replacement rule.
    /// </summary>
    [Test]
    public async Task Advance_TwiceInOneFrame_DoubleSteps_WhichIsWhyOnlyOneLayerOwnsIt()
    {
        MarkerSmoother single = new();
        single.Advance([Marker(0, 100f, 0f)], Dt);
        single.Advance([Marker(0, 140f, 0f)], Dt);

        MarkerSmoother doubled = new();
        doubled.Advance([Marker(0, 100f, 0f)], Dt);
        doubled.Advance([Marker(0, 140f, 0f)], Dt);
        doubled.Advance([Marker(0, 140f, 0f)], Dt);

        float once = single.Position(0)!.Value.X;
        float twice = doubled.Position(0)!.Value.X;
        Console.WriteLine($"[smoothing] one step={once:F3} two steps={twice:F3}");

        await Assert.That(twice).IsGreaterThan(once);
    }

    /// <summary>
    ///     The settle rule is what lets the render loop stop: once every dot is within half a unit of
    ///     its sample, nothing is moving and the loop stops requesting another frame.
    /// </summary>
    [Test]
    public async Task Advance_OnceSettled_ReportsNothingMoving_ForeverAfter()
    {
        MarkerSmoother smoother = new();
        PlayerMarker[] markers = [Marker(0, 100f, 0f), Marker(1, -40f, 900f)];

        bool moving = true;
        int frames = 0;
        while (moving && frames++ < 500)
        {
            moving = smoother.Advance(markers, Dt);
        }

        await Assert.That(moving).IsFalse();
        Console.WriteLine($"[smoothing] settled after {frames} frames");

        // A constant dt is the headless case, and the case that used to pin the loop on.
        for (int i = 0; i < 200; i++)
        {
            await Assert.That(smoother.Advance(markers, Dt)).IsFalse();
        }

        await Assert.That(smoother.AnyMoving).IsFalse();
    }

    /// <summary>
    ///     The steady state is allocation-free, which is what lets the smoother run every frame.
    ///     <para>
    ///         <b>Tagged Budget because it is an allocation figure</b>, and <c>ci.yml</c> says those
    ///         belong in the budget lane. Untagged it ran in <c>playback2d-tests</c>, both
    ///         <c>render-backends</c> passes and the GPU lane — four blocking lanes for one exact-zero
    ///         assertion that went red once and green on the re-run.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task Advance_SteadyState_AllocatesNothing()
    {
        MarkerSmoother smoother = new();
        PlayerMarker[] markers = new PlayerMarker[10];
        for (int i = 0; i < markers.Length; i++)
        {
            markers[i] = Marker(i, i * 30f, i * 17f);
        }

        for (int i = 0; i < 64; i++)
        {
            smoother.Advance(markers, Dt);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            smoother.Advance(markers, Dt);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 smoothing steps over 10 markers: {delta} bytes");
        await Assert.That(delta).IsEqualTo(0);
    }

    private static PlayerMarker Marker(int slot, float x, float y) =>
        new(slot, 2, x, y, 64f, 0f, RingState.Team, 1.0, "X", true);
}

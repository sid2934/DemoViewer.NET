#region

using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The port of the App's <c>Playback2DInterpolationTests</c> onto <see cref="MarkerSmoother" />,
///     case for case — plus the discontinuity snap B1 adds.
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
    ///     B1's addition. The distance rule already catches a big seek, but a SHORT one moves a player
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
    ///     Both the marker layer and the vision layer drive the smoothing, and the compositor advances
    ///     them in DRAW order — vision (30) before markers (40). The second call in one cycle must be a
    ///     no-op, or every dot would take two smoothing steps per frame and glide at double speed.
    /// </summary>
    [Test]
    public async Task AdvanceOnce_IsIdempotentWithinOneCycle_ButStepsAgainOnANewOne()
    {
        MarkerSmoother smoother = new();
        Scene2DFrame frame = new()
        {
            Markers = [Marker(0, 100f, 0f)]
        };
        SceneTime seed = new(0, 0, 0, Dt, false);
        smoother.AdvanceOnce(in seed, frame);

        Scene2DFrame moved = new()
        {
            Markers = [Marker(0, 140f, 0f)]
        };
        SceneTime time = new(1, 1, 0.5, Dt, false);

        smoother.AdvanceOnce(in time, moved);
        float afterFirst = smoother.Position(0)!.Value.X;
        smoother.AdvanceOnce(in time, moved); // the second layer of the same cycle
        float afterSecond = smoother.Position(0)!.Value.X;

        await Assert.That(afterSecond).IsEqualTo(afterFirst);

        SceneTime nextCycle = time with
        {
            DeltaSeconds = Dt * 1.01
        };
        smoother.AdvanceOnce(in nextCycle, moved);
        await Assert.That(smoother.Position(0)!.Value.X).IsGreaterThan(afterSecond);
    }

    [Test]
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

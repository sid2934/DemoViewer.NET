#region

using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Marker interpolation: the viewport chases each marker's smoothed DRAW position toward its latest
///     sampled spot on the render loop, so markers glide between discrete pushes instead of stepping. It must
///     snap (not glide) on first appearance and on a teleport-sized jump (seek / round reset / respawn
///     elsewhere) so a glide never streaks across the map, and prune slots that leave. Driven directly with a
///     known dt — the headless RAF dt can't be relied on (the "stopwatch fallback" comment notwithstanding,
///     there is none), so the smoothing logic is exercised without the render loop.
/// </summary>
/// <remarks>
///     Runs on the UI thread: <c>Playback2DViewport</c> is a Control, so constructing one verifies
///     dispatcher access. Off-thread these passed only while no dispatch had yet bound the UI thread — a
///     race the assembly warm-up now settles, and the "Call from invalid thread" half of issue #6.
/// </remarks>
public class Playback2DInterpolationTests
{
    private const double Dt = 1.0 / 60;

    [Test]
    public async Task FirstAppearance_SnapsToSampledSpot_NoGlideFromOrigin() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DViewport vp = new();
            bool moving = vp.AdvanceMarkers(new[]
            {
                Marker(0, 1200f, -800f)
            }, Dt);

            (float X, float Y)? p = vp.SmoothedMarkerPosition(0);
            await Assert.That(p).IsNotNull();
            await Assert.That(p!.Value.X).IsEqualTo(1200f);
            await Assert.That(p.Value.Y).IsEqualTo(-800f);
            await Assert.That(moving).IsFalse(); // nothing to animate on the seeding frame
        });

    [Test]
    public async Task SmallMove_GlidesPartway_ThenConverges() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DViewport vp = new();
            vp.AdvanceMarkers(new[]
            {
                Marker(0, 100f, 0f)
            }, Dt); // seed at A

            // One step toward B = (140,0): partway (between A and B), reported as moving — NOT snapped.
            bool moving = vp.AdvanceMarkers(new[]
            {
                Marker(0, 140f, 0f)
            }, Dt);
            (float X, float Y) p1 = vp.SmoothedMarkerPosition(0)!.Value;
            await Assert.That(moving).IsTrue();
            await Assert.That(p1.X).IsGreaterThan(100f);
            await Assert.That(p1.X).IsLessThan(140f);

            // Holding B, many render frames → converges onto B and settles.
            for (int i = 0; i < 240; i++)
            {
                vp.AdvanceMarkers(new[]
                {
                    Marker(0, 140f, 0f)
                }, Dt);
            }

            (float X, float Y) p2 = vp.SmoothedMarkerPosition(0)!.Value;
            await Assert.That(Math.Abs(p2.X - 140f)).IsLessThan(0.5f);
            await Assert.That(Math.Abs(p2.Y)).IsLessThan(0.5f);
        });

    [Test]
    public async Task TeleportJump_SnapsImmediately_NoStreakAcrossMap() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DViewport vp = new();
            vp.AdvanceMarkers(new[]
            {
                Marker(0, 0f, 0f)
            }, Dt); // seed at origin

            // A seek / round jump moves the sample thousands of units in one push → snap, do not glide.
            bool moving = vp.AdvanceMarkers(new[]
            {
                Marker(0, 4000f, -3000f)
            }, Dt);
            (float X, float Y) p = vp.SmoothedMarkerPosition(0)!.Value;
            await Assert.That(p.X).IsEqualTo(4000f);
            await Assert.That(p.Y).IsEqualTo(-3000f);
            await Assert.That(moving).IsFalse(); // an instant snap, not an animated glide
        });

    [Test]
    public async Task DepartedSlot_IsPruned_SoRejoinDoesNotGlideFromStaleSpot() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DViewport vp = new();
            vp.AdvanceMarkers(new[]
            {
                Marker(0, 500f, 500f), Marker(1, -500f, -500f)
            }, Dt);
            await Assert.That(vp.SmoothedMarkerPosition(1)).IsNotNull();

            // Slot 1 leaves (no longer emitted) → pruned.
            vp.AdvanceMarkers(new[]
            {
                Marker(0, 500f, 500f)
            }, Dt);
            await Assert.That(vp.SmoothedMarkerPosition(1)).IsNull();

            // A later re-join re-seeds (snaps to the new spot), never glides from the stale (-500,-500).
            vp.AdvanceMarkers(new[]
            {
                Marker(0, 500f, 500f), Marker(1, 2000f, 2000f)
            }, Dt);
            (float X, float Y) rejoined = vp.SmoothedMarkerPosition(1)!.Value;
            await Assert.That(rejoined.X).IsEqualTo(2000f);
            await Assert.That(rejoined.Y).IsEqualTo(2000f);
        });

    private static PlayerMarker Marker(int slot, float x, float y) =>
        new(slot, 2, x, y, 64f, 0f,
            RingState.Team, 1.0, "X", true);
}

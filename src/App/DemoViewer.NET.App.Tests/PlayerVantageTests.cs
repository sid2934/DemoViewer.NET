#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Exact-value unit tests for the pure vantage geometry (<see cref="PlayerVantage" />, <see cref="ViewFrustum" />)
///     — the angle/duck/anchor math where a convention slip (pitch↔yaw, deg↔rad, wrong lateral axis) hides.
///     No demo needed; these pin the conventions the kill-tick oracle later confirms on real data.
/// </summary>
public class PlayerVantageTests
{
    [Test]
    public async Task Forward_MatchesEngineAngleVectors()
    {
        await Close(PlayerVantage.Forward(0, 0), new Vector3(1, 0, 0)); // yaw 0 ⇒ +X
        await Close(PlayerVantage.Forward(0, 90), new Vector3(0, 1, 0)); // yaw 90 ⇒ +Y
        await Close(PlayerVantage.Forward(0, 180), new Vector3(-1, 0, 0)); // yaw 180 ⇒ −X
        await Close(PlayerVantage.Forward(0, -90), new Vector3(0, -1, 0)); // yaw −90 ⇒ −Y
        await Close(PlayerVantage.Forward(90, 0), new Vector3(0, 0, -1)); // pitch +90 ⇒ looking down (−Z)
        await Close(PlayerVantage.Forward(-90, 0), new Vector3(0, 0, 1)); // pitch −90 ⇒ looking up (+Z)
    }

    [Test]
    public async Task Eye_InterpolatesDuck()
    {
        await Close(PlayerVantage.Eye(new Vector3(10, 20, 0), 0f), new Vector3(10, 20, 64));
        await Close(PlayerVantage.Eye(new Vector3(10, 20, 0), 1f), new Vector3(10, 20, 46));
        await Close(PlayerVantage.Eye(new Vector3(10, 20, 0), 0.5f), new Vector3(10, 20, 55));
    }

    [Test]
    public async Task Frustum_YawPitchBounds()
    {
        ViewFrustum fr = new(new Vector3(0, 0, 0), new Vector3(1, 0, 0)); // ±53° yaw, ±37° pitch

        await Assert.That(fr.Contains(new Vector3(100, 0, 0))).IsTrue(); // dead ahead
        await Assert.That(fr.Contains(new Vector3(100, 100, 0))).IsTrue(); // yaw 45° < 53°
        await Assert.That(fr.Contains(new Vector3(100, 150, 0))).IsFalse(); // yaw 56° > 53°
        await Assert.That(fr.Contains(new Vector3(100, 0, 60))).IsTrue(); // pitch 31° < 37°
        await Assert.That(fr.Contains(new Vector3(100, 0, 90))).IsFalse(); // pitch 42° > 37°
        await Assert.That(fr.Contains(new Vector3(-100, 0, 0))).IsFalse(); // behind
    }

    [Test]
    public async Task Anchors_LateralPerpendicularToSightline_AndDuckScaled()
    {
        // Viewer to +X of the target ⇒ horizontal sightline is along X ⇒ shoulders offset along ±Y.
        Vector3[] a = new Vector3[PlayerVantage.MaxAnchors];
        int n = PlayerVantage.BuildAnchors(new Vector3(0, 0, 0), 0f, new Vector3(200, 0, 64), a);
        await Assert.That(n).IsEqualTo(6);

        // Centre anchors on the spine (x=y=0), standing heights.
        await Close(a[0], new Vector3(0, 0, 48)); // chest
        await Close(a[1], new Vector3(0, 0, 64)); // head
        // Shoulders at z=54, offset ±16 along Y (perpendicular to the X sightline), x stays 0.
        await Assert.That(MathF.Abs(a[4].Z - 54f) < 1e-3f).IsTrue();
        await Assert.That(MathF.Abs(a[4].X) < 1e-3f).IsTrue();
        await Assert.That(MathF.Abs(MathF.Abs(a[4].Y) - 16f) < 1e-3f).IsTrue();
        await Assert.That(MathF.Abs(a[5].Y + a[4].Y) < 1e-3f).IsTrue(); // opposite shoulders

        // Crouched target: heights compress toward 46/64.
        PlayerVantage.BuildAnchors(new Vector3(0, 0, 0), 1f, new Vector3(200, 0, 46), a);
        await Close(a[1], new Vector3(0, 0, 64f * (46f / 64f))); // head → 46
    }

    private static async Task Close(Vector3 actual, Vector3 expected, float tol = 1e-3f)
    {
        await Assert.That((actual - expected).Length() < tol)
            .IsTrue();
    }
}

#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure unit checks for <see cref="SmokeVolumes.SegmentBlocked" />: the segment-vs-sphere test behind
///     smoke vision occlusion. No assets: exercises the geometry directly (ray through a sphere, ray missing
///     it, endpoint inside, grazing, empty set, and the shared radius constant).
/// </summary>
public class SmokeVolumesTests
{
    private static ReadOnlySpan<Vector4> One(Vector3 centre, float r) => new[]
    {
        new Vector4(centre, r)
    };

    [Test]
    public async Task EmptySet_NeverBlocks()
    {
        bool blocked = SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), ReadOnlySpan<Vector4>.Empty);
        await Assert.That(blocked).IsFalse();
    }

    [Test]
    public async Task SegmentThroughSphereCentre_IsBlocked()
    {
        // A ray straight through a smoke centred on the path.
        Vector4[] smoke = new[]
        {
            new Vector4(500, 0, 0, 100)
        };
        bool blocked = SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), smoke);
        await Assert.That(blocked).IsTrue();
    }

    [Test]
    public async Task SegmentClearOfSphere_IsNotBlocked()
    {
        // Same smoke, but the ray runs 200u to the side (radius only 100u), misses.
        Vector4[] smoke = new[]
        {
            new Vector4(500, 200, 0, 100)
        };
        bool blocked = SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), smoke);
        await Assert.That(blocked).IsFalse();
    }

    [Test]
    public async Task EndpointInsideSphere_IsBlocked()
    {
        // Viewer standing INSIDE the smoke can't see out: the a-endpoint is within the radius.
        Vector4[] smoke = new[]
        {
            new Vector4(20, 0, 0, 100)
        };
        bool blocked = SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), smoke);
        await Assert.That(blocked).IsTrue();
    }

    [Test]
    public async Task SphereBehindViewer_IsNotBlocked()
    {
        // The nearest approach on the *segment* (clamped to [a,b]) is the a-endpoint; a smoke well behind the
        // viewer must not block a forward sightline.
        Vector4[] smoke = new[]
        {
            new Vector4(-500, 0, 0, 100)
        };
        bool blocked = SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), smoke);
        await Assert.That(blocked).IsFalse();
    }

    [Test]
    public async Task Grazing_JustInside_And_JustOutside()
    {
        // Perpendicular offset equal to radius−ε blocks; radius+ε clears. Guards the ≤ boundary + sign.
        Vector4[] near = new[]
        {
            new Vector4(500, 99f, 0, 100)
        };
        Vector4[] far = new[]
        {
            new Vector4(500, 101f, 0, 100)
        };
        await Assert.That(SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), near)).IsTrue();
        await Assert.That(SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), far)).IsFalse();
    }

    [Test]
    public async Task ThreeDimensional_ZOffset_Respected()
    {
        // The test is genuinely 3D: a smoke at the right XY but 300u below the horizontal sightline (radius
        // 144) must not block: a 2D-only test would wrongly report blocked.
        ReadOnlySpan<Vector4> below = One(new Vector3(500, 0, -300), SmokeVolumes.DefaultRadius);
        bool blocked = SmokeVolumes.SegmentBlocked(Vector3.Zero, new Vector3(1000, 0, 0), below);
        await Assert.That(blocked).IsFalse();
    }
}

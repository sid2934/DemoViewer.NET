#region

using System.Numerics;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using CS2DemoKit.Analysis.Visibility;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>Phase-1 automatic validation</b> of the VRF-free <see cref="VisibilityEngine" /> (BVH +
///     Möller-Trumbore) against a dependency-free oracle — a brute-force scan over the same triangle soup.
///     If the accelerated engine agrees with the naive baseline on thousands of REAL rays (player eyes,
///     bodies, feet on dust2) the primitive is correct; a BVH-traversal or slab-test bug would surface as a
///     disagreement. Plus map-independent geometric invariants (through-floor occlusion, symmetry) that hold
///     regardless of oracle. awpy's <c>.tri</c> + VisibilityChecker is the noted heavier second check
///     (Python + CDN) — deferred; this suite is the gate. Skips without the dust2 demo + baked collision.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class VisibilityEngineTests
{
    private const float EyeStanding = 64f;

    private static async Task<Fixture?> BuildFixtureAsync()
    {
        string? trisPath = FindBaked("de_dust2", "collision.tris");
        string? demoPath = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem");
        if (trisPath is null || demoPath is null)
        {
            return null;
        }

        CollisionTris.Data d = CollisionTris.Load(trisPath);
        VisibilityEngine engine = VisibilityEngine.FromTriangles(d.Vertices, d.TriangleCount);

        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        List<Vector3> feet = new();
        EntityTracker tracker = new();
        int start = Math.Min(frames.Count / 4, Math.Max(0, frames.Count - 1));
        int end = Math.Min(frames.Count - 1, start + 12000);
        tracker.AdvanceToIndex(start, frames);
        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            if ((i - start) % 400 != 0)
            {
                continue;
            }

            PawnLookup.ForEachLivePawn(tracker, (_, pawn) =>
            {
                if (PositionUtil.CellToWorld(pawn) is { } p)
                {
                    feet.Add(new Vector3(p.X, p.Y, p.Z));
                }
            });
        }

        return feet.Count == 0 ? null : new Fixture(engine, d.Vertices, d.TriangleCount, feet);
    }

    /// <summary>Ray-down: engine.RayDownDistance must match a brute-force nearest-below on every player feet sample.</summary>
    [Test]
    public async Task RayDown_Engine_EqualsBruteForce()
    {
        Fixture? fx = await BuildFixtureAsync();
        if (fx is null)
        {
            throw new SkipTestException("no dust2 demo + baked collision");
        }

        int compared = 0, mismatches = 0;
        foreach (Vector3 f in fx.Feet)
        {
            Vector3 o = new(f.X, f.Y, f.Z + EyeStanding);
            bool eHit = fx.Engine.RayDownDistance(o, 100_000f, out float eT);
            bool bHit = BruteRayDown(fx.V, fx.TriCount, o, out float bT);
            compared++;
            if (eHit != bHit || eHit && Math.Abs(eT - bT) > 0.05f)
            {
                mismatches++;
            }
        }

        Console.WriteLine($"[vengine] ray-down compared={compared} mismatches={mismatches}");
        await Assert.That(compared).IsGreaterThan(0);
        await Assert.That(mismatches).IsEqualTo(0);
    }

    /// <summary>
    ///     Segment occlusion: engine.IsVisible must match a brute-force any-hit on a large set of real rays —
    ///     eyes → other players' bodies, and eyes → nearby free/through-geometry probe points. This is the
    ///     master correctness proof for the BVH traversal.
    /// </summary>
    [Test]
    public async Task Segment_Engine_EqualsBruteForce()
    {
        Fixture? fx = await BuildFixtureAsync();
        if (fx is null)
        {
            throw new SkipTestException("no dust2 demo + baked collision");
        }

        // Build a diverse pair set (bounded so the brute-force oracle stays fast in Debug).
        List<(Vector3 A, Vector3 B)> pairs = new();
        List<Vector3> feet = fx.Feet;
        int n = feet.Count;
        for (int i = 0; i < n && pairs.Count < 500; i++)
        {
            Vector3 eyeA = feet[i] + new Vector3(0, 0, EyeStanding);
            // eye → a handful of other players' chest anchors (mix of visible + occluded across the map).
            for (int j = 0; j < n && pairs.Count < 500; j += Math.Max(1, n / 12))
            {
                if (j == i)
                {
                    continue;
                }

                pairs.Add((eyeA, feet[j] + new Vector3(0, 0, 50f)));
            }

            // eye → straight down to own floor (must be occluded) and slightly up (typically clear):
            // keeps both truth-values represented so the equivalence isn't trivially all-visible.
            pairs.Add((eyeA, feet[i] + new Vector3(0, 0, -40f)));
            pairs.Add((eyeA, eyeA + new Vector3(0, 0, 3f)));
        }

        int compared = 0, mismatches = 0, visible = 0;
        foreach ((Vector3 a, Vector3 b) in pairs)
        {
            bool e = fx.Engine.IsVisible(a, b);
            bool br = BruteSegmentClear(fx.V, fx.TriCount, a, b);
            compared++;
            if (e)
            {
                visible++;
            }

            if (e != br)
            {
                mismatches++;
            }
        }

        Console.WriteLine($"[vengine] segment compared={compared} mismatches={mismatches} visibleFrac={(double)visible / compared:P0}");
        await Assert.That(compared).IsGreaterThan(100);
        await Assert.That(mismatches).IsEqualTo(0);
        // Guard against a degenerate all-visible/all-blocked set that would make equivalence meaningless.
        await Assert.That(visible).IsGreaterThan(0);
        await Assert.That(visible).IsLessThan(compared);
    }

    /// <summary>
    ///     Map-independent geometric invariants (hold regardless of the oracle): a solid floor occludes a
    ///     sightline that passes through it; visibility is symmetric.
    /// </summary>
    [Test]
    public async Task Geometric_Invariants_ThroughFloor_And_Symmetry()
    {
        Fixture? fx = await BuildFixtureAsync();
        if (fx is null)
        {
            throw new SkipTestException("no dust2 demo + baked collision");
        }

        int throughFloorOccluded = 0, throughFloorTotal = 0;
        int symmetric = 0, symTotal = 0;
        List<Vector3> feet = fx.Feet;

        for (int i = 0; i < feet.Count; i++)
        {
            // Eye above the floor → a point well BELOW the standing floor: the floor lies between ⇒ occluded.
            Vector3 eye = feet[i] + new Vector3(0, 0, EyeStanding);
            Vector3 underground = feet[i] + new Vector3(0, 0, -300f);
            throughFloorTotal++;
            if (!fx.Engine.IsVisible(eye, underground))
            {
                throughFloorOccluded++;
            }

            // Symmetry over a spread of partners.
            for (int j = i + 1; j < feet.Count; j += Math.Max(1, feet.Count / 8))
            {
                Vector3 a = feet[i] + new Vector3(0, 0, 55f);
                Vector3 b = feet[j] + new Vector3(0, 0, 55f);
                symTotal++;
                if (fx.Engine.IsVisible(a, b) == fx.Engine.IsVisible(b, a))
                {
                    symmetric++;
                }
            }
        }

        Console.WriteLine($"[vengine] through-floor occluded {throughFloorOccluded}/{throughFloorTotal}  " +
                          $"symmetric {symmetric}/{symTotal}");
        await Assert.That(throughFloorTotal).IsGreaterThan(0);
        // Feet sit on solid floor (Phase-G gate), so a sightline into the ground is always blocked.
        await Assert.That((double)throughFloorOccluded / throughFloorTotal).IsGreaterThan(0.98);
        await Assert.That(symmetric).IsEqualTo(symTotal); // segment occlusion is direction-independent
    }

    // ── Brute-force oracles over the raw triangle soup (mirror the engine's eps semantics) ──

    private static bool BruteRayDown(float[] v, int count, Vector3 o, out float distance)
    {
        distance = float.MaxValue;
        bool hit = false;
        Vector3 dir = new(0, 0, -1);
        for (int tri = 0; tri < count; tri++)
        {
            if (RayTriangle(v, tri, o, dir, out float t) && t > 1e-3f && t < distance)
            {
                distance = t;
                hit = true;
            }
        }

        return hit;
    }

    private static bool BruteSegmentClear(float[] v, int count, Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        float len = d.Length();
        if (len <= 0.2f)
        {
            return true;
        }

        Vector3 dir = d / len;
        float lo = 0.1f, hi = len - 0.1f;
        for (int tri = 0; tri < count; tri++)
        {
            if (RayTriangle(v, tri, a, dir, out float t) && t > lo && t < hi)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RayTriangle(float[] v, int tri, Vector3 o, Vector3 d, out float t)
    {
        t = 0;
        int b = tri * 9;
        Vector3 a = new(v[b], v[b + 1], v[b + 2]);
        Vector3 e1 = new(v[b + 3] - a.X, v[b + 4] - a.Y, v[b + 5] - a.Z);
        Vector3 e2 = new(v[b + 6] - a.X, v[b + 7] - a.Y, v[b + 8] - a.Z);
        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (det is > -1e-8f and < 1e-8f)
        {
            return false;
        }

        float f = 1f / det;
        Vector3 s = o - a;
        float u = f * Vector3.Dot(s, p);
        if (u is < 0f or > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(s, e1);
        float vv = f * Vector3.Dot(d, q);
        if (vv < 0f || u + vv > 1f)
        {
            return false;
        }

        t = f * Vector3.Dot(e2, q);
        return true;
    }

    private static string? FindBaked(string mapName, string file)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "cs2-assets", "baked", mapName, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private sealed record Fixture(VisibilityEngine Engine, float[] V, int TriCount, List<Vector3> Feet);
}

#region

using System.Numerics;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>Phase-G coordinate-frame gate</b> for 3D visibility. The blocking
///     question before any raycast means anything: are the baker's collision triangles (from VRF
///     <c>PhysAggregateData</c>) in the SAME world frame as reconstructed player positions
///     (<see cref="PositionUtil.CellToWorld" />)? A silent origin/scale/Z-datum mismatch yields
///     plausible-but-wrong visibility, so we prove alignment empirically: cast a ray straight DOWN from a
///     scatter of living-player feet and confirm the nearest collision triangle sits ~0u below (feet rest on
///     the floor). Float hundreds of units / no hit ⇒ frame mismatch. This is the visibility analogue of the
///     player-Z histogram from the floor work. Skips if the dust2 demo or baked collision is absent.
///     <para>
///         The ray-down caster here is a self-contained brute-force primitive (XY-bbox prune +
///         Möller-Trumbore) — intentionally throwaway for the gate; Phase 1 promotes a BVH-backed engine.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class VisibilityFrameGateProbe
{
    // Start the down-ray this far ABOVE the reconstructed feet so a floor coplanar with the feet isn't
    // skipped by the t>epsilon guard; the nearest hit should then land at t ≈ StartHeight (gap ≈ 0).
    private const float StartHeight = 64f;

    // Substrings we look for when probing whether the eye offset / eye angles / duck state are dict-readable.
    private static readonly string[] _eyeFieldNeedles =
    {
        "ViewOffset", "vecView", "EyeAngle", "angEye", "m_flDuck", "bDuck"
    };

    [Test]
    public async Task Dust2_PlayerFeet_SitOnCollisionFloor()
    {
        string? trisPath = FindBaked("de_dust2", "collision.tris");
        if (trisPath is null)
        {
            throw new SkipTestException("no baked dust2 collision.tris (run the AssetBaker)");
        }

        string? demoPath = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem");
        if (demoPath is null)
        {
            throw new SkipTestException("no dust2 demo present");
        }

        TriMesh mesh = TriMesh.Load(trisPath);
        Console.WriteLine($"[vgate] collision tris={mesh.Count:N0}  " +
                          $"AABB X[{mesh.MinX:F0}..{mesh.MaxX:F0}] Y[{mesh.MinY:F0}..{mesh.MaxY:F0}] Z[{mesh.MinZ:F0}..{mesh.MaxZ:F0}]");

        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        Console.WriteLine($"[vgate] {Path.GetFileName(demoPath)} map={demo.MapName} frames={frames.Count}");

        // Sample a scatter of living-player feet across a mid-match window (bounded; brute-force caster).
        List<Vector3> samples = new();
        EntityTracker tracker = new();
        int start = Math.Min(frames.Count / 4, Math.Max(0, frames.Count - 1));
        int end = Math.Min(frames.Count - 1, start + 15000);
        tracker.AdvanceToIndex(start, frames);
        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            if ((i - start) % 250 != 0)
            {
                continue;
            }

            PawnLookup.ForEachLivePawn(tracker, (_, pawn) =>
            {
                if (PositionUtil.CellToWorld(pawn) is { } p)
                {
                    samples.Add(new Vector3(p.X, p.Y, p.Z));
                }
            });
        }

        if (samples.Count == 0)
        {
            throw new SkipTestException("no player positions reconstructed");
        }

        // Ray straight down from feet+StartHeight; gap = (hit distance) − StartHeight = feet-above-floor.
        List<double> gaps = new(samples.Count);
        int noHit = 0;
        foreach (Vector3 s in samples)
        {
            Vector3 origin = new(s.X, s.Y, s.Z + StartHeight);
            if (mesh.RayDownDistance(origin, out float t))
            {
                gaps.Add(t - StartHeight);
            }
            else
            {
                noHit++;
            }
        }

        gaps.Sort();
        double median = gaps.Count > 0 ? gaps[gaps.Count / 2] : double.NaN;
        double hitRate = (double)gaps.Count / samples.Count;
        int within8 = gaps.Count(g => Math.Abs(g) <= 8);
        int within32 = gaps.Count(g => Math.Abs(g) <= 32);
        double p10 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.10)] : double.NaN;
        double p90 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.90)] : double.NaN;

        Console.WriteLine($"[vgate] samples={samples.Count} hits={gaps.Count} ({hitRate:P0}) noHit={noHit}");
        Console.WriteLine($"[vgate] feet-above-floor gap: median={median:F1}u  p10={p10:F1}  p90={p90:F1}  " +
                          $"within±8u={within8} ({(double)within8 / gaps.Count:P0})  within±32u={within32} ({(double)within32 / gaps.Count:P0})");

        // ── The gate ──
        // 1. Almost every feet-ray finds floor below (a frame mismatch would miss the geometry entirely).
        await Assert.That(hitRate).IsGreaterThan(0.90);
        // 2. Feet rest on the floor: the median gap is a few units, NOT hundreds. This is the decisive
        //    alignment check — a datum/scale mismatch pushes the median far from 0.
        await Assert.That(Math.Abs(median)).IsLessThan(16.0);
        // 3. The bulk of players sit within a normal standing tolerance of their floor.
        await Assert.That((double)within32 / gaps.Count).IsGreaterThan(0.75);
    }

    /// <summary>
    ///     Phase-G eye-height probe: is <c>m_vecViewOffset</c> (or an eye-angle leaf) actually readable off a
    ///     real pawn's field dict? If yes, we use the real eye Z; if no, the attacker eye is approximated
    ///     (+64 standing / ~46 crouched). Pure diagnostic — never fails; dumps what it finds.
    /// </summary>
    [Test]
    public async Task Probe_EyeOffset_And_EyeAngles_Fields()
    {
        string? demoPath = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem")
                           ?? DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem");
        if (demoPath is null)
        {
            throw new SkipTestException("no demo present");
        }

        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        EntityTracker tracker = new();
        int start = Math.Min(frames.Count / 3, Math.Max(0, frames.Count - 1));
        tracker.AdvanceToIndex(start, frames);
        // Advance a little so pawns are fully populated.
        for (int i = start + 1; i <= Math.Min(frames.Count - 1, start + 200); i++)
        {
            tracker.AdvanceOneFrame(frames[i]);
        }

        bool dumped = false;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (dumped)
            {
                return;
            }

            dumped = true;
            Console.WriteLine($"[eyeprobe] pawn slot={slot} className={pawn.ClassName} fields={pawn.Fields.Count}");
            foreach (string needle in _eyeFieldNeedles)
            {
                foreach ((string key, object? val) in pawn.Fields)
                {
                    if (key.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[eyeprobe]   {key} = {val} ({val?.GetType().Name ?? "null"})");
                    }
                }
            }
        });

        await Assert.That(dumped).IsTrue();
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
}

/// <summary>
///     Throwaway brute-force collision triangle soup for the Phase-G gate: loads a baker <c>collision.tris</c>
///     blob and answers "nearest triangle straight down from here". XY-bbox prune keeps 435k triangles
///     tractable for a few hundred sample points; a BVH-backed engine replaces this in Phase 1.
/// </summary>
internal sealed class TriMesh
{
    private readonly float[] _v; // 9 floats per triangle: ax,ay,az, bx,by,bz, cx,cy,cz

    private TriMesh(float[] v, int count)
    {
        _v = v;
        Count = count;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < count * 9; i += 3)
        {
            minX = Math.Min(minX, v[i]);
            maxX = Math.Max(maxX, v[i]);
            minY = Math.Min(minY, v[i + 1]);
            maxY = Math.Max(maxY, v[i + 1]);
            minZ = Math.Min(minZ, v[i + 2]);
            maxZ = Math.Max(maxZ, v[i + 2]);
        }

        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
    }

    public int Count { get; }
    public float MinX { get; }
    public float MinY { get; }
    public float MinZ { get; }
    public float MaxX { get; }
    public float MaxY { get; }
    public float MaxZ { get; }

    public static TriMesh Load(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        using BinaryReader r = new(fs);
        uint magic = r.ReadUInt32();
        if (magic != 0x49525443)
        {
            throw new InvalidDataException($"{path}: bad magic 0x{magic:X8}");
        }

        _ = r.ReadInt32(); // format version
        int count = r.ReadInt32();
        float[] v = new float[count * 9];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = r.ReadSingle();
        }

        return new TriMesh(v, count);
    }

    /// <summary>
    ///     Distance to the nearest triangle straight below <paramref name="origin" /> (ray dir (0,0,−1)).
    ///     Returns the smallest positive hit distance, or false if nothing is below.
    /// </summary>
    public bool RayDownDistance(Vector3 origin, out float distance)
    {
        distance = float.MaxValue;
        bool hit = false;
        float px = origin.X, py = origin.Y;
        float[] v = _v;

        for (int i = 0; i < v.Length; i += 9)
        {
            float ax = v[i], ay = v[i + 1], az = v[i + 2];
            float bx = v[i + 3], by = v[i + 4], bz = v[i + 5];
            float cx = v[i + 6], cy = v[i + 7], cz = v[i + 8];

            // XY-bbox prune: a downward ray can only cross triangles whose XY footprint spans (px,py).
            if (px < MinF(ax, bx, cx) || px > MaxF(ax, bx, cx) ||
                py < MinF(ay, by, cy) || py > MaxF(ay, by, cy))
            {
                continue;
            }

            if (RayDownTriangle(px, py, origin.Z, ax, ay, az, bx, by, bz, cx, cy, cz, out float t) &&
                t > 1e-3f && t < distance)
            {
                distance = t;
                hit = true;
            }
        }

        return hit;
    }

    // Möller-Trumbore specialized for a downward ray D=(0,0,−1) from (px,py,oz). t is the downward distance.
    private static bool RayDownTriangle(
        float px, float py, float oz,
        float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz,
        out float t)
    {
        t = 0;
        // edge1 = B−A, edge2 = C−A
        float e1X = bx - ax, e1Y = by - ay, e1Z = bz - az;
        float e2X = cx - ax, e2Y = cy - ay, e2Z = cz - az;
        // h = D × edge2, with D = (0,0,−1): h = (e2y, −e2x, 0)
        float hx = e2Y, hy = -e2X, hz = 0f;
        float a = e1X * hx + e1Y * hy + e1Z * hz; // det = edge1·h
        if (a is > -1e-8f and < 1e-8f)
        {
            return false; // ray parallel to triangle
        }

        float f = 1f / a;
        float sx = px - ax, sy = py - ay, sz = oz - az;
        float u = f * (sx * hx + sy * hy + sz * hz);
        if (u is < 0f or > 1f)
        {
            return false;
        }

        // q = S × edge1
        float qx = sy * e1Z - sz * e1Y;
        float qy = sz * e1X - sx * e1Z;
        float qz = sx * e1Y - sy * e1X;
        // v = f * (D · q), D=(0,0,−1) ⇒ −qz
        float vv = f * -qz;
        if (vv < 0f || u + vv > 1f)
        {
            return false;
        }

        // t = f * (edge2 · q)
        t = f * (e2X * qx + e2Y * qy + e2Z * qz);
        return t > 0f;
    }

    private static float MinF(float a, float b, float c) => Math.Min(a, Math.Min(b, c));
    private static float MaxF(float a, float b, float c) => Math.Max(a, Math.Max(b, c));
}

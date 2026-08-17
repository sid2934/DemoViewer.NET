#region

using System.Numerics;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;
using PhysMesh = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Mesh;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Extracts the map's <b>world collision</b> triangle soup from <c>maps/&lt;map&gt;/world_physics.vmdl_c</c>
///     (the Rubikon <see cref="PhysAggregateData" /> embedded in the physics model) via VRF — the geometry the
///     app raycasts for 3D line-of-sight ("time enemy was visible"). The baker owns VRF; the app consumes the
///     baked <c>collision.tris</c> blob VRF-free. See <c>docs/3d-visibility/3d-visibility-plan.md</c>.
///     <para>
///         <b>Coordinate frame (empirically verified, dust2):</b> the physics part carries an EMPTY
///         <see cref="PhysAggregateData.BindPose" />, so vertices are already in <b>world space</b> — no
///         transform is applied. The Phase-G ray-down gate (players' feet must sit on these triangles)
///         is the authority on this; if a map ever ships a non-identity bind pose we must apply it.
///     </para>
///     <para>
///         <b>Solidity:</b> for now ALL shapes (hulls + meshes) are emitted. Non-bullet-blocking volumes
///         (player-clips, triggers, ladders) would over-occlude sight — filtering by collision attribute /
///         interaction layer is a Phase-1 correctness refinement, deliberately deferred. Over-inclusion only
///         makes the floor gate MORE likely to hit, so it doesn't mask a frame mismatch.
///     </para>
/// </summary>
public static class CollisionMesh
{
    // .tris binary: [magic "CTRI"][int32 version][int32 triCount] then triCount × 9 × float32 (v0,v1,v2).
    private const uint Magic = 0x49525443; // "CTRI" little-endian
    private const int FormatVersion = 1;

    /// <summary>Reads world_physics.vmdl_c out of the per-map vpk, triangulates, and writes <paramref name="outPath" />.</summary>
    public static Result Extract(string vpkPath, string mapName, string outPath)
    {
        using Package package = new();
        package.Read(vpkPath);
        string entryPath = $"maps/{mapName}/world_physics.vmdl_c";
        PackageEntry entry = package.FindEntry(entryPath)
                             ?? throw new FileNotFoundException($"{entryPath} not found in {vpkPath}");
        package.ReadEntry(entry, out byte[] bytes);

        using Resource res = new();
        res.Read(new MemoryStream(bytes), false);

        PhysAggregateData phys = res.DataBlock switch
        {
            Model model => model.GetEmbeddedPhys()
                           ?? throw new InvalidDataException($"{entryPath}: model has no embedded phys"),
            PhysAggregateData p => p,
            _ => throw new InvalidDataException($"{entryPath}: unexpected data block {res.DataBlock?.GetType().Name}")
        };

        if (phys.BindPose is { Length: > 0 })
        {
            // Not seen on dust2; surface it loudly rather than silently mis-place geometry.
            throw new NotSupportedException(
                $"{mapName}: physics BindPose has {phys.BindPose.Length} transform(s) — world-space assumption " +
                "is invalid for this map; per-part transform must be applied before shipping.");
        }

        List<(Vector3 A, Vector3 B, Vector3 C)> tris = new();
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        int hullCount = 0, meshCount = 0;

        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            tris.Add((a, b, c));
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }

        foreach (Part part in phys.Parts)
        {
            Shape shape = part.Shape;

            foreach (MeshDescriptor md in shape.Meshes ?? Array.Empty<MeshDescriptor>())
            {
                meshCount++;
                ReadOnlySpan<Vector3> verts = md.Shape.GetVertices();
                ReadOnlySpan<PhysMesh.Triangle> mtris = md.Shape.GetTriangles();
                foreach (PhysMesh.Triangle t in mtris)
                {
                    Add(verts[t.X], verts[t.Y], verts[t.Z]);
                }
            }

            foreach (HullDescriptor hd in shape.Hulls ?? Array.Empty<HullDescriptor>())
            {
                hullCount++;
                ReadOnlySpan<Vector3> vp = hd.Shape.GetVertexPositions();
                ReadOnlySpan<Hull.Face> faces = hd.Shape.GetFaces();
                ReadOnlySpan<Hull.HalfEdge> edges = hd.Shape.GetEdges();

                // Each convex face is a half-edge loop; fan-triangulate it (v0, vi, vi+1).
                foreach (Hull.Face face in faces)
                {
                    int start = face.Edge;
                    int e0 = edges[start].Origin;
                    int ePrev = edges[edges[start].Next].Origin;
                    int e = edges[edges[start].Next].Next;
                    int guard = 0;
                    while (e != start && guard++ < 256)
                    {
                        int cur = edges[e].Origin;
                        Add(vp[e0], vp[ePrev], vp[cur]);
                        ePrev = cur;
                        e = edges[e].Next;
                    }
                }
            }
        }

        WriteTris(outPath, tris);
        long len = new FileInfo(outPath).Length;
        string diag = $"  collision: parts={phys.Parts.Length} hulls={hullCount} meshes={meshCount} " +
                      $"tris={tris.Count:N0}  Z[{min.Z:F0}..{max.Z:F0}]  {len / 1024.0 / 1024.0:F1} MiB";
        return new Result(tris.Count, min, max, len, diag);
    }

    private static void WriteTris(string path, List<(Vector3 A, Vector3 B, Vector3 C)> tris)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter w = new(fs);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(tris.Count);
        foreach ((Vector3 a, Vector3 b, Vector3 c) in tris)
        {
            w.Write(a.X);
            w.Write(a.Y);
            w.Write(a.Z);
            w.Write(b.X);
            w.Write(b.Y);
            w.Write(b.Z);
            w.Write(c.X);
            w.Write(c.Y);
            w.Write(c.Z);
        }
    }

    public sealed record Result(int TriangleCount, Vector3 Min, Vector3 Max, long ByteLength, string Diagnostic);
}

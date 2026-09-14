#region

using System.IO.Compression;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Finds and loads a map's collision bake, compressed or not. The shipped asset pack carries
///     <c>collision.tris.gz</c>; a dev checkout or an out-of-band pack may carry the plain
///     <c>collision.tris</c>, and both resolve here.
///     <para>
///         <b>Why the app owns this at all.</b> <see cref="CollisionAssetLocator" /> is the engine's
///         locator and the authority on WHERE a bake lives, but it only knows the uncompressed name.
///         So <see cref="Find" /> asks it first and only probes for the compressed name when it comes
///         back empty. The consequence of that order is worth stating: if the engine ever gains a new
///         probe location, a plain bake there still resolves and only a compressed one would be
///         missed. The reverse order would break the plain case, which is the one every existing
///         deployment uses.
///     </para>
///     <para>
///         <b>Decompression is in memory, never to disk.</b> That is the whole point: the pack is
///         65 MiB rather than 319 MiB and it STAYS that size on the user's machine. Measured on the
///         ten shipped maps, inflating costs 17 to 23 percent of a map's total bake (read plus BVH
///         build), which is 2.9 ms on de_mirage and 64.7 ms on de_inferno, and it is paid once per
///         map per process because <c>VisibilityEngineCache</c> holds the built engine.
///         Expanding to a cache directory instead would buy those milliseconds back and hand the
///         253 MiB straight back with them.
///     </para>
/// </summary>
public static class CollisionSoup
{
    /// <summary>Suffix marking a gzip-compressed bake.</summary>
    public const string CompressedSuffix = ".gz";

    private const string BakeFileName = "collision.tris";

    /// <summary>
    ///     Absolute path of the map's bake, compressed or plain, or null when it has none. Never
    ///     throws, because every caller degrades rather than fails: the board hides a column, the
    ///     overlay draws no cones.
    /// </summary>
    /// <param name="mapName">Map to resolve, e.g. <c>de_mirage</c>.</param>
    public static string? Find(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        string? plain = CollisionAssetLocator.FindCollisionTris(mapName);
        if (plain is not null)
        {
            return plain;
        }

        try
        {
            return ProbeCompressed(mapName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The bake every surface must agree on for a map: this resolver's answer when it has one,
    ///     and only otherwise whatever path a loaded bundle named.
    ///     <para>
    ///         Stats and the analysis run reach a bake through <see cref="Find" />, which honours the
    ///         <c>CS2DEMOKIT_COLLISION_DIR</c> override. The Playback2D overlay reaches it through its
    ///         map bundle, and <see cref="MapAssetBundleReader" /> has no override branch at all, so
    ///         with that variable set the two named different files: two engines built for one map,
    ///         both of <c>VisibilityEngineCache</c>'s slots spent on it, and — if the override points
    ///         at different geometry — a Stats board and a vision overlay disagreeing about what can
    ///         be seen. Nothing in the repo sets the variable, which is exactly why no test caught it.
    ///         Routing the overlay through here is what makes "one map, one engine" a property of the
    ///         code rather than of the default configuration.
    ///     </para>
    /// </summary>
    /// <param name="mapName">Map to resolve, e.g. <c>de_mirage</c>.</param>
    /// <param name="bundlePath">The bake a loaded bundle names, or null when no bundle is loaded.</param>
    public static string? Resolve(string? mapName, string? bundlePath)
    {
        return Find(mapName) ?? bundlePath;
    }

    /// <summary>
    ///     Builds the visibility engine from a bake, inflating it on the way through when the path
    ///     names a compressed one. Drop-in for <see cref="VisibilityEngine.Load" />.
    /// </summary>
    /// <param name="path">Path returned by <see cref="Find" />.</param>
    public static VisibilityEngine Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!path.EndsWith(CompressedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return VisibilityEngine.Load(path);
        }

        // The same two steps VisibilityEngine.Load takes, with a GZipStream in front of the reader.
        // CollisionTris.Load reads the stream sequentially, so it never needs the inflated bytes on
        // disk or a seekable copy of them in memory.
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using GZipStream inflate = new(file, CompressionMode.Decompress);
        CollisionTris.Data data = CollisionTris.Load(inflate);
        return VisibilityEngine.FromTriangles(data.Vertices, data.TriangleCount);
    }

    /// <summary>Mirrors the engine locator's probe order, looking for the compressed name.</summary>
    private static string? ProbeCompressed(string mapName)
    {
        string bake = BakeFileName + CompressedSuffix;

        string? envDir = Environment.GetEnvironmentVariable(CollisionAssetLocator.EnvVar);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            string flat = Path.Combine(envDir, mapName + ".tris" + CompressedSuffix);
            if (File.Exists(flat))
            {
                return flat;
            }

            string nested = Path.Combine(envDir, mapName, bake);
            if (File.Exists(nested))
            {
                return nested;
            }
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string shipped = Path.Combine(dir.FullName, "assets", mapName, bake);
            if (File.Exists(shipped))
            {
                return shipped;
            }

            string devCache = Path.Combine(dir.FullName, "cs2-assets", "baked", mapName, bake);
            if (File.Exists(devCache))
            {
                return devCache;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

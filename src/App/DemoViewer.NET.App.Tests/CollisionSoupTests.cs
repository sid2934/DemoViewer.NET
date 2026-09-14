#region

using System.IO.Compression;
using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shipped bakes are gzipped, and <see cref="CollisionSoup" /> is what still finds and loads
///     them. Uses de_mirage, the smallest shipped soup at 0.8 MiB compressed, so this stays in the
///     in-flight tier rather than behind a category.
/// </summary>
public class CollisionSoupTests
{
    private const string Map = "de_mirage";

    /// <summary>
    ///     The pack ships only the compressed bake, so the engine's own locator finds nothing and the
    ///     app's resolver is load-bearing rather than a convenience. Asserting both halves is the
    ///     point: if a plain bake ever creeps back into <c>assets/</c> the first assertion fails and
    ///     says so, instead of this test passing for the wrong reason.
    /// </summary>
    [Test]
    public async Task ShippedBake_IsCompressed_AndTheEngineLocatorAloneCannotFindIt()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        await Assert.That(File.Exists(Path.Combine(root, "assets", Map, "collision.tris"))).IsFalse()
            .Because("the pack ships collision.tris.gz; an uncompressed one would defeat the point");

        string? found = CollisionSoup.Find(Map);
        await Assert.That(found).IsNotNull().Because("the app resolver must find the compressed bake");
        await Assert.That(found!.EndsWith(CollisionSoup.CompressedSuffix, StringComparison.Ordinal)).IsTrue();
        await Assert.That(File.Exists(found)).IsTrue();
    }

    /// <summary>
    ///     Inflating in memory gives the same engine as loading the identical soup from disk: same
    ///     triangle count, and the same answer to every sampled sightline. A container change that
    ///     altered one ray would be a silent wrong-answer bug, not a crash, so the rays are the
    ///     assertion that matters.
    /// </summary>
    [Test]
    public async Task CompressedLoad_MatchesTheSameSoupUncompressed()
    {
        string? gz = CollisionSoup.Find(Map);
        if (gz is null || !gz.EndsWith(CollisionSoup.CompressedSuffix, StringComparison.Ordinal))
        {
            throw new SkipTestException($"no compressed {Map} bake to compare");
        }

        string plain = Path.Combine(Path.GetTempPath(), $"soup-{Guid.NewGuid():N}.tris");
        try
        {
            using (FileStream src = File.OpenRead(gz))
            using (GZipStream inflate = new(src, CompressionMode.Decompress))
            using (FileStream dst = File.Create(plain))
            {
                await inflate.CopyToAsync(dst);
            }

            VisibilityEngine fromGz = CollisionSoup.Load(gz);
            VisibilityEngine fromPlain = CollisionSoup.Load(plain);

            await Assert.That(fromGz.TriangleCount).IsEqualTo(fromPlain.TriangleCount);
            await Assert.That(fromGz.Min).IsEqualTo(fromPlain.Min);
            await Assert.That(fromGz.Max).IsEqualTo(fromPlain.Max);

            // A deterministic spread of sightlines across the map's own bounds, so the comparison
            // covers real geometry rather than one lucky ray.
            Vector3 lo = fromPlain.Min, hi = fromPlain.Max;
            int compared = 0, agreed = 0;
            for (int i = 0; i < 12; i++)
            {
                for (int j = 0; j < 12; j++)
                {
                    Vector3 a = new(Lerp(lo.X, hi.X, i / 11f), Lerp(lo.Y, hi.Y, j / 11f), Lerp(lo.Z, hi.Z, 0.5f));
                    Vector3 b = new(Lerp(lo.X, hi.X, j / 11f), Lerp(lo.Y, hi.Y, i / 11f), Lerp(lo.Z, hi.Z, 0.6f));
                    compared++;
                    if (fromGz.IsVisible(a, b) == fromPlain.IsVisible(a, b))
                    {
                        agreed++;
                    }
                }
            }

            await Assert.That(compared).IsGreaterThan(0);
            await Assert.That(agreed).IsEqualTo(compared)
                .Because("inflating changes the container, never a single triangle");
        }
        finally
        {
            File.Delete(plain);
        }
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}

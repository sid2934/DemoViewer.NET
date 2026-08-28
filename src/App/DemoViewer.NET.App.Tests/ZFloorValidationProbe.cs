#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Engine-fidelity cross-check for the nav-derived floor boundary: does the baker's
///     Nuke split (nav-Z valley ≈ −528; the map's radar split = −495) actually sit in a VALLEY of the
///     REAL observed player-Z, and do players occupy BOTH bands? Reconstructs player-Z exactly as the app
///     does (<see cref="PawnLookup.ForEachLivePawn" /> → <see cref="PositionUtil.CellToWorld" />) so any
///     nav-surface-vs-player-feet datum offset shows up. Diagnostic — prints a histogram; skips if no Nuke
///     demo is present.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ZFloorValidationProbe
{
    private const double BucketWidth = 32;

    [Test]
    public async Task NukePlayerZ_Distribution_VsNavBoundary()
    {
        string? path = DemoTestHelper.FindDemoPath("003816306022075596881_1029495947.dem")
                       ?? DemoTestHelper.FindDemoPath("match730_003826256877184877003_0981591541_410.dem");
        if (path is null)
        {
            throw new SkipTestException("no Nuke demo present");
        }

        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        Console.WriteLine($"[zprobe] {Path.GetFileName(path)} map={demo.MapName} frames={frames.Count}");

        SortedDictionary<int, long> hist = new();
        long total = 0;
        EntityTracker tracker = new();

        // Window a representative mid-match slice (bounded so a Debug build stays fast). Seek to the start,
        // then walk sequentially (the tracker needs every frame delta).
        int start = Math.Min(frames.Count / 4, Math.Max(0, frames.Count - 1));
        int end = Math.Min(frames.Count - 1, start + 15000);
        tracker.ReplayToIndex(start, frames);
        Console.WriteLine($"[zprobe] sampling frames [{start}..{end}]");

        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            PawnLookup.ForEachLivePawn(tracker, (_, pawn) =>
            {
                if (PositionUtil.CellToWorld(pawn) is { } pos)
                {
                    int b = (int)Math.Floor(pos.Z / BucketWidth);
                    hist.TryGetValue(b, out long c);
                    hist[b] = c + 1;
                    total++;
                }
            });
        }

        if (total == 0)
        {
            throw new SkipTestException("no player positions reconstructed");
        }

        // Histogram + the two candidate boundaries.
        long peak = hist.Values.Max();
        Console.WriteLine($"[zprobe] samples={total}  Zbuckets={hist.Count}");
        foreach ((int b, long c) in hist)
        {
            double z = b * BucketWidth;
            int bars = (int)Math.Round((double)c / peak * 50);
            string mark = z is <= -496 and > -528 ? "  <-- [-495..-528 candidate band]" : "";
            Console.WriteLine($"  z {z,7:F0}  {new string('#', bars),-50}  {c,9}{mark}");
        }

        long below528 = hist.Where(kv => kv.Key * BucketWidth < -528).Sum(kv => kv.Value);
        long below495 = hist.Where(kv => kv.Key * BucketWidth < -495).Sum(kv => kv.Value);
        Console.WriteLine($"[zprobe] players below -528: {below528} ({100.0 * below528 / total:F1}%)  " +
                          $"above: {total - below528} ({100.0 * (total - below528) / total:F1}%)");
        Console.WriteLine($"[zprobe] players below -495: {below495} ({100.0 * below495 / total:F1}%)  " +
                          $"above: {total - below495} ({100.0 * (total - below495) / total:F1}%)");

        // Find the player-Z valley in the [-700,-300] inter-floor region (the true divide for players).
        int? valleyBucket = null;
        long valleyCount = long.MaxValue;
        for (int b = (int)(-700 / BucketWidth); b <= (int)(-300 / BucketWidth); b++)
        {
            long c = hist.GetValueOrDefault(b, 0);
            if (c < valleyCount)
            {
                valleyCount = c;
                valleyBucket = b;
            }
        }

        if (valleyBucket is { } vb)
        {
            Console.WriteLine($"[zprobe] player-Z valley in [-700..-300] at z≈{vb * BucketWidth:F0} " +
                              $"(count {valleyCount}) — the empirical player floor divide");
        }

        // ── Engine-fidelity gate: the baker's nav boundary (−528) must correctly classify real players. ──
        // 1. Both floors are genuinely used (not a degenerate single-floor split).
        double lowerFraction = (double)below528 / total;
        await Assert.That(lowerFraction).IsGreaterThan(0.05);
        await Assert.That(lowerFraction).IsLessThan(0.60);

        // 2. The boundary sits in a real valley: the inter-floor band [−560,−496] density is a small fraction
        //    of the dominant upper-floor peak — so few players straddle it (no cut through a continuous span).
        long interFloorMin = long.MaxValue;
        for (int b = (int)(-560 / BucketWidth); b <= (int)(-496 / BucketWidth); b++)
        {
            interFloorMin = Math.Min(interFloorMin, hist.GetValueOrDefault(b, 0));
        }

        await Assert.That((double)interFloorMin / peak).IsLessThan(0.10);
    }
}

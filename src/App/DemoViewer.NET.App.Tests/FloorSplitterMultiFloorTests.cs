#region

using CS2DemoKit.Analysis.Plugins;
using DemoViewer.NET.Modules.Playback2D;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Floor-split gate (the density-valley redesign). On a real multi-floor NUKE
///     demo the split must reach and HOLD 2 floors as the histogram accumulates (the old empty-gap heuristic
///     collapsed 2→1 once stair traffic filled the inter-floor buckets). On a single-floor demo it must stay
///     1 (no false split). Accumulates player Z in one forward pass (AdvanceOneFrame) and checks the floor
///     count at increasing checkpoints — proving it doesn't drop.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class FloorSplitterMultiFloorTests
{
    [Test]
    public async Task Nuke_ReachesAndHolds_TwoFloors()
    {
        string? nuke = DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem")
                       ?? DemoTestHelper.FindDemoPath("furia-vs-vitality-m3-nuke.dem");
        if (nuke is null)
        {
            throw new SkipTestException("no nuke demo");
        }

        List<int> counts = AccumulateFloorCounts(nuke, out string trail);
        Console.WriteLine($"[floors-nuke] {Path.GetFileName(nuke)} counts={trail}");

        // Reaches 2 floors…
        await Assert.That(counts.Max()).IsGreaterThanOrEqualTo(2);
        // …and HOLDS — once it has reached 2, it never drops back to 1 (the collapse bug).
        int firstTwo = counts.FindIndex(c => c >= 2);
        for (int i = firstTwo; i < counts.Count; i++)
        {
            await Assert.That(counts[i]).IsGreaterThanOrEqualTo(2);
        }
    }

    [Test]
    public async Task SingleFloorMap_StaysOneFloor()
    {
        string? dust2 = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem");
        if (dust2 is null)
        {
            throw new SkipTestException("no dust2 demo");
        }

        List<int> counts = AccumulateFloorCounts(dust2, out string trail);
        Console.WriteLine($"[floors-dust2] {Path.GetFileName(dust2)} counts={trail}");

        // No false split — a single-floor map stays at exactly one floor the whole way.
        await Assert.That(counts.Max()).IsEqualTo(1);
    }

    // Steps one tracker forward, folding live-player Z into a single FloorSplitter (mirrors the module's
    // running histogram), and samples the floor count at evenly-spaced checkpoints.
    private static List<int> AccumulateFloorCounts(string path, out string trail)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int cap = Math.Min(frames.Count, 130_000); // bound the pass; enough of the map to use both floors
        const int ObserveStride = 128; // ~2/sec at 64-tick — dense enough for the histogram
        int checkpointStride = Math.Max(1, cap / 6);

        FloorSplitter splitter = new();
        EntityTracker tracker = new();
        List<int> counts = new();

        for (int f = 0; f < cap; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);

            if (f % ObserveStride == 0)
            {
                PawnLookup.ForEachLivePawn(tracker, (_, pawn) =>
                {
                    if (PositionUtil.CellToWorld(pawn) is { } p)
                    {
                        splitter.Observe(p.Z);
                    }
                });
            }

            if (f > 0 && f % checkpointStride == 0)
            {
                counts.Add(splitter.Slices.Count);
            }
        }

        counts.Add(splitter.Slices.Count);
        trail = string.Join(",", counts);
        return counts;
    }
}

#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The port of the App's <c>GrenadeTrailFloorSplitTests</c>, case for case, plus the two things B1
///     adds: the non-allocating overload, and the context-driven form that needs no closure.
///     <para>
///         The behaviour under test is the reported bug that produced it: a grenade whose arc crosses
///         floors must render each portion on the right band, with the single crossing segment drawn on
///         BOTH so the arc reads as continuous. That is over-draw on purpose.
///     </para>
/// </summary>
public class TrailGeometryTests
{
    // Two floors split at Z = -528 (Nuke): upper = level 1 (Z > -528), lower = level 0.
    private static int LevelOf(double z) => z > -528 ? 1 : 0;

    [Test]
    public async Task CrossFloorArc_SplitsAcrossBands_CrossingSegmentOnBoth()
    {
        GrenadeTrailPoint[] points = [P(0), P(-100), P(-600), P(-700)];

        await AssertRuns(TrailGeometry.FloorSegmentRuns(points, 1, LevelOf), (0, 2));
        await AssertRuns(TrailGeometry.FloorSegmentRuns(points, 0, LevelOf), (1, 3));
    }

    [Test]
    public async Task SingleLevelRender_DrawsTheWholeArc() =>
        await AssertRuns(TrailGeometry.FloorSegmentRuns([P(0), P(-100), P(-600), P(-700)], -1, LevelOf),
            (0, 3));

    [Test]
    public async Task ArcEntirelyOnOneLevel_DrawsNothingOnTheOther()
    {
        GrenadeTrailPoint[] points = [P(0), P(-100), P(-200)];

        await AssertRuns(TrailGeometry.FloorSegmentRuns(points, 1, LevelOf), (0, 2));
        await Assert.That(TrailGeometry.FloorSegmentRuns(points, 0, LevelOf)).IsEmpty();
    }

    [Test]
    public async Task ArcDipsAndReturns_ProducesTwoRunsOnTheHomeLevel()
    {
        // A TWO-sample dip, so there is a segment fully on the lower level. A single-sample dip would
        // not split the home level: both of its segments still touch an upper endpoint.
        GrenadeTrailPoint[] points = [P(0), P(-100), P(-600), P(-700), P(-100), P(0)];

        await AssertRuns(TrailGeometry.FloorSegmentRuns(points, 1, LevelOf), (0, 2), (3, 5));
    }

    [Test]
    public async Task FewerThanTwoPoints_ProduceNoRuns()
    {
        await Assert.That(TrailGeometry.FloorSegmentRuns([], -1, LevelOf)).IsEmpty();
        await Assert.That(TrailGeometry.FloorSegmentRuns([P(0)], -1, LevelOf)).IsEmpty();
    }

    /// <summary>
    ///     The context overload must agree with the delegate one on every case above, because the render
    ///     path uses the former and every test in this file except this one uses the latter.
    /// </summary>
    [Test]
    public async Task ContextOverload_AgreesWithTheDelegateOverload()
    {
        GrenadeTrailPoint[] points = [P(0), P(-100), P(-600), P(-700), P(-100), P(0)];

        MapSpace space = new();
        space.Rebuild([new FloorSlice(-1200, -528), new FloorSlice(-528, 400)]);

        List<(int Start, int End)> viaContext = [];
        for (int level = 0; level <= 1; level++)
        {
            SceneRenderContext ctx = Context(space, level);
            TrailGeometry.FloorSegmentRuns(points, in ctx, viaContext);
            List<(int Start, int End)> viaDelegate =
                TrailGeometry.FloorSegmentRuns(points, level, space.LevelIndexFor);

            await Assert.That(string.Join(",", viaContext)).IsEqualTo(string.Join(",", viaDelegate));
        }
    }

    [Test]
    [Category("Budget")]
    public async Task NonAllocatingOverload_AllocatesNothingOnceTheBufferHasGrown()
    {
        GrenadeTrailPoint[] points = new GrenadeTrailPoint[256];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = P(i % 40 < 20 ? 0 : -700); // alternating bands → many runs
        }

        MapSpace space = new();
        space.Rebuild([new FloorSlice(-1200, -528), new FloorSlice(-528, 400)]);
        SceneRenderContext ctx = Context(space, 1);

        List<(int Start, int End)> buffer = [];
        for (int i = 0; i < 8; i++)
        {
            TrailGeometry.FloorSegmentRuns(points, in ctx, buffer);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            TrailGeometry.FloorSegmentRuns(points, in ctx, buffer);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 run-splits over 256 points ({buffer.Count} runs): {delta} bytes");
        await Assert.That(buffer.Count).IsGreaterThan(1);
        await Assert.That(delta).IsEqualTo(0);
    }

    private static GrenadeTrailPoint P(float z) => new(0, 0, z);

    private static SceneRenderContext Context(MapSpace space, int levelIndex)
    {
        MapLevel level = space.Levels[levelIndex];
        return new SceneRenderContext(Scene2DFrame.Empty, default, default, SKRect.Create(100, 100),
            levelIndex, level.ZMin, level.ZMax, RenderPurpose.Interactive, ScenePalette.Dark, 1f)
        {
            Levels = space
        };
    }

    private static async Task AssertRuns(IReadOnlyList<(int Start, int End)> actual,
        params (int Start, int End)[] expected) =>
        await Assert.That(string.Join(",", actual)).IsEqualTo(string.Join(",", expected));
}

#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A grenade whose flight arc crosses floors (e.g. a Nuke upper→lower throw) must render each portion on
///     the correct band — not the whole arc on the tip's floor (the reported bug). Verifies
///     <see cref="Playback2DViewport.FloorSegmentRuns" />: each floor draws only the segments whose points lie
///     on it, with the single crossing segment bridging both bands.
/// </summary>
public class GrenadeTrailFloorSplitTests
{
    // Two floors split at Z = -528 (Nuke): upper = floor 1 (Z > -528), lower = floor 0.
    private static int FloorOf(double z) => z > -528 ? 1 : 0;

    private static GrenadeTrailPoint P(float z) => new(0, 0, z);

    [Test]
    public async Task CrossFloorArc_SplitsAcrossBands_CrossingSegmentOnBoth()
    {
        // Upper → lower throw: two points up top, then two down below.
        List<GrenadeTrailPoint> pts = new()
        {
            P(0),
            P(-100),
            P(-600),
            P(-700)
        };

        List<(int Start, int End)> upper = Playback2DViewport.FloorSegmentRuns(pts, 1, FloorOf);
        List<(int Start, int End)> lower = Playback2DViewport.FloorSegmentRuns(pts, 0, FloorOf);

        // Upper band draws points 0..2 (the two upper points + the crossing segment down to the boundary).
        await Assert.That(upper).IsEquivalentTo(new List<(int, int)>
        {
            (0, 2)
        });
        // Lower band draws points 1..3 (the crossing segment up from the boundary + the two lower points).
        await Assert.That(lower).IsEquivalentTo(new List<(int, int)>
        {
            (1, 3)
        });
        // The crossing segment (index 1→2) appears in BOTH — continuity across the floor boundary.
    }

    [Test]
    public async Task SingleFloorRender_DrawsWholeArc()
    {
        List<GrenadeTrailPoint> pts = new()
        {
            P(0),
            P(-100),
            P(-600),
            P(-700)
        };

        List<(int Start, int End)> all = Playback2DViewport.FloorSegmentRuns(pts, -1, FloorOf);

        await Assert.That(all).IsEquivalentTo(new List<(int, int)>
        {
            (0, 3)
        });
    }

    [Test]
    public async Task ArcEntirelyOnOneFloor_DrawsNothingOnTheOther()
    {
        List<GrenadeTrailPoint> pts = new()
        {
            P(0),
            P(-100),
            P(-200)
        }; // all upper (Z > -528)

        List<(int Start, int End)> upper = Playback2DViewport.FloorSegmentRuns(pts, 1, FloorOf);
        List<(int Start, int End)> lower = Playback2DViewport.FloorSegmentRuns(pts, 0, FloorOf);

        await Assert.That(upper).IsEquivalentTo(new List<(int, int)>
        {
            (0, 2)
        });
        await Assert.That(lower.Count).IsEqualTo(0); // nothing drawn on the lower band
    }

    [Test]
    public async Task ArcDipsToOtherFloorAndReturns_ProducesTwoRunsOnHomeFloor()
    {
        // Upper, upper, DIP to lower (TWO samples — so there's a segment fully on the lower floor), back up.
        // The home (upper) floor gets two runs split by the dip; a single-sample dip would NOT split (its two
        // segments each still touch an upper endpoint under the either-endpoint rule).
        List<GrenadeTrailPoint> pts = new()
        {
            P(0),
            P(-100),
            P(-600),
            P(-700),
            P(-100),
            P(0)
        };

        List<(int Start, int End)> upper = Playback2DViewport.FloorSegmentRuns(pts, 1, FloorOf);

        // Run 1: pts 0..2 (down to the boundary). Run 2: pts 3..5 (back up). The fully-lower segment (2→3) is
        // excluded from the upper band.
        await Assert.That(upper).IsEquivalentTo(new List<(int, int)>
        {
            (0, 2),
            (3, 5)
        });
    }
}

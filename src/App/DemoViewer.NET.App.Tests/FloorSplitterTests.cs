#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Gates for the Z floor-split histogram heuristic. Pure / deterministic — no Avalonia,
///     no demo. Covers the single-cluster common case (one section), the multi-floor case (Nuke/Vertigo-like
///     two-storey Z separation → two sections), slice assignment, and gap snapping.
/// </summary>
public class FloorSplitterTests
{
    [Test]
    public async Task NoObservations_YieldsNoSlices()
    {
        FloorSplitter s = new();
        await Assert.That(s.Slices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleCluster_YieldsOneSlice()
    {
        FloorSplitter s = new();
        // A spread of Z values all within one floor (a Dust2-like single-storey map).
        foreach (double z in new[]
                 {
                     -120.0, -64.0, 0.0, 48.0, 96.0, 130.0
                 })
        {
            s.Observe(z);
        }

        await Assert.That(s.Slices.Count).IsEqualTo(1);
        // Every observed Z falls in slice 0.
        await Assert.That(s.SliceIndexFor(0.0)).IsEqualTo(0);
        await Assert.That(s.SliceIndexFor(130.0)).IsEqualTo(0);
    }

    [Test]
    public async Task TwoFloors_SeparatedByLargeGap_YieldTwoSlices()
    {
        FloorSplitter s = new();

        // Lower floor cluster around z≈0, upper floor cluster around z≈800 (Nuke-style two storeys with a
        // clear > 180u empty Z gap between them).
        foreach (double z in new[]
                 {
                     -50.0, 0.0, 40.0, 80.0
                 })
        {
            s.Observe(z);
        }

        foreach (double z in new[]
                 {
                     760.0, 800.0, 840.0, 880.0
                 })
        {
            s.Observe(z);
        }

        await Assert.That(s.Slices.Count).IsEqualTo(2);

        // Slices are ordered low→high.
        await Assert.That(s.Slices[0].MidZ).IsLessThan(s.Slices[1].MidZ);

        // A lower-floor player is slice 0; an upper-floor player is slice 1.
        await Assert.That(s.SliceIndexFor(20.0)).IsEqualTo(0);
        await Assert.That(s.SliceIndexFor(820.0)).IsEqualTo(1);
    }

    [Test]
    public async Task ShallowValley_WithinOneFloor_StaysOneCluster()
    {
        FloorSplitter s = new();

        // A single floor with a shallow density dip (a ramp/stairs within the floor — players still fill the
        // in-between Z). A valley ABOVE ValleyDepthFraction of the smaller peak does NOT split (density-valley
        // semantics; replaces the old empty-Z-gap heuristic that collapsed on real multi-floor maps).
        Repeat(s, 0.0, 100);
        Repeat(s, 64.0, 70);
        Repeat(s, 128.0, 60); // the dip — still ~60% of the flanking peaks → one floor
        Repeat(s, 192.0, 75);
        Repeat(s, 256.0, 90);

        await Assert.That(s.Slices.Count).IsEqualTo(1);
    }

    private static void Repeat(FloorSplitter s, double z, int n)
    {
        for (int i = 0; i < n; i++)
        {
            s.Observe(z);
        }
    }

    [Test]
    public async Task GapZ_SnapsToNearestSlice()
    {
        FloorSplitter s = new();
        foreach (double z in new[]
                 {
                     0.0, 40.0
                 })
        {
            s.Observe(z);
        }

        foreach (double z in new[]
                 {
                     800.0, 840.0
                 })
        {
            s.Observe(z);
        }

        // A Z in the empty gap (a player on a ramp) snaps to the nearer floor — never a phantom slice.
        await Assert.That(s.SliceIndexFor(300.0)).IsEqualTo(0); // closer to the low cluster
        await Assert.That(s.SliceIndexFor(700.0)).IsEqualTo(1); // closer to the high cluster
    }

    [Test]
    public async Task FloorCount_IsSticky_DoesNotDropWhenAFloorEmpties()
    {
        FloorSplitter s = new();

        // Establish two floors: a dense lower cluster (z≈0) and a dense upper cluster (z≈800).
        Repeat(s, 0.0, 100);
        Repeat(s, 800.0, 100);
        await Assert.That(s.Slices.Count).IsEqualTo(2);

        // Flood the lower floor so the upper floor's RELATIVE dwell-mass dilutes far below the threshold
        // (≡ the upper floor sitting empty for a long stretch). The split must STAY at two — the upper
        // viewport must not vanish (count hysteresis: once revealed, a floor sticks for the demo).
        Repeat(s, 0.0, 5000);
        await Assert.That(s.Slices.Count).IsEqualTo(2);

        // …until a new demo (Reset) clears the established structure.
        s.Reset();
        await Assert.That(s.Slices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reset_ClearsHistogram()
    {
        FloorSplitter s = new();
        s.Observe(0);
        s.Observe(900);
        await Assert.That(s.Slices.Count).IsGreaterThanOrEqualTo(1);

        s.Reset();
        await Assert.That(s.Slices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreeFloors_YieldThreeSlicesOrderedLowToHigh()
    {
        FloorSplitter s = new();
        foreach (double z in new[]
                 {
                     0.0, 50.0, -300.0, -340.0, 600.0, 650.0
                 })
        {
            s.Observe(z);
        }

        await Assert.That(s.Slices.Count).IsEqualTo(3);
        await Assert.That(s.Slices[0].MidZ).IsLessThan(s.Slices[1].MidZ);
        await Assert.That(s.Slices[1].MidZ).IsLessThan(s.Slices[2].MidZ);
    }

    // ── REAL networked m_MinimapVerticalSectionHeights vs the histogram heuristic. ──

    [Test]
    public async Task SectionHeights_AreStoredButNotAdopted_HistogramOwnsSplit()
    {
        // Section-height adoption is DEFERRED: section heights are READ + STORED (so the VM can surface them and adoption can
        // be re-enabled with a real multi-floor demo) but NOT adopted as the split. Empirically the schema's
        // "radar floor-switching" sections are render sub-divisions, not storeys: on the resolving demo the
        // boundaries (-456,-416,-352) cut THROUGH a continuous single-floor player-Z span and every adoption
        // variant fragments / flickers (Playback2DFloorThresholdProbeTests). So the histogram owns the split.
        FloorSplitter s = new();
        foreach (double z in new[]
                 {
                     -416.0, -416.0, -400.0, -352.0, -300.0, -200.0, -150.0, -111.0
                 })
        {
            s.Observe(z);
        }

        await Assert.That(s.Slices.Count).IsEqualTo(1); // histogram: one continuous floor

        // Supplying the demo's real section heights does NOT change the split (stored, not adopted).
        double[] heights =
        {
            -456.0, -416.0, -352.0
        };
        s.SetSectionHeights(heights);
        await Assert.That(s.HasSectionHeights).IsTrue(); // stored / surfaced...
        await Assert.That(s.Slices.Count).IsEqualTo(1); // ...but the histogram still owns the split.
    }

    [Test]
    public async Task SectionHeights_SingleUsableBoundary_FallsBackToHistogram()
    {
        FloorSplitter s = new();
        foreach (double z in new[]
                 {
                     0.0, 40.0
                 })
        {
            s.Observe(z);
        }

        // A map publishing only one real value (rest sentinel) is single-floor → no usable split; histogram runs.
        double[] heights =
        {
            64.0, 3.4e38, 3.4e38
        };
        s.SetSectionHeights(heights);
        await Assert.That(s.HasSectionHeights).IsFalse();
        await Assert.That(s.Slices.Count).IsEqualTo(1); // from the histogram
    }

    [Test]
    public async Task SectionHeights_NullOrEmpty_StaysOnHistogram()
    {
        FloorSplitter s = new();
        s.Observe(0);
        s.SetSectionHeights(null);
        await Assert.That(s.HasSectionHeights).IsFalse();

        s.SetSectionHeights(Array.Empty<double>());
        await Assert.That(s.HasSectionHeights).IsFalse();
    }

    [Test]
    public async Task SectionHeights_ClearedByReset()
    {
        FloorSplitter s = new();
        double[] heights =
        {
            0.0, 256.0, 512.0
        };
        s.SetSectionHeights(heights);
        await Assert.That(s.HasSectionHeights).IsTrue();

        s.Reset();
        await Assert.That(s.HasSectionHeights).IsFalse();
    }
}

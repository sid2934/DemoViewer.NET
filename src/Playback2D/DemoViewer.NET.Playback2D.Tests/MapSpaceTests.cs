#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The level model, and the one assertion that stops the port from silently re-assigning players to
///     other floors: <see cref="MapSpace.LevelIndexFor" /> must answer exactly what
///     <see cref="FloorSplitter.SliceIndexFor" /> answers (plan decision D-15, test 3's parity oracle).
/// </summary>
public class MapSpaceTests
{
    [Test]
    public async Task LevelId_IsQuantizedZMin_NotAnIndex()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        await Assert.That(space.Levels[0].Id).IsEqualTo(new MapLevelId(-7)); // -448 / 64
        await Assert.That(space.Levels[1].Id).IsEqualTo(new MapLevelId(-6)); // -384 / 64
        await Assert.That(space.IndexOf(new MapLevelId(-6))).IsEqualTo(1);
        await Assert.That(space.IndexOf(new MapLevelId(999))).IsEqualTo(-1);
    }

    /// <summary>
    ///     Design risk 5, stated as a test: inserting a lower band shifts every index but no identity.
    ///     <c>PaneSetReconcileTests</c> then proves the cameras follow the identities, not the indices.
    /// </summary>
    [Test]
    public async Task Rebuild_InsertingALowerBand_KeepsTheUpperBandsIdentity()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-384, -128)]);
        MapLevelId upper = space.Levels[0].Id;
        await Assert.That(space.IndexOf(upper)).IsEqualTo(0);

        LevelSetChange change = space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        await Assert.That(space.IndexOf(upper)).IsEqualTo(1);
        await Assert.That(change.Retained).Contains(upper);
        await Assert.That(change.Added.Count).IsEqualTo(1);
        await Assert.That(change.Removed).IsEmpty();
    }

    [Test]
    public async Task Rebuild_WithTheSameBands_IsIdempotentAndSilent()
    {
        MapSpace space = new();
        int raised = 0;
        space.LevelSetChanged += () => raised++;

        FloorSlice[] bands = [new(-448, -384), new(-384, -128)];
        space.Rebuild(bands);
        int versionAfterFirst = space.Version;

        LevelSetChange again = space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        await Assert.That(again.Changed).IsFalse();
        await Assert.That(raised).IsEqualTo(1);
        await Assert.That(space.Version).IsEqualTo(versionAfterFirst);
    }

    [Test]
    public async Task Rebuild_DroppingABand_ReportsItRemoved()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);
        MapLevelId lower = space.Levels[0].Id;

        LevelSetChange change = space.Rebuild([new FloorSlice(-384, -128)]);

        await Assert.That(change.Removed).Contains(lower);
        await Assert.That(space.ById(lower)).IsNull();
    }

    /// <summary>
    ///     <b>The parity oracle.</b> 200 Z values spanning both bands, the gap between them, and well
    ///     outside the observed range on each side: the two implementations must agree on every one.
    ///     The out-of-range values are the point: a naive band test would return "no level" there and
    ///     make a grenade arcing over the map disappear.
    /// </summary>
    [Test]
    public async Task LevelIndexFor_MatchesFloorSplitter_OverAZTable()
    {
        FloorSplitter splitter = new();
        FloorSlice[] bands = [new(-448, -352), new(-200, 96)];
        splitter.SetAuthoritativeFloors(bands);

        MapSpace space = new();
        space.Rebuild(bands);

        List<double> disagreements = [];
        for (int i = 0; i < 200; i++)
        {
            double z = -900 + i * 9.0; // -900 .. +891, straddling both bands and the gap
            int expected = splitter.SliceIndexFor(z);
            int actual = space.LevelIndexFor(z);
            if (expected != actual)
            {
                disagreements.Add(z);
            }
        }

        Console.WriteLine($"[level-parity] checked 200 Z values, {disagreements.Count} disagreements");
        await Assert.That(disagreements).IsEmpty();
    }

    [Test]
    public async Task LevelIndexFor_OnAnEmptySpace_IsZero()
    {
        MapSpace space = new();
        await Assert.That(space.LevelIndexFor(1234)).IsEqualTo(0);
        await Assert.That(space.LevelFor(1234)).IsNull();
    }

    /// <summary>
    ///     B1 ships the stateless answer so level assignment cannot regress; B3 fills in the hysteresis
    ///     band. Pinning it now means B3 changes a body and this test, not a hundred call sites.
    /// </summary>
    [Test]
    public async Task StickyOverload_InB1_MatchesTheStatelessAnswer()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -352), new FloorSlice(-200, 96)]);

        MapLevel? sticky = space.LevelFor(-351, space.Levels[0].Id);
        await Assert.That(sticky).IsEqualTo(space.LevelFor(-351));
    }

    [Test]
    public async Task Contains_IsInclusiveAtBothEnds_MatchingFloorSlice()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -352)]);
        MapLevel level = space.Levels[0];

        await Assert.That(level.Contains(-448)).IsTrue();
        await Assert.That(level.Contains(-352)).IsTrue();
        await Assert.That(new FloorSlice(-448, -352).Contains(-352)).IsTrue();
    }

    [Test]
    public async Task Reset_ClearsEverythingAndRaisesOnce()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 64)]);

        int raised = 0;
        space.LevelSetChanged += () => raised++;
        space.Reset();
        space.Reset(); // idempotent

        await Assert.That(space.Levels).IsEmpty();
        await Assert.That(raised).IsEqualTo(1);
    }
}

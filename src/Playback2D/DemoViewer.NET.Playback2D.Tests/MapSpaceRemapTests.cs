#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <b>Design risk 5, as a test suite.</b> A level's identity must survive a boundary that drifts —
///     which is what the density-valley histogram does for the whole demo — and must NOT survive a
///     genuine floor split, because two floors sharing one identity is one camera, one picture cache and
///     (from B2) one annotation anchor pointing at the wrong storey.
/// </summary>
public class MapSpaceRemapTests
{
    [Test]
    public async Task QuantizeZ_IsIdentity_OnHistogramBoundaries()
    {
        // FloorSplitter emits every boundary as an exact multiple of its 64u bucket width, so the
        // quantum is the identity function on the common path and only snaps a bundle's arbitrary
        // doubles.
        List<double> moved = [];
        for (int k = -40; k <= 40; k++)
        {
            double z = k * MapSpace.LevelQuantum;
            if (Math.Abs(MapSpace.QuantizeZ(z) - z) > 1e-9)
            {
                moved.Add(z);
            }
        }

        await Assert.That(moved).IsEmpty();
    }

    /// <summary>
    ///     Half-UP, not banker's. <c>Math.Round(-1.5)</c> is -2 and <c>Math.Round(-0.5)</c> is 0, so a
    ///     round-to-even rule is asymmetric about zero — and CS2 maps sit at negative Z routinely.
    /// </summary>
    [Test]
    public async Task QuantizeZ_RoundsHalfUp_Symmetrically()
    {
        await Assert.That(MapSpace.QuantizeZ(-96)).IsEqualTo(-64);
        await Assert.That(MapSpace.QuantizeZ(-32)).IsEqualTo(0);
        await Assert.That(MapSpace.QuantizeZ(0)).IsEqualTo(0);
        await Assert.That(MapSpace.QuantizeZ(32)).IsEqualTo(64);
        await Assert.That(MapSpace.QuantizeZ(96)).IsEqualTo(128);
    }

    [Test]
    public async Task Rebuild_IsIdempotent_ForEqualBands()
    {
        MapSpace space = new();
        int raised = 0;
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);
        space.LevelSetChanged += () => raised++;

        LevelSetChange again = space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);

        await Assert.That(again.IsEmpty).IsTrue();
        await Assert.That(ReferenceEquals(again, LevelSetChange.None)).IsTrue();
        await Assert.That(raised).IsEqualTo(0);
    }

    /// <summary>
    ///     The whole point of overlap-carry: the boundary between two bands moves by a bucket as the
    ///     histogram accumulates, and both identities hold. Under the pre-B3 key-equality rule the upper
    ///     band's ZMin changed, so it was Removed and re-Added — losing its camera every time the
    ///     histogram twitched.
    /// </summary>
    [Test]
    public async Task BoundaryDrift_OneBucket_PreservesIds()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);
        MapLevelId lower = space.Levels[0].Id;
        MapLevelId upper = space.Levels[1].Id;

        LevelSetChange change = space.Rebuild([new FloorSlice(0, 704), new FloorSlice(704, 1280)]);

        await Assert.That(space.Levels[0].Id).IsEqualTo(lower);
        await Assert.That(space.Levels[1].Id).IsEqualTo(upper);
        await Assert.That(change.Added).IsEmpty();
        await Assert.That(change.Removed).IsEmpty();
        await Assert.That(change.Remapped[upper]).IsEqualTo(upper);
    }

    [Test]
    public async Task SplitOneIntoTwo_KeepsLowerId_AddsUpper()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 1280)]);
        MapLevelId original = space.Levels[0].Id;

        // A real 1→2 split: the lower half still shares most of the thinner band with the original, the
        // upper half does not.
        LevelSetChange change = space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);

        await Assert.That(space.Levels).HasCount().EqualTo(2);
        await Assert.That(change.Removed).IsEmpty();
        await Assert.That(change.Added).HasCount().EqualTo(1);
        await Assert.That(space.Levels[0].Id).IsEqualTo(original);
        await Assert.That(space.Levels[0].Id).IsNotEqualTo(space.Levels[1].Id);
    }

    [Test]
    public async Task MergeTwoIntoOne_RemovesOne()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);

        LevelSetChange change = space.Rebuild([new FloorSlice(0, 1280)]);

        await Assert.That(space.Levels).HasCount().EqualTo(1);
        await Assert.That(change.Removed).HasCount().EqualTo(1);
        await Assert.That(change.Added).IsEmpty();
    }

    /// <summary>
    ///     A key that was ever minted is never minted again. Without the monotonic set, removing a level
    ///     and later re-observing the same band would hand the newcomer the departed level's identity —
    ///     and with it whatever camera, cached picture or annotation still remembered that id.
    /// </summary>
    [Test]
    public async Task MintedKeys_NeverCollide_AfterRemoveThenAdd()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 128), new FloorSlice(512, 640)]);
        MapLevelId departed = space.Levels[0].Id;

        space.Rebuild([new FloorSlice(512, 640)]);
        space.Rebuild([new FloorSlice(0, 128), new FloorSlice(512, 640)]);

        await Assert.That(space.Levels[0].Id).IsNotEqualTo(departed);
        await Assert.That(space.ById(departed)).IsNull();
    }

    [Test]
    public async Task TryRemapAnchor_Containing_Nearest_And_Empty()
    {
        MapSpace empty = new();
        await Assert.That(empty.LastChange.TryRemapAnchor(0, out double _)).IsFalse();

        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(1280, 1920)]);
        LevelSetChange change = space.Rebuild([new FloorSlice(-64, 704), new FloorSlice(1280, 1920)]);

        // a. an anchor inside a surviving band moves to that band's (new) lower Z
        await Assert.That(change.TryRemapAnchor(0, out double containing)).IsTrue();
        await Assert.That(containing).IsEqualTo(-64);

        // c. an anchor in the gap between bands snaps to the nearest band centre's level
        await Assert.That(change.TryRemapAnchor(1100, out double nearest)).IsTrue();
        await Assert.That(nearest).IsEqualTo(1280);
    }

    /// <summary>
    ///     Rule (b): the band that WAS at this Z is gone from under the anchor, but the level that
    ///     inherited its identity is still there — follow the identity, not the number.
    /// </summary>
    [Test]
    public async Task TryRemapAnchor_FollowsTheIdentity_WhenTheBandMovedAway()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640)]);
        LevelSetChange change = space.Rebuild([new FloorSlice(320, 960)]);

        await Assert.That(change.Retained).HasCount().EqualTo(1);
        await Assert.That(change.TryRemapAnchor(0, out double moved)).IsTrue();
        await Assert.That(moved).IsEqualTo(320);
    }

    [Test]
    public async Task Names_ReorderFreely_ButIdsDoNot()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640)]);
        MapLevelId original = space.Levels[0].Id;
        await Assert.That(space.Levels[0].Name).IsEqualTo("L0");

        space.Rebuild([new FloorSlice(-1280, -640), new FloorSlice(0, 640)]);

        await Assert.That(space.ById(original)!.Name).IsEqualTo("L1");
        await Assert.That(space.IndexOf(original)).IsEqualTo(1);
    }

    /// <summary>
    ///     Level bands stay RAW. The quantum mints identity; snapping the band itself would move a
    ///     player standing between the raw and quantized boundary onto the other floor, and the pre-v2
    ///     assignment is what every golden contains (plan deviation 1).
    /// </summary>
    [Test]
    public async Task RebuiltBands_KeepTheirRawZ_SoAssignmentIsUnchanged()
    {
        // de_nuke's baked nav floors, which are nowhere near a 64u multiple.
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-100000, -528), new FloorSlice(-528, 100000)]);

        await Assert.That(space.Levels[0].ZMax).IsEqualTo(-528);
        await Assert.That(space.Levels[1].ZMin).IsEqualTo(-528);
        await Assert.That(space.LevelIndexFor(-520)).IsEqualTo(1);
    }
}

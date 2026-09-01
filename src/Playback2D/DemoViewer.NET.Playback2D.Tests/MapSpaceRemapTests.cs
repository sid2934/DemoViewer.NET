#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A level's identity must survive a boundary that drifts (the density-valley histogram drifts one
///     for the whole demo) and must NOT survive a genuine floor split. Two floors sharing one identity
///     is one camera, one picture cache and one annotation anchor pointing at the wrong storey.
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
    ///     round-to-even rule is asymmetric about zero, and CS2 maps sit at negative Z routinely.
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
    ///     What overlap-carry is for: the boundary between two bands moves by a bucket as the histogram
    ///     accumulates, and both identities hold. Under a plain ZMin key-equality rule the upper band's
    ///     ZMin changes, so it is Removed and re-Added, losing its camera every time the histogram
    ///     twitches.
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
    ///     and later re-observing the same band hands the newcomer the departed level's identity, and
    ///     with it whatever camera, cached picture or annotation still remembered that id.
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

    /// <summary>
    ///     <b>The other side of <see cref="MintedKeys_NeverCollide_AfterRemoveThenAdd" />.</b> The bump
    ///     that protects a departed level's identity also breaks
    ///     <c>level.Id == IdForZMin(level.ZMin)</c>, so an anchor resolved by the minting rule stops
    ///     matching the pane that is visibly drawing it. <see cref="MapSpace.IdForAnchor" /> is where
    ///     ZMin-keyed level identity lives.
    /// </summary>
    [Test]
    public async Task IdForAnchor_FollowsTheCarriedIdentity_NotTheMintingRule()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        // Lose the lower floor, then find it again: what a histogram that briefly sees no samples down
        // there does, and what a demo reload does deliberately.
        space.Rebuild([new FloorSlice(-384, -128)]);
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        MapLevel lower = space.Levels[0];
        double anchor = MapSpace.QuantizeZ(lower.ZMin);

        Console.WriteLine($"[anchor] level={lower.Id} mintingRule={MapSpace.IdForZMin(anchor)}");

        await Assert.That(lower.Id).IsNotEqualTo(MapSpace.IdForZMin(anchor))
            .Because("Mint walked past the key this band used to hold — that IS the defect");
        await Assert.That(space.IdForAnchor(anchor)).IsEqualTo(lower.Id);
        await Assert.That(space.IdForAnchor(MapSpace.QuantizeZ(space.Levels[1].ZMin)))
            .IsEqualTo(space.Levels[1].Id);
    }

    /// <summary>
    ///     Contiguous bands share a boundary value, so containment alone answers "both" and picks the
    ///     floor BELOW, the same trap <see cref="TryRemapAnchor_OnASharedBoundary_PrefersTheBandAbove" />
    ///     documents. The quantized key has to win first, and the gap case still has to land somewhere.
    /// </summary>
    [Test]
    public async Task IdForAnchor_PrefersTheBandAbove_AndNeverAnswersNothing()
    {
        MapSpace empty = new();
        await Assert.That(empty.IdForAnchor(0)).IsEqualTo(MapSpace.IdForZMin(0))
            .Because("before the first rebuild there is nothing to resolve against");

        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);

        await Assert.That(space.IdForAnchor(640)).IsEqualTo(space.Levels[1].Id)
            .Because("an anchor is a band's LOWER bound, never its neighbour's top");
        await Assert.That(space.IdForAnchor(0)).IsEqualTo(space.Levels[0].Id);
        await Assert.That(space.IdForAnchor(320)).IsEqualTo(space.Levels[0].Id);

        MapSpace gapped = new();
        gapped.Rebuild([new FloorSlice(0, 640), new FloorSlice(4000, 4640)]);
        await Assert.That(gapped.IdForAnchor(3900)).IsEqualTo(gapped.Levels[1].Id)
            .Because("nearest band centre, exactly as TryRemapAnchor's last rule — ink never belongs "
                     + "to no floor at all");
    }

    /// <summary>
    ///     <b>Reset removes every level, and has to say so.</b> A handler that reconciles against
    ///     <c>LastChange</c> (<c>PaneSet.RetainUnarranged</c>, which <c>Scene2DHost</c> calls) told
    ///     <c>LevelSetChange.None</c> keeps a pane and a camera for every floor of the demo that has
    ///     just closed.
    /// </summary>
    [Test]
    public async Task Reset_PublishesEveryLevelAsRemoved_SoPanesReconcile()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(600, 400),
            new WorldBounds(-1000, -1000, 1000, 1000));
        await Assert.That(panes.Panes).HasCount().EqualTo(2);

        MapLevelId[] before = [.. space.Levels.Select(l => l.Id)];
        space.Reset();
        LevelSetChange change = space.LastChange;

        await Assert.That(change.Changed).IsTrue();
        await Assert.That(change.Removed).HasCount().EqualTo(2);
        await Assert.That(change.Removed.Contains(before[0])).IsTrue();
        await Assert.That(change.Removed.Contains(before[1])).IsTrue();
        await Assert.That(change.Added).IsEmpty();
        await Assert.That(change.Retained).IsEmpty();

        panes.RetainUnarranged(change);
        await Assert.That(panes.Panes).IsEmpty()
            .Because("every floor of the outgoing demo is gone, not just off screen");

        // Nothing to rebase an annotation anchor ONTO, so the rebase path stands down rather than
        // rewriting the closing demo's sidecar.
        await Assert.That(change.TryRemapAnchor(-448, out double _)).IsFalse();
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
    ///     inherited its identity is still there. Follow the identity, not the number.
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

    /// <summary>
    ///     <b>The anchor case the non-contiguous fixtures above cannot reach.</b> Real band lists touch:
    ///     <c>FloorSplitter</c> emits slice N's <c>MaxZ</c> as slice N+1's <c>MinZ</c>, and de_nuke's baked
    ///     bundle publishes <c>[-100000..-528]</c> / <c>[-528..100000]</c>. An anchor stamped with the
    ///     upper level's <c>ZMin</c> therefore sits exactly on the shared boundary, and the boundary is
    ///     the thing that drifts (see <see cref="BoundaryDrift_OneBucket_PreservesIds" />). The anchor
    ///     must follow the identity it named, not the geometry that moved out from under it.
    /// </summary>
    [Test]
    public async Task TryRemapAnchor_OnContiguousBands_FollowsTheIdentity_NotTheBandBelow()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);
        MapLevelId upper = space.Levels[1].Id;

        // The boundary drifts UP one bucket, exactly as the histogram does all demo long. Both ids hold.
        LevelSetChange change = space.Rebuild([new FloorSlice(0, 704), new FloorSlice(704, 1280)]);
        await Assert.That(space.Levels[1].Id).IsEqualTo(upper);

        await Assert.That(change.TryRemapAnchor(640, out double rebased)).IsTrue();
        await Assert.That(rebased).IsEqualTo(704)
            .Because("an anchor on the upper floor must stay on the upper floor");
    }

    /// <summary>
    ///     A band's lower Z is its own, not its neighbour's upper edge. With contiguous bands both
    ///     <c>Contains</c> the shared value, so the tie must break upward or every boundary anchor sinks
    ///     one floor on the first rebuild that touches it.
    /// </summary>
    [Test]
    public async Task TryRemapAnchor_OnASharedBoundary_PrefersTheBandAbove()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1280)]);

        // Only the TOP of the upper band moves, so the anchor's own level is untouched and its identity
        // is not in LevelsBefore's way. That isolates the containment tie-break.
        LevelSetChange change = space.Rebuild([new FloorSlice(0, 640), new FloorSlice(640, 1344)]);

        await Assert.That(change.TryRemapAnchor(640, out double rebased)).IsTrue();
        await Assert.That(rebased).IsEqualTo(640);

        // And the lower band's own ZMin still resolves to the lower band.
        await Assert.That(change.TryRemapAnchor(0, out double lower)).IsTrue();
        await Assert.That(lower).IsEqualTo(0);
    }

    /// <summary>
    ///     A malformed authoritative bundle can publish a zero-width band; <c>Rebuild</c> widens it so
    ///     nothing downstream divides by a zero span. The same list fed again must still be a no-op, or
    ///     every frame that re-derives the levels raises <c>LevelSetChanged</c> and drops the
    ///     compositor's picture caches with it.
    /// </summary>
    [Test]
    public async Task Rebuild_IsIdempotent_ForADegenerateBand()
    {
        MapSpace space = new();
        int raised = 0;
        space.Rebuild([new FloorSlice(0, 0), new FloorSlice(640, 1280)]);
        space.LevelSetChanged += () => raised++;

        LevelSetChange again = space.Rebuild([new FloorSlice(0, 0), new FloorSlice(640, 1280)]);

        await Assert.That(again.IsEmpty).IsTrue();
        await Assert.That(raised).IsEqualTo(0);
        await Assert.That(space.Levels[0].Span).IsGreaterThan(0);
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
    ///     assignment is what every golden contains.
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

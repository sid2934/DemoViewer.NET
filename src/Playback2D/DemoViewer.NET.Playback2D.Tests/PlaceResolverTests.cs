#region

using System.Numerics;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The resolver's cascade over the synthetic set: volumes first, then the nearest same-floor area
///     within the snap distance, then nothing; the floor-keyed variant never looks at another floor;
///     the bombsite test never snaps; the graphs are symmetric.
/// </summary>
public class PlaceResolverTests
{
    private static PlaceResolver Resolver() => new(ZoneFixtures.Build());

    [Test]
    public async Task PointInsideAHull_ResolvesByVolume()
    {
        PlaceResolver resolver = Resolver();

        PlaceHit hit = resolver.Resolve(new Vector3(250, 50, -400));

        await Assert.That(hit.Kind).IsEqualTo(PlaceHitKind.Volume);
        await Assert.That(hit.Name).IsEqualTo("Hut");
        await Assert.That(hit.PlaceId).IsEqualTo(1);
        await Assert.That(hit.SnapDistance).IsEqualTo(0);
        await Assert.That(hit.IsHit).IsTrue();
    }

    /// <summary>A pawn stands on the floor; the second probe 32 units up catches a volume that starts above it.</summary>
    [Test]
    public async Task PointJustUnderAHull_ResolvesByTheLiftedProbe()
    {
        PlaceResolver resolver = Resolver();

        PlaceHit under = resolver.Resolve(new Vector3(250, 50, -470));
        PlaceHit farUnder = resolver.Resolve(new Vector3(250, 50, -500));

        await Assert.That(under.Kind).IsEqualTo(PlaceHitKind.Volume);
        await Assert.That(under.Name).IsEqualTo("Hut");

        // 50 under the hull's bottom is past the lift, so the area answers instead: same place here,
        // a different kind, which is what a consumer storing Kind sees.
        await Assert.That(farUnder.Kind).IsEqualTo(PlaceHitKind.NearestArea);
        await Assert.That(farUnder.Name).IsEqualTo("Hut");
    }

    [Test]
    public async Task PointOffAnArea_SnapsWithin128_AndMissesBeyond()
    {
        PlaceResolver resolver = Resolver();

        PlaceHit inside = resolver.Resolve(new Vector3(50, 150, -400));
        PlaceHit near = resolver.Resolve(new Vector3(50, 300, -400));
        PlaceHit far = resolver.Resolve(new Vector3(50, 400, -400));

        await Assert.That(inside.Kind).IsEqualTo(PlaceHitKind.NearestArea);
        await Assert.That(inside.Name).IsEqualTo("Outside");
        await Assert.That(inside.SnapDistance).IsEqualTo(0);

        await Assert.That(near.Kind).IsEqualTo(PlaceHitKind.NearestArea);
        await Assert.That(near.Name).IsEqualTo("Outside");
        await Assert.That(near.SnapDistance).IsEqualTo(100);

        await Assert.That(far.Kind).IsEqualTo(PlaceHitKind.None);
        await Assert.That(far.Name).IsNull();
        await Assert.That(far.PlaceId).IsEqualTo(-1);
        await Assert.That(far).IsEqualTo(PlaceHit.None);
    }

    [Test]
    public async Task NearestArea_IsTheClosestOne_NotTheFirstInTheCell()
    {
        PlaceResolver resolver = Resolver();

        // 10 units right of Hut's area 3 and 110 from Ramp's area 2: the closer one answers.
        PlaceHit hit = resolver.Resolve(new Vector3(310, 50, -400));

        await Assert.That(hit.Name).IsEqualTo("Hut");
        await Assert.That(hit.SnapDistance).IsEqualTo(10);
    }

    [Test]
    public async Task UnassignedIsland_NeverAnswers()
    {
        PlaceResolver resolver = Resolver();

        PlaceHit hit = resolver.Resolve(new Vector3(1050, 1050, -400));

        await Assert.That(hit.Kind).IsEqualTo(PlaceHitKind.None);
    }

    [Test]
    public async Task ResolveOnFloor_IgnoresAreasOfAnotherFloor()
    {
        PlaceResolver resolver = Resolver();

        PlaceHit upper = resolver.ResolveOnFloor(50, 50, ZoneFixtures.Upper);
        PlaceHit lower = resolver.ResolveOnFloor(50, 50, ZoneFixtures.Lower);
        PlaceHit lowerOff = resolver.ResolveOnFloor(50, 250, ZoneFixtures.Lower);
        PlaceHit hutVolume = resolver.ResolveOnFloor(250, 50, ZoneFixtures.Upper);
        PlaceHit hutOnLower = resolver.ResolveOnFloor(250, 50, ZoneFixtures.Lower);
        PlaceHit unknownFloor = resolver.ResolveOnFloor(50, 50, 4096);

        await Assert.That(upper.Name).IsEqualTo("Ramp");
        await Assert.That(lower.Name).IsEqualTo("Tunnels");

        // Area 4 (Outside, upper floor) is 50 away; the lower floor's nearest area is 150 away.
        await Assert.That(lowerOff.Kind).IsEqualTo(PlaceHitKind.None);

        // The Hut volume spans [-450, -300], which meets the upper band only.
        await Assert.That(hutVolume.Kind).IsEqualTo(PlaceHitKind.Volume);
        await Assert.That(hutVolume.Name).IsEqualTo("Hut");
        await Assert.That(hutOnLower.Kind).IsEqualTo(PlaceHitKind.None);

        await Assert.That(unknownFloor.Kind).IsEqualTo(PlaceHitKind.None);
    }

    [Test]
    public async Task BombsiteAt_TestsTheVolumeOnly_AndNeverSnaps()
    {
        PlaceResolver resolver = Resolver();

        await Assert.That(resolver.BombsiteAt(new Vector3(450, 450, -400))).IsEqualTo(Bombsite.A);
        await Assert.That(resolver.BombsiteAt(new Vector3(650, 650, -400))).IsEqualTo(Bombsite.B);
        await Assert.That(resolver.BombsiteAt(new Vector3(450, 450, -250))).IsNull();
        await Assert.That(resolver.BombsiteAt(new Vector3(450, 510, -400))).IsNull();
        await Assert.That(resolver.IsInside(new Vector3(450, 450, -400), Bombsite.A)).IsTrue();
        await Assert.That(resolver.IsInside(new Vector3(450, 450, -400), Bombsite.B)).IsFalse();
    }

    [Test]
    public async Task FloorKeyFor_IsTheContainingBandsKey()
    {
        PlaceResolver resolver = Resolver();

        await Assert.That(resolver.FloorKeyFor(-700)).IsEqualTo(ZoneFixtures.Lower);
        await Assert.That(resolver.FloorKeyFor(-528)).IsEqualTo(ZoneFixtures.Upper);
        await Assert.That(resolver.FloorKeyFor(-400)).IsEqualTo(ZoneFixtures.Upper);
        await Assert.That(resolver.FloorKeyFor(5000)).IsEqualTo(ZoneFixtures.Upper);
    }

    [Test]
    public async Task Adjacency_IsSymmetric_AndNamesResolveOrdinally()
    {
        PlaceResolver resolver = Resolver();

        await Assert.That(resolver.AreAdjacent(0, 1)).IsTrue();
        await Assert.That(resolver.AreAdjacent(1, 0)).IsTrue();
        await Assert.That(resolver.AreAdjacent(1, 2)).IsFalse();
        await Assert.That(resolver.AreAdjacent(0, 0)).IsFalse();
        await Assert.That(resolver.Adjacent(0).ToArray()).IsEquivalentTo(Ids(1, 2));
        await Assert.That(resolver.Adjacent(3).Count).IsEqualTo(0);
        await Assert.That(resolver.Adjacent(99).Count).IsEqualTo(0);

        await Assert.That(resolver.PlaceId("Hut")).IsEqualTo(1);
        await Assert.That(resolver.PlaceId("hut")).IsNull();
        await Assert.That(resolver.PlaceId("Nowhere")).IsNull();
    }

    /// <summary>
    ///     Decision D4: two overlapping baked volumes resolve to the first in lump order by default, and
    ///     to the smaller one on a map the override table names (mirage, where the validation run showed
    ///     the more specific volume winning on every demo measured).
    /// </summary>
    [Test]
    public async Task OverlappingVolumes_FollowTheMapsTieRule()
    {
        PlaceResolver byLump = new(ZoneFixtures.Build(overlappingRampVolume: true));
        PlaceResolver bySize = new(ZoneFixtures.Build("de_mirage", true));
        Vector3 inBoth = new(250, 50, -400);

        await Assert.That(PlaceResolver.TieRuleFor("de_synthetic")).IsEqualTo(VolumeTieRule.FirstInLump);
        await Assert.That(PlaceResolver.TieRuleFor("de_mirage")).IsEqualTo(VolumeTieRule.SmallestVolume);
        await Assert.That(PlaceResolver.TieRuleFor(null)).IsEqualTo(VolumeTieRule.FirstInLump);

        await Assert.That(byLump.Resolve(inBoth).Name).IsEqualTo("Ramp");
        await Assert.That(bySize.Resolve(inBoth).Name).IsEqualTo("Hut");
        await Assert.That(bySize.Resolve(inBoth).Kind).IsEqualTo(PlaceHitKind.Volume);

        // Outside the overlap the rule is moot: only the Ramp volume contains this point.
        await Assert.That(byLump.Resolve(new Vector3(50, 50, -400)).Name).IsEqualTo("Ramp");
        await Assert.That(bySize.Resolve(new Vector3(50, 50, -400)).Name).IsEqualTo("Ramp");
    }

    [Test]
    public async Task Resolve_IsDeterministic_AcrossInstances()
    {
        PlaceResolver a = Resolver();
        PlaceResolver b = Resolver();
        Random random = new(7);

        for (int i = 0; i < 2000; i++)
        {
            Vector3 p = new(random.Next(-200, 1200), random.Next(-200, 1200), random.Next(-800, 0));
            await Assert.That(a.Resolve(p)).IsEqualTo(b.Resolve(p));
        }
    }

    // Through a call rather than an inline array so the analyzer's constant-array rule stays quiet.
    private static int[] Ids(params int[] ids) => ids;
}

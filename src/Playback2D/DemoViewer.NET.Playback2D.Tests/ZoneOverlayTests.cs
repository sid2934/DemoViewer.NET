#region

using System.Numerics;
using System.Text;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The user overlay over the synthetic baked set: a custom polygon wins inside itself and changes
///     nothing outside it, <c>hidden</c> re-floods, <c>merges</c> unions, <c>replaces</c> removes only a
///     fully covered baked place, a malformed entry is a diagnostic and the rest applies, and the
///     effective version tracks every byte of the overlay.
/// </summary>
public class ZoneOverlayTests
{
    // The Hut area, exactly: [200, 300] x [0, 100] on the upper floor.
    private static readonly double[] _hutPolygon = [200, 0, 300, 0, 300, 100, 200, 100];

    private static ZoneOverlayResult Apply(ZoneOverlayDocument overlay, string bytes = "{}") =>
        ZoneOverlayApplier.Apply(ZoneFixtures.Build(), overlay, Encoding.UTF8.GetBytes(bytes), "test.zones.json");

    private static ZoneOverlayDocument Overlay(IReadOnlyList<ZoneOverlayZone>? zones = null,
        IReadOnlyList<ZoneOverlayMerge>? merges = null, IReadOnlyList<string>? hidden = null,
        string? basedOn = null, string? mapName = null) =>
        new(1, mapName, basedOn, zones ?? [], merges ?? [], hidden ?? []);

    [Test]
    public async Task CustomPolygon_WinsInsideItself_AndChangesNothingOutside()
    {
        ZoneOverlayResult result = Apply(Overlay(
            zones: [new ZoneOverlayZone("E-box", ZoneFixtures.Upper, _hutPolygon, null, null, false)]));
        PlaceResolver resolver = new(result.Effective);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);

        // Inside: the custom volume sits first in the cascade, ahead of the baked Hut volume that
        // covers the same box.
        PlaceHit inside = resolver.Resolve(new Vector3(250, 50, -400));
        await Assert.That(inside.Name).IsEqualTo("E-box");
        await Assert.That(inside.Kind).IsEqualTo(PlaceHitKind.Volume);
        await Assert.That(result.Effective.Places[inside.PlaceId].Origin).IsEqualTo(PlaceOrigin.Custom);

        // Outside: every baked answer is what it was.
        PlaceResolver baked = new(ZoneFixtures.Build());
        foreach (Vector3 p in new[]
                 {
                     new Vector3(50, 50, -400), new Vector3(150, 50, -400), new Vector3(50, 150, -400),
                     new Vector3(50, 50, -700), new Vector3(50, 300, -400), new Vector3(450, 450, -400)
                 })
        {
            await Assert.That(resolver.Resolve(p).Name).IsEqualTo(baked.Resolve(p).Name);
            await Assert.That(resolver.Resolve(p).Kind).IsEqualTo(baked.Resolve(p).Kind);
        }

        // replaces: false leaves the baked place in the vocabulary even though every one of its areas
        // was taken; its volume is still there, behind the custom one.
        await Assert.That(resolver.PlaceId("Hut")).IsNotNull();
        await Assert.That(result.Effective.Places.Count).IsEqualTo(5);
        await Assert.That(result.Effective.Volumes[0].Origin).IsEqualTo(PlaceOrigin.Custom);
        await Assert.That(result.Effective.Volumes[0].Polygon).IsNotNull();
    }

    [Test]
    public async Task Hidden_RefloodsItsAreasFromNeighbours_AndDropsItsVolume()
    {
        ZoneOverlayResult result = Apply(Overlay(hidden: ["Hut"]));
        PlaceResolver resolver = new(result.Effective);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(resolver.PlaceId("Hut")).IsNull();
        await Assert.That(result.Effective.Places.Select(p => p.Name).ToArray())
            .IsEquivalentTo(Names("Ramp", "Outside", "Tunnels"));

        // Area 3 was Hut's; its only neighbour is Ramp's area 2, so it is Ramp now, and the Hut volume
        // is gone so the volume step no longer answers here either.
        PlaceHit hit = resolver.Resolve(new Vector3(250, 50, -400));
        await Assert.That(hit.Name).IsEqualTo("Ramp");
        await Assert.That(hit.Kind).IsEqualTo(PlaceHitKind.NearestArea);
        await Assert.That(result.Effective.Volumes.Any(v => v.Kind == ZoneVolumeKind.Place)).IsFalse();

        // Ramp lost its Hut neighbour and adjacency was rebuilt over the new assignment.
        await Assert.That(resolver.Adjacent(resolver.PlaceId("Ramp")!.Value).Count).IsEqualTo(1);

        // The island stays an island: nothing assigned links to it.
        await Assert.That(result.Effective.Areas.Single(a => a.Id == 6).PlaceId).IsEqualTo(-1);
    }

    [Test]
    public async Task Merges_YieldOnePlaceWithTheUnion()
    {
        ZoneOverlayResult result = Apply(Overlay(merges: [new ZoneOverlayMerge("Site", ["Ramp", "Hut"])]));
        PlaceResolver resolver = new(result.Effective);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Effective.Places.Select(p => p.Name).ToArray())
            .IsEquivalentTo(Names("Outside", "Tunnels", "Site"));

        int site = resolver.PlaceId("Site")!.Value;
        await Assert.That(result.Effective.Places[site].Origin).IsEqualTo(PlaceOrigin.Custom);
        await Assert.That(result.Effective.Areas.Count(a => a.PlaceId == site)).IsEqualTo(4);

        // Hut's volume came along and answers with the merged name.
        PlaceHit hit = resolver.Resolve(new Vector3(250, 50, -400));
        await Assert.That(hit.Name).IsEqualTo("Site");
        await Assert.That(hit.Kind).IsEqualTo(PlaceHitKind.Volume);
        await Assert.That(resolver.Resolve(new Vector3(50, 50, -400)).Name).IsEqualTo("Site");
        await Assert.That(resolver.AreAdjacent(site, resolver.PlaceId("Outside")!.Value)).IsTrue();
    }

    [Test]
    public async Task Replaces_RemovesAFullyCoveredBakedPlace_AndLeavesAPartlyCoveredOne()
    {
        ZoneOverlayResult full = Apply(Overlay(
            zones: [new ZoneOverlayZone("E-box", ZoneFixtures.Upper, _hutPolygon, null, null, true)]));
        ZoneOverlayResult partial = Apply(Overlay(
            zones: [new ZoneOverlayZone("Top", ZoneFixtures.Upper, [0, 0, 100, 0, 100, 100, 0, 100], null, null, true)]));

        await Assert.That(new PlaceResolver(full.Effective).PlaceId("Hut")).IsNull();
        await Assert.That(full.Effective.Places.Count).IsEqualTo(4);
        await Assert.That(full.Effective.Volumes.Count(v => v.Kind == ZoneVolumeKind.Place)).IsEqualTo(1);

        // Ramp keeps areas 2 and 7, so it stays; only area 1 changed hands.
        PlaceResolver partialResolver = new(partial.Effective);
        await Assert.That(partialResolver.PlaceId("Ramp")).IsNotNull();
        await Assert.That(partialResolver.Resolve(new Vector3(50, 50, -400)).Name).IsEqualTo("Top");
        await Assert.That(partialResolver.Resolve(new Vector3(150, 50, -400)).Name).IsEqualTo("Ramp");
    }

    [Test]
    public async Task MalformedEntry_IsSkippedWithADiagnostic_AndTheRestApplies()
    {
        ZoneOverlayResult result = Apply(Overlay(
            zones:
            [
                new ZoneOverlayZone("Nowhere", 4096, _hutPolygon, null, null, false),
                new ZoneOverlayZone("Bowtie", ZoneFixtures.Upper, [0, 0, 100, 100, 100, 0, 0, 100], null, null, false),
                new ZoneOverlayZone("", ZoneFixtures.Upper, _hutPolygon, null, null, false),
                new ZoneOverlayZone("Flat", ZoneFixtures.Upper, _hutPolygon, -300, -400, false),
                new ZoneOverlayZone("Hut", ZoneFixtures.Upper, _hutPolygon, null, null, false),
                new ZoneOverlayZone("E-box", ZoneFixtures.Upper, _hutPolygon, null, null, false)
            ],
            merges: [new ZoneOverlayMerge("Site", ["Ramp", "Nope"]), new ZoneOverlayMerge("Outside", ["Ramp"])],
            hidden: ["Ghost"],
            basedOn: "00000000",
            mapName: "de_other"));

        string[] codes = [.. result.Diagnostics.Select(d => d.Code)];
        Console.WriteLine("[overlay] " + string.Join("\n[overlay] ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        await Assert.That(codes).Contains("zones.overlay.unknown-floor");
        await Assert.That(codes).Contains("zones.overlay.bad-polygon");
        await Assert.That(codes).Contains("zones.overlay.empty-name");
        await Assert.That(codes).Contains("zones.overlay.bad-band");
        await Assert.That(codes).Contains("zones.overlay.name-collision");
        await Assert.That(codes).Contains("zones.overlay.unknown-place");
        await Assert.That(codes).Contains("zones.overlay.based-on");
        await Assert.That(codes).Contains("zones.overlay.map-mismatch");
        await Assert.That(codes.Count(c => c == "zones.overlay.name-collision")).IsEqualTo(2);
        await Assert.That(codes.Count(c => c == "zones.overlay.unknown-place")).IsEqualTo(2);

        // Only the last zone was well-formed, and it applied.
        PlaceResolver resolver = new(result.Effective);
        await Assert.That(resolver.Resolve(new Vector3(250, 50, -400)).Name).IsEqualTo("E-box");
        await Assert.That(result.Effective.Places.Select(p => p.Name).ToArray())
            .IsEquivalentTo(Names("Ramp", "Hut", "Outside", "Tunnels", "E-box"));
    }

    [Test]
    public async Task NameCollisionWithReplaces_GivesTheNameOneOwner()
    {
        ZoneOverlayResult result = Apply(Overlay(
            zones: [new ZoneOverlayZone("Hut", ZoneFixtures.Upper, [200, 0, 250, 0, 250, 100, 200, 100], null, null, true)]));
        PlaceResolver resolver = new(result.Effective);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Effective.Places.Count(p => p.Name == "Hut")).IsEqualTo(1);
        await Assert.That(result.Effective.Places.Single(p => p.Name == "Hut").Origin).IsEqualTo(PlaceOrigin.Custom);
        await Assert.That(resolver.Resolve(new Vector3(225, 50, -400)).Name).IsEqualTo("Hut");

        // The baked namesake's area 3 centroid (250, 50) is on the polygon's edge and counts as inside;
        // the baked Hut volume is gone with its place.
        await Assert.That(result.Effective.Volumes.Count(v => v.Kind == ZoneVolumeKind.Place)).IsEqualTo(1);
    }

    [Test]
    public async Task EffectiveVersion_TracksEveryByte_AndIsTheBakedStampWithoutAnOverlay()
    {
        ZoneOverlayDocument overlay = Overlay(hidden: ["Hut"]);
        ZoneOverlayResult a = Apply(overlay, "{ \"hidden\": [\"Hut\"] }");
        ZoneOverlayResult b = Apply(overlay, "{ \"hidden\": [\"Hut\"]  }");
        ZoneOverlayResult c = Apply(ZoneOverlayDocument.Empty, "{}");

        await Assert.That(a.Effective.EffectiveVersion).IsNotEqualTo(b.Effective.EffectiveVersion);
        await Assert.That(a.Effective.EffectiveVersion).IsNotEqualTo(ZoneFixtures.ZonesVersion);
        await Assert.That(c.Effective.EffectiveVersion).IsNotEqualTo(ZoneFixtures.ZonesVersion);
        await Assert.That(a.Effective.EffectiveVersion.Length).IsEqualTo(8);
        await Assert.That(a.Effective.HasOverlay).IsTrue();
        await Assert.That(a.Effective.ZonesVersion).IsEqualTo(ZoneFixtures.ZonesVersion);

        // Same bytes, same stamp, every time.
        await Assert.That(Apply(overlay, "{ \"hidden\": [\"Hut\"] }").Effective.EffectiveVersion)
            .IsEqualTo(a.Effective.EffectiveVersion);

        ZoneSet baked = ZoneFixtures.Build();
        await Assert.That(baked.EffectiveVersion).IsEqualTo(baked.ZonesVersion);
        await Assert.That(baked.HasOverlay).IsFalse();
    }

    [Test]
    public async Task Origin_IsCustomForCustomPlaces_AndBakedForTheRest()
    {
        ZoneOverlayResult result = Apply(Overlay(
            zones: [new ZoneOverlayZone("E-box", ZoneFixtures.Upper, _hutPolygon, null, null, false)],
            merges: [new ZoneOverlayMerge("Site", ["Outside"])]));

        foreach (ZonePlace place in result.Effective.Places)
        {
            PlaceOrigin expected = place.Name is "E-box" or "Site" ? PlaceOrigin.Custom : PlaceOrigin.Baked;
            await Assert.That(place.Origin).IsEqualTo(expected);
            await Assert.That(result.Effective.Places[place.Id]).IsEqualTo(place);
        }
    }

    [Test]
    public async Task Reader_ParsesTheDocumentedShape_AndIgnoresUnknownFields()
    {
        const string Json = """
                            {
                              "schemaVersion": 1,
                              "mapName": "de_mirage",
                              "basedOn": "9f1c02aa",
                              "defaults": { "floorQuantum": 64 },
                              "futureField": true,
                              "zones": [
                                { "name": "E-box", "floor": -512, "polygon": [ -1560, -420, -1380, -420, -1380, -190, -1560, -190 ],
                                  "minZ": -528, "maxZ": -300, "replaces": false, "colour": "red" },
                                { "name": "Default", "floor": -512, "polygon": [ 0, 0, 1, 0, 1, 1 ] }
                              ],
                              "merges": [ { "into": "Site", "from": ["BombsiteA", "Stairs", "Firebox"] } ],
                              "hidden": ["Scaffolding"]
                            }
                            """;

        ZoneOverlayDocument doc = ZoneOverlayReader.Read(Encoding.UTF8.GetBytes(Json));

        await Assert.That(doc.MapName).IsEqualTo("de_mirage");
        await Assert.That(doc.BasedOn).IsEqualTo("9f1c02aa");
        await Assert.That(doc.Zones.Count).IsEqualTo(2);
        await Assert.That(doc.Zones[0].Name).IsEqualTo("E-box");
        await Assert.That(doc.Zones[0].Floor).IsEqualTo(-512);
        await Assert.That(doc.Zones[0].Polygon.Count).IsEqualTo(8);
        await Assert.That(doc.Zones[0].MinZ).IsEqualTo(-528);
        await Assert.That(doc.Zones[0].MaxZ).IsEqualTo(-300);
        await Assert.That(doc.Zones[0].Replaces).IsFalse();
        await Assert.That(doc.Zones[1].MinZ).IsNull();
        await Assert.That(doc.Merges[0].Into).IsEqualTo("Site");
        await Assert.That(doc.Merges[0].From.Count).IsEqualTo(3);
        await Assert.That(doc.Hidden.ToArray()).IsEquivalentTo(Names("Scaffolding"));
    }

    // Through a call rather than an inline array so the analyzer's constant-array rule stays quiet.
    private static string[] Names(params string[] names) => names;
}

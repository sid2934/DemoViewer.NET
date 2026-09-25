#region

using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Callout resolution (strat-model.md §3.7) and the embedded canonical lists, with overview correction 15's
///     precedence: a map's zones supply the canonical names when they load, else the embedded list.
/// </summary>
public class CalloutResolverTests
{
    private static readonly string[] ShippedMaps =
        ["de_ancient", "de_anubis", "de_cache", "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_train", "de_vertigo"];

    private static CalloutTable Aliases(params CalloutAlias[] aliases) => new() { Map = "de_mirage", Aliases = [.. aliases] };

    private static CalloutResolver Mirage(CalloutTable? table = null) => new(CanonicalPlaces.Embedded("de_mirage"), table);

    [Test]
    public async Task ACanonicalName_WinsOverAnAliasWithTheSameSpelling()
    {
        CalloutResolver resolver = Mirage(Aliases(new CalloutAlias { Alias = "connector", Place = "Jungle" }, new CalloutAlias { Alias = "con", Place = "Connector" }));

        using (Assert.Multiple())
        {
            await Assert.That(resolver.Resolve("connector")).IsEqualTo("Connector");
            await Assert.That(resolver.Resolve("con")).IsEqualTo("Connector");
        }
    }

    [Test]
    public async Task CaseWhitespaceAndHyphens_Fold()
    {
        CalloutResolver resolver = Mirage(Aliases(new CalloutAlias { Alias = "A ramp", Place = "TRamp" }));

        using (Assert.Multiple())
        {
            await Assert.That(resolver.Resolve("  a   RAMP ")).IsEqualTo("TRamp");
            await Assert.That(resolver.Resolve("a-ramp")).IsEqualTo("TRamp");
            await Assert.That(resolver.Resolve("top of mid")).IsEqualTo("TopofMid");
            await Assert.That(resolver.Resolve("palace-interior")).IsEqualTo("PalaceInterior");
        }
    }

    [Test]
    public async Task TheEmptyPlace_AndAnUnknownWord_ResolveToNull()
    {
        CalloutResolver resolver = Mirage();

        using (Assert.Multiple())
        {
            await Assert.That(resolver.Resolve("")).IsNull().Because("the pawn reports an empty string before its place is networked");
            await Assert.That(resolver.Resolve("   ")).IsNull();
            await Assert.That(resolver.Resolve(null)).IsNull();
            await Assert.That(resolver.Resolve("kitchen")).IsNull();
        }
    }

    [Test]
    public async Task ADuplicateAlias_IsRefusedByTheValidator_AndTheFirstWinsMeanwhile()
    {
        CalloutTable table = Aliases(new CalloutAlias { Alias = "ramp", Place = "TRamp" }, new CalloutAlias { Alias = "Ramp", Place = "PalaceAlley" });

        await Assert.That(StratValidator.ValidateCallouts(table).Any(i => i.Severity == StratIssueSeverity.Refusal)).IsTrue();
        await Assert.That(Mirage(table).Resolve("ramp")).IsEqualTo("TRamp");
    }

    [Test]
    public async Task Display_PrefersThePrimaryAlias_ElseSplitsTheName()
    {
        CalloutResolver resolver = Mirage(Aliases(
            new CalloutAlias { Alias = "ramp", Place = "TRamp" },
            new CalloutAlias { Alias = "A ramp", Place = "TRamp", Primary = true },
            new CalloutAlias { Alias = "palace", Place = "PalaceInterior", Primary = true }));

        using (Assert.Multiple())
        {
            await Assert.That(resolver.Display("TRamp")).IsEqualTo("A ramp");
            await Assert.That(resolver.Display("PalaceInterior")).IsEqualTo("palace");
            await Assert.That(resolver.Display("SnipersNest")).IsEqualTo("Snipers Nest");
        }
    }

    [Test]
    [Arguments("TopofMid", "Top of Mid")]
    [Arguments("BackofB", "Back of B")]
    [Arguments("PalaceInterior", "Palace Interior")]
    [Arguments("CTSpawn", "CT Spawn")]
    [Arguments("TSideLower", "T Side Lower")]
    [Arguments("BombsiteA", "Bombsite A")]
    [Arguments("APlatform", "A Platform")]
    [Arguments("HutRoof", "Hut Roof")]
    [Arguments("Roof", "Roof")]
    public async Task SplitDisplay(string place, string expected)
    {
        await Assert.That(CalloutResolver.SplitDisplay(place)).IsEqualTo(expected);
    }

    [Test]
    public async Task EveryShippedMap_HasAnEmbeddedList_MatchingItsBakedPlaceNames()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        await Assert.That(CanonicalPlaces.Maps).IsEquivalentTo(ShippedMaps);
        await Assert.That(CanonicalPlaces.Embedded("de_mirage").Count).IsEqualTo(23).Because("the 23 pawn places measured on de_mirage");
        await Assert.That(CanonicalPlaces.Embedded("de_nuke").Count).IsEqualTo(29).Because("the 29 pawn places measured on de_nuke");
        await Assert.That(CanonicalPlaces.Embedded("de_nowhere").Count).IsEqualTo(0);

        foreach (string map in ShippedMaps)
        {
            ZoneSet baked = ZoneAssetPipeline.TryReadBaked(Path.Combine(repo, "assets", map))!;
            await Assert.That(CanonicalPlaces.Embedded(map))
                .IsEquivalentTo(baked.Places.Select(p => p.Name).Distinct().Order(StringComparer.Ordinal))
                .Because($"the embedded list for {map} is generated from its baked env_cs_place names and must not drift");
        }
    }

    [Test]
    public async Task For_PrefersTheMapsZones_ElseTheEmbeddedList()
    {
        ZoneSet zones = new("de_mirage", "1c2b3a49", "075a27b3", null, 64, [], [new ZonePlace(0, "BombsiteA", PlaceOrigin.Baked), new ZonePlace(1, "Short", PlaceOrigin.Custom)],
            [], [], [], [], null);

        CalloutResolver fromZones = CalloutResolver.For("de_mirage", zones, null);
        CalloutResolver embedded = CalloutResolver.For("de_mirage", null, null);

        using (Assert.Multiple())
        {
            await Assert.That(fromZones.Source).IsEqualTo("zones:1c2b3a49");
            await Assert.That(fromZones.Resolve("short")).IsEqualTo("Short").Because("custom zones supply place names of their own");
            await Assert.That(fromZones.IsCanonical("TRamp")).IsFalse();
            await Assert.That(embedded.Source).IsEqualTo(CanonicalPlaces.EmbeddedSource);
            await Assert.That(embedded.IsCanonical("TRamp")).IsTrue();
        }
    }
}

#region

using DemoViewer.NET.GameIcons;

#endregion

namespace DemoViewer.NET.GameIconsTests;

/// <summary>
///     The icon catalogue's contract: that the bake landed, that demo weapon strings resolve without a
///     mapping table, and that the non-square majority keeps its aspect.
/// </summary>
public class GameIconsTests
{
    private static readonly int[] BakedScales = [32, 96];

    /// <summary>An empty catalogue means the bake never ran, and every consumer would silently draw nothing.</summary>
    [Test]
    public async Task Catalogue_IsPopulated_AndCarriesASourceHash()
    {
        await Assert.That(IconCatalogue.Keys.Count).IsGreaterThan(100);
        await Assert.That(IconCatalogue.SourceHash).IsNotEmpty();
        await Assert.That(IconCatalogue.Scales).IsEquivalentTo(BakedScales);
    }

    /// <summary>
    ///     The headline claim: a <c>player_death.weapon</c> string is an icon key as-is. These are the
    ///     spellings the demo actually emits, including the two that look like typos and are not.
    /// </summary>
    [Test]
    [Arguments("ak47")]
    [Arguments("awp")]
    [Arguments("usp_silencer")]
    [Arguments("m4a1_silencer")]
    [Arguments("hkp2000")]
    [Arguments("knife_t")]
    [Arguments("inferno")]
    [Arguments("molotov")]
    [Arguments("hegrenade")]
    [Arguments("taser")]
    [Arguments("galilar")]
    [Arguments("mp5sd")]
    public async Task Weapon_ResolvesDemoWeaponString(string demoName)
    {
        IconRef? icon = IconCatalogue.Weapon(demoName);
        await Assert.That(icon).IsNotNull();
        await Assert.That(icon!.Key).IsEqualTo("equipment/" + demoName);
    }

    /// <summary>
    ///     CS2 ships deliberately empty artwork for environment deaths, and the bake drops it. Null here
    ///     is the correct answer and means "draw nothing", not "lookup failed".
    /// </summary>
    [Test]
    [Arguments("world")]
    [Arguments("worldent")]
    [Arguments("trigger_hurt")]
    public async Task Weapon_EnvironmentDeaths_HaveNoIcon(string demoName) =>
        await Assert.That(IconCatalogue.Weapon(demoName)).IsNull();

    /// <summary>An unknown weapon must be a quiet null, not a throw: demos carry strings we have never seen.</summary>
    [Test]
    public async Task Weapon_Unknown_IsNull()
    {
        await Assert.That(IconCatalogue.Weapon("weapon_that_does_not_exist")).IsNull();
        await Assert.That(IconCatalogue.Weapon(null)).IsNull();
        await Assert.That(IconCatalogue.Weapon("")).IsNull();
    }

    /// <summary>Every modifier the kill feed can set must have artwork, or a real kill would draw a hole.</summary>
    [Test]
    [Arguments(KillModifier.Headshot)]
    [Arguments(KillModifier.NoScope)]
    [Arguments(KillModifier.Penetrate)]
    [Arguments(KillModifier.Smoke)]
    [Arguments(KillModifier.Blind)]
    [Arguments(KillModifier.InAir)]
    [Arguments(KillModifier.Suicide)]
    [Arguments(KillModifier.Domination)]
    [Arguments(KillModifier.Revenge)]
    public async Task Modifier_HasArtwork(KillModifier modifier) =>
        await Assert.That(IconCatalogue.Modifier(modifier)).IsNotNull();

    /// <summary>Every catalogued icon must actually have bytes at every advertised scale.</summary>
    [Test]
    public async Task EveryIcon_HasBytesAtEveryScale()
    {
        List<string> broken = [];
        foreach (string key in IconCatalogue.Keys)
        {
            IconRef icon = IconCatalogue.Get(key)!;
            foreach (int scale in IconCatalogue.Scales)
            {
                // A PNG is at least an 8-byte signature plus IHDR; anything tiny is a truncated bake.
                if (icon.Bytes(scale).Length < 64)
                {
                    broken.Add($"{key}@{scale}");
                }
            }
        }

        await Assert.That(broken).IsEmpty();
    }

    /// <summary>
    ///     The aspect contract. Weapons are wide and glyphs are square; a consumer that assumed one box
    ///     would squash the other, so the manifest has to carry real intrinsic sizes.
    /// </summary>
    [Test]
    public async Task Aspect_IsCarried_AndWeaponsAreWide()
    {
        IconRef awp = IconCatalogue.Weapon("awp")!;
        IconRef headshot = IconCatalogue.Modifier(KillModifier.Headshot)!;

        await Assert.That(awp.Aspect).IsGreaterThan(3.0);
        await Assert.That(headshot.Aspect).IsEqualTo(1.0).Within(0.01);
        await Assert.That(awp.WidthAt(32)).IsGreaterThan(100.0);

        // The catalogue is genuinely mixed — a large block of wide weapon silhouettes alongside a large
        // block of square glyphs — so a consumer can assume NEITHER shape. Asserted as "plenty of both"
        // rather than as a ratio: the ratio moves every time the curation grows, and the layout advice
        // does not.
        int nonSquare = IconCatalogue.Keys.Count(k => Math.Abs(IconCatalogue.Get(k)!.Aspect - 1) > 0.02);
        int square = IconCatalogue.Keys.Count - nonSquare;
        await Assert.That(nonSquare).IsGreaterThan(80);
        await Assert.That(square).IsGreaterThan(80);
    }

    /// <summary>Scale selection never upscales while a larger master exists.</summary>
    [Test]
    [Arguments(16.0, 32)]
    [Arguments(32.0, 32)]
    [Arguments(33.0, 96)]
    [Arguments(96.0, 96)]
    [Arguments(400.0, 96)]
    public async Task ScaleFor_PicksSmallestSufficient(double height, int expected) =>
        await Assert.That(IconCatalogue.ScaleFor(height)).IsEqualTo(expected);

    /// <summary>
    ///     Resource names are baked at compile time; if they ever carry a platform separator the lookup
    ///     silently misses on the other platform.
    /// </summary>
    [Test]
    public async Task ResourceNames_AreSeparatorNeutral()
    {
        IconRef icon = IconCatalogue.Weapon("ak47")!;
        await using Stream s = icon.Open(32);
        await Assert.That(s.Length).IsGreaterThan(64);
    }
}

/// <summary>
///     The Premier CS Rating emblem. Unlike the rest of the catalogue this artwork is shaded rather
///     than a flat alpha mask, so it is baked once per tier and must never be re-tinted by a consumer.
/// </summary>
public class PremierTierTests
{
    private static readonly string[] TierHexes =
        ["#b0c3d9", "#8cc6ff", "#6a7dff", "#c166ff", "#f03cff", "#eb4b4b", "#ffd700"];

    private static readonly int[] TierFloors = [0, 5000, 10000, 15000, 20000, 25000, 30000];

    /// <summary>All seven tiers, with the colours CS2's own rating_emblem.vcss defines.</summary>
    [Test]
    public async Task Tiers_MatchTheGamesOwnTable()
    {
        await Assert.That(IconCatalogue.PremierTiers.Count).IsEqualTo(7);
        await Assert.That(IconCatalogue.PremierTiers.Select(t => t.Hex))
                    .IsEquivalentTo(TierHexes);
        await Assert.That(IconCatalogue.PremierTiers.Select(t => t.MinRating))
                    .IsEquivalentTo(TierFloors);
    }

    /// <summary>
    ///     CS2 computes the tier as clamp(floor(rating / 5000), 0, 6). The boundaries are where a
    ///     mistake would hide, so they are what gets asserted.
    /// </summary>
    [Test]
    [Arguments(0, 0)]
    [Arguments(4999, 0)]
    [Arguments(5000, 1)]
    [Arguments(9999, 1)]
    [Arguments(10000, 2)]
    [Arguments(15000, 3)]
    [Arguments(19999, 3)]
    [Arguments(20000, 4)]
    [Arguments(25000, 5)]
    [Arguments(29999, 5)]
    [Arguments(30000, 6)]
    [Arguments(41234, 6)]
    public async Task TierForRating_MatchesCs2(int rating, int expected) =>
        await Assert.That(IconCatalogue.TierForRating(rating).Index).IsEqualTo(expected);

    /// <summary>A rating below the floor still gets a badge rather than an exception.</summary>
    [Test]
    public async Task TierForRating_BelowFloor_ClampsToTierZero() =>
        await Assert.That(IconCatalogue.TierForRating(-1).Index).IsEqualTo(0);

    /// <summary>Every tier, and the unranked plate, must actually have artwork at every scale.</summary>
    [Test]
    public async Task EveryTier_HasArtwork()
    {
        foreach (PremierTier tier in IconCatalogue.PremierTiers)
        {
            IconRef? icon = IconCatalogue.Get(tier.Key);
            await Assert.That(icon).IsNotNull();
            foreach (int scale in IconCatalogue.Scales)
            {
                await Assert.That(icon!.Bytes(scale).Length).IsGreaterThan(200);
            }
        }

        await Assert.That(IconCatalogue.PremierUnranked()).IsNotNull();
    }

    /// <summary>A rating resolves straight to its own emblem.</summary>
    [Test]
    public async Task PremierEmblem_ResolvesByRating()
    {
        await Assert.That(IconCatalogue.PremierEmblem(18340)!.Key).IsEqualTo("premier/tier3");
        await Assert.That(IconCatalogue.PremierEmblem(31004)!.Key).IsEqualTo("premier/tier6");
    }

    /// <summary>
    ///     The emblem is 2.5:1 in CS2's own stylesheet (height 26px, width height-percentage(250%)) and
    ///     roughly 2.78:1 in the source artwork it draws. Either way it is emphatically not square, and a
    ///     consumer that boxed it would crush it.
    /// </summary>
    [Test]
    public async Task Emblem_IsWide() =>
        await Assert.That(IconCatalogue.PremierEmblem(0)!.Aspect).IsGreaterThan(2.0);
}

/// <summary>
///     The classic Skill Group badges, Competitive and Wingman, and the names CS2 gives them.
/// </summary>
public class RankBadgeTests
{
    /// <summary>Every rank 0–18 has a badge in both sets, or a scoreboard would show a hole.</summary>
    [Test]
    public async Task EveryRank_HasABadgeInBothSets()
    {
        for (int rank = 0; rank <= IconCatalogue.MaxRank; rank++)
        {
            await Assert.That(IconCatalogue.CompetitiveRank(rank)).IsNotNull().Because($"competitive {rank}");
            await Assert.That(IconCatalogue.WingmanRank(rank)).IsNotNull().Because($"wingman {rank}");
        }
    }

    /// <summary>A rank the catalogue has no badge for is a quiet null, never a throw.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(19)]
    [Arguments(99)]
    public async Task RanksOutOfRange_AreNull(int rank)
    {
        await Assert.That(IconCatalogue.CompetitiveRank(rank)).IsNull();
        await Assert.That(IconCatalogue.WingmanRank(rank)).IsNull();
    }

    /// <summary>The states a player can be in without a rank each have their own badge.</summary>
    [Test]
    public async Task UnrankedStates_HaveBadges()
    {
        await Assert.That(IconCatalogue.UnrankedBadge()!.Key).IsEqualTo("rank/competitive/none");
        await Assert.That(IconCatalogue.UnrankedBadge(expired: true)!.Key).IsEqualTo("rank/competitive/expired");
        await Assert.That(IconCatalogue.UnrankedBadge(wingman: true)!.Key).IsEqualTo("rank/wingman/none");
        await Assert.That(IconCatalogue.UnrankedBadge(expired: true, wingman: true)!.Key)
                    .IsEqualTo("rank/wingman/expired");
        await Assert.That(IconCatalogue.NeedsWinsBadge()).IsNotNull();
    }

    /// <summary>Names come from CS2's localization, so the ends of the ladder are worth pinning.</summary>
    [Test]
    public async Task RankNames_ComeFromTheGame()
    {
        await Assert.That(IconCatalogue.RankNames.Count).IsEqualTo(19);
        await Assert.That(IconCatalogue.RankName(0)).IsEqualTo("No Skill Group");
        await Assert.That(IconCatalogue.RankName(1)).IsEqualTo("Silver I");
        await Assert.That(IconCatalogue.RankName(11)).IsEqualTo("Master Guardian I");
        await Assert.That(IconCatalogue.RankName(18)).IsEqualTo("The Global Elite");
        await Assert.That(IconCatalogue.RankName(19)).IsEmpty();
        await Assert.That(IconCatalogue.RankName(-1)).IsEmpty();
    }

    /// <summary>
    ///     Badges are pictures. A consumer that tinted one would render The Global Elite as a flat blob,
    ///     so the flag that stops it is asserted rather than trusted.
    /// </summary>
    [Test]
    public async Task Badges_AreNotTintable()
    {
        await Assert.That(IconCatalogue.CompetitiveRank(18)!.Tintable).IsFalse();
        await Assert.That(IconCatalogue.WingmanRank(7)!.Tintable).IsFalse();
        await Assert.That(IconCatalogue.UnrankedBadge()!.Tintable).IsFalse();
        await Assert.That(IconCatalogue.PremierEmblem(31000)!.Tintable).IsFalse();
        await Assert.That(IconCatalogue.PremierUnranked()!.Tintable).IsFalse();
    }

    /// <summary>
    ///     The flag has to discriminate, not just be false everywhere. Weapons and kill modifiers are
    ///     hue-free and must stay tintable, or they would draw white-on-white in the light theme.
    /// </summary>
    [Test]
    public async Task HueFreeArtwork_StaysTintable()
    {
        await Assert.That(IconCatalogue.Weapon("ak47")!.Tintable).IsTrue();
        await Assert.That(IconCatalogue.Modifier(KillModifier.Headshot)!.Tintable).IsTrue();
        await Assert.That(IconCatalogue.Modifier(KillModifier.InAir)!.Tintable).IsTrue();
        await Assert.That(IconCatalogue.Ui("bomb_c4")!.Tintable).IsTrue();

        // …and the genuinely coloured ones are excluded.
        await Assert.That(IconCatalogue.Ui("defuser")!.Tintable).IsFalse();
        await Assert.That(IconCatalogue.Ui("mvp")!.Tintable).IsFalse();
    }
}

/// <summary>Per-map icons, keyed by the map name a demo already carries.</summary>
public class MapIconTests
{
    /// <summary>The maps the app ships baked bundles for must all resolve.</summary>
    [Test]
    [Arguments("de_dust2")]
    [Arguments("de_mirage")]
    [Arguments("de_inferno")]
    [Arguments("de_nuke")]
    [Arguments("de_overpass")]
    [Arguments("de_vertigo")]
    [Arguments("de_ancient")]
    [Arguments("de_anubis")]
    [Arguments("de_cache")]
    public async Task ActiveDutyMaps_HaveAnIcon(string map) =>
        await Assert.That(IconCatalogue.Map(map)).IsNotNull();

    /// <summary>A community map is a quiet null, not a throw — callers fall back to a name.</summary>
    [Test]
    public async Task UnknownMaps_AreNull()
    {
        await Assert.That(IconCatalogue.Map("de_notarealmap")).IsNull();
        await Assert.That(IconCatalogue.Map(null)).IsNull();
        await Assert.That(IconCatalogue.Map("")).IsNull();
    }

    /// <summary>Map icons are full-colour renders, so they must never be tinted.</summary>
    [Test]
    public async Task MapIcons_AreNotTintable() =>
        await Assert.That(IconCatalogue.Map("de_dust2")!.Tintable).IsFalse();
}

#region

using System.Globalization;
using DemoViewer.NET.Controls;
using DemoViewer.NET.GameIcons;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The two ways a view reaches CS2's weapon artwork: a demo's own weapon string, and an entity's
///     schema class name.
/// </summary>
[NotInParallel] // MissingKeyObserver is one global hook; two tests swapping it at once is a race.
public class WeaponIconKeyTests
{
    /// <summary>
    ///     The entity list's mapping. The prefix rule carries most of CS2's class vocabulary on its own;
    ///     these are the shapes it has to cover, irregulars included.
    /// </summary>
    [Test]
    [Arguments("CAK47", "equipment/ak47")]
    [Arguments("CWeaponAWP", "equipment/awp")]
    [Arguments("CDEagle", "equipment/deagle")]
    [Arguments("CWeaponGalilAR", "equipment/galilar")]
    [Arguments("CWeaponSSG08", "equipment/ssg08")]
    [Arguments("CWeaponM4A1", "equipment/m4a1")]
    [Arguments("CWeaponCZ75a", "equipment/cz75a")]
    [Arguments("CWeaponHKP2000", "equipment/hkp2000")]
    [Arguments("CSmokeGrenade", "equipment/smokegrenade")]
    [Arguments("CFlashbang", "equipment/flashbang")]
    [Arguments("CHEGrenade", "equipment/hegrenade")]
    [Arguments("CKnife", "equipment/knife")]
    [Arguments("CC4", "equipment/c4")]
    [Arguments("CWeaponUSPSilencer", "equipment/usp_silencer")]
    [Arguments("CMolotovGrenade", "equipment/molotov")]
    [Arguments("CDecoyGrenade", "equipment/decoy")]
    // CInferno is the burning-ground entity and inferno IS the molotov's own artwork, so it resolves.
    [Arguments("CInferno", "equipment/inferno")]
    public async Task ForEntityClass_ResolvesWeaponClasses(string className, string expectedKey)
    {
        IconRef? icon = WeaponIconKey.ForEntityClass(className);
        await Assert.That(icon).IsNotNull();
        await Assert.That(icon!.Key).IsEqualTo(expectedKey);
    }

    /// <summary>A player, a fog volume and a rules proxy are not weapons and must leave the gutter empty.</summary>
    [Test]
    [Arguments("CCSPlayerPawn")]
    [Arguments("CCSGameRulesProxy")]
    [Arguments("CEnvFogController")]
    [Arguments("")]
    [Arguments(null)]
    public async Task ForEntityClass_NonWeapons_AreNull(string? className) =>
        await Assert.That(WeaponIconKey.ForEntityClass(className)).IsNull();

    /// <summary>
    ///     The converter COMPOSES and never resolves. It must hand back a key even for a weapon with no
    ///     artwork, because only the catalogue can say whether that absence is CS2's own answer or a gap
    ///     — and collapsing the two here is exactly the bug the pattern exists to prevent.
    /// </summary>
    [Test]
    public async Task Key_ComposesWithoutResolving()
    {
        await Assert.That(Convert(WeaponIconKey.Key, "ak47")).IsEqualTo("equipment/ak47");
        await Assert.That(Convert(WeaponIconKey.Key, "world")).IsEqualTo("equipment/world");
        await Assert.That(Convert(WeaponIconKey.Key, "not_a_weapon")).IsEqualTo("equipment/not_a_weapon");
        await Assert.That(Convert(WeaponIconKey.Key, "")).IsNull();
        await Assert.That(Convert(WeaponIconKey.Key, null)).IsNull();
    }

    /// <summary>And the catalogue classifies those two absences differently, which is the whole point.</summary>
    [Test]
    public async Task TheCatalogue_SeparatesBlankFromMissing()
    {
        IconCatalogue.Demand("equipment/ak47", out IconAvailability ok);
        IconCatalogue.Demand("equipment/world", out IconAvailability blank);
        IconCatalogue.Demand("equipment/not_a_weapon", out IconAvailability missing);
        IconCatalogue.Demand(null, out IconAvailability none);

        await Assert.That(ok).IsEqualTo(IconAvailability.Available);
        await Assert.That(blank).IsEqualTo(IconAvailability.Blank);
        await Assert.That(missing).IsEqualTo(IconAvailability.Missing);
        await Assert.That(none).IsEqualTo(IconAvailability.None);
    }

    /// <summary>A miss is reported once per distinct key, however many frames draw it.</summary>
    [Test]
    public async Task AMissingKey_IsReportedOnce()
    {
        List<string> seen = [];
        Action<string>? previous = IconCatalogue.MissingKeyObserver;
        IconCatalogue.MissingKeyObserver = seen.Add;
        try
        {
            string key = "ui/never_baked_" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < 50; i++)
            {
                IconCatalogue.Demand(key, out _);
            }

            await Assert.That(seen.Count(k => k == key)).IsEqualTo(1);
            await Assert.That(IconCatalogue.MissingKeys).Contains(key);
        }
        finally
        {
            IconCatalogue.MissingKeyObserver = previous;
        }
    }

    /// <summary>Blank keys are never reported: the game means them, so there is nothing to fix.</summary>
    [Test]
    public async Task ABlankKey_IsNeverReported()
    {
        List<string> seen = [];
        Action<string>? previous = IconCatalogue.MissingKeyObserver;
        IconCatalogue.MissingKeyObserver = seen.Add;
        try
        {
            IconCatalogue.Demand("equipment/world", out _);
            await Assert.That(seen).IsEmpty();
            await Assert.That(IconCatalogue.IsBlank("equipment/world")).IsTrue();
        }
        finally
        {
            IconCatalogue.MissingKeyObserver = previous;
        }
    }

    private static object? Convert(WeaponIconKey converter, string? value) =>
        converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);
}

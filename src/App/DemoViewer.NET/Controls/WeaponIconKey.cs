#region

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using DemoViewer.NET.GameIcons;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Turns a demo's <c>player_death.weapon</c> string into an icon key for <see cref="GameIcon" />.
///     <para>
///         <b>It composes; it does not resolve.</b> <see cref="Key" /> returns
///         <c>equipment/&lt;weapon&gt;</c> unconditionally and lets the catalogue classify it, because
///         resolving here would collapse two different absences into one null: CS2's deliberately empty
///         environment-death artwork (draw nothing, say nothing) and a weapon this build has never heard
///         of (draw the name, report it once). Only <see cref="GameIcon" /> can tell those apart, and only
///         if it is handed the key rather than the answer.
///     </para>
///     <para>
///         <see cref="EntityClass" /> is the exception and still resolves, because it answers a different
///         question — "is this entity class a weapon at all?" — and a <c>CCSPlayerPawn</c> must not be
///         recorded as a missing icon.
///     </para>
/// </summary>
public sealed class WeaponIconKey : IValueConverter
{
    /// <summary>Weapon string → <c>equipment/&lt;weapon&gt;</c>. Never resolves; the catalogue does that.</summary>
    public static readonly WeaponIconKey Key = new(fromEntityClass: false);

    /// <summary>
    ///     Entity class name → icon key, for the entity list. <c>CAK47</c> and <c>CWeaponAWP</c> are the
    ///     same weapon as <c>ak47</c> and <c>awp</c>; a non-weapon class simply resolves to nothing.
    /// </summary>
    public static readonly WeaponIconKey EntityClass = new(fromEntityClass: true);

    // Where stripping "C"/"CWeapon" and lower-casing does not land on the catalogue's spelling. Small
    // and explicit on purpose: a table that tried to list every weapon would rot against CS2 updates,
    // whereas the prefix rule keeps working and only the genuine irregulars need naming.
    private static readonly Dictionary<string, string> ClassAliases = new(StringComparer.Ordinal)
    {
        ["uspsilencer"] = "usp_silencer",
        ["m4a1silencer"] = "m4a1_silencer",
        ["molotovgrenade"] = "molotov",
        ["incendiarygrenade"] = "incgrenade",
        ["decoygrenade"] = "decoy",
        ["knifegg"] = "knifegg",
        ["planted_c4"] = "planted_c4",
        ["plantedc4"] = "planted_c4"
    };

    private readonly bool _fromEntityClass;

    private WeaponIconKey(bool fromEntityClass) => _fromEntityClass = fromEntityClass;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (_fromEntityClass)
        {
            return ForEntityClass(value as string)?.Key;
        }

        return value is string weapon && weapon.Length > 0 ? "equipment/" + weapon : null;
    }

    /// <summary>
    ///     Resolves a CS2 entity class name to weapon artwork, or null when the class is not a weapon.
    ///     <para>
    ///         The rule is the prefix rule — drop a leading <c>C</c>, then a leading <c>Weapon</c>, and
    ///         lower-case what is left — which lands <c>CAK47</c>, <c>CWeaponAWP</c>, <c>CDEagle</c>,
    ///         <c>CWeaponGalilAR</c> and <c>CSmokeGrenade</c> straight onto their catalogue names. Only
    ///         the handful it cannot reach are aliased.
    ///     </para>
    /// </summary>
    /// <param name="className">The entity's schema class name, e.g. <c>CWeaponAWP</c>.</param>
    public static IconRef? ForEntityClass(string? className)
    {
        if (string.IsNullOrEmpty(className) || className[0] is not 'C')
        {
            return null;
        }

        string bare = className[1..];
        if (bare.StartsWith("Weapon", StringComparison.Ordinal))
        {
            bare = bare["Weapon".Length..];
        }

        bare = bare.ToLowerInvariant();
        return IconCatalogue.Weapon(ClassAliases.GetValueOrDefault(bare, bare));
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("icon keys are display-only");
}

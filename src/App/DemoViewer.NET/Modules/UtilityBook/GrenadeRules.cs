#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The named thresholds and the pure classification steps of the grenade walk (grenade-walk.md §3.5,
///     D5). Every number here was measured on Valve matchmaking demos (§2.4) and sits beside its reason, so
///     moving one is an edit to a constant rather than a re-walk: the rows keep the raw speed and strength.
/// </summary>
public static class GrenadeRules
{
    /// <summary>Release to projectile creation, median over 620 throws (min 6, max 15).</summary>
    public const int ReleaseToSpawnTicks = 7;

    /// <summary>The shortest measured release-to-spawn gap.</summary>
    public const int ReleaseToSpawnMinTicks = 6;

    /// <summary>The longest measured release-to-spawn gap.</summary>
    public const int ReleaseToSpawnMaxTicks = 15;

    /// <summary>How far before the spawn a resolved thrower's <c>weapon_fire</c> may sit.</summary>
    public const int ReleaseLookbackTicks = 40;

    /// <summary>A jump press counts when it rose this many ticks before the spawn: 20 before the release plus the 7-tick gap.</summary>
    public const int JumpWindowTicks = 27;

    /// <summary>The speed window before the release.</summary>
    public const int SpeedWindowTicks = 8;

    /// <summary>Under this, units per second, the throw is standing.</summary>
    public const float StationaryMaxSpeed = 10;

    /// <summary>Under this the throw is walking; CS2 walks a grenade at 127 u/s and runs it at 245.</summary>
    public const float WalkingMaxSpeed = 140;

    /// <summary>Left click reads 1.00.</summary>
    public const float FullStrengthMin = 0.95f;

    /// <summary>Both buttons read about 0.5.</summary>
    public const float HalfStrengthMin = 0.35f;

    /// <summary>Both buttons read about 0.5.</summary>
    public const float HalfStrengthMax = 0.65f;

    /// <summary>Right click reads 0.00.</summary>
    public const float UnderhandMax = 0.05f;

    /// <summary>A fire's <c>inferno_startburn</c> falls within this many ticks of the molotov's last sample.</summary>
    public const int InfernoJoinTicks = 4;

    /// <summary>Movement below this, world units, is not movement (a resting projectile is not re-sent).</summary>
    public const float MovedEpsilon = 0.1f;

    /// <summary><c>FL_ONGROUND</c> in <c>m_fFlags</c>.</summary>
    public const uint OnGroundFlag = 1;

    /// <summary>
    ///     <c>FL_DUCKING</c> in <c>m_fFlags</c>: the crouch read. <c>m_pMovementServices.m_bDucked</c> decodes on
    ///     GOTV but stayed false on every pawn sample of a real demo, while this bit followed a full
    ///     <c>m_flDuckAmount</c> on 58 of 59.
    /// </summary>
    public const uint DuckingFlag = 2;

    /// <summary><c>IN_JUMP</c> in <c>buttonstate1</c> and in a sub-tick move's <c>button</c>.</summary>
    public const ulong JumpButton = 1UL << 1;

    // weapon_fire's weapon, with or without the weapon_ prefix, to the projectile family it throws.
    private static readonly Dictionary<string, string> _weaponFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smokegrenade"] = GrenadeProjectileClasses.Smoke,
        ["flashbang"] = GrenadeProjectileClasses.Flashbang,
        ["hegrenade"] = GrenadeProjectileClasses.HEGrenade,
        ["molotov"] = GrenadeProjectileClasses.Molotov,
        ["incgrenade"] = GrenadeProjectileClasses.Molotov,
        ["decoy"] = GrenadeProjectileClasses.Decoy
    };

    // The held weapons whose m_flThrowStrength means anything.
    private static readonly HashSet<string> _grenadeWeaponClasses = new(StringComparer.Ordinal)
    {
        "CSmokeGrenade", "CFlashbang", "CHEGrenade", "CMolotovGrenade", "CIncendiaryGrenade", "CDecoyGrenade"
    };

    /// <summary>The held-weapon classes a throw strength is read from.</summary>
    public static IReadOnlyCollection<string> GrenadeWeaponClasses => _grenadeWeaponClasses;

    /// <summary>The projectile class a grenade <c>weapon_fire</c> produces, or null for any other weapon.</summary>
    /// <param name="weapon"><c>weapon_fire.weapon</c>, e.g. <c>weapon_smokegrenade</c>.</param>
    public static string? ProjectileClassOfWeapon(string? weapon)
    {
        if (string.IsNullOrEmpty(weapon))
        {
            return null;
        }

        string bare = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
        return _weaponFamilies.GetValueOrDefault(bare);
    }

    /// <summary>Whether a held weapon's class is a grenade.</summary>
    public static bool IsGrenadeWeaponClass(string? className) =>
        className is not null && _grenadeWeaponClasses.Contains(className);

    /// <summary>
    ///     The row's kind. A molotov-class projectile is an incendiary when the joined <c>weapon_fire</c>
    ///     says so, else when the entity's <c>m_bIsIncGrenade</c> does; null for a class outside the five.
    /// </summary>
    /// <param name="className">One of <see cref="GrenadeProjectileClasses" />.</param>
    /// <param name="weapon">The joined <c>weapon_fire.weapon</c>, or null.</param>
    /// <param name="isIncendiary">The entity's <c>m_bIsIncGrenade</c>, or null when unread.</param>
    public static GrenadeKind? KindOf(string className, string? weapon, bool? isIncendiary)
    {
        switch (className)
        {
            case GrenadeProjectileClasses.Smoke:
                return GrenadeKind.Smoke;
            case GrenadeProjectileClasses.Flashbang:
                return GrenadeKind.Flash;
            case GrenadeProjectileClasses.HEGrenade:
                return GrenadeKind.He;
            case GrenadeProjectileClasses.Decoy:
                return GrenadeKind.Decoy;
            case GrenadeProjectileClasses.Molotov:
                if (weapon is not null && weapon.EndsWith("incgrenade", StringComparison.OrdinalIgnoreCase))
                {
                    return GrenadeKind.Incendiary;
                }

                if (weapon is not null && weapon.EndsWith("molotov", StringComparison.OrdinalIgnoreCase))
                {
                    return GrenadeKind.Molotov;
                }

                return isIncendiary == true ? GrenadeKind.Incendiary : GrenadeKind.Molotov;
            default:
                return null;
        }
    }

    /// <summary>Stationary under <see cref="StationaryMaxSpeed" />, walking under <see cref="WalkingMaxSpeed" />, else running.</summary>
    /// <param name="speed">Horizontal units per second, or null when unread.</param>
    public static MovementClass ClassifyMovement(float? speed) => speed switch
    {
        null => MovementClass.Unknown,
        < StationaryMaxSpeed => MovementClass.Stationary,
        < WalkingMaxSpeed => MovementClass.Walking,
        _ => MovementClass.Running
    };

    /// <summary>The strength word; Unknown when unread or when the held weapon was not a grenade.</summary>
    /// <param name="strength">The held weapon's <c>m_flThrowStrength</c>.</param>
    /// <param name="heldIsGrenade">Whether the held weapon at the release was a grenade class.</param>
    public static ThrowStrengthClass ClassifyStrength(float? strength, bool heldIsGrenade)
    {
        if (strength is not { } s || !heldIsGrenade)
        {
            return ThrowStrengthClass.Unknown;
        }

        return s switch
        {
            >= FullStrengthMin => ThrowStrengthClass.Full,
            <= UnderhandMax => ThrowStrengthClass.Underhand,
            >= HalfStrengthMin and <= HalfStrengthMax => ThrowStrengthClass.Half,
            _ => ThrowStrengthClass.Other
        };
    }

    /// <summary>
    ///     Jump-throw per §3.5: the inputs decide when they cover the window; else the ground flag on the
    ///     spawn frame (not the release one: 6 of 20 measured binds press jump after the attack release);
    ///     else nothing says it is one.
    /// </summary>
    /// <param name="inputsCover">At least one of the thrower's commands decoded in the window.</param>
    /// <param name="jumpPressTick">The latest rising jump in the window, or null.</param>
    /// <param name="onGroundAtSpawn">The thrower's ground flag on the spawn frame, or null when no pawn was read.</param>
    public static (bool JumpThrow, JumpThrowSource Source, int? PressTick) ClassifyJumpThrow(
        bool inputsCover, int? jumpPressTick, bool? onGroundAtSpawn)
    {
        if (inputsCover)
        {
            return (jumpPressTick is not null, JumpThrowSource.Inputs, jumpPressTick);
        }

        return onGroundAtSpawn is { } grounded
            ? (!grounded, JumpThrowSource.GroundFlag, null)
            : (false, JumpThrowSource.None, null);
    }

    /// <summary>Horizontal speed in units per second between two pawn reads, or null when either is missing.</summary>
    /// <param name="from">The earlier position.</param>
    /// <param name="fromTick">Its tick.</param>
    /// <param name="to">The later position.</param>
    /// <param name="toTick">Its tick.</param>
    /// <param name="tickRate">Ticks per second.</param>
    public static float? HorizontalSpeed(Vector3? from, int fromTick, Vector3? to, int toTick, int tickRate)
    {
        if (from is not { } a || to is not { } b || toTick <= fromTick || tickRate <= 0)
        {
            return null;
        }

        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy) * tickRate / (toTick - fromTick);
    }

    /// <summary>The number of the last round starting at or before <paramref name="tick" />, or 0.</summary>
    /// <param name="rounds">Rounds in start order.</param>
    /// <param name="tick">Frame clock.</param>
    public static int RoundAt(IReadOnlyList<ClipRound> rounds, int tick)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        int number = 0;
        foreach (ClipRound round in rounds)
        {
            if (round.StartTickFrameClock > tick)
            {
                break;
            }

            number = round.Number;
        }

        return number;
    }
}

/// <summary>The practice-server console line for a throw (grenade-walk.md §3.6), built on demand so the format can change without a schema bump.</summary>
public static class GrenadeConsole
{
    /// <summary>
    ///     <c>setpos x y z; setang pitch yaw 0.00</c> at two decimals in the invariant culture, from the
    ///     release position and eye angles; null when the release state was not read.
    /// </summary>
    /// <param name="row">The grenade.</param>
    public static string? Format(GrenadeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ReleasePosition is not { } p || row.ReleaseEyePitch is not { } pitch || row.ReleaseEyeYaw is not { } yaw)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"setpos {p.X:0.00} {p.Y:0.00} {p.Z:0.00}; setang {pitch:0.00} {yaw:0.00} 0.00");
    }
}

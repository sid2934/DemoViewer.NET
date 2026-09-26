#region

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>What was thrown. Molotov and incendiary share one projectile class and are told apart here.</summary>
public enum GrenadeKind
{
    Smoke,
    Molotov,
    Incendiary,
    He,
    Flash,
    Decoy
}

/// <summary>Where a row's thrower came from.</summary>
public enum ThrowerSource
{
    /// <summary>The projectile's own <c>m_hThrower</c> chain, as <c>ProjectileSampler</c> resolved it.</summary>
    Entity,

    /// <summary>The chain never resolved (the smoke decode defect, CS2DemoKit #56); the matching <c>weapon_fire</c> named it.</summary>
    WeaponFire,

    /// <summary>Neither: the row has no thrower.</summary>
    None
}

/// <summary>Where a row's release tick came from.</summary>
public enum ReleaseSource
{
    /// <summary>The thrower's grenade <c>weapon_fire</c>, 6 to 15 ticks before the projectile appeared.</summary>
    WeaponFire,

    /// <summary><c>grenade_thrown</c>, which only HLTV broadcasts carry.</summary>
    GrenadeThrown,

    /// <summary>No event: the measured seven ticks before the projectile appeared.</summary>
    SpawnOffset
}

/// <summary>What decided <see cref="GrenadeRow.JumpThrow" />.</summary>
public enum JumpThrowSource
{
    /// <summary>The thrower's decoded commands covered the jump window.</summary>
    Inputs,

    /// <summary>No commands decoded there: the thrower's ground flag on the frame the projectile appeared.</summary>
    GroundFlag,

    /// <summary>No thrower pawn to read: the row says it is not a jump-throw because nothing says it is.</summary>
    None
}

/// <summary>The movement word a lineup card prints, from <see cref="GrenadeRow.SpeedAtRelease" />.</summary>
public enum MovementClass
{
    Stationary,
    Walking,
    Running,

    /// <summary>The thrower's positions around the release were not both read.</summary>
    Unknown
}

/// <summary>Left click, both buttons, right click, from the held grenade's <c>m_flThrowStrength</c>.</summary>
public enum ThrowStrengthClass
{
    Full,
    Half,
    Underhand,
    Other,

    /// <summary>No strength read, or the held weapon at the release was not a grenade (a fast switch).</summary>
    Unknown
}

/// <summary>Where a row's detonation came from.</summary>
public enum DetonationSource
{
    /// <summary>The kind's detonation event, joined by entity index (or by tick for a fire).</summary>
    Event,

    /// <summary>The projectile's own effect-tick field, when the event is missing.</summary>
    Entity,

    /// <summary>An HE or a flash with neither: the last position the projectile was seen at.</summary>
    LastSample,

    /// <summary>It did not go off, as far as the demo says.</summary>
    None
}

/// <summary>How a projectile's life ended.</summary>
public enum GrenadeEndKind
{
    /// <summary>A detonation was found.</summary>
    Detonated,

    /// <summary>The entity was removed with no detonation found.</summary>
    Removed,

    /// <summary>Still in the world when the walk stopped.</summary>
    DemoEnded
}

/// <summary>
///     A world position that serializes (a <see cref="Vector3" /> has fields, not properties, so the JSON
///     writer would print it as <c>{}</c>). Written as <c>[x, y, z]</c> at two decimals.
/// </summary>
/// <param name="X">World units.</param>
/// <param name="Y">World units.</param>
/// <param name="Z">World units.</param>
[JsonConverter(typeof(WorldPointConverter))]
public readonly record struct WorldPoint(float X, float Y, float Z)
{
    public static WorldPoint From(Vector3 v) => new(v.X, v.Y, v.Z);

    public Vector3 ToVector() => new(X, Y, Z);
}

/// <summary>One trajectory vertex: frame clock, world position, and the bounce count when it was taken.</summary>
/// <param name="Tick">Frame clock.</param>
/// <param name="X">World units.</param>
/// <param name="Y">World units.</param>
/// <param name="Z">World units.</param>
/// <param name="Bounce"><c>m_nBounces</c> at this vertex; a change from the previous vertex marks a bounce.</param>
[JsonConverter(typeof(TrajectoryPointConverter))]
public readonly record struct TrajectoryPoint(int Tick, float X, float Y, float Z, int Bounce)
{
    public Vector3 Position => new(X, Y, Z);
}

/// <summary>
///     One grenade, from the hand to where it went off (grenade-walk.md §3.4). Every tick is the frame
///     clock and every position is in world units. A field the demo did not carry is null rather than zero:
///     the card prints "release state unavailable", never a confident wrong number.
/// </summary>
public sealed class GrenadeRow
{
    /// <summary><c>g{index}-{serial}</c>: unique within a demo; the cross-demo key is the demo's hash plus this.</summary>
    public string Id { get; set; } = "";

    public GrenadeKind Kind { get; set; }

    /// <summary>The thrower's player slot, or -1 when <see cref="ThrowerSource" /> is None.</summary>
    public int ThrowerSlot { get; set; } = -1;

    public string? ThrowerSteamId64 { get; set; }

    /// <summary>2 = T, 3 = CT, 0 when the thrower's pawn was not read.</summary>
    public int ThrowerTeam { get; set; }

    public ThrowerSource ThrowerSource { get; set; }

    /// <summary>The <c>ClipRound</c> the throw fell in; 0 before the first freeze end.</summary>
    public int RoundNumber { get; set; }

    public int ReleaseTick { get; set; }

    public ReleaseSource ReleaseSource { get; set; }

    /// <summary>The thrower's pawn origin at the release: the <c>setpos</c>.</summary>
    public WorldPoint? ReleasePosition { get; set; }

    /// <summary><c>m_angEyeAngles</c> pitch at the release: the <c>setang</c> pitch.</summary>
    public float? ReleaseEyePitch { get; set; }

    /// <summary><c>m_angEyeAngles</c> yaw at the release: the <c>setang</c> yaw.</summary>
    public float? ReleaseEyeYaw { get; set; }

    public bool? ReleaseOnGround { get; set; }

    public bool? ReleaseCrouched { get; set; }

    /// <summary>The held grenade's <c>m_flThrowStrength</c> at the release.</summary>
    public float? ThrowStrength { get; set; }

    public ThrowStrengthClass ThrowStrengthClass { get; set; } = ThrowStrengthClass.Unknown;

    public MovementClass Movement { get; set; } = MovementClass.Unknown;

    /// <summary>Horizontal speed over the ticks before the release, units per second; kept so the thresholds can move without a re-walk.</summary>
    public float? SpeedAtRelease { get; set; }

    /// <summary>The frame the projectile appeared on.</summary>
    public int SpawnTick { get; set; }

    /// <summary><c>m_vInitialPosition</c>.</summary>
    public WorldPoint? SpawnPosition { get; set; }

    /// <summary><c>m_vInitialVelocity</c>.</summary>
    public WorldPoint? SpawnVelocity { get; set; }

    public WorldPoint? ThrowerPositionAtSpawn { get; set; }

    /// <summary>The ground-flag fallback's input: the flag on the spawn frame, not the release one (§2.4).</summary>
    public bool? ThrowerOnGroundAtSpawn { get; set; }

    public bool JumpThrow { get; set; }

    public JumpThrowSource JumpThrowSource { get; set; } = JumpThrowSource.None;

    /// <summary>The tick the jump rose, when <see cref="JumpThrowSource" /> is Inputs and one did.</summary>
    public int? JumpPressTick { get; set; }

    /// <summary>
    ///     The flight at the configured stride with every bounce vertex kept; first the spawn point, last
    ///     where it came to rest or went off. Written to the paths sibling, never to the rows file.
    /// </summary>
    [JsonIgnore]
    public List<TrajectoryPoint> Trajectory { get; set; } = [];

    public int BounceCount { get; set; }

    /// <summary>Spawn to the last tick the projectile moved, or to the detonation if that came first.</summary>
    public int AirTimeTicks { get; set; }

    public int? DetonationTick { get; set; }

    public WorldPoint? DetonationPosition { get; set; }

    public DetonationSource DetonationSource { get; set; } = DetonationSource.None;

    /// <summary>The last tick the projectile was seen, or the removal frame's tick.</summary>
    public int EndTick { get; set; }

    public GrenadeEndKind EndKind { get; set; }

    /// <summary>A molotov's <c>inferno_startburn</c> entity index, for a later fire-area join.</summary>
    public int? InfernoEntityIndex { get; set; }

    /// <summary>Reserved for Zone Baking's resolver at Grenade Index time; the walk leaves it null.</summary>
    public string? LandingPlace { get; set; }
}

/// <summary>Writes a <see cref="WorldPoint" /> as <c>[x, y, z]</c> at two decimals.</summary>
public sealed class WorldPointConverter : JsonConverter<WorldPoint>
{
    public override WorldPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        float[] values = JsonNumbers.ReadArray(ref reader, 3);
        return new WorldPoint(values[0], values[1], values[2]);
    }

    public override void Write(Utf8JsonWriter writer, WorldPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        JsonNumbers.Write(writer, value.X, 2);
        JsonNumbers.Write(writer, value.Y, 2);
        JsonNumbers.Write(writer, value.Z, 2);
        writer.WriteEndArray();
    }
}

/// <summary>Writes a <see cref="TrajectoryPoint" /> as <c>[tick, x, y, z, bounce]</c>, positions at one decimal (about 30 bytes).</summary>
public sealed class TrajectoryPointConverter : JsonConverter<TrajectoryPoint>
{
    public override TrajectoryPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        float[] values = JsonNumbers.ReadArray(ref reader, 5);
        return new TrajectoryPoint((int)values[0], values[1], values[2], values[3], (int)values[4]);
    }

    public override void Write(Utf8JsonWriter writer, TrajectoryPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Tick);
        JsonNumbers.Write(writer, value.X, 1);
        JsonNumbers.Write(writer, value.Y, 1);
        JsonNumbers.Write(writer, value.Z, 1);
        writer.WriteNumberValue(value.Bounce);
        writer.WriteEndArray();
    }
}

internal static class JsonNumbers
{
    public static void Write(Utf8JsonWriter writer, float value, int decimals) =>
        writer.WriteRawValue(Math.Round((double)value, decimals, MidpointRounding.AwayFromZero)
            .ToString("0.##", CultureInfo.InvariantCulture));

    public static float[] ReadArray(ref Utf8JsonReader reader, int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("expected an array");
        }

        float[] values = new float[count];
        int i = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (i >= count)
            {
                throw new JsonException($"expected {count} numbers");
            }

            values[i++] = (float)reader.GetDouble();
        }

        if (i != count)
        {
            throw new JsonException($"expected {count} numbers");
        }

        return values;
    }
}

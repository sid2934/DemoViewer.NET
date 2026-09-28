#region

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The per-demo throw log (grenades-v2.md §3): every field of every <see cref="GrenadeRow" /> except the
///     trajectory, binary and gzipped. A JSON header carries the document's walker, demo, clock and source;
///     strings (ids, SteamIDs, names, places) go through one table. Positions are hundredths of a unit, the
///     precision the JSON rows kept; angles, speed and strength are full floats.
/// </summary>
public static class GrenadeThrowLog
{
    /// <summary>The sibling suffix.</summary>
    public const string Suffix = ".grenades.log.gz";

    private const int Version = 1;
    private static readonly byte[] Magic = "DVGL"u8.ToArray();

    [Flags]
    private enum Has : uint
    {
        SteamId = 1 << 0,
        Name = 1 << 1,
        ReleasePosition = 1 << 2,
        Pitch = 1 << 3,
        Yaw = 1 << 4,
        OnGround = 1 << 5,
        Crouched = 1 << 6,
        Strength = 1 << 7,
        Speed = 1 << 8,
        SpawnPosition = 1 << 9,
        SpawnVelocity = 1 << 10,
        ThrowerAtSpawn = 1 << 11,
        GroundAtSpawn = 1 << 12,
        JumpPress = 1 << 13,
        DetonationTick = 1 << 14,
        DetonationPosition = 1 << 15,
        Inferno = 1 << 16,
        Place = 1 << 17,
        OnGroundValue = 1 << 24,
        CrouchedValue = 1 << 25,
        GroundAtSpawnValue = 1 << 26,
        JumpThrow = 1 << 27,

        // A coordinate hundredths cannot hold (non-finite, beyond 20 million units, negative zero): this row's points are floats.
        RawPoints = 1 << 28
    }

    /// <summary>The gzipped log of <paramref name="document" />.</summary>
    public static byte[] Encode(GrenadeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<string> strings = [];
        Dictionary<string, int> index = new(StringComparer.Ordinal);

        int Str(string value)
        {
            if (!index.TryGetValue(value, out int i))
            {
                i = strings.Count;
                strings.Add(value);
                index[value] = i;
            }

            return i;
        }

        using MemoryStream rowsStream = new();
        using (BinaryWriter w = new(rowsStream, Encoding.UTF8, true))
        {
            w.Write7BitEncodedInt(document.Grenades.Count);
            foreach (GrenadeRow r in document.Grenades)
            {
                Has has = 0;
                Set(ref has, Has.SteamId, r.ThrowerSteamId64 is not null);
                Set(ref has, Has.Name, r.ThrowerName is not null);
                Set(ref has, Has.ReleasePosition, r.ReleasePosition is not null);
                Set(ref has, Has.Pitch, r.ReleaseEyePitch is not null);
                Set(ref has, Has.Yaw, r.ReleaseEyeYaw is not null);
                Set(ref has, Has.OnGround, r.ReleaseOnGround is not null);
                Set(ref has, Has.OnGroundValue, r.ReleaseOnGround == true);
                Set(ref has, Has.Crouched, r.ReleaseCrouched is not null);
                Set(ref has, Has.CrouchedValue, r.ReleaseCrouched == true);
                Set(ref has, Has.Strength, r.ThrowStrength is not null);
                Set(ref has, Has.Speed, r.SpeedAtRelease is not null);
                Set(ref has, Has.SpawnPosition, r.SpawnPosition is not null);
                Set(ref has, Has.SpawnVelocity, r.SpawnVelocity is not null);
                Set(ref has, Has.ThrowerAtSpawn, r.ThrowerPositionAtSpawn is not null);
                Set(ref has, Has.GroundAtSpawn, r.ThrowerOnGroundAtSpawn is not null);
                Set(ref has, Has.GroundAtSpawnValue, r.ThrowerOnGroundAtSpawn == true);
                Set(ref has, Has.JumpPress, r.JumpPressTick is not null);
                Set(ref has, Has.DetonationTick, r.DetonationTick is not null);
                Set(ref has, Has.DetonationPosition, r.DetonationPosition is not null);
                Set(ref has, Has.Inferno, r.InfernoEntityIndex is not null);
                Set(ref has, Has.Place, r.LandingPlace is not null);
                Set(ref has, Has.JumpThrow, r.JumpThrow);
                bool raw = !Fits(r.ReleasePosition) || !Fits(r.SpawnPosition) || !Fits(r.SpawnVelocity)
                           || !Fits(r.ThrowerPositionAtSpawn) || !Fits(r.DetonationPosition);
                Set(ref has, Has.RawPoints, raw);

                w.Write((uint)has);
                w.Write7BitEncodedInt(Str(r.Id));
                w.Write((byte)r.Kind);
                w.Write(r.ThrowerSlot);
                w.Write((byte)r.ThrowerTeam);
                w.Write((byte)r.ThrowerSource);
                w.Write(r.RoundNumber);
                w.Write(r.ReleaseTick);
                w.Write((byte)r.ReleaseSource);
                w.Write((byte)r.ThrowStrengthClass);
                w.Write((byte)r.Movement);
                w.Write(r.SpawnTick);
                w.Write((byte)r.JumpThrowSource);
                w.Write(r.BounceCount);
                w.Write(r.AirTimeTicks);
                w.Write((byte)r.DetonationSource);
                w.Write(r.EndTick);
                w.Write((byte)r.EndKind);
                if (r.ThrowerSteamId64 is { } steam)
                {
                    w.Write7BitEncodedInt(Str(steam));
                }

                if (r.ThrowerName is { } name)
                {
                    w.Write7BitEncodedInt(Str(name));
                }

                Point(w, raw, r.ReleasePosition);
                Float(w, r.ReleaseEyePitch);
                Float(w, r.ReleaseEyeYaw);
                Float(w, r.ThrowStrength);
                Float(w, r.SpeedAtRelease);
                Point(w, raw, r.SpawnPosition);
                Point(w, raw, r.SpawnVelocity);
                Point(w, raw, r.ThrowerPositionAtSpawn);
                Int(w, r.JumpPressTick);
                Int(w, r.DetonationTick);
                Point(w, raw, r.DetonationPosition);
                Int(w, r.InfernoEntityIndex);
                if (r.LandingPlace is { } place)
                {
                    w.Write7BitEncodedInt(Str(place));
                }
            }
        }

        GrenadeDocument header = new()
        {
            SchemaVersion = document.SchemaVersion,
            Walker = document.Walker,
            Demo = document.Demo,
            Clock = document.Clock,
            Source = document.Source
        };

        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, true))
        using (BinaryWriter w = new(gzip, Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write((byte)Version);
            w.Write(JsonSerializer.Serialize(header, GrenadeSidecar.JsonOptions));
            w.Write7BitEncodedInt(strings.Count);
            foreach (string s in strings)
            {
                w.Write(s);
            }

            w.Flush();
            rowsStream.Position = 0;
            rowsStream.CopyTo(gzip);
        }

        return output.ToArray();
    }

    /// <summary>The document a log holds, or null when it is not a log this version reads.</summary>
    public static GrenadeDocument? TryDecode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using MemoryStream input = new(bytes);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using BinaryReader r = new(gzip, Encoding.UTF8);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || r.ReadByte() != Version)
            {
                return null;
            }

            GrenadeDocument? document = JsonSerializer.Deserialize<GrenadeDocument>(r.ReadString(), GrenadeSidecar.JsonOptions);
            if (document is null)
            {
                return null;
            }

            string[] strings = new string[r.Read7BitEncodedInt()];
            for (int i = 0; i < strings.Length; i++)
            {
                strings[i] = r.ReadString();
            }

            int count = r.Read7BitEncodedInt();
            List<GrenadeRow> rows = new(count);
            for (int i = 0; i < count; i++)
            {
                Has has = (Has)r.ReadUInt32();
                GrenadeRow row = new()
                {
                    Id = strings[r.Read7BitEncodedInt()],
                    Kind = (GrenadeKind)r.ReadByte(),
                    ThrowerSlot = r.ReadInt32(),
                    ThrowerTeam = r.ReadByte(),
                    ThrowerSource = (ThrowerSource)r.ReadByte(),
                    RoundNumber = r.ReadInt32(),
                    ReleaseTick = r.ReadInt32(),
                    ReleaseSource = (ReleaseSource)r.ReadByte(),
                    ThrowStrengthClass = (ThrowStrengthClass)r.ReadByte(),
                    Movement = (MovementClass)r.ReadByte(),
                    SpawnTick = r.ReadInt32(),
                    JumpThrowSource = (JumpThrowSource)r.ReadByte(),
                    BounceCount = r.ReadInt32(),
                    AirTimeTicks = r.ReadInt32(),
                    DetonationSource = (DetonationSource)r.ReadByte(),
                    EndTick = r.ReadInt32(),
                    EndKind = (GrenadeEndKind)r.ReadByte(),
                    JumpThrow = has.HasFlag(Has.JumpThrow),
                    ReleaseOnGround = has.HasFlag(Has.OnGround) ? has.HasFlag(Has.OnGroundValue) : null,
                    ReleaseCrouched = has.HasFlag(Has.Crouched) ? has.HasFlag(Has.CrouchedValue) : null,
                    ThrowerOnGroundAtSpawn = has.HasFlag(Has.GroundAtSpawn) ? has.HasFlag(Has.GroundAtSpawnValue) : null
                };
                row.ThrowerSteamId64 = has.HasFlag(Has.SteamId) ? strings[r.Read7BitEncodedInt()] : null;
                row.ThrowerName = has.HasFlag(Has.Name) ? strings[r.Read7BitEncodedInt()] : null;
                row.ReleasePosition = has.HasFlag(Has.ReleasePosition) ? ReadPoint(r, has.HasFlag(Has.RawPoints)) : null;
                row.ReleaseEyePitch = has.HasFlag(Has.Pitch) ? r.ReadSingle() : null;
                row.ReleaseEyeYaw = has.HasFlag(Has.Yaw) ? r.ReadSingle() : null;
                row.ThrowStrength = has.HasFlag(Has.Strength) ? r.ReadSingle() : null;
                row.SpeedAtRelease = has.HasFlag(Has.Speed) ? r.ReadSingle() : null;
                row.SpawnPosition = has.HasFlag(Has.SpawnPosition) ? ReadPoint(r, has.HasFlag(Has.RawPoints)) : null;
                row.SpawnVelocity = has.HasFlag(Has.SpawnVelocity) ? ReadPoint(r, has.HasFlag(Has.RawPoints)) : null;
                row.ThrowerPositionAtSpawn = has.HasFlag(Has.ThrowerAtSpawn) ? ReadPoint(r, has.HasFlag(Has.RawPoints)) : null;
                row.JumpPressTick = has.HasFlag(Has.JumpPress) ? r.ReadInt32() : null;
                row.DetonationTick = has.HasFlag(Has.DetonationTick) ? r.ReadInt32() : null;
                row.DetonationPosition = has.HasFlag(Has.DetonationPosition) ? ReadPoint(r, has.HasFlag(Has.RawPoints)) : null;
                row.InfernoEntityIndex = has.HasFlag(Has.Inferno) ? r.ReadInt32() : null;
                row.LandingPlace = has.HasFlag(Has.Place) ? strings[r.Read7BitEncodedInt()] : null;
                rows.Add(row);
            }

            document.Grenades = rows;
            return document;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or EndOfStreamException
                                       or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     True when two row lists hold the same values, field for field, trajectories aside: the check a
    ///     conversion runs before it deletes the file it converted.
    /// </summary>
    public static bool SameRows(IReadOnlyList<GrenadeRow> a, IReadOnlyList<GrenadeRow> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Count == b.Count
               && a.Zip(b).All(p => string.Equals(JsonSerializer.Serialize(p.First, GrenadeSidecar.JsonOptions),
                   JsonSerializer.Serialize(p.Second, GrenadeSidecar.JsonOptions), StringComparison.Ordinal));
    }

    private static void Set(ref Has has, Has flag, bool on)
    {
        if (on)
        {
            has |= flag;
        }
    }

    private static void Point(BinaryWriter w, bool raw, WorldPoint? p)
    {
        if (p is not { } v)
        {
            return;
        }

        if (raw)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }
        else
        {
            w.Write(Hundredths(v.X));
            w.Write(Hundredths(v.Y));
            w.Write(Hundredths(v.Z));
        }
    }

    private static bool Fits(WorldPoint? p) => p is not { } v || (Fits(v.X) && Fits(v.Y) && Fits(v.Z));

    private static bool Fits(float v) => float.IsFinite(v) && MathF.Abs(v) < 2e7f && !(v == 0 && float.IsNegative(v));

    private static int Hundredths(float v) => (int)Math.Round((double)v * 100, MidpointRounding.AwayFromZero);

    private static void Float(BinaryWriter w, float? f)
    {
        if (f is { } v)
        {
            w.Write(v);
        }
    }

    private static void Int(BinaryWriter w, int? i)
    {
        if (i is { } v)
        {
            w.Write(v);
        }
    }

    private static WorldPoint ReadPoint(BinaryReader r, bool raw) => raw
        ? new WorldPoint(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())
        : new WorldPoint(r.ReadInt32() / 100f, r.ReadInt32() / 100f, r.ReadInt32() / 100f);
}

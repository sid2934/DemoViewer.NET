#region

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>
///     Parses the baker's <c>zones.json</c> into a <see cref="ZoneSet" />. Reflection-based
///     <see cref="JsonSerializer" /> over private DTOs, the same shape the golden manifest reader uses;
///     the file is a few hundred kilobytes and is read once per map.
///     <para>
///         Strict where a wrong answer would be silent: a place index out of range, a volume kind this
///         build does not know, or a missing stamp is a <see cref="JsonException" />, which the
///         pipeline turns into "no zones" rather than a set with holes. Unknown fields are ignored, so a
///         newer baker's additive fields do not break an older reader.
///     </para>
/// </summary>
public static class ZoneSetReader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses a file's bytes.</summary>
    /// <param name="json">The UTF-8 document.</param>
    /// <exception cref="JsonException">The document is not a zones file this build can read.</exception>
    public static ZoneSet Read(ReadOnlySpan<byte> json)
    {
        ZonesFileDto dto = JsonSerializer.Deserialize<ZonesFileDto>(json, _options)
                           ?? throw new JsonException("zones.json is empty.");

        if (dto.SchemaVersion < 1)
        {
            throw new JsonException($"zones.json schemaVersion {dto.SchemaVersion} is not a version this build reads.");
        }

        if (string.IsNullOrEmpty(dto.MapName) || string.IsNullOrEmpty(dto.ZonesVersion))
        {
            throw new JsonException("zones.json needs mapName and zonesVersion.");
        }

        List<ZoneFloor> floors = new(dto.Floors?.Count ?? 0);
        foreach (FloorDto floor in dto.Floors ?? [])
        {
            floors.Add(new ZoneFloor(floor.Key, floor.MinZ, floor.MaxZ));
        }

        List<PlaceDto> placeDtos = [.. dto.Places ?? []];
        placeDtos.Sort(static (l, r) => l.Id.CompareTo(r.Id));
        List<ZonePlace> places = new(placeDtos.Count);
        for (int i = 0; i < placeDtos.Count; i++)
        {
            if (placeDtos[i].Id != i || placeDtos[i].Name is null)
            {
                throw new JsonException($"zones.json places must be numbered 0..n-1 with a name; entry {i} is not.");
            }

            places.Add(new ZonePlace(i, placeDtos[i].Name!, PlaceOrigin.Baked));
        }

        List<ZoneVolume> volumes = new(dto.Volumes?.Count ?? 0);
        foreach (VolumeDto volume in dto.Volumes ?? [])
        {
            volumes.Add(ToVolume(volume, places.Count));
        }

        List<ZoneArea> areas = new(dto.Areas?.Count ?? 0);
        foreach (AreaDto area in dto.Areas ?? [])
        {
            if (area.Place >= places.Count)
            {
                throw new JsonException($"zones.json area {area.Id} names place {area.Place}, beyond the {places.Count} places.");
            }

            areas.Add(new ZoneArea(area.Id, area.Place < 0 ? -1 : area.Place, area.Floor, area.Seed, area.Z,
                area.Xy ?? []));
        }

        return new ZoneSet(dto.MapName, dto.ZonesVersion, dto.BundleMapVersion ?? "", dto.BakerVersion,
            dto.FloorQuantum, floors, places, volumes, areas, Pairs(dto.AreaLinks, "areaLinks"),
            Pairs(dto.Adjacency, "adjacency"), dto.Parameters?.BombRadius);
    }

    private static ZoneVolume ToVolume(VolumeDto dto, int placeCount)
    {
        ZoneVolumeKind kind = dto.Kind switch
        {
            "place" => ZoneVolumeKind.Place,
            "bombsite" => ZoneVolumeKind.Bombsite,
            "buyzone" => ZoneVolumeKind.Buyzone,
            "hostage_rescue" or "hostagerescue" => ZoneVolumeKind.HostageRescue,
            _ => throw new JsonException($"zones.json volume kind '{dto.Kind}' is not one this build knows.")
        };

        int placeId = dto.Place ?? -1;
        if (kind == ZoneVolumeKind.Place && (placeId < 0 || placeId >= placeCount))
        {
            throw new JsonException($"zones.json place volume {dto.Entity} names place {placeId}, beyond the {placeCount} places.");
        }

        Bombsite? site = dto.Site switch
        {
            null => null,
            "A" => Bombsite.A,
            "B" => Bombsite.B,
            _ => throw new JsonException($"zones.json bombsite designation '{dto.Site}' is not A or B.")
        };

        List<ZoneHull> hulls = new(dto.Hulls?.Count ?? 0);
        foreach (HullDto hull in dto.Hulls ?? [])
        {
            List<ZonePlane> planes = new(hull.Planes?.Count ?? 0);
            foreach (double[] plane in hull.Planes ?? [])
            {
                if (plane.Length != 4)
                {
                    throw new JsonException("zones.json planes are [nx, ny, nz, d].");
                }

                planes.Add(new ZonePlane(plane[0], plane[1], plane[2], plane[3]));
            }

            hulls.Add(new ZoneHull(planes));
        }

        Vector3 min = ToVector(dto.Min);
        Vector3 max = ToVector(dto.Max);
        return new ZoneVolume(kind, kind == ZoneVolumeKind.Place ? placeId : -1, site, dto.Team, dto.Entity,
            min, max, hulls);
    }

    private static Vector3 ToVector(double[]? v) =>
        v is { Length: 3 } ? new Vector3((float)v[0], (float)v[1], (float)v[2]) : default;

    private static List<(int A, int B)> Pairs(List<int[]>? raw, string field)
    {
        List<(int, int)> pairs = new(raw?.Count ?? 0);
        foreach (int[] pair in raw ?? [])
        {
            if (pair.Length != 2)
            {
                throw new JsonException($"zones.json {field} entries are [a, b].");
            }

            pairs.Add((pair[0], pair[1]));
        }

        return pairs;
    }

    private sealed class ZonesFileDto
    {
        public int SchemaVersion { get; set; }
        public string? MapName { get; set; }
        public string? BundleMapVersion { get; set; }
        public string? ZonesVersion { get; set; }
        public string? BakerVersion { get; set; }
        public double FloorQuantum { get; set; } = 64;
        public List<FloorDto>? Floors { get; set; }
        public List<PlaceDto>? Places { get; set; }
        public List<VolumeDto>? Volumes { get; set; }
        public List<AreaDto>? Areas { get; set; }
        public List<int[]>? AreaLinks { get; set; }
        public List<int[]>? Adjacency { get; set; }
        public ParametersDto? Parameters { get; set; }
    }

    private sealed class FloorDto
    {
        public double Key { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
    }

    private sealed class PlaceDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class VolumeDto
    {
        public string? Kind { get; set; }
        public int? Place { get; set; }
        public string? Site { get; set; }
        public string? Team { get; set; }
        public string? Entity { get; set; }
        public double[]? Min { get; set; }
        public double[]? Max { get; set; }
        public List<HullDto>? Hulls { get; set; }
    }

    private sealed class HullDto
    {
        public List<double[]>? Planes { get; set; }
    }

    private sealed class AreaDto
    {
        public int Id { get; set; }
        public int Place { get; set; } = -1;
        public double Floor { get; set; }
        public bool Seed { get; set; }
        public double Z { get; set; }
        public double[]? Xy { get; set; }
    }

    private sealed class ParametersDto
    {
        [JsonPropertyName("bombRadius")]
        public double? BombRadius { get; set; }
    }
}

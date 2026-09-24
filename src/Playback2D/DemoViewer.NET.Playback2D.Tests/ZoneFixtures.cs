#region

using System.Globalization;
using System.Numerics;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A synthetic two-floor zone set small enough to reason about by hand, and the <c>zones.json</c>
///     text that describes it, so the reader, the resolver, the overlay and the outline layer are all
///     tested over one geometry.
///     <para>
///         Upper floor (key -512, band [-528, 100000)): <c>Ramp</c> is areas 1, 2 and 7 (7 is a wide
///         area under 1 and 2, a T-junction), <c>Hut</c> is area 3 to the right of 2 with a place volume
///         over it, <c>Outside</c> is area 4 above 1. Lower floor (key -99968): <c>Tunnels</c> is area 5,
///         directly under area 1. Area 6 is an unreachable island. Bombsites A and B are boxes off to
///         the side.
///     </para>
/// </summary>
internal static class ZoneFixtures
{
    public const string ZonesVersion = "9f1c02aa";
    public const double Upper = -512;
    public const double Lower = -99968;

    public static readonly string[] PlaceNames = ["Ramp", "Hut", "Outside", "Tunnels"];

    /// <summary>Builds the set.</summary>
    /// <param name="mapName">The map name, which selects the D4 tie rule; the synthetic name gets the default.</param>
    /// <param name="overlappingRampVolume">
    ///     True adds a Ramp volume over [0, 300] x [0, 100] AHEAD of the Hut volume in lump order, so the
    ///     two overlap over Hut's box and the tie rule decides.
    /// </param>
    public static ZoneSet Build(string mapName = "de_synthetic", bool overlappingRampVolume = false)
    {
        ZoneFloor[] floors =
        [
            new(Lower, -100000, -528),
            new(Upper, -528, 100000)
        ];

        ZonePlace[] places =
        [
            new(0, "Ramp", PlaceOrigin.Baked),
            new(1, "Hut", PlaceOrigin.Baked),
            new(2, "Outside", PlaceOrigin.Baked),
            new(3, "Tunnels", PlaceOrigin.Baked)
        ];

        List<ZoneVolume> volumes = [];
        if (overlappingRampVolume)
        {
            volumes.Add(Box(ZoneVolumeKind.Place, 0, null, "2:0", 0, 0, -450, 300, 100, -300));
        }

        volumes.Add(Box(ZoneVolumeKind.Place, 1, null, "2:1", 200, 0, -450, 300, 100, -300));
        volumes.Add(Box(ZoneVolumeKind.Bombsite, -1, Bombsite.A, "2:2", 400, 400, -450, 500, 500, -300));
        volumes.Add(Box(ZoneVolumeKind.Bombsite, -1, Bombsite.B, "2:3", 600, 600, -450, 700, 700, -300));

        ZoneArea[] areas =
        [
            Rect(1, 0, Upper, -400, 0, 0, 100, 100),
            Rect(2, 0, Upper, -400, 100, 0, 200, 100),
            Rect(3, 1, Upper, -400, 200, 0, 300, 100),
            Rect(4, 2, Upper, -400, 0, 100, 100, 200),
            Rect(5, 3, Lower, -700, 0, 0, 100, 100),
            Rect(6, -1, Upper, -400, 1000, 1000, 1100, 1100),
            Rect(7, 0, Upper, -400, 0, -100, 200, 0)
        ];

        (int, int)[] links = [(1, 2), (2, 3), (1, 4), (7, 1), (7, 2)];
        (int, int)[] adjacency = [(0, 1), (0, 2)];

        return new ZoneSet(mapName, ZonesVersion, "075a27b3", "0.4+test", 64, floors, places, volumes,
            areas, links, adjacency, 650);
    }

    /// <summary>The same set as the baker would write it.</summary>
    public static string Json()
    {
        ZoneSet set = Build();
        List<string> areaLines = [];
        foreach (ZoneArea a in set.Areas)
        {
            string xy = string.Join(", ", a.Xy.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            areaLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"    {{ \"id\": {a.Id}, \"place\": {a.PlaceId}, \"floor\": {a.FloorKey}, \"seed\": {(a.IsSeed ? "true" : "false")}, \"z\": {a.Z}, \"xy\": [{xy}] }}"));
        }

        List<string> volumeLines = [];
        foreach (ZoneVolume v in set.Volumes)
        {
            string kind = v.Kind switch
            {
                ZoneVolumeKind.Place => "place",
                ZoneVolumeKind.Bombsite => "bombsite",
                ZoneVolumeKind.Buyzone => "buyzone",
                _ => "hostage_rescue"
            };
            string head = $"    {{ \"kind\": \"{kind}\"";
            if (v.PlaceId >= 0)
            {
                head += string.Create(CultureInfo.InvariantCulture, $", \"place\": {v.PlaceId}");
            }

            if (v.Site is { } site)
            {
                head += $", \"site\": \"{site}\"";
            }

            string planes = string.Join(", ", v.Hulls[0].Planes.Select(p => string.Create(CultureInfo.InvariantCulture,
                $"[{p.Nx}, {p.Ny}, {p.Nz}, {p.D}]")));
            volumeLines.Add(head + string.Create(CultureInfo.InvariantCulture,
                $", \"entity\": \"{v.Entity}\", \"min\": [{v.Min.X}, {v.Min.Y}, {v.Min.Z}], \"max\": [{v.Max.X}, {v.Max.Y}, {v.Max.Z}], \"hulls\": [{{ \"planes\": [{planes}] }}] }}"));
        }

        string Pairs(IReadOnlyList<(int A, int B)> pairs) =>
            string.Join(", ", pairs.Select(p => string.Create(CultureInfo.InvariantCulture, $"[{p.A}, {p.B}]")));

        return $$"""
                 {
                   "schemaVersion": 1,
                   "mapName": "de_synthetic",
                   "bundleMapVersion": "075a27b3",
                   "zonesVersion": "{{ZonesVersion}}",
                   "bakerVersion": "0.4+test",
                   "floorQuantum": 64,
                   "floors": [
                     { "key": -99968, "minZ": -100000, "maxZ": -528 },
                     { "key": -512, "minZ": -528, "maxZ": 100000 }
                   ],
                   "places": [
                     { "id": 0, "name": "Ramp" },
                     { "id": 1, "name": "Hut" },
                     { "id": 2, "name": "Outside" },
                     { "id": 3, "name": "Tunnels" }
                   ],
                   "volumes": [
                 {{string.Join(",\n", volumeLines)}}
                   ],
                   "areas": [
                 {{string.Join(",\n", areaLines)}}
                   ],
                   "areaLinks": [
                     {{Pairs(set.AreaLinks)}}
                   ],
                   "adjacency": [
                     {{Pairs(set.Adjacency)}}
                   ],
                   "parameters": { "bombRadius": 650 }
                 }
                 """;
    }

    /// <summary>An axis-aligned rectangle area.</summary>
    public static ZoneArea Rect(int id, int place, double floor, double z, double x0, double y0, double x1, double y1) =>
        new(id, place, floor, place >= 0, z, [x0, y0, x1, y0, x1, y1, x0, y1]);

    /// <summary>An axis-aligned box volume as six planes, inside being <c>n·p - d &lt;= 0</c>.</summary>
    public static ZoneVolume Box(ZoneVolumeKind kind, int place, Bombsite? site, string entity,
        double x0, double y0, double z0, double x1, double y1, double z1)
    {
        ZonePlane[] planes =
        [
            new(1, 0, 0, x1), new(-1, 0, 0, -x0),
            new(0, 1, 0, y1), new(0, -1, 0, -y0),
            new(0, 0, 1, z1), new(0, 0, -1, -z0)
        ];
        return new ZoneVolume(kind, place, site, null, entity,
            new Vector3((float)x0, (float)y0, (float)z0), new Vector3((float)x1, (float)y1, (float)z1),
            [new ZoneHull(planes)]);
    }
}

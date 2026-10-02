#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>A token's spawn spot: world XY, the level key it is drawn on, and the place it is in, when it has one.</summary>
public readonly record struct SpawnSpot(double X, double Y, double LevelMinZ, string? Place = null);

/// <summary>
///     Five spots in each team's spawn on one map, and the start a new blank strat is given.
///     A spawn is the team's largest buy zone in the baked zones; with none, the nav areas of its
///     <c>TSpawn</c>/<c>CTSpawn</c> place.
/// </summary>
/// <param name="T">The T spawn's five spots, nearest the spawn's centre first.</param>
/// <param name="Ct">The CT spawn's.</param>
public sealed record StratSpawns(IReadOnlyList<SpawnSpot> T, IReadOnlyList<SpawnSpot> Ct)
{
    /// <summary>Minimum spacing between two tokens, world units: a token's marker is about 64 across.</summary>
    public const double Spacing = 96;

    /// <summary>A buy zone's floor can sit a little above the nav mesh.</summary>
    private const double ZSlack = 64;

    /// <summary>Both spawns, or null when either side has no spots.</summary>
    /// <param name="zones">The map's zones.</param>
    /// <param name="levelFor">A floor Z's level key (<c>StratFromRound.FloorLevelKeys</c>); quantized Z when null.</param>
    public static StratSpawns? From(ZoneSet zones, Func<double, double>? levelFor = null)
    {
        ArgumentNullException.ThrowIfNull(zones);
        levelFor ??= StratFromRound.QuantizedLevel;
        IReadOnlyList<SpawnSpot> t = Spots(zones, StratVocabulary.SideT, "TSpawn", levelFor);
        IReadOnlyList<SpawnSpot> ct = Spots(zones, StratVocabulary.SideCt, "CTSpawn", levelFor);
        return t.Count > 0 && ct.Count > 0 ? new StratSpawns(t, ct) : null;
    }

    /// <summary>
    ///     Up to five spots for one team: the nav area nearest the spawn's centre, then each next nearest at
    ///     least <see cref="Spacing" /> from those taken; half the spacing, then none, when the spawn is small.
    /// </summary>
    /// <param name="zones">The map's zones.</param>
    /// <param name="team"><c>T</c> or <c>CT</c>.</param>
    /// <param name="place">The spawn's place name, for a map without a buy zone.</param>
    /// <param name="levelFor">A floor Z's level key.</param>
    public static IReadOnlyList<SpawnSpot> Spots(ZoneSet zones, string team, string place, Func<double, double> levelFor)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(levelFor);
        List<ZoneArea> areas;
        double cx, cy;
        ZoneVolume? buy = zones.Volumes
            .Where(v => v.Kind == ZoneVolumeKind.Buyzone && string.Equals(v.Team, team, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => (v.Max.X - v.Min.X) * (double)(v.Max.Y - v.Min.Y))
            .FirstOrDefault();
        if (buy is not null)
        {
            areas =
            [
                .. zones.Areas.Where(a => a.CornerCount > 0
                                          && a.CentroidX >= buy.Min.X && a.CentroidX <= buy.Max.X
                                          && a.CentroidY >= buy.Min.Y && a.CentroidY <= buy.Max.Y
                                          && a.Z >= buy.Min.Z - ZSlack && a.Z <= buy.Max.Z)
            ];
            cx = (buy.Min.X + buy.Max.X) / 2.0;
            cy = (buy.Min.Y + buy.Max.Y) / 2.0;
        }
        else
        {
            int placeId = -1;
            for (int i = 0; i < zones.Places.Count; i++)
            {
                if (string.Equals(zones.Places[i].Name, place, StringComparison.Ordinal))
                {
                    placeId = i;
                    break;
                }
            }

            areas = [.. zones.Areas.Where(a => a.PlaceId == placeId && placeId >= 0 && a.CornerCount > 0)];
            double weight = areas.Sum(a => Math.Max(a.Area, 1));
            cx = weight > 0 ? areas.Sum(a => a.CentroidX * Math.Max(a.Area, 1)) / weight : 0;
            cy = weight > 0 ? areas.Sum(a => a.CentroidY * Math.Max(a.Area, 1)) / weight : 0;
        }

        // Nearest the centre first; area id breaks ties so the layout is the same every run.
        List<ZoneArea> byDistance =
        [
            .. areas.OrderBy(a => Squared(a.CentroidX - cx, a.CentroidY - cy)).ThenBy(a => a.Id)
        ];
        List<ZoneArea> taken = [];
        foreach (double spacing in (double[])[Spacing, Spacing / 2, 0])
        {
            foreach (ZoneArea area in byDistance)
            {
                if (taken.Count == StratVocabulary.Slots.Count)
                {
                    break;
                }

                if (!taken.Contains(area)
                    && taken.All(t => Squared(t.CentroidX - area.CentroidX, t.CentroidY - area.CentroidY) >= spacing * spacing))
                {
                    taken.Add(area);
                }
            }
        }

        return
        [
            .. taken.Select(a => new SpawnSpot(Math.Round(a.CentroidX), Math.Round(a.CentroidY), levelFor(a.Z),
                a.PlaceId >= 0 && a.PlaceId < zones.Places.Count ? zones.Places[a.PlaceId].Name : null))
        ];
    }

    /// <summary>
    ///     The start a new blank strat gets: A to E at its side's spawn spots and O1 to O5 at the other side's, a
    ///     <c>spawn</c> start. A strat that already has a start keeps it.
    /// </summary>
    /// <param name="document">A new blank strat.</param>
    public void PlaceStart(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Start is not null)
        {
            return;
        }

        bool ct = string.Equals(document.Side, StratVocabulary.SideCt, StringComparison.Ordinal);
        List<StartPosition> positions = [.. Entries(StratVocabulary.Slots, ct ? Ct : T), .. Entries(StratVocabulary.OpponentSlots, ct ? T : Ct)];
        if (positions.Count > 0)
        {
            document.Start = new StratStart { Kind = StratStart.SpawnKind, Positions = positions };
        }
    }

    private static IEnumerable<StartPosition> Entries(IReadOnlyList<string> slots, IReadOnlyList<SpawnSpot> spots) =>
        slots.Take(spots.Count).Select((slot, i) => new StartPosition
        {
            Slot = slot, Place = spots[i].Place, X = spots[i].X, Y = spots[i].Y, LevelMinZ = spots[i].LevelMinZ
        });

    private static double Squared(double dx, double dy) => dx * dx + dy * dy;
}

/// <summary>
///     Each map's <see cref="StratSpawns" />, read from its baked zones once, off the UI thread. A map with no
///     zones caches null.
/// </summary>
public sealed class StratSpawnSource
{
    private readonly Func<string, StratSpawns?> _load;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<StratSpawns?>> _maps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the shipped bundle's zones with the user's overlay, keyed on the bundle's floors.</summary>
    public StratSpawnSource()
        : this(LoadShipped)
    {
    }

    /// <param name="load">Reads one map's spawns; runs on the thread pool.</param>
    public StratSpawnSource(Func<string, StratSpawns?> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
    }

    /// <summary>A map's spawns, loading them on the thread pool the first time.</summary>
    /// <param name="map">The map.</param>
    public Task<StratSpawns?> ForAsync(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (_gate)
        {
            if (!_maps.TryGetValue(map, out Task<StratSpawns?>? task))
            {
                task = Task.Run(() =>
                {
                    try
                    {
                        return _load(map);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                                  or System.Text.Json.JsonException)
                    {
                        return null;
                    }
                });
                _maps[map] = task;
            }

            return task;
        }
    }

    /// <summary>A map's spawns from its bundle directory, levels keyed the way the strat canvas draws them.</summary>
    /// <param name="map">The map.</param>
    public static StratSpawns? LoadShipped(string map)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(map);
        if (ZoneAssetPipeline.Load(dir, AppPaths.ZonesDirectory).Resolver?.Zones is not { } zones)
        {
            return null;
        }

        IEnumerable<FloorSlice>? floors = dir is null ? null : MapAssetBundleReader.TryRead(dir)?.Floors?.Select(f => new FloorSlice(f.MinZ, f.MaxZ));
        return StratSpawns.From(zones, StratFromRound.FloorLevelKeys(floors));
    }

    /// <summary>Starts a map's load without waiting.</summary>
    /// <param name="map">The map, or null for none.</param>
    public void Warm(string? map)
    {
        if (!string.IsNullOrWhiteSpace(map))
        {
            _ = ForAsync(map);
        }
    }
}

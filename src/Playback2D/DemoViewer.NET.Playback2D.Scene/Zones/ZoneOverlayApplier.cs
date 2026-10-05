#region

using System.Globalization;
using System.Numerics;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>The effective set an overlay produced, and what the loader skipped on the way.</summary>
/// <param name="Effective">The set every consumer sees.</param>
/// <param name="Diagnostics">One row per skipped or flagged entry. Empty when the overlay applied cleanly.</param>
public sealed record ZoneOverlayResult(ZoneSet Effective, IReadOnlyList<ZoneDiagnostic> Diagnostics);

/// <summary>
///     Applies a user overlay to a baked set, in the order the format promises: <c>hidden</c> and
///     <c>merges</c> first, <c>zones</c> next, then the vocabulary is compacted, adjacency is rebuilt
///     over the new assignment, and the stamp becomes <c>CRC32(zonesVersion ‖ overlay bytes)</c>.
///     <para>
///         Every malformed entry is a diagnostic and is skipped; the rest applies. Nothing here throws
///         on user input, because an overlay that fails to load must never silently shadow the bundle,
///         and a loader that threw would do exactly that one layer up.
///     </para>
/// </summary>
public static class ZoneOverlayApplier
{
    /// <summary>Applies the overlay.</summary>
    /// <param name="baked">The baked set.</param>
    /// <param name="overlay">The parsed overlay.</param>
    /// <param name="overlayBytes">The overlay file's bytes, folded into the effective version.</param>
    /// <param name="overlayPath">Where the overlay came from, recorded on the set.</param>
    public static ZoneOverlayResult Apply(ZoneSet baked, ZoneOverlayDocument overlay,
        ReadOnlySpan<byte> overlayBytes, string? overlayPath)
    {
        ArgumentNullException.ThrowIfNull(baked);
        ArgumentNullException.ThrowIfNull(overlay);

        State state = new(baked);
        List<ZoneDiagnostic> diagnostics = [];

        if (!string.IsNullOrEmpty(overlay.MapName) &&
            !string.Equals(overlay.MapName, baked.MapName, StringComparison.Ordinal))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.map-mismatch",
                $"the overlay says mapName '{overlay.MapName}' but is being applied to '{baked.MapName}'; " +
                "applied anyway"));
        }

        if (!string.IsNullOrEmpty(overlay.BasedOn) &&
            !string.Equals(overlay.BasedOn, baked.ZonesVersion, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.based-on",
                $"written against zonesVersion {overlay.BasedOn}, but the baked set is {baked.ZonesVersion}; " +
                "polygons still apply, and a merge or hidden entry naming a place that moved is reported below"));
        }

        foreach (string name in overlay.Hidden)
        {
            int? id = state.FindAlive(name);
            if (id is null)
            {
                diagnostics.Add(new ZoneDiagnostic("zones.overlay.unknown-place",
                    $"hidden names '{name}', which is not a place of this map's baked set", name));
                continue;
            }

            state.Hide(id.Value);
        }

        foreach (ZoneOverlayMerge merge in overlay.Merges)
        {
            ApplyMerge(state, merge, diagnostics);
        }

        foreach (ZoneOverlayZone zone in overlay.Zones)
        {
            ApplyZone(state, zone, diagnostics);
        }

        state.Reflood();

        string effective = Crc32.EffectiveVersion(baked.ZonesVersion, overlayBytes);
        return new ZoneOverlayResult(state.Compact(effective, overlayPath), diagnostics);
    }

    private static void ApplyMerge(State state, ZoneOverlayMerge merge, List<ZoneDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(merge.Into))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.empty-name",
                "a merge has no 'into' name; expected the name of the place the sources become"));
            return;
        }

        if (merge.From.Count == 0)
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.unknown-place",
                $"merge '{merge.Into}' names no 'from' places", merge.Into));
            return;
        }

        List<int> sources = new(merge.From.Count);
        foreach (string name in merge.From)
        {
            int? id = state.FindAlive(name);
            if (id is null)
            {
                diagnostics.Add(new ZoneDiagnostic("zones.overlay.unknown-place",
                    $"merge '{merge.Into}' names '{name}', which is not a place of this map's baked set; " +
                    "the whole merge is skipped", merge.Into));
                return;
            }

            if (!sources.Contains(id.Value))
            {
                sources.Add(id.Value);
            }
        }

        int? collision = state.FindAlive(merge.Into);
        if (collision is { } existing && !sources.Contains(existing))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.name-collision",
                $"merge '{merge.Into}' would take the name of a place that is not one of its sources; " +
                "add it to 'from' or pick another name", merge.Into));
            return;
        }

        int merged = state.AddPlace(merge.Into);
        foreach (int source in sources)
        {
            state.Absorb(source, merged);
        }
    }

    private static void ApplyZone(State state, ZoneOverlayZone zone, List<ZoneDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(zone.Name))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.empty-name",
                "a zone has no 'name'; expected the callout it becomes"));
            return;
        }

        ZoneFloor? band = null;
        foreach (ZoneFloor floor in state.Baked.Floors)
        {
            if (floor.Key.Equals(zone.Floor))
            {
                band = floor;
                break;
            }
        }

        if (band is not { } floorBand)
        {
            string keys = string.Join(", ",
                state.Baked.Floors.Select(f => f.Key.ToString(CultureInfo.InvariantCulture)));
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.unknown-floor",
                $"zone '{zone.Name}' names floor {zone.Floor.ToString(CultureInfo.InvariantCulture)}; " +
                $"this map's floor keys are {keys}", zone.Name));
            return;
        }

        if (!ZoneGeometry.IsSimple(zone.Polygon))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.bad-polygon",
                $"zone '{zone.Name}' has no simple polygon; expected at least three distinct corners as a " +
                "flat x, y list with no crossing edges", zone.Name));
            return;
        }

        double minZ = zone.MinZ ?? floorBand.MinZ;
        double maxZ = zone.MaxZ ?? floorBand.MaxZ;
        if (!(minZ < maxZ))
        {
            diagnostics.Add(new ZoneDiagnostic("zones.overlay.bad-band",
                $"zone '{zone.Name}' has minZ {minZ.ToString(CultureInfo.InvariantCulture)} at or above maxZ " +
                $"{maxZ.ToString(CultureInfo.InvariantCulture)}", zone.Name));
            return;
        }

        if (state.FindAlive(zone.Name) is { } namesake)
        {
            if (!zone.Replaces)
            {
                diagnostics.Add(new ZoneDiagnostic("zones.overlay.name-collision",
                    $"zone '{zone.Name}' has the name of a baked place; set \"replaces\": true to take it over, " +
                    "or pick another name", zone.Name));
                return;
            }

            // The name gets one owner: the baked namesake goes the way of a hidden place, and whatever
            // of it lies outside the polygon re-floods from its neighbours.
            state.Hide(namesake);
        }

        int placeId = state.AddPlace(zone.Name);
        state.AddVolume(BuildPrism(zone.Polygon, minZ, maxZ, placeId), placeId);

        Dictionary<int, int> previous = new();
        for (int i = 0; i < state.Baked.Areas.Count; i++)
        {
            ZoneArea area = state.Baked.Areas[i];
            if (area.Z < minZ || area.Z > maxZ || !ZoneGeometry.PointInPolygon(zone.Polygon, area.CentroidX, area.CentroidY))
            {
                continue;
            }

            int was = state.AreaPlace[i];
            if (was >= 0 && was != placeId)
            {
                previous[was] = previous.GetValueOrDefault(was) + 1;
            }

            state.AreaPlace[i] = placeId;
        }

        if (!zone.Replaces)
        {
            return;
        }

        foreach (int was in previous.Keys)
        {
            if (state.IsBaked(was) && state.AreaCount(was) == 0)
            {
                state.Hide(was);
            }
        }
    }

    // A vertical prism as planes, the same shape as a baked hull so the resolver has one code path.
    // Edge normals point outward whichever way the polygon winds; the top and bottom close the band.
    private static ZoneVolume BuildPrism(IReadOnlyList<double> polygon, double minZ, double maxZ, int placeId)
    {
        int n = polygon.Count / 2;
        bool ccw = ZoneGeometry.SignedArea(polygon) > 0;
        List<ZonePlane> planes = new(n + 2);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double ax = polygon[2 * i], ay = polygon[2 * i + 1];
            double bx = polygon[2 * j], by = polygon[2 * j + 1];
            minX = Math.Min(minX, ax);
            minY = Math.Min(minY, ay);
            maxX = Math.Max(maxX, ax);
            maxY = Math.Max(maxY, ay);

            double ex = bx - ax, ey = by - ay;
            double len = Math.Sqrt(ex * ex + ey * ey);
            double nx = ccw ? ey / len : -ey / len;
            double ny = ccw ? -ex / len : ex / len;
            planes.Add(new ZonePlane(nx, ny, 0, nx * ax + ny * ay));
        }

        planes.Add(new ZonePlane(0, 0, -1, -minZ));
        planes.Add(new ZonePlane(0, 0, 1, maxZ));

        return new ZoneVolume(ZoneVolumeKind.Place, placeId, null, null, null,
            new Vector3((float)minX, (float)minY, (float)minZ), new Vector3((float)maxX, (float)maxY, (float)maxZ),
            [new ZoneHull(planes)], PlaceOrigin.Custom, polygon);
    }

    /// <summary>The mutable working copy of one application.</summary>
    private sealed class State
    {
        private readonly List<(string Name, PlaceOrigin Origin, bool Alive)> _places;
        private readonly List<(ZoneVolume Volume, int PlaceId, bool Alive)> _bakedVolumes;
        private readonly List<(ZoneVolume Volume, int PlaceId)> _customVolumes = [];
        private readonly Dictionary<int, int> _indexById;
        private readonly Dictionary<int, List<int>> _neighbours = new();
        private readonly int _bakedCount;
        private bool _needsReflood;

        public State(ZoneSet baked)
        {
            Baked = baked;
            _bakedCount = baked.Places.Count;
            _places = new List<(string, PlaceOrigin, bool)>(baked.Places.Count);
            foreach (ZonePlace place in baked.Places)
            {
                _places.Add((place.Name, place.Origin, true));
            }

            AreaPlace = new int[baked.Areas.Count];
            _indexById = new Dictionary<int, int>(baked.Areas.Count);
            for (int i = 0; i < baked.Areas.Count; i++)
            {
                AreaPlace[i] = baked.Areas[i].PlaceId;
                _indexById[baked.Areas[i].Id] = i;
            }

            _bakedVolumes = new List<(ZoneVolume, int, bool)>(baked.Volumes.Count);
            foreach (ZoneVolume volume in baked.Volumes)
            {
                if (volume.Origin == PlaceOrigin.Custom)
                {
                    _customVolumes.Add((volume, volume.PlaceId));
                }
                else
                {
                    _bakedVolumes.Add((volume, volume.PlaceId, true));
                }
            }

            foreach ((int a, int b) in baked.AreaLinks)
            {
                if (_indexById.TryGetValue(a, out int ia) && _indexById.TryGetValue(b, out int ib))
                {
                    Link(ia, ib);
                    Link(ib, ia);
                }
            }
        }

        public ZoneSet Baked { get; }

        public int[] AreaPlace { get; }

        public bool IsBaked(int placeId) => placeId < _bakedCount;

        public int? FindAlive(string name)
        {
            for (int i = 0; i < _places.Count; i++)
            {
                if (_places[i].Alive && string.Equals(_places[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return null;
        }

        public int AddPlace(string name)
        {
            _places.Add((name, PlaceOrigin.Custom, true));
            return _places.Count - 1;
        }

        public void AddVolume(ZoneVolume volume, int placeId) => _customVolumes.Add((volume, placeId));

        public int AreaCount(int placeId)
        {
            int count = 0;
            for (int i = 0; i < AreaPlace.Length; i++)
            {
                if (AreaPlace[i] == placeId)
                {
                    count++;
                }
            }

            return count;
        }

        public void Hide(int placeId)
        {
            _places[placeId] = _places[placeId] with { Alive = false };
            for (int i = 0; i < _bakedVolumes.Count; i++)
            {
                if (_bakedVolumes[i].PlaceId == placeId)
                {
                    _bakedVolumes[i] = _bakedVolumes[i] with { Alive = false };
                }
            }

            for (int i = 0; i < _customVolumes.Count; i++)
            {
                if (_customVolumes[i].PlaceId == placeId)
                {
                    _customVolumes.RemoveAt(i--);
                }
            }

            for (int i = 0; i < AreaPlace.Length; i++)
            {
                if (AreaPlace[i] == placeId)
                {
                    AreaPlace[i] = -1;
                    _needsReflood = true;
                }
            }
        }

        public void Absorb(int source, int into)
        {
            _places[source] = _places[source] with { Alive = false };
            for (int i = 0; i < _bakedVolumes.Count; i++)
            {
                if (_bakedVolumes[i].PlaceId == source)
                {
                    _bakedVolumes[i] = (_bakedVolumes[i].Volume.WithPlace(into), into, _bakedVolumes[i].Alive);
                }
            }

            for (int i = 0; i < _customVolumes.Count; i++)
            {
                if (_customVolumes[i].PlaceId == source)
                {
                    _customVolumes[i] = (_customVolumes[i].Volume.WithPlace(into), into);
                }
            }

            for (int i = 0; i < AreaPlace.Length; i++)
            {
                if (AreaPlace[i] == source)
                {
                    AreaPlace[i] = into;
                }
            }
        }

        // Breadth-first from every assigned area at once, so an area that lost its place takes the
        // place of its nearest assigned neighbour by hop count: the baker's own flood rule. Islands the
        // bake could not reach stay unassigned, because nothing assigned links to them.
        public void Reflood()
        {
            if (!_needsReflood)
            {
                return;
            }

            Queue<int> queue = new();
            for (int i = 0; i < AreaPlace.Length; i++)
            {
                if (AreaPlace[i] >= 0)
                {
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                if (!_neighbours.TryGetValue(index, out List<int>? neighbours))
                {
                    continue;
                }

                foreach (int next in neighbours)
                {
                    if (AreaPlace[next] < 0)
                    {
                        AreaPlace[next] = AreaPlace[index];
                        queue.Enqueue(next);
                    }
                }
            }

            _needsReflood = false;
        }

        public ZoneSet Compact(string effectiveVersion, string? overlayPath)
        {
            int[] remap = new int[_places.Count];
            List<ZonePlace> places = [];
            for (int i = 0; i < _places.Count; i++)
            {
                (string name, PlaceOrigin origin, bool alive) = _places[i];
                remap[i] = alive ? places.Count : -1;
                if (alive)
                {
                    places.Add(new ZonePlace(places.Count, name, origin));
                }
            }

            ZoneArea[] areas = new ZoneArea[Baked.Areas.Count];
            for (int i = 0; i < areas.Length; i++)
            {
                int place = AreaPlace[i] >= 0 ? remap[AreaPlace[i]] : -1;
                areas[i] = Baked.Areas[i].PlaceId == place ? Baked.Areas[i] : Baked.Areas[i].WithPlace(place);
            }

            List<ZoneVolume> volumes = new(_customVolumes.Count + _bakedVolumes.Count);
            foreach ((ZoneVolume volume, int placeId) in _customVolumes)
            {
                int place = remap[placeId];
                if (place >= 0)
                {
                    volumes.Add(volume.PlaceId == place ? volume : volume.WithPlace(place));
                }
            }

            foreach ((ZoneVolume volume, int placeId, bool alive) in _bakedVolumes)
            {
                if (!alive)
                {
                    continue;
                }

                int place = placeId >= 0 ? remap[placeId] : -1;
                if (placeId >= 0 && place < 0)
                {
                    continue;
                }

                volumes.Add(volume.PlaceId == place ? volume : volume.WithPlace(place));
            }

            SortedSet<long> pairs = [];
            foreach ((int a, int b) in Baked.AreaLinks)
            {
                if (!_indexById.TryGetValue(a, out int ia) || !_indexById.TryGetValue(b, out int ib))
                {
                    continue;
                }

                int pa = areas[ia].PlaceId, pb = areas[ib].PlaceId;
                if (pa < 0 || pb < 0 || pa == pb)
                {
                    continue;
                }

                pairs.Add(pa < pb ? ((long)pa << 32) | (uint)pb : ((long)pb << 32) | (uint)pa);
            }

            List<(int A, int B)> adjacency = new(pairs.Count);
            foreach (long pair in pairs)
            {
                adjacency.Add(((int)(pair >> 32), (int)(pair & 0xFFFFFFFF)));
            }

            return new ZoneSet(Baked.MapName, Baked.ZonesVersion, Baked.BundleMapVersion, Baked.BakerVersion,
                Baked.FloorQuantum, Baked.Floors, places, volumes, areas, Baked.AreaLinks, adjacency,
                Baked.BombRadius, effectiveVersion, overlayPath);
        }

        private void Link(int from, int to)
        {
            if (!_neighbours.TryGetValue(from, out List<int>? list))
            {
                list = [];
                _neighbours[from] = list;
            }

            list.Add(to);
        }
    }
}

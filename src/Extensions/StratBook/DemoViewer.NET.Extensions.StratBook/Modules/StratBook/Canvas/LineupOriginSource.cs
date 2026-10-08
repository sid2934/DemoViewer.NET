#region

using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

/// <summary>One lineup of a grouped map with what a picker shows for it.</summary>
/// <param name="Lineup">The lineup.</param>
/// <param name="Title">Its landing group's card title (<see cref="LineupClipPlanner.Title" />).</param>
/// <param name="Kind">What is thrown.</param>
public sealed record MapLineup(GrenadeLineup Lineup, string Title, GrenadeKind Kind)
{
    /// <summary>Where the lineup's landing group goes off.</summary>
    public WorldPoint Landing { get; init; }

    /// <summary>Per technique key, how its throw flies; built with the map, never on the UI thread.</summary>
    public IReadOnlyDictionary<string, LineupThrow> Throws { get; init; } = new Dictionary<string, LineupThrow>();
}

/// <summary>One technique's throw as the strat preview plays it.</summary>
/// <param name="Origin">The technique's mean release point.</param>
/// <param name="AirSeconds">Release to rest: the members' median air time.</param>
/// <param name="PopSeconds">Release to going off, never less than <paramref name="AirSeconds" />.</param>
/// <param name="Path">The stored representative flight, seconds from release, or null when none is stored.</param>
public sealed record LineupThrow(WorldPoint Origin, double AirSeconds, double PopSeconds, IReadOnlyList<ThrowPathPoint>? Path);

/// <summary>One point of a recorded flight.</summary>
/// <param name="Seconds">Seconds since release.</param>
/// <param name="Position">World position.</param>
public readonly record struct ThrowPathPoint(double Seconds, WorldPoint Position);

/// <summary>A resolved lineup throw in the canvas's level keys, for <see cref="ThrowFlightResolver" />.</summary>
/// <param name="Origin">Where the thrower stands.</param>
/// <param name="LandingX">World X of the landing group's detonation point.</param>
/// <param name="LandingY">World Y of it.</param>
/// <param name="LandingLevelMinZ">Its level key.</param>
/// <param name="AirSeconds">Release to rest.</param>
/// <param name="PopSeconds">Release to going off.</param>
/// <param name="Path">The recorded flight, each point with its level key, or null.</param>
public sealed record LineupFlight(TokenPlacement Origin, double LandingX, double LandingY, double LandingLevelMinZ,
    double AirSeconds, double PopSeconds, IReadOnlyList<(double Seconds, float X, float Y, double LevelMinZ)>? Path);

/// <summary>A grouped map: every lineup once, most thrown first, and each by every id that names it.</summary>
/// <param name="Lineups">Every lineup once.</param>
/// <param name="ById">Each lineup by its id and every alias id.</param>
public sealed record MapLineups(IReadOnlyList<MapLineup> Lineups, IReadOnlyDictionary<Guid, MapLineup> ById);

/// <summary>
///     A map's lineups by every id that names them, alias ids included, for <see cref="ThrowOriginResolver" />, <see cref="ThrowFlightResolver" />
///     and the strat editor's lineup choices.
///     A map is grouped once off the UI thread and kept until the index changes; a lookup before that answers
///     null and <see cref="Changed" /> fires when the map is ready.
/// </summary>
public sealed class LineupOriginSource : IDisposable
{
    private readonly GrenadeIndex _index;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MapLineups> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<Action> _post;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    /// <param name="index">The Grenade Index.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread.</param>
    public LineupOriginSource(GrenadeIndex index, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(post);
        _index = index;
        _post = post;
        _index.Changed += OnIndexChanged;
    }

    /// <summary>A map finished grouping, or the index changed: projections built before are stale.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose() => _index.Changed -= OnIndexChanged;

    /// <summary>
    ///     Where <paramref name="utility" />'s lineup is thrown from, or null (not ready, or not found).
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="utility">A step's utility with a lineup id.</param>
    /// <param name="levelFor">A world Z's level key: the canvas's floors (<c>StratFromRound.FloorLevelKeys</c>).</param>
    public TokenPlacement? Resolve(string map, UtilityRef utility, Func<double, double> levelFor)
    {
        ArgumentNullException.ThrowIfNull(levelFor);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(utility);
        if (utility.LineupId is not { } id || map.Length == 0)
        {
            return null;
        }

        return For(map)?.ById.TryGetValue(id, out MapLineup? found) == true ? PlacementOf(found.Lineup, utility.Technique, levelFor) : null;
    }

    /// <summary>
    ///     How <paramref name="utility" />'s lineup is thrown and where it lands, or null (not ready, or not found).
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="utility">A step's utility with a lineup id.</param>
    /// <param name="levelFor">A world Z's level key.</param>
    public LineupFlight? ResolveFlight(string map, UtilityRef utility, Func<double, double> levelFor)
    {
        ArgumentNullException.ThrowIfNull(levelFor);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(utility);
        if (utility.LineupId is not { } id || map.Length == 0 || For(map)?.ById.TryGetValue(id, out MapLineup? found) != true)
        {
            return null;
        }

        string? key = GrenadeLineups.TechniqueFor(found!.Lineup, utility.Technique)?.Key;
        if (!found.Throws.TryGetValue(key ?? "", out LineupThrow? thrown))
        {
            return null;
        }

        TokenPlacement origin = PlacementOf(found.Lineup, utility.Technique, levelFor);
        return new LineupFlight(origin, found.Landing.X, found.Landing.Y, levelFor(found.Landing.Z), thrown.AirSeconds,
            thrown.PopSeconds, thrown.Path?.Select(p => (p.Seconds, p.Position.X, p.Position.Y, levelFor(p.Position.Z))).ToList());
    }

    /// <summary>The map's grouped lineups, or null while it is being grouped (<see cref="Changed" /> fires when ready).</summary>
    /// <param name="map">The map.</param>
    public MapLineups? For(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_maps.TryGetValue(map, out MapLineups? lineups))
            {
                return lineups;
            }

            Warm(map);
            return null;
        }
    }

    /// <summary>The technique's mean release point, on the level of its Z, facing the representative throw's yaw.</summary>
    /// <param name="lineup">The lineup.</param>
    /// <param name="technique">A technique key, or null for the most thrown.</param>
    /// <param name="levelFor">A world Z's level key.</param>
    public static TokenPlacement PlacementOf(GrenadeLineup lineup, string? technique, Func<double, double> levelFor)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        ArgumentNullException.ThrowIfNull(levelFor);
        LineupTechnique? chosen = GrenadeLineups.TechniqueFor(lineup, technique);
        WorldPoint origin = chosen?.Origin ?? lineup.Origin;
        float? yaw = (chosen?.Representative ?? lineup.Representative).Row.ReleaseEyeYaw;
        return new TokenPlacement(origin.X, origin.Y, levelFor(origin.Z),
            yaw is { } y && float.IsFinite(y) ? (float)StratFromRound.NormalizeYaw(y) : null);
    }

    /// <summary>Every lineup on a map keyed by each id it answers to.</summary>
    /// <param name="clusters">The map's query result.</param>
    public static Dictionary<Guid, GrenadeLineup> ByAnyId(IEnumerable<GrenadeCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        Dictionary<Guid, GrenadeLineup> byId = [];
        foreach (GrenadeLineup lineup in clusters.SelectMany(c => c.Lineups))
        {
            // The primary id wins over another lineup's alias of it.
            byId[lineup.Id] = lineup;
        }

        foreach (GrenadeLineup lineup in clusters.SelectMany(c => c.Lineups))
        {
            foreach (Guid alias in lineup.AliasIds)
            {
                byId.TryAdd(alias, lineup);
            }
        }

        return byId;
    }

    /// <summary>Every lineup of a query result once, with its title, keyed by each id that names it.</summary>
    /// <param name="clusters">The map's query result.</param>
    /// <param name="paths">A technique's stored flight (<see cref="GrenadeIndex.PathFor" />), or null for none.</param>
    public static MapLineups Build(IReadOnlyList<GrenadeCluster> clusters,
        Func<GrenadeLineup, LineupTechnique?, IReadOnlyList<TrajectoryPoint>?>? paths = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        List<MapLineup> all =
        [
            .. clusters.SelectMany(c => c.Lineups.Select(l => new MapLineup(l, LineupClipPlanner.Title(c), c.Kind)
            {
                Landing = c.Landing,
                Throws = ThrowsOf(l, paths)
            }))
        ];
        Dictionary<Guid, MapLineup> byId = [];
        foreach (MapLineup entry in all)
        {
            byId[entry.Lineup.Id] = entry;
        }

        foreach (MapLineup entry in all)
        {
            foreach (Guid alias in entry.Lineup.AliasIds)
            {
                byId.TryAdd(alias, entry);
            }
        }

        return new MapLineups(all, byId);
    }

    /// <summary>
    ///     Each technique's throw, by key; a lineup without techniques (the grid grouping) keys its throws under "".
    /// </summary>
    /// <param name="lineup">The lineup.</param>
    /// <param name="paths">A technique's stored flight, or null for none.</param>
    public static Dictionary<string, LineupThrow> ThrowsOf(GrenadeLineup lineup,
        Func<GrenadeLineup, LineupTechnique?, IReadOnlyList<TrajectoryPoint>?>? paths)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        Dictionary<string, LineupThrow> throws = new(StringComparer.Ordinal);
        if (lineup.Techniques.Count == 0)
        {
            if (lineup.Throws.Count > 0)
            {
                throws[""] = ThrowOf(lineup.Origin, lineup.Throws, paths?.Invoke(lineup, null));
            }

            return throws;
        }

        foreach (LineupTechnique technique in lineup.Techniques)
        {
            throws[technique.Key] = ThrowOf(technique.Origin, technique.Throws, paths?.Invoke(lineup, technique));
        }

        return throws;
    }

    private static LineupThrow ThrowOf(WorldPoint origin, IReadOnlyList<IndexedGrenade> members, IReadOnlyList<TrajectoryPoint>? path)
    {
        double air = Median(members.Where(g => g.Row.AirTimeTicks > 0).Select(g => (double)g.AirTimeSeconds));
        double pop = Median(members.Where(g => g.Row.DetonationTick > g.Row.SpawnTick)
            .Select(g => (g.Row.DetonationTick!.Value - g.Row.SpawnTick) / (double)Math.Max(1, g.TickRate)));
        int rate = Math.Max(1, members.Count > 0 ? members[0].TickRate : 64);
        List<ThrowPathPoint>? points = path is { Count: >= 2 }
            ? [.. path.Select(p => new ThrowPathPoint((p.Tick - path[0].Tick) / (double)rate, new WorldPoint(p.X, p.Y, p.Z)))]
            : null;
        if (air <= 0)
        {
            air = points is { } flown ? flown[^1].Seconds : pop;
        }

        return new LineupThrow(origin, air, Math.Max(air, pop), points);
    }

    // 0 for none.
    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = [.. values.Where(double.IsFinite).Order()];
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>
    ///     Groups a map now, on the caller's thread, again while the index changes under it. For tests and a
    ///     caller already off the UI thread.
    /// </summary>
    /// <param name="map">The map.</param>
    public void Load(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        while (true)
        {
            int generation;
            lock (_gate)
            {
                generation = _generation;
            }

            MapLineups lineups = Build(_index.Query(new GrenadeQuery(map)), _index.PathFor);
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _maps[map] = lineups;
                    _pending.Remove(map);
                    return;
                }
            }
        }
    }

    // Must be called under _gate.
    private void Warm(string map)
    {
        if (!_pending.Add(map))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Load(map);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _pending.Remove(map);
                }

                return;
            }

            _post(() => Changed?.Invoke());
        });
    }

    // One merged demo at a time during a scan: the old lineups keep answering until the regroup lands, so a
    // thrower never drops back to its authored spot in between.
    private void OnIndexChanged()
    {
        lock (_gate)
        {
            _generation++;
            foreach (string map in _maps.Keys)
            {
                Warm(map);
            }
        }
    }
}

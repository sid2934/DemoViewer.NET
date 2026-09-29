#region

using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     A map's lineups by every id that names them, alias ids included, for <see cref="ThrowOriginResolver" />.
///     A map is grouped once off the UI thread and kept until the index changes; a lookup before that answers
///     null and <see cref="Changed" /> fires when the map is ready.
/// </summary>
public sealed class LineupOriginSource : IDisposable
{
    private readonly GrenadeIndex _index;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<Guid, GrenadeLineup>> _maps = new(StringComparer.OrdinalIgnoreCase);
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

        Dictionary<Guid, GrenadeLineup>? lineups;
        lock (_gate)
        {
            if (!_maps.TryGetValue(map, out lineups))
            {
                Warm(map);
                return null;
            }
        }

        return lineups.TryGetValue(id, out GrenadeLineup? lineup) ? PlacementOf(lineup, utility.Technique, levelFor) : null;
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

            Dictionary<Guid, GrenadeLineup> lineups = ByAnyId(_index.Query(new GrenadeQuery(map)));
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

#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>How the index groups grenades into landing groups and lineups.</summary>
public enum GrenadeGrouping
{
    /// <summary>Stored lineup anchors: one lineup per spot and landing, its techniques as positions (grenades-v2.md §2).</summary>
    Lineups,

    /// <summary>The v1 grid: landing cell plus rounded origin. Kept to resolve the ids it minted.</summary>
    Grid
}

/// <summary>One way a lineup is thrown: standing or running, jump-throw or not, and which click.</summary>
/// <param name="Key">Stable text key, e.g. <c>stand-jump-left</c>.</param>
/// <param name="Label">What a card prints, e.g. "jump-throw, left click".</param>
/// <param name="Origin">The members' mean release point.</param>
/// <param name="JumpThrow">Whether it is a jump-throw.</param>
/// <param name="Throws">The members, oldest demo first, then by release tick.</param>
public sealed record LineupTechnique(string Key, string Label, WorldPoint Origin, bool JumpThrow, IReadOnlyList<IndexedGrenade> Throws)
{
    /// <summary>The member released nearest the technique's mean origin: the one a card and a clip use.</summary>
    public IndexedGrenade Representative => GrenadeLineups.Medoid(Throws, Origin);
}

/// <summary>
///     Lineup assignment and grouping (grenades-v2.md §2). A lineup is one spot and one landing: throws of a
///     kind whose release points sit within <see cref="SpotRadius" /> (<see cref="RunningSpotRadius" /> for a
///     running throw) and whose landings sit within <see cref="LineupLandingRadius" /> of a stored anchor
///     join it. Leftovers cluster by leader clustering in density order (the most crowded throw seeds, the
///     others join the nearest seed, never another member, so nothing chains); a cluster of two or more mints
///     an anchor. Landing groups cluster the lineups' mean landings the same way, per query.
/// </summary>
public static class GrenadeLineups
{
    /// <summary>A standing, walking or jump-throw spot: two feet in the plane.</summary>
    public const float SpotRadius = 24f;

    /// <summary>A running throw releases somewhere along the run.</summary>
    public const float RunningSpotRadius = 64f;

    /// <summary>A jump-throw releases up to about 55 units above the floor.</summary>
    public const float SpotHeight = 64f;

    /// <summary>Throws of one lineup land within this of its anchor (bounces scatter them).</summary>
    public const float LineupLandingRadius = 192f;

    /// <summary>...and this far apart in height, so a ledge and the floor below part.</summary>
    public const float LandingHeight = 96f;

    /// <summary>Landing group radius per kind: about a smoke's radius, less for a fire, more for a flash.</summary>
    public static float GroupRadius(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Molotov or GrenadeKind.Incendiary => 96f,
        GrenadeKind.Flash => 160f,
        _ => 128f
    };

    /// <summary>Whether <paramref name="g" /> counts as a running throw for the spot radius.</summary>
    public static bool IsRunning(IndexedGrenade g) => g.Row.Movement == MovementClass.Running;

    /// <summary>The technique key: movement, jump, click.</summary>
    public static string TechniqueKey(GrenadeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string click = row.ThrowStrengthClass switch
        {
            ThrowStrengthClass.Half => "both",
            ThrowStrengthClass.Underhand => "right",
            _ => "left"
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{(row.Movement == MovementClass.Running ? "run" : "stand")}-{(row.JumpThrow ? "jump" : "throw")}-{click}");
    }

    /// <summary>What a card prints for <see cref="TechniqueKey" />.</summary>
    public static string TechniqueLabel(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string[] parts = key.Split('-');
        if (parts.Length != 3)
        {
            return key;
        }

        string how = (parts[0], parts[1]) switch
        {
            ("run", "jump") => "running jump-throw",
            ("run", _) => "running throw",
            (_, "jump") => "jump-throw",
            _ => "standing throw"
        };
        string click = parts[2] switch
        {
            "both" => "both buttons",
            "right" => "right click",
            _ => "left click"
        };
        return how + ", " + click;
    }

    /// <summary>A minted anchor's id: the map, the kind and the seed throw's key.</summary>
    public static Guid AnchorId(string map, GrenadeKind kind, string seed) =>
        Hash(string.Create(CultureInfo.InvariantCulture, $"lineup|{map.ToLowerInvariant()}|{kind}|{seed}"));

    /// <summary>A throw in no lineup still needs an id the map can select: one derived from the throw.</summary>
    public static Guid SingleId(string throwKey) => Hash("single|" + throwKey);

    /// <summary>
    ///     Assigns every throw of one map to a lineup id: the nearest anchor that accepts it, else a new anchor
    ///     for a leftover cluster of two or more when <paramref name="mint" /> is set, else its single id.
    /// </summary>
    /// <param name="map">The map, as the headers spell it.</param>
    /// <param name="throws">Every loaded throw on the map, all kinds.</param>
    /// <param name="store">The map's anchors; new ones are appended.</param>
    /// <param name="mint">False while the library is still loading: leftovers stay singles and nothing is stored.</param>
    /// <returns>Throw key to lineup id, and whether an anchor was added.</returns>
    public static (Dictionary<string, Guid> Assignment, bool Minted) Assign(string map, IReadOnlyList<IndexedGrenade> throws,
        MapLineups store, bool mint)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(throws);
        ArgumentNullException.ThrowIfNull(store);
        Dictionary<string, Guid> assignment = new(StringComparer.Ordinal);
        bool minted = false;
        foreach (IGrouping<GrenadeKind, IndexedGrenade> kind in throws.GroupBy(t => t.Kind))
        {
            List<LineupAnchor> anchors = [.. store.Anchors.Where(a => a.Kind == kind.Key)];
            Dictionary<(int, int), List<LineupAnchor>> cells = [];
            foreach (LineupAnchor anchor in anchors)
            {
                AddTo(cells, Bucket(anchor.Origin, RunningSpotRadius), anchor);
            }

            List<IndexedGrenade> leftovers = [];
            foreach (IndexedGrenade g in kind)
            {
                if (Nearest(cells, g) is { } anchor)
                {
                    assignment[g.Key] = anchor.Id;
                }
                else
                {
                    leftovers.Add(g);
                }
            }

            foreach (List<IndexedGrenade> cluster in ClusterLeftovers(leftovers))
            {
                if (mint && cluster.Count >= 2)
                {
                    IndexedGrenade seed = cluster[0];
                    LineupAnchor anchor = new(AnchorId(map, kind.Key, seed.Key), kind.Key,
                        Mean(cluster.Select(c => c.Origin)), Mean(cluster.Select(c => c.Landing)), seed.Key);
                    store.Anchors.Add(anchor);
                    minted = true;
                    foreach (IndexedGrenade g in cluster)
                    {
                        assignment[g.Key] = anchor.Id;
                    }
                }
                else
                {
                    // Not stored: a cluster that has not minted yet shows under its seed's single id.
                    Guid id = SingleId(cluster[0].Key);
                    foreach (IndexedGrenade g in cluster)
                    {
                        assignment[g.Key] = id;
                    }
                }
            }
        }

        return (assignment, minted);
    }

    /// <summary>
    ///     Maps every grid id (<see cref="GrenadeIndex.Cluster" />, v1) to exactly one lineup: the one that
    ///     received most of that grid position's throws, ties to the bigger lineup, then the lower id.
    /// </summary>
    public static Dictionary<Guid, Guid> BuildAliases(IReadOnlyList<IndexedGrenade> throws, IReadOnlyDictionary<string, Guid> assignment)
    {
        ArgumentNullException.ThrowIfNull(throws);
        ArgumentNullException.ThrowIfNull(assignment);
        Dictionary<Guid, int> sizes = [];
        foreach (Guid id in assignment.Values)
        {
            sizes[id] = sizes.GetValueOrDefault(id) + 1;
        }

        Dictionary<Guid, Guid> aliases = [];
        foreach (GrenadeCluster cluster in GrenadeIndex.Cluster(throws))
        {
            foreach (GrenadeLineup grid in cluster.Lineups)
            {
                Guid winner = grid.Throws
                    .Select(t => assignment.TryGetValue(t.Key, out Guid id) ? id : SingleId(t.Key))
                    .GroupBy(id => id)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => sizes.GetValueOrDefault(g.Key))
                    .ThenBy(g => g.Key.ToString("N"), StringComparer.Ordinal)
                    .First().Key;
                foreach (Guid old in grid.AliasIds.Append(grid.Id))
                {
                    if (old != winner)
                    {
                        aliases[old] = winner;
                    }
                }
            }
        }

        return aliases;
    }

    /// <summary>
    ///     Groups filtered throws by their assigned lineup, splits each lineup into techniques and gathers the
    ///     lineups into landing groups, most thrown first.
    /// </summary>
    /// <param name="throws">The filtered throws.</param>
    /// <param name="lineupOf">A throw's lineup id.</param>
    /// <param name="aliasesOf">The old ids that resolve to a lineup.</param>
    public static IReadOnlyList<GrenadeCluster> Group(IEnumerable<IndexedGrenade> throws, Func<IndexedGrenade, Guid> lineupOf,
        Func<Guid, IReadOnlyList<Guid>> aliasesOf)
    {
        ArgumentNullException.ThrowIfNull(throws);
        ArgumentNullException.ThrowIfNull(lineupOf);
        ArgumentNullException.ThrowIfNull(aliasesOf);
        List<GrenadeCluster> clusters = [];
        foreach (IGrouping<GrenadeKind, IndexedGrenade> kind in throws.GroupBy(t => t.Kind))
        {
            List<GrenadeLineup> lineups = [.. kind.GroupBy(lineupOf).Select(g => Shape(g.Key, [.. g], aliasesOf(g.Key)))];
            float radius = GroupRadius(kind.Key);
            List<(WorldPoint Centre, List<GrenadeLineup> Lineups)> groups = [];
            foreach (Seed<GrenadeLineup> seed in Leader(lineups, Landing, radius, LandingHeight, null, l => l.Throws.Count))
            {
                groups.Add((Weighted(seed.Members), seed.Members));
            }

            // Re-centred groups that ended up within half a radius of a bigger one fold into it.
            List<(WorldPoint Centre, List<GrenadeLineup> Lineups)> kept = [];
            foreach ((WorldPoint centre, List<GrenadeLineup> members) in groups
                         .OrderByDescending(g => g.Lineups.Sum(l => l.Throws.Count))
                         .ThenBy(g => g.Centre.X).ThenBy(g => g.Centre.Y))
            {
                int home = kept.FindIndex(k => Near(k.Centre, centre, radius / 2, LandingHeight));
                if (home >= 0)
                {
                    kept[home].Lineups.AddRange(members);
                }
                else
                {
                    kept.Add((centre, members));
                }
            }

            foreach ((_, List<GrenadeLineup> members) in kept)
            {
                List<GrenadeLineup> ordered =
                [
                    .. members.OrderByDescending(l => l.Throws.Count)
                        .ThenBy(l => l.Origin.X).ThenBy(l => l.Origin.Y).ThenBy(l => l.Id)
                ];
                List<IndexedGrenade> all = [.. ordered.SelectMany(l => l.Throws)];
                WorldPoint landing = Mean(all.Select(g => g.Landing));
                string? place = all.Select(g => g.LandingPlace).OfType<string>()
                    .GroupBy(p => p, StringComparer.Ordinal)
                    .OrderByDescending(p => p.Count()).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key).FirstOrDefault();
                clusters.Add(new GrenadeCluster(kind.Key, GrenadeIndex.CellOf(landing), landing, place, ordered));
            }
        }

        return
        [
            .. clusters.OrderByDescending(c => c.ThrowCount).ThenBy(c => c.Kind)
                .ThenBy(c => c.Landing.X).ThenBy(c => c.Landing.Y)
        ];
    }

    /// <summary>
    ///     The technique <paramref name="key" /> names, else the most thrown one; null when the lineup has none
    ///     (the grid grouping).
    /// </summary>
    /// <param name="lineup">The lineup.</param>
    /// <param name="key">A <see cref="TechniqueKey" />, or null.</param>
    public static LineupTechnique? TechniqueFor(GrenadeLineup lineup, string? key)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        return lineup.Techniques.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal))
               ?? (lineup.Techniques.Count > 0 ? lineup.Techniques[0] : null);
    }

    /// <summary>The throw released nearest <paramref name="origin" />; ties to the earlier in list order.</summary>
    public static IndexedGrenade Medoid(IReadOnlyList<IndexedGrenade> throws, WorldPoint origin)
    {
        ArgumentNullException.ThrowIfNull(throws);
        IndexedGrenade best = throws[0];
        float bestDistance = float.MaxValue;
        foreach (IndexedGrenade g in throws)
        {
            float d = Distance3(g.Origin, origin);
            if (d < bestDistance)
            {
                best = g;
                bestDistance = d;
            }
        }

        return best;
    }

    private static GrenadeLineup Shape(Guid id, List<IndexedGrenade> members, IReadOnlyList<Guid> aliases)
    {
        List<IndexedGrenade> throws =
        [
            .. members.OrderBy(g => g.Demo.Path, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Row.ReleaseTick)
        ];
        List<LineupTechnique> techniques =
        [
            .. throws.GroupBy(t => TechniqueKey(t.Row), StringComparer.Ordinal)
                .Select(g => new LineupTechnique(g.Key, TechniqueLabel(g.Key), Mean(g.Select(t => t.Origin)), g.First().Row.JumpThrow, [.. g]))
                .OrderByDescending(t => t.Throws.Count).ThenBy(t => t.Key, StringComparer.Ordinal)
        ];
        return new GrenadeLineup(Mean(throws.Select(t => t.Origin)), techniques[0].JumpThrow, throws, id)
        {
            AliasIds = [id, .. aliases],
            Techniques = techniques
        };
    }

    private static WorldPoint Landing(GrenadeLineup lineup) => Mean(lineup.Throws.Select(t => t.Landing));

    // Leftovers of one kind: standing spots first at the tight radius, then running throws join the nearest
    // of those within the running radius or cluster among themselves at it.
    private static List<List<IndexedGrenade>> ClusterLeftovers(List<IndexedGrenade> leftovers)
    {
        static bool LandsWith(IndexedGrenade g, Seed<IndexedGrenade> s) =>
            Near(g.Landing, s.First.Landing, LineupLandingRadius, LandingHeight);

        List<Seed<IndexedGrenade>> still = Leader([.. leftovers.Where(g => !IsRunning(g))], g => g.Origin, SpotRadius, SpotHeight,
            LandsWith, _ => 1);
        Dictionary<(int, int), List<Seed<IndexedGrenade>>> cells = [];
        foreach (Seed<IndexedGrenade> seed in still)
        {
            AddTo(cells, Bucket(seed.Centre, RunningSpotRadius), seed);
        }

        List<IndexedGrenade> unplaced = [];
        foreach (IndexedGrenade g in Sorted(leftovers.Where(IsRunning)))
        {
            Seed<IndexedGrenade>? best = null;
            float bestDistance = float.MaxValue;
            foreach (Seed<IndexedGrenade> seed in Around(cells, g.Origin, RunningSpotRadius))
            {
                float d = Planar(g.Origin, seed.Centre);
                if (d <= RunningSpotRadius && d < bestDistance && MathF.Abs(g.Origin.Z - seed.Centre.Z) <= SpotHeight && LandsWith(g, seed))
                {
                    best = seed;
                    bestDistance = d;
                }
            }

            if (best is null)
            {
                unplaced.Add(g);
            }
            else
            {
                best.Members.Add(g);
            }
        }

        List<Seed<IndexedGrenade>> running = Leader(unplaced, g => g.Origin, RunningSpotRadius, SpotHeight, LandsWith, _ => 1);
        return [.. still.Concat(running).Select(s => s.Members)];
    }

    private static IEnumerable<IndexedGrenade> Sorted(IEnumerable<IndexedGrenade> throws) =>
        throws.OrderBy(g => g.Origin.X).ThenBy(g => g.Origin.Y).ThenBy(g => g.Origin.Z).ThenBy(g => g.Key, StringComparer.Ordinal);

    private static LineupAnchor? Nearest(Dictionary<(int, int), List<LineupAnchor>> cells, IndexedGrenade g)
    {
        float radius = IsRunning(g) ? RunningSpotRadius : SpotRadius;
        LineupAnchor? best = null;
        float bestDistance = float.MaxValue;
        foreach (LineupAnchor anchor in Around(cells, g.Origin, RunningSpotRadius))
        {
            float d = Planar(g.Origin, anchor.Origin);
            if (d <= radius && d < bestDistance && MathF.Abs(g.Origin.Z - anchor.Origin.Z) <= SpotHeight
                && Near(g.Landing, anchor.Landing, LineupLandingRadius, LandingHeight))
            {
                best = anchor;
                bestDistance = d;
            }
        }

        return best;
    }

    private static List<Seed<T>> Leader<T>(IReadOnlyList<T> items, Func<T, WorldPoint> pos, float radius, float height,
        Func<T, Seed<T>, bool>? fits, Func<T, int> weight)
        where T : class
    {
        Dictionary<(int, int), List<T>> cells = [];
        foreach (T item in items)
        {
            AddTo(cells, Bucket(pos(item), radius), item);
        }

        Dictionary<T, int> density = new(ReferenceEqualityComparer.Instance);
        foreach (T item in items)
        {
            WorldPoint p = pos(item);
            int sum = 0;
            foreach (T other in Around(cells, p, radius))
            {
                if (Near(p, pos(other), radius, height))
                {
                    sum += weight(other);
                }
            }

            density[item] = sum;
        }

        List<Seed<T>> seeds = [];
        Dictionary<(int, int), List<Seed<T>>> seedCells = [];
        foreach (T item in items.OrderByDescending(i => density[i]).ThenByDescending(weight)
                     .ThenBy(i => pos(i).X).ThenBy(i => pos(i).Y).ThenBy(i => pos(i).Z))
        {
            WorldPoint p = pos(item);
            Seed<T>? best = null;
            float bestDistance = float.MaxValue;
            foreach (Seed<T> seed in Around(seedCells, p, radius))
            {
                float d = Planar(p, seed.Centre);
                if (d <= radius && d < bestDistance && MathF.Abs(p.Z - seed.Centre.Z) <= height && (fits is null || fits(item, seed)))
                {
                    best = seed;
                    bestDistance = d;
                }
            }

            if (best is null)
            {
                Seed<T> seed = new(item, p, [item]);
                seeds.Add(seed);
                AddTo(seedCells, Bucket(p, radius), seed);
            }
            else
            {
                best.Members.Add(item);
            }
        }

        return seeds;
    }

    private static (int, int) Bucket(WorldPoint p, float size) => ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size));

    private static IEnumerable<TItem> Around<TItem>(Dictionary<(int, int), List<TItem>> cells, WorldPoint p, float size)
    {
        (int cx, int cy) = Bucket(p, size);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (cells.TryGetValue((cx + dx, cy + dy), out List<TItem>? list))
                {
                    foreach (TItem item in list)
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    private static void AddTo<TItem>(Dictionary<(int, int), List<TItem>> cells, (int, int) cell, TItem item)
    {
        if (!cells.TryGetValue(cell, out List<TItem>? list))
        {
            list = [];
            cells[cell] = list;
        }

        list.Add(item);
    }

    private static float Planar(WorldPoint a, WorldPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static float Distance3(WorldPoint a, WorldPoint b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static bool Near(WorldPoint a, WorldPoint b, float radius, float height) =>
        Planar(a, b) <= radius && MathF.Abs(a.Z - b.Z) <= height;

    private static WorldPoint Mean(IEnumerable<WorldPoint> points)
    {
        double x = 0, y = 0, z = 0;
        int n = 0;
        foreach (WorldPoint p in points)
        {
            x += p.X;
            y += p.Y;
            z += p.Z;
            n++;
        }

        return n == 0 ? default : new WorldPoint((float)(x / n), (float)(y / n), (float)(z / n));
    }

    private static WorldPoint Weighted(IReadOnlyList<GrenadeLineup> lineups)
    {
        double x = 0, y = 0, z = 0;
        int n = 0;
        foreach (GrenadeLineup l in lineups)
        {
            WorldPoint landing = Landing(l);
            int w = l.Throws.Count;
            x += landing.X * w;
            y += landing.Y * w;
            z += landing.Z * w;
            n += w;
        }

        return new WorldPoint((float)(x / n), (float)(y / n), (float)(z / n));
    }

    private static Guid Hash(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    private sealed class Seed<T>(T first, WorldPoint centre, List<T> members)
    {
        public T First { get; } = first;
        public WorldPoint Centre { get; } = centre;
        public List<T> Members { get; } = members;
    }
}

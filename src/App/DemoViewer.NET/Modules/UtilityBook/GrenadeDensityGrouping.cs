#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>How the index groups grenades into landing groups and lineups.</summary>
public enum GrenadeGrouping
{
    /// <summary>Landing cell plus rounded origin, merged within 16 units and 2 degrees.</summary>
    Grid,

    /// <summary>The grenades-v2 prototype: lineups by spot, technique and landing, then landing groups by distance.</summary>
    Density
}

/// <summary>
///     Prototype grouping for docs/utility-book/grenades-v2.md. Lineups first: throws of one kind and one
///     technique (jump, strength, running or not) whose origins sit within a spot radius and whose landings
///     sit within <see cref="LineupLandingRadius" /> of the lineup's seed. Then landing groups over the
///     lineups' mean landings. Both steps are leader clustering in density order: the most crowded point
///     seeds first and every other point joins the nearest seed, never another member, so nothing chains.
///     Ids here are deterministic over the seed only; the proposal persists them (see the doc).
/// </summary>
public static class GrenadeDensityGrouping
{
    /// <summary>A standing, walking or jump-throw spot: two feet in the plane.</summary>
    public const float SpotRadius = 24f;

    /// <summary>A running throw releases somewhere along the run.</summary>
    public const float RunningSpotRadius = 64f;

    /// <summary>A jump-throw releases up to about 55 units above the floor.</summary>
    public const float SpotHeight = 64f;

    /// <summary>Throws of one lineup land within this of its seed's landing (bounces scatter them).</summary>
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

    /// <summary>Groups <paramref name="grenades" /> the density way, most thrown first.</summary>
    /// <param name="grenades">Filtered index rows.</param>
    public static IReadOnlyList<GrenadeCluster> Cluster(IEnumerable<IndexedGrenade> grenades)
    {
        ArgumentNullException.ThrowIfNull(grenades);
        List<GrenadeCluster> clusters = [];
        foreach (IGrouping<GrenadeKind, IndexedGrenade> kind in grenades.GroupBy(g => g.Kind))
        {
            List<Lineup> lineups = [];
            foreach (bool running in new[] { false, true })
            {
                List<IndexedGrenade> throws = [.. kind.Where(g => (g.Row.Movement == MovementClass.Running) == running)];
                float radius = running ? RunningSpotRadius : SpotRadius;
                foreach (Seed<IndexedGrenade> seed in Leader(throws, g => g.Origin, radius, SpotHeight, Technique,
                             (g, s) => Near(g.Landing, s.First.Landing, LineupLandingRadius, LandingHeight), _ => 1))
                {
                    lineups.Add(new Lineup(seed.First, seed.Members, Mean(seed.Members.Select(m => m.Landing))));
                }
            }

            float groupRadius = GroupRadius(kind.Key);
            List<(WorldPoint Centre, List<Lineup> Lineups)> groups = [];
            foreach (Seed<Lineup> seed in Leader(lineups, l => l.Landing, groupRadius, LandingHeight, _ => "",
                         null, l => l.Throws.Count))
            {
                groups.Add((Weighted(seed.Members), seed.Members));
            }

            // Re-centred groups that ended up within half a radius of a bigger one fold into it.
            List<(WorldPoint Centre, List<Lineup> Lineups)> kept = [];
            foreach ((WorldPoint centre, List<Lineup> members) in groups.OrderByDescending(g => g.Lineups.Sum(l => l.Throws.Count)))
            {
                int home = kept.FindIndex(k => Near(k.Centre, centre, groupRadius / 2, LandingHeight));
                if (home >= 0)
                {
                    kept[home].Lineups.AddRange(members);
                }
                else
                {
                    kept.Add((centre, members));
                }
            }

            foreach ((_, List<Lineup> members) in kept)
            {
                List<GrenadeLineup> shaped =
                [
                    .. members.OrderByDescending(l => l.Throws.Count)
                        .ThenBy(l => l.Seed.Origin.X).ThenBy(l => l.Seed.Origin.Y)
                        .Select(Shape)
                ];
                List<IndexedGrenade> all = [.. shaped.SelectMany(l => l.Throws)];
                WorldPoint landing = Mean(all.Select(g => g.Landing));
                string? place = all.Select(g => g.LandingPlace).OfType<string>()
                    .GroupBy(p => p, StringComparer.Ordinal)
                    .OrderByDescending(p => p.Count()).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key).FirstOrDefault();
                clusters.Add(new GrenadeCluster(kind.Key, GrenadeIndex.CellOf(landing), landing, place, shaped));
            }
        }

        return
        [
            .. clusters.OrderByDescending(c => c.ThrowCount).ThenBy(c => c.Kind)
                .ThenBy(c => c.Landing.X).ThenBy(c => c.Landing.Y)
        ];
    }

    /// <summary>
    ///     The prototype's lineup id: map, kind, technique and the seed throw's origin and landing on a
    ///     32-unit grid. The proposal replaces it with an id minted once and stored with the lineup.
    /// </summary>
    public static Guid LineupId(IndexedGrenade seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        string key = string.Create(CultureInfo.InvariantCulture,
            $"v2|{seed.Map.ToLowerInvariant()}|{seed.Kind}|{Technique(seed)}|{Q(seed.Origin.X)},{Q(seed.Origin.Y)},{Q(seed.Origin.Z)}|{Q(seed.Landing.X)},{Q(seed.Landing.Y)},{Q(seed.Landing.Z)}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

        static int Q(float v) => (int)MathF.Floor(v / 32f);
    }

    /// <summary>What must match for two throws to be one lineup besides where: jump, strength, running.</summary>
    public static string Technique(IndexedGrenade g)
    {
        ArgumentNullException.ThrowIfNull(g);
        string strength = g.Row.ThrowStrengthClass is ThrowStrengthClass.Half or ThrowStrengthClass.Underhand
            ? g.Row.ThrowStrengthClass.ToString()
            : nameof(ThrowStrengthClass.Full);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(g.Row.JumpThrow ? "jump" : "stand")}-{strength}-{(g.Row.Movement == MovementClass.Running ? "run" : "still")}");
    }

    private static GrenadeLineup Shape(Lineup lineup)
    {
        List<IndexedGrenade> throws =
        [
            .. lineup.Throws.OrderBy(g => g.Demo.Path, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Row.ReleaseTick)
        ];
        Guid id = LineupId(lineup.Seed);

        // Every grid id a member had, so a strat step or clip stem stored under the old grouping resolves.
        HashSet<Guid> aliases = [id];
        foreach (IndexedGrenade g in throws)
        {
            aliases.Add(GrenadeIndex.LineupId(g.Map, g.Kind, GrenadeIndex.CellOf(g.Landing), GrenadeIndex.RoundedOrigin(g.Origin),
                g.Row.JumpThrow));
        }

        return new GrenadeLineup(Mean(throws.Select(t => t.Origin)), lineup.Seed.Row.JumpThrow, throws, id) { AliasIds = [.. aliases] };
    }

    private static List<Seed<T>> Leader<T>(IReadOnlyList<T> items, Func<T, WorldPoint> pos, float radius, float height,
        Func<T, string> key, Func<T, Seed<T>, bool>? fits, Func<T, int> weight)
        where T : class
    {
        Dictionary<(string, int, int), List<T>> cells = [];
        foreach (T item in items)
        {
            AddTo(cells, Cell(key(item), pos(item), radius), item);
        }

        Dictionary<T, int> density = new(ReferenceEqualityComparer.Instance);
        foreach (T item in items)
        {
            WorldPoint p = pos(item);
            int sum = 0;
            foreach (T other in Around(cells, key(item), p, radius))
            {
                if (Near(p, pos(other), radius, height))
                {
                    sum += weight(other);
                }
            }

            density[item] = sum;
        }

        List<Seed<T>> seeds = [];
        Dictionary<(string, int, int), List<Seed<T>>> seedCells = [];
        foreach (T item in items.OrderByDescending(i => density[i]).ThenByDescending(weight)
                     .ThenBy(i => pos(i).X).ThenBy(i => pos(i).Y).ThenBy(i => pos(i).Z))
        {
            WorldPoint p = pos(item);
            Seed<T>? best = null;
            float bestDistance = float.MaxValue;
            foreach (Seed<T> seed in Around(seedCells, key(item), p, radius))
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
                AddTo(seedCells, Cell(key(item), p, radius), seed);
            }
            else
            {
                best.Members.Add(item);
            }
        }

        return seeds;
    }

    private static (string, int, int) Cell(string key, WorldPoint p, float size) =>
        (key, (int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size));

    private static IEnumerable<TItem> Around<TItem>(Dictionary<(string, int, int), List<TItem>> cells, string key, WorldPoint p, float size)
    {
        (string _, int cx, int cy) = Cell(key, p, size);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (cells.TryGetValue((key, cx + dx, cy + dy), out List<TItem>? list))
                {
                    foreach (TItem item in list)
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    private static void AddTo<TItem>(Dictionary<(string, int, int), List<TItem>> cells, (string, int, int) cell, TItem item)
    {
        if (!cells.TryGetValue(cell, out List<TItem>? list))
        {
            list = [];
            cells[cell] = list;
        }

        list.Add(item);
    }

    private static float Planar(WorldPoint a, WorldPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

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

    private static WorldPoint Weighted(IReadOnlyList<Lineup> lineups)
    {
        double x = 0, y = 0, z = 0;
        int n = 0;
        foreach (Lineup l in lineups)
        {
            int w = l.Throws.Count;
            x += l.Landing.X * w;
            y += l.Landing.Y * w;
            z += l.Landing.Z * w;
            n += w;
        }

        return new WorldPoint((float)(x / n), (float)(y / n), (float)(z / n));
    }

    private sealed record Lineup(IndexedGrenade Seed, List<IndexedGrenade> Throws, WorldPoint Landing);

    private sealed class Seed<T>(T first, WorldPoint centre, List<T> members)
    {
        public T First { get; } = first;
        public WorldPoint Centre { get; } = centre;
        public List<T> Members { get; } = members;
    }
}

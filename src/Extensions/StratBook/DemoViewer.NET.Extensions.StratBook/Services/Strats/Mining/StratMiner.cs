#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;

/// <summary>
///     Groups near-identical rounds into patterns: complete-linkage clustering over <see cref="Distance" />,
///     within one map, side, kind and site, cut at <see cref="Cutoff" />. Complete linkage so every member is
///     close to every other, not chained through a neighbour. Pure and deterministic.
/// </summary>
public static class StratMiner
{
    /// <summary>Rounds that make a pattern (owner, 2026-09-27).</summary>
    public const int MinSupport = 2;

    /// <summary>World units at which a player pairing costs 1.</summary>
    public const double PositionScale = 500;

    /// <summary>Two grenades land on the same spot when this close, world units.</summary>
    public const double LandingRadius = 350;

    /// <summary>Two grenades are the same throw when this close in time, seconds.</summary>
    public const double ThrowTimeWindow = 10;

    /// <summary>Seconds of execute timing that cost 1.</summary>
    public const double ExecuteTimeScale = 20;

    public const double PositionWeight = 1.0;
    public const double UtilityWeight = 0.6;
    public const double TimeWeight = 0.3;

    /// <summary>
    ///     What an uncompared utility costs. Positive so a positions-only pair has to sit closer than a pair
    ///     whose utility matched.
    /// </summary>
    public const double UnknownUtilityPenalty = 0.25;

    /// <summary>The linkage cut: two rounds further apart never share a pattern.</summary>
    public const double Cutoff = 0.55;

    /// <summary>Anchor offsets in seconds from a setup's freeze end.</summary>
    public static IReadOnlyList<int> SetupAnchors { get; } = [15, 25];

    /// <summary>Anchor offsets in seconds from an execute's take.</summary>
    public static IReadOnlyList<int> ExecuteAnchors { get; } = [-10, -3];

    /// <summary>A setup's utility window, seconds from freeze end.</summary>
    public static (int From, int To) SetupThrowWindow { get; } = (0, 30);

    /// <summary>An execute's utility window, seconds from the take.</summary>
    public static (int From, int To) ExecuteThrowWindow { get; } = (-25, 5);

    /// <summary>The anchor offsets for a kind.</summary>
    /// <param name="kind">The kind.</param>
    public static IReadOnlyList<int> AnchorsFor(PatternKind kind) => kind == PatternKind.Execute ? ExecuteAnchors : SetupAnchors;

    /// <summary>
    ///     How far apart two rounds are: positions, utility and (for an execute) timing. Infinity when they
    ///     cannot share a pattern: another map, side, kind or site, or no anchor sampled in both.
    /// </summary>
    public static double Distance(RoundSignature a, RoundSignature b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!string.Equals(a.Map, b.Map, StringComparison.OrdinalIgnoreCase) || a.Side != b.Side || a.Kind != b.Kind
            || !string.Equals(a.Site, b.Site, StringComparison.Ordinal))
        {
            return double.PositiveInfinity;
        }

        double positions = PositionDistance(a.Anchors, b.Anchors);
        if (double.IsNaN(positions))
        {
            return double.PositiveInfinity;
        }

        double utility = a.Throws is { } left && b.Throws is { } right
            ? UtilityWeight * (1 - UtilitySimilarity(left, right))
            : UnknownUtilityPenalty;
        double time = a.Kind == PatternKind.Execute
            ? TimeWeight * Math.Min(1, Math.Abs(a.AnchorSeconds - b.AnchorSeconds) / ExecuteTimeScale)
            : 0;
        return PositionWeight * positions + utility + time;
    }

    /// <summary>
    ///     Mean over the anchors of the cheapest pairing of the two sides' players, in <see cref="PositionScale" />
    ///     units. Players pair by position, not slot: the same setup is run by different people in different
    ///     demos. An unpaired player costs 1. NaN when no anchor has players on both.
    /// </summary>
    public static double PositionDistance(IReadOnlyList<IReadOnlyList<MinedPawn>> a, IReadOnlyList<IReadOnlyList<MinedPawn>> b)
    {
        double sum = 0;
        int anchors = 0;
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i].Count == 0 || b[i].Count == 0)
            {
                continue;
            }

            sum += PairingCost(a[i], b[i]);
            anchors++;
        }

        return anchors == 0 ? double.NaN : sum / anchors;
    }

    // Exhaustive over at most 5!/(5-p)! assignments, so exact.
    private static double PairingCost(IReadOnlyList<MinedPawn> a, IReadOnlyList<MinedPawn> b)
    {
        (IReadOnlyList<MinedPawn> small, IReadOnlyList<MinedPawn> large) = a.Count <= b.Count ? (a, b) : (b, a);
        int rows = small.Count;
        int cols = large.Count;
        Span<double> cost = rows * cols <= 64 ? stackalloc double[64] : new double[rows * cols];
        Span<bool> used = cols <= 64 ? stackalloc bool[64] : new bool[cols];
        used = used[..cols];
        used.Clear();
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                double dx = small[i].X - large[j].X, dy = small[i].Y - large[j].Y, dz = small[i].Z - large[j].Z;
                cost[i * cols + j] = Math.Min(1, Math.Sqrt(dx * dx + dy * dy + dz * dz) / PositionScale);
            }
        }

        double best = double.PositiveInfinity;
        Assign(cost, rows, cols, used, 0, 0, ref best);
        return (best + (cols - rows)) / cols;
    }

    private static void Assign(ReadOnlySpan<double> cost, int rows, int cols, Span<bool> used, int i, double acc, ref double best)
    {
        if (acc >= best)
        {
            return;
        }

        if (i == rows)
        {
            best = acc;
            return;
        }

        for (int j = 0; j < cols; j++)
        {
            if (used[j])
            {
                continue;
            }

            used[j] = true;
            Assign(cost, rows, cols, used, i + 1, acc + cost[i * cols + j], ref best);
            used[j] = false;
        }
    }

    /// <summary>
    ///     The share of the two rounds' grenades that pair up: same kind, landing within
    ///     <see cref="LandingRadius" />, thrown within <see cref="ThrowTimeWindow" />; the nearest pairs first.
    ///     Keyed on the landing, not the lineup, since one smoke is thrown from several spots. Two rounds that
    ///     threw nothing are alike.
    /// </summary>
    public static double UtilitySimilarity(IReadOnlyList<MinedThrow> a, IReadOnlyList<MinedThrow> b) =>
        a.Count + b.Count == 0 ? 1 : 2.0 * PairCount(a, b) / (a.Count + b.Count);

    /// <summary>The matched throws of two rounds as index pairs, the closest first.</summary>
    public static List<(int A, int B)> Pairs(IReadOnlyList<MinedThrow> a, IReadOnlyList<MinedThrow> b)
    {
        List<(int, int)> pairs = [];
        Match(a, b, pairs);
        return pairs;
    }

    private static int PairCount(IReadOnlyList<MinedThrow> a, IReadOnlyList<MinedThrow> b) => Match(a, b, null);

    // Greedy over the candidates sorted by (cost, A, B), a total order, so the pairing is deterministic.
    private static int Match(IReadOnlyList<MinedThrow> a, IReadOnlyList<MinedThrow> b, List<(int, int)>? pairs)
    {
        int size = a.Count * b.Count;
        Span<(double Cost, int A, int B)> candidates = size <= 128 ? stackalloc (double, int, int)[128] : new (double, int, int)[size];
        int n = 0;
        for (int i = 0; i < a.Count; i++)
        {
            for (int j = 0; j < b.Count; j++)
            {
                if (a[i].Kind != b[j].Kind)
                {
                    continue;
                }

                double dx = a[i].Landing.X - b[j].Landing.X, dy = a[i].Landing.Y - b[j].Landing.Y, dz = a[i].Landing.Z - b[j].Landing.Z;
                double landing = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double dt = Math.Abs(a[i].Seconds - b[j].Seconds);
                if (landing > LandingRadius || dt > ThrowTimeWindow)
                {
                    continue;
                }

                double cost = landing / LandingRadius + dt / ThrowTimeWindow;
                if (a[i].LineupId is { } lineup && lineup == b[j].LineupId)
                {
                    cost -= 0.5;
                }

                candidates[n++] = (cost, i, j);
            }
        }

        if (n == 0)
        {
            return 0;
        }

        candidates = candidates[..n];
        candidates.Sort(static (x, y) => x.Cost != y.Cost ? x.Cost.CompareTo(y.Cost)
            : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        Span<bool> takenA = a.Count <= 64 ? stackalloc bool[64] : new bool[a.Count];
        Span<bool> takenB = b.Count <= 64 ? stackalloc bool[64] : new bool[b.Count];
        takenA.Clear();
        takenB.Clear();
        int count = 0;
        foreach ((_, int i, int j) in candidates)
        {
            if (takenA[i] || takenB[j])
            {
                continue;
            }

            takenA[i] = takenB[j] = true;
            pairs?.Add((i, j));
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Every pattern among the signatures, the largest first. Signatures are grouped by map, side, kind
    ///     and site and clustered within each group.
    /// </summary>
    /// <param name="signatures">One per side, round and kind.</param>
    /// <param name="cutoff">The linkage cut; <see cref="Cutoff" /> unless calibrating.</param>
    /// <param name="minSupport">Rounds a pattern needs; <see cref="MinSupport" /> unless calibrating.</param>
    public static IReadOnlyList<MinedPattern> Mine(IEnumerable<RoundSignature> signatures, double cutoff = Cutoff,
        int minSupport = MinSupport)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        List<MinedPattern> patterns = [];
        Scratch scratch = new();
        IEnumerable<IGrouping<string, RoundSignature>> groups = DropCopies(signatures)
            .GroupBy(s => $"{s.Map.ToLowerInvariant()}|{s.Side}|{s.Kind}|{s.Site}", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (IGrouping<string, RoundSignature> group in groups)
        {
            List<RoundSignature> members = [.. group.OrderBy(s => s.Key, StringComparer.Ordinal)];
            foreach (List<int> cluster in CompleteLinkage(members, cutoff, scratch))
            {
                if (cluster.Count >= minSupport)
                {
                    patterns.Add(ToPattern([.. cluster.Select(i => members[i])], scratch));
                }
            }
        }

        return
        [
            .. patterns
                .OrderByDescending(p => p.Support)
                .ThenBy(p => p.Spread)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
        ];
    }

    /// <summary>Rounds of two demos this close (same round number, side and kind) are one demo copied.</summary>
    public const double CopyDistance = 0.02;

    /// <summary>
    ///     Drops a round that repeats another demo's round: same map, round number, side and kind and every
    ///     player within a few units. A trimmed or renamed copy of one demo would otherwise pair with its
    ///     original as a perfect pattern. The first by path is kept.
    /// </summary>
    public static IEnumerable<RoundSignature> DropCopies(IEnumerable<RoundSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        foreach (IGrouping<string, RoundSignature> group in signatures
                     .OrderBy(s => s.DemoPath, StringComparer.Ordinal)
                     .ThenBy(s => s.Round)
                     .GroupBy(s => $"{s.Map.ToLowerInvariant()}|{s.Round}|{s.Side}|{s.Kind}", StringComparer.Ordinal))
        {
            List<RoundSignature> kept = [];
            foreach (RoundSignature s in group)
            {
                if (!kept.Any(k => PositionDistance(k.Anchors, s.Anchors) < CopyDistance))
                {
                    kept.Add(s);
                }
            }

            foreach (RoundSignature s in kept)
            {
                yield return s;
            }
        }
    }

    // Merges the closest pair of clusters until none is closer than the cut. A cluster's distance to another
    // is its farthest pair, so a merged cluster's row is the elementwise max of the two it replaced.
    // The matrix is scratch shared across groups; its diagonal is stale and never read.
    private static List<List<int>> CompleteLinkage(List<RoundSignature> items, double cutoff, Scratch scratch)
    {
        int n = items.Count;
        float[] d = scratch.Linkage(n);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double value = Distance(items[i], items[j]);
                d[i * n + j] = d[j * n + i] = double.IsInfinity(value) ? float.PositiveInfinity : (float)value;
            }
        }

        List<List<int>?> clusters = [.. Enumerable.Range(0, n).Select(i => new List<int> { i })];
        int[] nearest = new int[n];
        for (int i = 0; i < n; i++)
        {
            nearest[i] = Nearest(i);
        }

        while (true)
        {
            int a = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (clusters[i] is not null && nearest[i] >= 0 && d[i * n + nearest[i]] < best)
                {
                    best = d[i * n + nearest[i]];
                    a = i;
                }
            }

            if (a < 0 || best > cutoff)
            {
                break;
            }

            int b = nearest[a];
            (int keep, int drop) = a < b ? (a, b) : (b, a);
            clusters[keep]!.AddRange(clusters[drop]!);
            clusters[drop] = null;
            for (int k = 0; k < n; k++)
            {
                if (k != keep && clusters[k] is not null)
                {
                    d[keep * n + k] = d[k * n + keep] = Math.Max(d[keep * n + k], d[drop * n + k]);
                }
            }

            for (int k = 0; k < n; k++)
            {
                if (clusters[k] is not null && (k == keep || nearest[k] == keep || nearest[k] == drop))
                {
                    nearest[k] = Nearest(k);
                }
            }
        }

        return [.. clusters.Where(c => c is not null).Select(c => c!.Order().ToList())];

        int Nearest(int i)
        {
            int arg = -1;
            float min = float.PositiveInfinity;
            for (int k = 0; k < n; k++)
            {
                if (k != i && clusters[k] is not null && d[i * n + k] < min)
                {
                    min = d[i * n + k];
                    arg = k;
                }
            }

            return arg;
        }
    }

    private static MinedPattern ToPattern(List<RoundSignature> members, Scratch scratch)
    {
        int n = members.Count;
        double[] d = scratch.Members(n);
        double spread = 0;
        for (int i = 0; i < n; i++)
        {
            d[i * n + i] = 0;
            for (int j = i + 1; j < n; j++)
            {
                d[i * n + j] = d[j * n + i] = Distance(members[i], members[j]);
                spread = Math.Max(spread, d[i * n + j]);
            }
        }

        int medoid = 0;
        double bestSum = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++)
            {
                sum += d[i * n + j];
            }

            if (sum < bestSum)
            {
                bestSum = sum;
                medoid = i;
            }
        }

        RoundSignature center = members[medoid];
        List<MinedCommonThrow> common = CommonThrows(center, members);
        List<MinedMember> ordered =
        [
            .. Enumerable.Range(0, n)
                .OrderBy(i => i == medoid ? 0 : 1)
                .ThenBy(i => d[medoid * n + i])
                .ThenBy(i => members[i].Key, StringComparer.Ordinal)
                .Select(i => Member(members[i], d[medoid * n + i]))
        ];

        return new MinedPattern
        {
            Key = PatternKey(center, common),
            Kind = center.Kind,
            Map = center.Map,
            Side = center.Side,
            Site = center.Site,
            Members = ordered,
            Medoid = center,
            Spread = spread,
            UtilityCompared = members.All(m => m.Throws is not null),
            CommonThrows = common
        };
    }

    private static MinedMember Member(RoundSignature s, double distance) =>
        new(s.DemoPath, s.Sha256, s.Round, s.TeamId, s.Won, s.Buy, s.FreezeEndTick, s.AnchorTick, s.TickRate, distance);

    // The medoid's throws that at least half the members with grenade rows also threw.
    private static List<MinedCommonThrow> CommonThrows(RoundSignature medoid, List<RoundSignature> members)
    {
        if (medoid.Throws is not { Count: > 0 } throws)
        {
            return [];
        }

        int[] counts = new int[throws.Count];
        int known = 0;
        foreach (RoundSignature member in members)
        {
            if (member.Throws is not { } other)
            {
                continue;
            }

            known++;
            foreach ((int a, _) in Pairs(throws, other))
            {
                counts[a]++;
            }
        }

        return
        [
            .. Enumerable.Range(0, throws.Count)
                .Where(i => counts[i] * 2 >= known)
                .OrderBy(i => throws[i].Seconds)
                .Select(i => new MinedCommonThrow(throws[i], counts[i]))
        ];
    }

    /// <summary>
    ///     The pattern's identity: map, side, kind, site, the common throws' kind and landing (place, else a
    ///     512-unit cell) and the medoid's places at the first anchor. Members come and go as demos are
    ///     indexed; the shape they share is what a dismissal or promotion should follow.
    /// </summary>
    public static string PatternKey(RoundSignature medoid, IReadOnlyList<MinedCommonThrow> common)
    {
        ArgumentNullException.ThrowIfNull(medoid);
        ArgumentNullException.ThrowIfNull(common);
        IEnumerable<string> throws = common
            .Select(c => $"{c.Throw.Kind}@{c.Throw.LandingPlace ?? Cell(c.Throw.Landing)}")
            .Order(StringComparer.Ordinal);
        IEnumerable<string> places = (medoid.Anchors.Count > 0 ? medoid.Anchors[0] : [])
            .Select(p => p.Place ?? "?")
            .Order(StringComparer.Ordinal);
        string text = string.Join("|", medoid.Map.ToLowerInvariant(), medoid.Side, medoid.Kind, medoid.Site ?? "",
            string.Join(",", throws), string.Join(",", places));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
    }

    // One mine's distance matrices, grown to the largest group and reused for the rest.
    private sealed class Scratch
    {
        private float[] _linkage = [];
        private double[] _members = [];

        public float[] Linkage(int n) => _linkage.Length >= n * n ? _linkage : _linkage = new float[n * n];

        public double[] Members(int n) => _members.Length >= n * n ? _members : _members = new double[n * n];
    }

    private static string Cell(WorldPoint p) => string.Create(CultureInfo.InvariantCulture,
        $"{Math.Floor(p.X / 512)},{Math.Floor(p.Y / 512)}");
}

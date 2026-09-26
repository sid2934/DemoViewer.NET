#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Builds a team's <see cref="PostPlantSet" /> (plan.md §3, Post-Plant And Retake) over Round Facts
///     and the Round Index's positions files: per map and side, the rounds with a plant, the man count at
///     the plant and how those rounds ended; on T the plant clusters and the post-plant holds, on CT the
///     retake grouping. A round counts when Team Identity puts the team on that side in it
///     (<see cref="TeamIdentityService.SideAtRound" />); no demo is opened.
///     <para>
///         <b>The plant spot</b> is the planter's position at the sampled step of the plant, or up to two
///         steps before it. <b>Plant clusters</b> gather the spots of one site greedily in round order: a
///         spot joins the nearest cluster whose centre is within <see cref="PlantClusterRadius" />, else
///         starts one.
///     </para>
///     <para>
///         <b>The hold</b> is read <see cref="HoldSeconds" /> after the plant (or at the last sampled step
///         before the end): each alive T player is near the bomb, within <see cref="HoldNearDistance" />,
///         or away from it, and the places they stand in are counted once per round.
///     </para>
///     <para>
///         <b>Retake grouping</b> follows the CTs alive at the plant: each one arrives at the first step it
///         is within <see cref="RetakeRadius" /> of the bomb, and arrivals more than
///         <see cref="RetakeGapSeconds" /> apart (the Suggested Tags retake detector's default gap) start a
///         new group. One group of two or more is "together", more than one group is "trickled".
///     </para>
///     <para>
///         A demo with no current positions file is counted, not read as rounds with no hold or no
///         retake; its rounds still count toward the man count and the outcomes. Stateless apart from its
///         sources and safe off the UI thread: the Dossier builds on a worker.
///     </para>
/// </summary>
public sealed class PostPlantService
{
    /// <summary>World units (XY) from a cluster's centre within which a plant spot joins it.</summary>
    public const int PlantClusterRadius = 300;

    /// <summary>Seconds after the plant the hold is read at.</summary>
    public const int HoldSeconds = 10;

    /// <summary>World units (XY) to the bomb within which a holding player counts as near it.</summary>
    public const int HoldNearDistance = 700;

    /// <summary>World units (XY) to the bomb within which a retaking CT counts as arrived.</summary>
    public const int RetakeRadius = 1000;

    /// <summary>Seconds between one CT's arrival and the next within one group.</summary>
    public const int RetakeGapSeconds = 6;

    /// <summary>Seconds a clip runs before the plant.</summary>
    public const int ClipLeadSeconds = 5;

    /// <summary>Seconds after the plant a clip runs to when the round has no end: the bomb timer and a margin.</summary>
    public const int PostPlantSeconds = 45;

    /// <summary>The retake group of rounds whose CTs reached the bomb in one group of two or more.</summary>
    public const string Together = "together";

    /// <summary>The retake group of rounds whose CTs reached the bomb in more than one group.</summary>
    public const string Trickled = "trickled";

    /// <summary>The retake group of rounds where one CT reached the bomb.</summary>
    public const string OnePlayer = "one player";

    /// <summary>The retake group of rounds where CTs were alive and none reached the bomb.</summary>
    public const string NobodyReached = "nobody reached the bomb";

    /// <summary>The retake group of rounds with no CT alive at the plant.</summary>
    public const string NoCtAlive = "no CT alive";

    /// <summary>The hold of rounds with no T alive when it is read.</summary>
    public const string NoTAlive = "no T alive";

    /// <summary>A position list's bucket for a round whose plant spot the positions do not give.</summary>
    public const string SpotUnread = "spot unread";

    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    private readonly DemoCacheStore _demoCache;
    private readonly Func<string?, string> _fingerprintFor;
    private readonly RoundIndexStore _positions;
    private readonly TeamIdentityService _teams;

    /// <param name="teams">Team Identity: the team's demos and its side per round.</param>
    /// <param name="demoCache">The records: map, hash and Round Facts rows per demo.</param>
    /// <param name="positions">The round positions files, for the spots, the holds and the retakes.</param>
    /// <param name="fingerprintFor">The fingerprint current positions carry per map; a file under another is stale.</param>
    public PostPlantService(TeamIdentityService teams, DemoCacheStore demoCache, RoundIndexStore positions,
        Func<string?, string> fingerprintFor)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fingerprintFor);
        _teams = teams;
        _demoCache = demoCache;
        _positions = positions;
        _fingerprintFor = fingerprintFor;
    }

    /// <summary>The team's post-plant and retake numbers. Reads records and positions files: call it off the UI thread.</summary>
    /// <param name="teamId">The team.</param>
    public PostPlantSet Build(Guid teamId)
    {
        Dictionary<(string Map, int Side), Accumulator> cells = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        int withoutPositions = 0;

        foreach ((DemoRef demo, _, _) in _teams.SidesOf(teamId))
        {
            if (!seen.Add(demo.Path)
                || _demoCache.TryGetIndex(demo.Path)?.Map is not { Length: > 0 } map
                || _demoCache.TryLoadRecord(demo.Path) is not { RoundFacts: { } rows } record)
            {
                continue;
            }

            read++;
            RoundPositionsDocument? positions = _positions.TryReadPositions(demo.Path, _fingerprintFor(map), record.Sha256);
            if (positions is null)
            {
                withoutPositions++;
            }

            int rate = rows.Clock?.TickRate ?? 0;
            if (rate <= 0)
            {
                rate = FallbackTickRate;
            }

            DemoContext context = new(demo.Path, record.Sha256, rate, positions);
            foreach (RoundFacts.RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
            {
                if (!round.IsLive || _teams.SideAtRound(demo.Path, teamId, round.Number) is not { } side)
                {
                    continue;
                }

                if (!cells.TryGetValue((map, side), out Accumulator? cell))
                {
                    cell = new Accumulator();
                    cells[(map, side)] = cell;
                }

                cell.Add(context, round, side);
            }
        }

        List<PostPlantBlock> blocks =
        [
            .. cells
                .GroupBy(c => c.Key.Map, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(c => c.Value.RoundCount))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g
                    .OrderBy(c => c.Key.Side)
                    .Select(c => c.Value.ToBlock(c.Key.Map, c.Key.Side)))
        ];

        return new PostPlantSet
        {
            TeamId = teamId,
            Blocks = blocks,
            DemosRead = read,
            DemosWithoutPositions = withoutPositions
        };
    }

    /// <summary>The alive count at the plant, the team's first: "4v3".</summary>
    /// <param name="round">A round with a plant.</param>
    /// <param name="side">The team's side that round.</param>
    public static string ManCountLabel(RoundFacts.RoundFacts round, int side)
    {
        ArgumentNullException.ThrowIfNull(round);
        (int ct, int t) = RoundPhases.AliveAt(round, round.PlantTick ?? round.FreezeEndTick);
        return side == 3 ? $"{ct}v{t}" : $"{t}v{ct}";
    }

    /// <summary>How a round ended for the side: "won: exploded", "lost: defused", "won: elimination", or "no result".</summary>
    /// <param name="round">The round.</param>
    /// <param name="side">The team's side that round.</param>
    public static string OutcomeLabel(RoundFacts.RoundFacts round, int side)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.WinnerSide is not (2 or 3))
        {
            return "no result";
        }

        string how = round.DefuseTick is not null || round.EndReason == RoundEndReason.BombDefused
            ? "defused"
            : round.ExplodeTick is not null || round.EndReason == RoundEndReason.TargetBombed
                ? "exploded"
                : "elimination";
        return $"{(round.WinnerSide == side ? "won" : "lost")}: {how}";
    }

    /// <summary>
    ///     Where the bomb went down: the planter's position at the plant's sampled step or up to two steps
    ///     before it. Null when the round has no plant or planter, or the positions do not hold them.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="round">The round.</param>
    public static RoundPosition? PlantSpot(RoundPositionsDocument positions, RoundFacts.RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(round);
        if (round.PlantTick is not { } plant || round.PlanterSlot is not { } planter
                                             || positions.Round(round.Number) is not { } stored || positions.CadenceTicks <= 0)
        {
            return null;
        }

        int step = positions.StepFor(stored, plant);
        for (int s = step; s >= Math.Max(0, step - 2); s--)
        {
            foreach (RoundPosition p in stored.At(s))
            {
                if (p.Slot == planter)
                {
                    return p;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     A round's retake: how the CTs alive at the plant reached the bomb at <paramref name="spot" />,
    ///     as one of <see cref="Together" />, <see cref="Trickled" />, <see cref="OnePlayer" />,
    ///     <see cref="NobodyReached" /> or <see cref="NoCtAlive" />. Null when the round is not in the file.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="round">A round with a plant.</param>
    /// <param name="spot">The plant spot.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static string? RetakeGroup(RoundPositionsDocument positions, RoundFacts.RoundFacts round, RoundPosition spot, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(round);
        if (round.PlantTick is not { } plant || positions.Round(round.Number) is not { } stored || positions.CadenceTicks <= 0)
        {
            return null;
        }

        HashSet<int> ct = [.. stored.Ct];
        int first = Math.Max(0, positions.StepFor(stored, plant));
        HashSet<int> alive = [.. stored.At(first).Where(p => ct.Contains(p.Slot)).Select(p => p.Slot)];
        if (alive.Count == 0)
        {
            return NoCtAlive;
        }

        int end = round.EndTick ?? int.MaxValue;
        Dictionary<int, int> arrivals = [];
        for (int step = first; step < stored.Pos.Count && arrivals.Count < alive.Count; step++)
        {
            if (stored.FreezeEndTick + step * positions.CadenceTicks >= end)
            {
                break;
            }

            foreach (RoundPosition p in stored.At(step))
            {
                if (alive.Contains(p.Slot) && !arrivals.ContainsKey(p.Slot) && DistanceXy(p, spot) <= RetakeRadius)
                {
                    arrivals[p.Slot] = step;
                }
            }
        }

        if (arrivals.Count == 0)
        {
            return NobodyReached;
        }

        if (arrivals.Count == 1)
        {
            return OnePlayer;
        }

        double gapSteps = RetakeGapSeconds * (double)tickRate / positions.CadenceTicks;
        List<int> ordered = [.. arrivals.Values.Order()];
        int groups = 1;
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i] - ordered[i - 1] > gapSteps)
            {
                groups++;
            }
        }

        return groups == 1 ? Together : Trickled;
    }

    /// <summary>
    ///     A round's hold: the alive T players at the step <see cref="HoldSeconds" /> after the plant, or
    ///     the last sampled step before the end, never before the plant. Null when the round is not in the file.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="round">A round with a plant.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static IReadOnlyList<RoundPosition>? Hold(RoundPositionsDocument positions, RoundFacts.RoundFacts round, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(round);
        if (round.PlantTick is not { } plant || positions.Round(round.Number) is not { } stored || positions.CadenceTicks <= 0)
        {
            return null;
        }

        int first = Math.Max(0, positions.StepFor(stored, plant));
        int step = positions.StepFor(stored, plant + HoldSeconds * tickRate);
        if (round.EndTick is { } end && stored.FreezeEndTick + step * positions.CadenceTicks >= end)
        {
            step = positions.StepFor(stored, end - 1);
        }

        step = Math.Min(step, stored.Pos.Count - 1);
        HashSet<int> ct = [.. stored.Ct];
        for (int s = step; s >= first; s--)
        {
            if (stored.At(s).Count > 0)
            {
                return [.. stored.At(s).Where(p => !ct.Contains(p.Slot))];
            }
        }

        return [];
    }

    private static double DistanceXy(RoundPosition a, RoundPosition b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string SiteLabel(RoundFacts.RoundFacts round) =>
        round.PlantSite == BombSite.Unknown ? "unknown site" : round.PlantSite.ToString();

    private sealed record DemoContext(string Path, string? Sha256, int Rate, RoundPositionsDocument? Positions);

    // Rounds per label in first-seen order, ranked on the way out.
    private sealed class Buckets
    {
        private readonly Dictionary<string, List<TendencyRound>> _rounds = new(StringComparer.Ordinal);

        public void Add(string label, TendencyRound round)
        {
            if (!_rounds.TryGetValue(label, out List<TendencyRound>? list))
            {
                list = [];
                _rounds[label] = list;
            }

            list.Add(round);
        }

        // Most rounds first, ties by label; the given label (the rounds that could not be read) last.
        public List<TendencyBucket> Ranked(string? last = null) =>
        [
            .. _rounds
                .OrderBy(p => p.Key == last)
                .ThenByDescending(p => p.Value.Count)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new TendencyBucket(p.Key, p.Value))
        ];
    }

    // One site's plant spots, gathered greedily in round order; the centre is the running mean.
    private sealed class Cluster(string site, RoundPosition spot, TendencyRound round)
    {
        private double _sumX = spot.X;
        private double _sumY = spot.Y;

        public string Site { get; } = site;

        public List<TendencyRound> Rounds { get; } = [round];

        public double X => _sumX / Rounds.Count;

        public double Y => _sumY / Rounds.Count;

        public double DistanceTo(RoundPosition p)
        {
            double dx = p.X - X;
            double dy = p.Y - Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public void Add(RoundPosition p, TendencyRound clip)
        {
            _sumX += p.X;
            _sumY += p.Y;
            Rounds.Add(clip);
        }
    }

    private sealed class Accumulator
    {
        private readonly List<Cluster> _clusters = [];
        private readonly Buckets _holdPlaces = new();
        private readonly Buckets _holdShapes = new();
        private readonly Buckets _manCount = new();
        private readonly Buckets _outcomes = new();
        private readonly List<TendencyRound> _planted = [];
        private readonly Buckets _retakes = new();
        private readonly List<TendencyRound> _rounds = [];
        private readonly Buckets _unreadSpots = new();
        private int _positionRounds;

        public int RoundCount => _rounds.Count;

        public void Add(DemoContext demo, RoundFacts.RoundFacts round, int side)
        {
            int rate = demo.Rate;
            _rounds.Add(Clip(demo, round, round.FreezeEndTick, round.EndTick ?? round.FreezeEndTick + 3 * PostPlantSeconds * rate));
            if (round.PlantTick is not { } plant)
            {
                return;
            }

            TendencyRound clip = Clip(demo, round, plant - ClipLeadSeconds * rate, plant + PostPlantSeconds * rate);
            _planted.Add(clip);
            _manCount.Add(ManCountLabel(round, side), clip);
            _outcomes.Add(OutcomeLabel(round, side), clip);

            if (demo.Positions is not { } positions || positions.Round(round.Number) is null)
            {
                return;
            }

            _positionRounds++;
            RoundPosition? spot = PlantSpot(positions, round);
            if (side == 3)
            {
                _retakes.Add(spot is { } bomb ? RetakeGroup(positions, round, bomb, rate) ?? SpotUnread : SpotUnread, clip);
                return;
            }

            AddPlant(round, spot, clip);
            IReadOnlyList<RoundPosition> hold = Hold(positions, round, rate) ?? [];
            if (hold.Count == 0)
            {
                _holdShapes.Add(NoTAlive, clip);
            }
            else if (spot is { } bomb)
            {
                int near = hold.Count(p => DistanceXy(p, bomb) <= HoldNearDistance);
                _holdShapes.Add($"{near} near the bomb, {hold.Count - near} away", clip);
            }
            else
            {
                _holdShapes.Add(SpotUnread, clip);
            }

            foreach (string place in hold.Select(p => positions.PlaceOf(p.PlaceId) ?? "unplaced").Distinct(StringComparer.Ordinal))
            {
                _holdPlaces.Add(place, clip);
            }
        }

        public PostPlantBlock ToBlock(string map, int side) => new()
        {
            Map = map,
            Side = side,
            SideRounds = new TendencyBucket("rounds", [.. _rounds]),
            Planted = new TendencyBucket("planted", [.. _planted]),
            ManCount = _manCount.Ranked(),
            Outcomes = _outcomes.Ranked(),
            PositionRounds = _positionRounds,
            PlantClusters = Clusters(),
            HoldShapes = _holdShapes.Ranked(SpotUnread),
            HoldPlaces = _holdPlaces.Ranked(),
            RetakeGroups = _retakes.Ranked(SpotUnread)
        };

        private void AddPlant(RoundFacts.RoundFacts round, RoundPosition? spot, TendencyRound clip)
        {
            string site = SiteLabel(round);
            if (spot is not { } p)
            {
                _unreadSpots.Add($"{site} {SpotUnread}", clip);
                return;
            }

            Cluster? nearest = _clusters
                .Where(c => c.Site == site && c.DistanceTo(p) <= PlantClusterRadius)
                .OrderBy(c => c.DistanceTo(p))
                .FirstOrDefault();
            if (nearest is null)
            {
                _clusters.Add(new Cluster(site, p, clip));
            }
            else
            {
                nearest.Add(p, clip);
            }
        }

        // Each site's clusters numbered by size, then every cluster most rounds first; the unread spots last.
        private List<TendencyBucket> Clusters()
        {
            List<TendencyBucket> named = [];
            foreach (IGrouping<string, Cluster> site in _clusters.GroupBy(c => c.Site, StringComparer.Ordinal))
            {
                int n = 0;
                foreach (Cluster c in site.OrderByDescending(c => c.Rounds.Count))
                {
                    n++;
                    named.Add(new TendencyBucket(
                        string.Create(CultureInfo.InvariantCulture, $"{site.Key} spot {n} ({Math.Round(c.X):0}, {Math.Round(c.Y):0})"),
                        c.Rounds));
                }
            }

            return
            [
                .. named.OrderByDescending(b => b.Count).ThenBy(b => b.Label, StringComparer.Ordinal),
                .. _unreadSpots.Ranked()
            ];
        }

        // The clip for a round, clamped to the live window: never before freeze end, never past the end.
        private static TendencyRound Clip(DemoContext demo, RoundFacts.RoundFacts round, int from, int to)
        {
            int start = Math.Max(round.FreezeEndTick, from);
            int end = round.EndTick is { } e ? Math.Min(e, to) : to;
            return new TendencyRound(demo.Path, demo.Sha256, round.Number, start, Math.Max(start, end), demo.Rate);
        }
    }
}

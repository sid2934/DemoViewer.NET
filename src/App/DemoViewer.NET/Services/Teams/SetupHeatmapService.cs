#region

using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Builds a team's <see cref="SetupHeatmapSet" /> (plan.md §3, Setup Heatmaps By Buy): for each map
///     and economy state, where the team stood on defence. A round counts when Team Identity puts the
///     team on CT that round (<see cref="TeamIdentityService.SideAtRound" />), its buy is the CT side's
///     Round Facts <see cref="SideFacts.BuyType" />, and its positions are the round positions file's
///     CT tuples over the setup window, so no demo is opened: the same reads the Overlay View makes.
///     <para>
///         <b>The setup window.</b> From <see cref="SetupFromSeconds" /> to <see cref="SetupToSeconds" />
///         after freeze end, cut short at first contact, the plant or the round's end, whichever comes
///         first: after any of those the positions are a fight or a retake, not a setup. A round whose
///         window is empty (contact before the window opened, or no sample) still belongs to the
///         heatmap's rounds and opens with them; it just adds no points.
///     </para>
///     <para>
///         Stateless apart from its sources and safe off the UI thread: the Dossier builds on a worker.
///     </para>
/// </summary>
public sealed class SetupHeatmapService
{
    /// <summary>Seconds after freeze end the setup window opens: players are on their spots by then.</summary>
    public const int SetupFromSeconds = 15;

    /// <summary>Seconds after freeze end the setup window closes when nothing cut it short.</summary>
    public const int SetupToSeconds = 35;

    /// <summary>How far past the window a Review Queue clip runs, so the reviewer sees the setup break.</summary>
    public const int ClipTailSeconds = 5;

    /// <summary>The share of sampled rounds a place must be held in to count as fixed.</summary>
    public const double FixedShare = 0.6;

    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    private readonly DemoCacheStore _demoCache;
    private readonly Func<string?, string> _fingerprintFor;
    private readonly RoundIndexStore _positions;
    private readonly TeamIdentityService _teams;

    /// <param name="teams">Team Identity: the team's demos and its side per round.</param>
    /// <param name="demoCache">The records: map, hash and Round Facts rows per demo.</param>
    /// <param name="positions">The round positions files.</param>
    /// <param name="fingerprintFor">The fingerprint current positions carry per map; a file under another is stale.</param>
    public SetupHeatmapService(TeamIdentityService teams, DemoCacheStore demoCache, RoundIndexStore positions,
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

    /// <summary>The team's heatmaps. Reads records and positions files: call it off the UI thread.</summary>
    /// <param name="teamId">The team.</param>
    public SetupHeatmapSet Build(Guid teamId)
    {
        Dictionary<(string Map, BuyType Buy), Accumulator> cells = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        int without = 0;

        foreach ((DemoRef demo, _, _) in _teams.SidesOf(teamId))
        {
            if (!seen.Add(demo.Path)
                || _demoCache.TryGetIndex(demo.Path)?.Map is not { Length: > 0 } map
                || _demoCache.TryLoadRecord(demo.Path) is not { RoundFacts: { } rows } record)
            {
                continue;
            }

            RoundPositionsDocument? positions = _positions.TryReadPositions(demo.Path, _fingerprintFor(map), record.Sha256);
            if (positions is null)
            {
                without++;
            }
            else
            {
                read++;
            }

            int rate = rows.Clock?.TickRate ?? 0;
            if (rate <= 0)
            {
                rate = FallbackTickRate;
            }

            foreach (RoundFacts.RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
            {
                if (!round.IsLive || _teams.SideAtRound(demo.Path, teamId, round.Number) != 3)
                {
                    continue;
                }

                (string, BuyType) key = (map, round.Ct.BuyType);
                if (!cells.TryGetValue(key, out Accumulator? cell))
                {
                    cell = new Accumulator();
                    cells[key] = cell;
                }

                cell.Add(demo.Path, record.Sha256, round, positions, rate);
            }
        }

        List<SetupHeatmap> heatmaps =
        [
            .. cells
                .GroupBy(c => c.Key.Map, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(c => c.Value.Rounds.Count))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g
                    .OrderBy(c => BuyOrder(c.Key.Buy))
                    .Select(c => c.Value.ToHeatmap(c.Key.Map, c.Key.Buy)))
        ];

        return new SetupHeatmapSet
        {
            TeamId = teamId,
            Heatmaps = heatmaps,
            DemosRead = read,
            DemosWithoutPositions = without
        };
    }

    /// <summary>The setup window of a round in frame-clock ticks, inclusive; empty when <c>to &lt; from</c>.</summary>
    /// <param name="round">The round.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static (int From, int To) SetupWindow(RoundFacts.RoundFacts round, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(round);
        int from = round.FreezeEndTick + SetupFromSeconds * tickRate;
        int to = round.FreezeEndTick + SetupToSeconds * tickRate;
        foreach (int? cut in (int?[]) [round.FirstContactTick, round.PlantTick, round.EndTick])
        {
            if (cut is { } tick)
            {
                to = Math.Min(to, tick - 1);
            }
        }

        return (from, to);
    }

    // Unknown last: a heatmap of rounds whose buy the classifier could not read is still worth a look,
    // but it is the least telling one.
    private static int BuyOrder(BuyType buy) => buy == BuyType.Unknown ? int.MaxValue : (int)buy;

    private sealed class Accumulator
    {
        private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);
        private readonly List<OverlayPoint> _points = [];
        private int _sampledRounds;
        private int _states;

        public List<SetupHeatmapRound> Rounds { get; } = [];

        public void Add(string path, string? sha, RoundFacts.RoundFacts round, RoundPositionsDocument? positions, int rate)
        {
            (int from, int to) = SetupWindow(round, rate);
            bool sampled = false;
            if (to >= from && positions?.Round(round.Number) is { } stored)
            {
                HashSet<int> ct = [.. stored.Ct];
                HashSet<string> places = new(StringComparer.Ordinal);
                int first = positions.StepFor(stored, from);
                int last = positions.StepFor(stored, to);
                for (int step = Math.Max(0, first); step <= last; step++)
                {
                    bool any = false;
                    foreach (RoundPosition tuple in stored.At(step))
                    {
                        if (!ct.Contains(tuple.Slot))
                        {
                            continue;
                        }

                        any = true;
                        _points.Add(new OverlayPoint(tuple.X, tuple.Y, tuple.Z, QuerySide.Ct));
                        if (positions.PlaceOf(tuple.PlaceId) is { } place)
                        {
                            places.Add(place);
                        }
                    }

                    if (any)
                    {
                        _states++;
                        sampled = true;
                    }
                }

                foreach (string place in places)
                {
                    _held[place] = _held.GetValueOrDefault(place) + 1;
                }
            }

            if (sampled)
            {
                _sampledRounds++;
            }

            int clipEnd = round.FreezeEndTick + (SetupToSeconds + ClipTailSeconds) * rate;
            if (round.EndTick is { } end)
            {
                clipEnd = Math.Min(clipEnd, end);
            }

            Rounds.Add(new SetupHeatmapRound(path, sha, round.Number, round.FreezeEndTick, clipEnd, rate, sampled));
        }

        public SetupHeatmap ToHeatmap(string map, BuyType buy) => new()
        {
            Map = map,
            Buy = buy,
            Rounds = [.. Rounds],
            Points = [.. _points],
            StateCount = _states,
            SampledRounds = _sampledRounds,
            Positions =
            [
                .. _held
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new SetupPosition(p.Key, p.Value, _sampledRounds,
                        _sampledRounds > 0 && p.Value >= FixedShare * _sampledRounds))
            ]
        };
    }
}

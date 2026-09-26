#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Builds a team's <see cref="OpeningTendenciesSet" /> (plan.md §3, Opening Tendencies) over the
///     Grenade Index and Round Facts: per map and side, the first utility's timing as a clock histogram
///     and its kind and landing place, the first-contact distribution, the site split, the entry player
///     by site and the lurk timing. A round counts when Team Identity puts the team on that side in it
///     (<see cref="TeamIdentityService.SideAtRound" />); no demo is opened.
///     <para>
///         <b>First utility</b> is the team's earliest release in the round among the Grenade Index rows
///         thrown by its side after freeze end (<see cref="GrenadeRow.ReleaseTick" /> joined to the round by
///         <see cref="GrenadeRow.RoundNumber" />). A demo with no rows in the index is left out of the
///         utility numbers and counted, rather than read as a demo where nobody threw anything.
///     </para>
///     <para>
///         <b>Site</b> is where the bomb went down, so a round with no plant is "no plant". <b>Entry</b> is
///         the team's player in the round's opening duel: the first kill between the two sides' slots,
///         won or lost. <b>Lurk</b> reads the round positions file: from <see cref="LurkFromSeconds" />
///         after freeze end until the plant or the end, the first player who stays at least
///         <see cref="LurkDistance" /> from every alive teammate for <see cref="LurkMinSteps" /> sampled
///         steps while two or more teammates are alive. Its time is the first of those steps. The site
///         split, the entry and the lurk are the T side's: on CT the opponent picks the site.
///     </para>
///     <para>Stateless apart from its sources and safe off the UI thread: the Dossier builds on a worker.</para>
/// </summary>
public sealed class OpeningTendenciesService
{
    /// <summary>The width of one clock histogram bar.</summary>
    public const int ClockBinSeconds = 5;

    /// <summary>The last bar is everything from here on.</summary>
    public const int ClockCapSeconds = 60;

    /// <summary>Seconds after freeze end before a split counts as a lurk: the spawn spread is not one.</summary>
    public const int LurkFromSeconds = 10;

    /// <summary>World units (XY) to the nearest alive teammate for a player to count as alone.</summary>
    public const int LurkDistance = 1500;

    /// <summary>Consecutive sampled steps a player must stay alone for a lurk.</summary>
    public const int LurkMinSteps = 3;

    /// <summary>Seconds a clip runs before the moment it was counted for.</summary>
    public const int ClipLeadSeconds = 5;

    /// <summary>Seconds a clip runs after the moment it was counted for.</summary>
    public const int ClipTailSeconds = 8;

    /// <summary>The bucket of rounds where the team threw nothing.</summary>
    public const string NoUtility = "none";

    /// <summary>The bucket of rounds with no enemy damage at all.</summary>
    public const string NoContact = "no contact";

    /// <summary>The bucket of T rounds with no plant.</summary>
    public const string NoPlant = "no plant";

    /// <summary>The bucket of T rounds where nobody split off alone.</summary>
    public const string NoLurk = "no lurk";

    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    private readonly DemoCacheStore _demoCache;
    private readonly Func<string?, string> _fingerprintFor;
    private readonly GrenadeIndex _grenades;
    private readonly RoundIndexStore _positions;
    private readonly TeamIdentityService _teams;

    /// <param name="teams">Team Identity: the team's demos and its side per round.</param>
    /// <param name="demoCache">The records: map, hash, players and Round Facts rows per demo.</param>
    /// <param name="grenades">The Grenade Index: every thrown grenade's release and landing.</param>
    /// <param name="positions">The round positions files, for the lurk.</param>
    /// <param name="fingerprintFor">The fingerprint current positions carry per map; a file under another is stale.</param>
    public OpeningTendenciesService(TeamIdentityService teams, DemoCacheStore demoCache, GrenadeIndex grenades,
        RoundIndexStore positions, Func<string?, string> fingerprintFor)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(grenades);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fingerprintFor);
        _teams = teams;
        _demoCache = demoCache;
        _grenades = grenades;
        _positions = positions;
        _fingerprintFor = fingerprintFor;
    }

    /// <summary>The team's opening tendencies. Reads records, rows and positions files: call it off the UI thread.</summary>
    /// <param name="teamId">The team.</param>
    public OpeningTendenciesSet Build(Guid teamId)
    {
        Dictionary<(string Map, int Side), Accumulator> cells = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        int withoutGrenades = 0;
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
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase) { demo.Path };
            IReadOnlyList<IndexedGrenade> grenades = _grenades.Rows(new GrenadeQuery(map, DemoPaths: paths));
            if (grenades.Count == 0)
            {
                withoutGrenades++;
            }

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

            Dictionary<int, string> names = [];
            foreach (CachedPlayerInfo player in record.Players)
            {
                names[player.Slot] = DisplayText.Sanitize(player.Name);
            }

            DemoContext context = new(demo.Path, record.Sha256, rate, names, grenades.Count > 0 ? grenades : null, positions);
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

        List<OpeningTendencies> blocks =
        [
            .. cells
                .GroupBy(c => c.Key.Map, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(c => c.Value.RoundCount))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g
                    .OrderBy(c => c.Key.Side)
                    .Select(c => c.Value.ToTendencies(c.Key.Map, c.Key.Side)))
        ];

        return new OpeningTendenciesSet
        {
            TeamId = teamId,
            Blocks = blocks,
            DemosRead = read,
            DemosWithoutGrenades = withoutGrenades,
            DemosWithoutPositions = withoutPositions
        };
    }

    /// <summary>The clock bar a time after freeze end falls in: "0-5 s" to "55-60 s", then "60 s+".</summary>
    /// <param name="seconds">Seconds after freeze end; negative counts as zero.</param>
    public static string ClockLabel(double seconds)
    {
        int bin = ClockBin(seconds);
        return bin * ClockBinSeconds >= ClockCapSeconds
            ? $"{ClockCapSeconds} s+"
            : $"{bin * ClockBinSeconds}-{(bin + 1) * ClockBinSeconds} s";
    }

    /// <summary>
    ///     The team's player in a round's opening duel: the first kill whose attacker and victim sit on
    ///     opposite sides, with the team's slot and whether the team won it. Null when there is no such kill.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="side">The team's side that round.</param>
    public static (KillStep Kill, int Slot, bool Won)? OpeningDuel(RoundFacts.RoundFacts round, int side)
    {
        ArgumentNullException.ThrowIfNull(round);
        HashSet<int> ours = [.. (side == 3 ? round.Ct : round.T).Slots];
        HashSet<int> theirs = [.. (side == 3 ? round.T : round.Ct).Slots];
        foreach (KillStep kill in round.Kills.OrderBy(k => k.Tick))
        {
            if (ours.Contains(kill.AttackerSlot) && theirs.Contains(kill.VictimSlot))
            {
                return (kill, kill.AttackerSlot, true);
            }

            if (theirs.Contains(kill.AttackerSlot) && ours.Contains(kill.VictimSlot))
            {
                return (kill, kill.VictimSlot, false);
            }
        }

        return null;
    }

    /// <summary>
    ///     A round's lurk: the first player of the side who stays alone (see the class notes) for
    ///     <see cref="LurkMinSteps" /> sampled steps, and the tick of the first of them. Null when nobody did.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="round">The round.</param>
    /// <param name="side">The side whose players are read: 2 = T, 3 = CT.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static (int Slot, int Tick)? Lurk(RoundPositionsDocument positions, RoundFacts.RoundFacts round, int side, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(round);
        if (positions.Round(round.Number) is not { } stored || positions.CadenceTicks <= 0)
        {
            return null;
        }

        int end = round.PlantTick ?? round.EndTick ?? int.MaxValue;
        HashSet<int> ct = [.. stored.Ct];
        Dictionary<int, (int Run, int FirstTick)> alone = [];
        int first = Math.Max(0, positions.StepFor(stored, round.FreezeEndTick + LurkFromSeconds * tickRate));
        for (int step = first; step < stored.Pos.Count; step++)
        {
            int tick = stored.FreezeEndTick + step * positions.CadenceTicks;
            if (tick >= end)
            {
                break;
            }

            List<RoundPosition> team = [.. stored.At(step).Where(p => ct.Contains(p.Slot) == (side == 3))];
            if (team.Count < 3)
            {
                alone.Clear();
                continue;
            }

            foreach (RoundPosition player in team)
            {
                if (!team.All(o => o.Slot == player.Slot || DistanceXy(player, o) >= LurkDistance))
                {
                    alone.Remove(player.Slot);
                    continue;
                }

                (int run, int firstTick) = alone.TryGetValue(player.Slot, out (int Run, int FirstTick) was) ? was : (0, tick);
                alone[player.Slot] = (run + 1, firstTick);
                if (run + 1 >= LurkMinSteps)
                {
                    return (player.Slot, firstTick);
                }
            }

            foreach (int gone in alone.Keys.Where(s => team.All(p => p.Slot != s)).ToList())
            {
                alone.Remove(gone);
            }
        }

        return null;
    }

    private static int ClockBin(double seconds) =>
        Math.Min(ClockCapSeconds / ClockBinSeconds, (int)Math.Floor(Math.Max(0, seconds) / ClockBinSeconds));

    private static double DistanceXy(RoundPosition a, RoundPosition b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string KindLabel(GrenadeKind kind) =>
        kind == GrenadeKind.He ? "HE" : kind.ToString().ToLowerInvariant();

    private static string SiteLabel(RoundFacts.RoundFacts round) =>
        round.PlantTick is null ? NoPlant : round.PlantSite == BombSite.Unknown ? "unknown site" : round.PlantSite.ToString();

    private static string NameOf(DemoContext demo, int slot) =>
        demo.Names.TryGetValue(slot, out string? name) && name.Length > 0 ? name : $"slot {slot}";

    private sealed record DemoContext(
        string Path,
        string? Sha256,
        int Rate,
        IReadOnlyDictionary<int, string> Names,
        IReadOnlyList<IndexedGrenade>? Grenades,
        RoundPositionsDocument? Positions);

    // Rounds per label, in first-seen order until a list asks for its own order.
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

        // Every bar from 0 s to the last one with a round, empty bars included so the histogram reads as a
        // clock, then the rounds without the thing counted.
        public List<TendencyBucket> Clock(string none)
        {
            int last = -1;
            for (int bin = 0; bin <= ClockCapSeconds / ClockBinSeconds; bin++)
            {
                if (_rounds.ContainsKey(ClockLabel(bin * ClockBinSeconds)))
                {
                    last = bin;
                }
            }

            List<TendencyBucket> bars = [];
            for (int bin = 0; bin <= last; bin++)
            {
                string label = ClockLabel(bin * ClockBinSeconds);
                bars.Add(new TendencyBucket(label, _rounds.TryGetValue(label, out List<TendencyRound>? r) ? r : []));
            }

            if (_rounds.TryGetValue(none, out List<TendencyRound>? without))
            {
                bars.Add(new TendencyBucket(none, without));
            }

            return bars;
        }

        // Most rounds first, ties by label; the given label (the rounds without) last.
        public List<TendencyBucket> Ranked(string? last = null) =>
        [
            .. _rounds
                .OrderBy(p => p.Key == last)
                .ThenByDescending(p => p.Value.Count)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new TendencyBucket(p.Key, p.Value))
        ];
    }

    private sealed class Accumulator
    {
        private readonly Buckets _contact = new();
        private readonly Buckets _entry = new();
        private readonly Buckets _lurkClock = new();
        private readonly Buckets _lurkers = new();
        private readonly List<TendencyRound> _rounds = [];
        private readonly Buckets _sites = new();
        private readonly Buckets _utilityClock = new();
        private readonly Buckets _utilityPlaces = new();
        private int _lurkRounds;
        private int _utilityRounds;

        public int RoundCount => _rounds.Count;

        public void Add(DemoContext demo, RoundFacts.RoundFacts round, int side)
        {
            int rate = demo.Rate;
            int freezeEnd = round.FreezeEndTick;
            TendencyRound opening = Clip(demo, round, freezeEnd, freezeEnd + ClockCapSeconds * rate);
            _rounds.Add(opening);

            double Seconds(int tick) => (tick - freezeEnd) / (double)rate;

            if (demo.Grenades is { } grenades)
            {
                _utilityRounds++;
                IndexedGrenade? first = grenades
                    .Where(g => g.Row.RoundNumber == round.Number && g.Row.ThrowerTeam == side && g.Row.ReleaseTick >= freezeEnd)
                    .OrderBy(g => g.Row.ReleaseTick)
                    .FirstOrDefault();
                if (first is null)
                {
                    _utilityClock.Add(NoUtility, opening);
                }
                else
                {
                    int release = first.Row.ReleaseTick;
                    TendencyRound clip = Clip(demo, round, release - ClipLeadSeconds * rate,
                        (first.Row.DetonationTick ?? release) + ClipTailSeconds * rate);
                    _utilityClock.Add(ClockLabel(Seconds(release)), clip);
                    _utilityPlaces.Add($"{KindLabel(first.Kind)} at {first.LandingPlace ?? "unplaced"}", clip);
                }
            }

            if (round.FirstContactTick is { } contact)
            {
                _contact.Add(ClockLabel(Seconds(contact)),
                    Clip(demo, round, contact - ClipLeadSeconds * rate, contact + ClipTailSeconds * rate));
            }
            else
            {
                _contact.Add(NoContact, opening);
            }

            if (side != 2)
            {
                return;
            }

            string site = SiteLabel(round);
            TendencyRound siteClip = round.PlantTick is { } plant
                ? Clip(demo, round, plant - 2 * ClipLeadSeconds * rate, plant + ClipTailSeconds * rate)
                : opening;
            _sites.Add(site, siteClip);

            if (OpeningDuel(round, side) is { } duel)
            {
                _entry.Add($"{NameOf(demo, duel.Slot)} at {site}",
                    Clip(demo, round, duel.Kill.Tick - ClipLeadSeconds * rate, duel.Kill.Tick + ClipTailSeconds * rate));
            }

            if (demo.Positions is { } positions && positions.Round(round.Number) is not null)
            {
                _lurkRounds++;
                if (Lurk(positions, round, side, rate) is { } lurk)
                {
                    TendencyRound clip = Clip(demo, round, lurk.Tick - 2 * rate, lurk.Tick + 2 * ClipTailSeconds * rate);
                    _lurkClock.Add(ClockLabel(Seconds(lurk.Tick)), clip);
                    _lurkers.Add(NameOf(demo, lurk.Slot), clip);
                }
                else
                {
                    _lurkClock.Add(NoLurk, opening);
                }
            }
        }

        public OpeningTendencies ToTendencies(string map, int side) => new()
        {
            Map = map,
            Side = side,
            Rounds = new TendencyBucket("rounds", [.. _rounds]),
            FirstUtilityClock = _utilityClock.Clock(NoUtility),
            FirstUtilityPlaces = _utilityPlaces.Ranked(),
            UtilityRounds = _utilityRounds,
            FirstContactClock = _contact.Clock(NoContact),
            SiteSplit = _sites.Ranked(NoPlant),
            EntryBySite = _entry.Ranked(),
            LurkClock = _lurkClock.Clock(NoLurk),
            Lurkers = _lurkers.Ranked(),
            LurkRounds = _lurkRounds
        };

        // The clip for a round, clamped to the live window: never before freeze end, never past the end.
        private static TendencyRound Clip(DemoContext demo, RoundFacts.RoundFacts round, int from, int to)
        {
            int start = Math.Max(round.FreezeEndTick, from);
            int end = round.EndTick is { } e ? Math.Min(e, to) : to;
            return new TendencyRound(demo.Path, demo.Sha256, round.Number, start, Math.Max(start, end), demo.Rate);
        }
    }
}

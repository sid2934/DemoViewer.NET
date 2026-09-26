#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Builds a team's <see cref="SituationalBehaviourSet" /> (plan.md §3, Situational Behaviour) over
///     Round Facts alone: per map and side, the pistol rounds and how they convert into the bonus round,
///     the rounds where the opponent bought eco or semi and how the team played them, the rounds where
///     the team held an alive-count edge and whether it closed them out, and what the team bought the
///     round after a loss. A round counts when Team Identity puts the team on that side in it
///     (<see cref="TeamIdentityService.SideAtRound" />); no demo is opened and no positions file is read,
///     unlike the Setup Heatmaps, Opening Tendencies and Post-Plant sections.
///     <para>
///         <b>Pistol</b> follows Round Facts' own <see cref="BuyType.Pistol" /> classification for the
///         team's side, so it needs no round-number guess. Its follow-up pairs a pistol round with round
///         <c>N + 1</c>, only when that round is live and Team Identity puts the team on the same side in
///         it (the same-half bonus round; a half boundary changes side and is left out).
///     </para>
///     <para>
///         <b>Anti-eco</b> is any round where the opponent's side bought <see cref="BuyType.Eco" /> or
///         <see cref="BuyType.Semi" />, regardless of the team's own buy. <b>Man advantage</b> is read from
///         <see cref="KillStep.CtAlive" />/<see cref="KillStep.TAlive" />, which every kill in
///         <see cref="RoundFacts.RoundFacts.Kills" /> already carries: the largest alive-count edge the team
///         held from freeze end to the round's end, the size of it and the tick it was first reached.
///     </para>
///     <para>
///         <b>Save discipline</b> looks at round <c>N + 1</c> after a round the team lost: what the team
///         (on whichever side Team Identity puts it in that round, even across a half boundary) bought
///         there. A loss with no live next round, or one Team Identity cannot place the team in, is left
///         out.
///     </para>
///     <para>Stateless apart from its sources and safe off the UI thread: the Dossier builds on a worker.</para>
/// </summary>
public sealed class SituationalBehaviourService
{
    /// <summary>Seconds a clip runs before the moment it was counted for.</summary>
    public const int ClipLeadSeconds = 5;

    /// <summary>Seconds a clip runs after the moment it was counted for.</summary>
    public const int ClipTailSeconds = 8;

    /// <summary>Seconds a round clip runs when the round has no end: comfortably past a live round's length.</summary>
    public const int FallbackRoundSeconds = 115;

    /// <summary>The bucket of anti-eco rounds with no enemy damage at all.</summary>
    public const string NoContact = "no contact";

    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    private readonly DemoCacheStore _demoCache;
    private readonly TeamIdentityService _teams;

    /// <param name="teams">Team Identity: the team's demos and its side per round.</param>
    /// <param name="demoCache">The records: map, hash and Round Facts rows per demo.</param>
    public SituationalBehaviourService(TeamIdentityService teams, DemoCacheStore demoCache)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        _teams = teams;
        _demoCache = demoCache;
    }

    /// <summary>The team's situational numbers. Reads records: call it off the UI thread like the other Dossier sections.</summary>
    /// <param name="teamId">The team.</param>
    public SituationalBehaviourSet Build(Guid teamId)
    {
        Dictionary<(string Map, int Side), Accumulator> cells = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;

        foreach ((DemoRef demo, _, _) in _teams.SidesOf(teamId))
        {
            if (!seen.Add(demo.Path)
                || _demoCache.TryGetIndex(demo.Path)?.Map is not { Length: > 0 } map
                || _demoCache.TryLoadRecord(demo.Path) is not { RoundFacts: { } rows } record)
            {
                continue;
            }

            read++;
            int rate = rows.Clock?.TickRate ?? 0;
            if (rate <= 0)
            {
                rate = FallbackTickRate;
            }

            Dictionary<int, RoundFacts.RoundFacts> byNumber = [];
            foreach (RoundFacts.RoundFacts row in rows.Rounds)
            {
                byNumber[row.Number] = row;
            }

            DemoContext context = new(demo.Path, record.Sha256, rate);
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

                byNumber.TryGetValue(round.Number + 1, out RoundFacts.RoundFacts? next);
                int? nextSide = next is { IsLive: true } ? _teams.SideAtRound(demo.Path, teamId, next.Number) : null;
                cell.Add(context, round, side, next, nextSide);
            }
        }

        List<SituationalBehaviourBlock> blocks =
        [
            .. cells
                .GroupBy(c => c.Key.Map, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(c => c.Value.RoundCount))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g
                    .OrderBy(c => c.Key.Side)
                    .Select(c => c.Value.ToBlock(c.Key.Map, c.Key.Side)))
        ];

        return new SituationalBehaviourSet
        {
            TeamId = teamId,
            Blocks = blocks,
            DemosRead = read
        };
    }

    /// <summary>"won"/"lost"/"no result" for the side in the round, the vocabulary <see cref="PistolFollowUpLabel" /> combines.</summary>
    /// <param name="round">The round.</param>
    /// <param name="side">The team's side that round.</param>
    public static string ResultWord(RoundFacts.RoundFacts round, int side)
    {
        ArgumentNullException.ThrowIfNull(round);
        return round.WinnerSide == side ? "won" : round.WinnerSide is 2 or 3 ? "lost" : "no result";
    }

    /// <summary>"won pistol, won the bonus" and the like.</summary>
    /// <param name="pistol">The pistol round.</param>
    /// <param name="bonus">Round <c>N + 1</c>.</param>
    /// <param name="side">The team's side in both.</param>
    public static string PistolFollowUpLabel(RoundFacts.RoundFacts pistol, RoundFacts.RoundFacts bonus, int side) =>
        $"{ResultWord(pistol, side)} pistol, {ResultWord(bonus, side)} the bonus";

    /// <summary>The buy vocabulary's own label, lower case: "eco", "semi buy", "force buy", "full buy", "pistol", "unknown buy".</summary>
    /// <param name="buy">The buy.</param>
    public static string BuyLabel(BuyType buy) => buy switch
    {
        BuyType.Pistol => "pistol",
        BuyType.Eco => "eco",
        BuyType.Semi => "semi buy",
        BuyType.Force => "force buy",
        BuyType.Full => "full buy",
        _ => "unknown buy"
    };

    private static TendencyRound FullRoundClip(DemoContext demo, RoundFacts.RoundFacts round) =>
        ClipRange(demo, round, round.FreezeEndTick, round.EndTick ?? round.FreezeEndTick + FallbackRoundSeconds * demo.Rate);

    private static TendencyRound MomentClip(DemoContext demo, RoundFacts.RoundFacts round, int tick) =>
        ClipRange(demo, round, tick - ClipLeadSeconds * demo.Rate, tick + ClipTailSeconds * demo.Rate);

    // The clip for a round, clamped to the live window: never before freeze end, never past the end.
    private static TendencyRound ClipRange(DemoContext demo, RoundFacts.RoundFacts round, int from, int to)
    {
        int start = Math.Max(round.FreezeEndTick, from);
        int end = round.EndTick is { } e ? Math.Min(e, to) : to;
        return new TendencyRound(demo.Path, demo.Sha256, round.Number, start, Math.Max(start, end), demo.Rate);
    }

    private sealed record DemoContext(string Path, string? Sha256, int Rate);

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
            for (int bin = 0; bin <= OpeningTendenciesService.ClockCapSeconds / OpeningTendenciesService.ClockBinSeconds; bin++)
            {
                if (_rounds.ContainsKey(OpeningTendenciesService.ClockLabel(bin * OpeningTendenciesService.ClockBinSeconds)))
                {
                    last = bin;
                }
            }

            List<TendencyBucket> bars = [];
            for (int bin = 0; bin <= last; bin++)
            {
                string label = OpeningTendenciesService.ClockLabel(bin * OpeningTendenciesService.ClockBinSeconds);
                bars.Add(new TendencyBucket(label, _rounds.TryGetValue(label, out List<TendencyRound>? r) ? r : []));
            }

            if (_rounds.TryGetValue(none, out List<TendencyRound>? without))
            {
                bars.Add(new TendencyBucket(none, without));
            }

            return bars;
        }

        // Most rounds first, ties by label.
        public List<TendencyBucket> Ranked() =>
        [
            .. _rounds
                .OrderByDescending(p => p.Value.Count)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new TendencyBucket(p.Key, p.Value))
        ];
    }

    private sealed class Accumulator
    {
        private readonly List<TendencyRound> _antiEco = [];
        private readonly Buckets _antiEcoBuyTypes = new();
        private readonly Buckets _antiEcoContact = new();
        private readonly Buckets _antiEcoOutcomes = new();
        private readonly List<TendencyRound> _lost = [];
        private readonly Buckets _manAdvantage = new();
        private readonly List<TendencyRound> _manAdvantageRounds = [];
        private readonly Buckets _manAdvantageOutcomes = new();
        private readonly Buckets _pistolFollowUp = new();
        private readonly Buckets _pistolOutcomes = new();
        private readonly List<TendencyRound> _pistols = [];
        private readonly List<TendencyRound> _rounds = [];
        private readonly Buckets _saveDiscipline = new();

        public int RoundCount => _rounds.Count;

        public void Add(DemoContext demo, RoundFacts.RoundFacts round, int side, RoundFacts.RoundFacts? next, int? nextSide)
        {
            TendencyRound clip = FullRoundClip(demo, round);
            _rounds.Add(clip);

            AddPistol(demo, round, side, next, nextSide, clip);
            AddAntiEco(demo, round, side, clip);
            AddManAdvantage(demo, round, side);
            AddSaveDiscipline(demo, round, side, next, nextSide, clip);
        }

        public SituationalBehaviourBlock ToBlock(string map, int side) => new()
        {
            Map = map,
            Side = side,
            SideRounds = new TendencyBucket("rounds", [.. _rounds]),
            PistolRounds = new TendencyBucket("pistols", [.. _pistols]),
            PistolOutcomes = _pistolOutcomes.Ranked(),
            PistolFollowUp = _pistolFollowUp.Ranked(),
            AntiEcoRounds = new TendencyBucket("vs low buy", [.. _antiEco]),
            AntiEcoBuyTypes = _antiEcoBuyTypes.Ranked(),
            AntiEcoOutcomes = _antiEcoOutcomes.Ranked(),
            AntiEcoContactClock = _antiEcoContact.Clock(NoContact),
            ManAdvantageRounds = new TendencyBucket("man advantage", [.. _manAdvantageRounds]),
            ManAdvantage = _manAdvantage.Ranked(),
            ManAdvantageOutcomes = _manAdvantageOutcomes.Ranked(),
            RoundsLost = new TendencyBucket("losses", [.. _lost]),
            SaveDiscipline = _saveDiscipline.Ranked()
        };

        private void AddPistol(DemoContext demo, RoundFacts.RoundFacts round, int side, RoundFacts.RoundFacts? next, int? nextSide,
            TendencyRound clip)
        {
            BuyType ownBuy = side == 3 ? round.Ct.BuyType : round.T.BuyType;
            if (ownBuy != BuyType.Pistol)
            {
                return;
            }

            _pistols.Add(clip);
            _pistolOutcomes.Add(PostPlantService.OutcomeLabel(round, side), clip);
            if (next is { IsLive: true } && nextSide == side)
            {
                _pistolFollowUp.Add(PistolFollowUpLabel(round, next, side), FullRoundClip(demo, next));
            }
        }

        private void AddAntiEco(DemoContext demo, RoundFacts.RoundFacts round, int side, TendencyRound clip)
        {
            BuyType enemyBuy = side == 3 ? round.T.BuyType : round.Ct.BuyType;
            if (enemyBuy is not (BuyType.Eco or BuyType.Semi))
            {
                return;
            }

            _antiEco.Add(clip);
            _antiEcoBuyTypes.Add(BuyLabel(enemyBuy), clip);
            _antiEcoOutcomes.Add(PostPlantService.OutcomeLabel(round, side), clip);
            if (round.FirstContactTick is { } contact)
            {
                double seconds = (contact - round.FreezeEndTick) / (double)demo.Rate;
                _antiEcoContact.Add(OpeningTendenciesService.ClockLabel(seconds), MomentClip(demo, round, contact));
            }
            else
            {
                _antiEcoContact.Add(NoContact, clip);
            }
        }

        private void AddManAdvantage(DemoContext demo, RoundFacts.RoundFacts round, int side)
        {
            int ct = round.Ct.PlayersAtFreezeEnd;
            int t = round.T.PlayersAtFreezeEnd;
            int best = side == 3 ? ct - t : t - ct;
            int bestTick = round.FreezeEndTick;
            foreach (KillStep kill in round.Kills.OrderBy(k => k.Tick))
            {
                ct = kill.CtAlive;
                t = kill.TAlive;
                int margin = side == 3 ? ct - t : t - ct;
                if (margin > best)
                {
                    best = margin;
                    bestTick = kill.Tick;
                }
            }

            if (best <= 0)
            {
                return;
            }

            TendencyRound clip = MomentClip(demo, round, bestTick);
            _manAdvantageRounds.Add(clip);
            _manAdvantage.Add($"{best}-up", clip);
            _manAdvantageOutcomes.Add(round.WinnerSide == side ? "closed it out" : round.WinnerSide is 2 or 3 ? "lead given back" : "no result", clip);
        }

        private void AddSaveDiscipline(DemoContext demo, RoundFacts.RoundFacts round, int side, RoundFacts.RoundFacts? next, int? nextSide,
            TendencyRound clip)
        {
            if (round.WinnerSide is not (2 or 3) || round.WinnerSide == side)
            {
                return;
            }

            _lost.Add(clip);
            if (next is not { IsLive: true } || nextSide is not { } ns)
            {
                return;
            }

            BuyType nextBuy = ns == 3 ? next.Ct.BuyType : next.T.BuyType;
            _saveDiscipline.Add($"{BuyLabel(nextBuy)} next", FullRoundClip(demo, next));
        }
    }
}

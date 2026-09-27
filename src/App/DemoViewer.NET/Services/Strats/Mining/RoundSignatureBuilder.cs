#region

using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Strats.Mining;

/// <summary>A grenade with the lineup the Grenade Index put it in.</summary>
public sealed record MiningGrenade(IndexedGrenade Grenade, Guid? LineupId);

/// <summary>
///     Builds <see cref="RoundSignature" />s from cached files only: Round Facts rows, the round positions
///     files, the Grenade Index and Team Identity. No demo is opened.
///     <para>
///         A setup is every live side's opening: players at freeze end + 15 s and + 25 s and grenades in the
///         first 30 s, all cut at first contact. An execute is a T side reaching a bomb site: the take starts
///         at the first sample with two alive Ts in the site's place (the plant site when there was a plant,
///         else the first site three Ts stood in at once), falling back to the plant when no sample shows it.
///     </para>
/// </summary>
public sealed class RoundSignatureBuilder
{
    /// <summary>Ts in the site's place that start a take.</summary>
    public const int TakeCount = 2;

    /// <summary>Ts in one site at once that make a take on a round without a plant.</summary>
    public const int UnplantedTakeCount = 3;

    /// <summary>Seconds after freeze end before which an execute anchor says nothing.</summary>
    public const int SpawnSeconds = 4;

    private const string SiteAPlace = "BombsiteA";
    private const string SiteBPlace = "BombsiteB";
    private const int FallbackTickRate = 64;

    private readonly DemoCacheStore _demoCache;
    private readonly Func<string?, string> _fingerprintFor;
    private readonly Func<string, IReadOnlyList<MiningGrenade>>? _grenades;
    private readonly RoundIndexStore _positions;
    private readonly TeamIdentityService? _teams;

    /// <param name="demoCache">The records: map, hash and Round Facts rows.</param>
    /// <param name="positions">The round positions files.</param>
    /// <param name="fingerprintFor">The fingerprint current positions carry per map.</param>
    /// <param name="grenades">A map's indexed grenades with their lineups; null when nothing is indexed.</param>
    /// <param name="teams">Team Identity, for the team on each side; null leaves every round unowned.</param>
    public RoundSignatureBuilder(DemoCacheStore demoCache, RoundIndexStore positions, Func<string?, string> fingerprintFor,
        Func<string, IReadOnlyList<MiningGrenade>>? grenades, TeamIdentityService? teams)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fingerprintFor);
        _demoCache = demoCache;
        _positions = positions;
        _fingerprintFor = fingerprintFor;
        _grenades = grenades;
        _teams = teams;
    }

    /// <summary>The Grenade Index's rows for a map with each throw's lineup id.</summary>
    /// <param name="index">The index.</param>
    public static Func<string, IReadOnlyList<MiningGrenade>> FromIndex(GrenadeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return map =>
        {
            List<MiningGrenade> rows = [];
            foreach (GrenadeCluster cluster in index.Query(new GrenadeQuery(map)))
            {
                foreach (GrenadeLineup lineup in cluster.Lineups)
                {
                    rows.AddRange(lineup.Throws.Select(t => new MiningGrenade(t, lineup.Id)));
                }
            }

            return rows;
        };
    }

    /// <summary>Signatures for every indexed demo. Reads files: call it off the UI thread.</summary>
    /// <param name="ct">Stops between demos.</param>
    public IReadOnlyList<RoundSignature> Build(CancellationToken ct = default)
    {
        List<RoundSignature> signatures = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (IGrouping<string, DemoCacheIndexEntry> map in _demoCache.Index
                     .Where(e => e.Map is { Length: > 0 })
                     .GroupBy(e => e.Map!, StringComparer.OrdinalIgnoreCase))
        {
            ILookup<string, MiningGrenade> grenades = (_grenades?.Invoke(map.Key) ?? [])
                .ToLookup(g => g.Grenade.Demo.Path, StringComparer.OrdinalIgnoreCase);
            foreach (DemoCacheIndexEntry entry in map)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(entry.Sha256 ?? entry.Path))
                {
                    continue;
                }

                signatures.AddRange(ForDemo(entry.Path, map.Key, grenades.Contains(entry.Path) ? [.. grenades[entry.Path]] : null));
            }
        }

        return signatures;
    }

    /// <summary>One demo's signatures.</summary>
    /// <param name="path">The demo.</param>
    /// <param name="map">Its map.</param>
    /// <param name="grenades">Its grenades, or null when it has no grenade rows.</param>
    public IEnumerable<RoundSignature> ForDemo(string path, string map, IReadOnlyList<MiningGrenade>? grenades)
    {
        if (_demoCache.TryLoadRecord(path) is not { RoundFacts: { } rows } record
            || _positions.TryReadPositions(path, _fingerprintFor(map), record.Sha256) is not { } positions)
        {
            yield break;
        }

        int rate = rows.Clock?.TickRate is > 0 and var r ? r : FallbackTickRate;
        List<(Team Team, int EndSide)> teams = [];
        if (_teams is not null)
        {
            foreach (int side in (int[]) [2, 3])
            {
                if (_teams.TeamOnSide(path, side) is { } team)
                {
                    teams.Add((team, side));
                }
            }
        }

        foreach (RoundFacts.RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
        {
            if (!round.IsLive || positions.Round(round.Number) is not { } stored)
            {
                continue;
            }

            foreach (int side in (int[]) [2, 3])
            {
                Guid? team = teams.FirstOrDefault(t => _teams!.SideAtRound(path, t.Team.Id, round.Number) == side).Team?.Id;
                Context context = new(path, record.Sha256, map, round, side, rate, positions, stored, grenades, team);
                if (Setup(context) is { } setup)
                {
                    yield return setup;
                }

                if (side == 2 && Execute(context) is { } execute)
                {
                    yield return execute;
                }
            }
        }
    }

    private static RoundSignature? Setup(Context c)
    {
        int contact = c.Round.FirstContactTick ?? int.MaxValue;
        List<IReadOnlyList<MinedPawn>> anchors = [];
        foreach (int offset in StratMiner.SetupAnchors)
        {
            int tick = c.Round.FreezeEndTick + offset * c.Rate;
            anchors.Add(tick < contact ? c.PawnsAt(tick) : []);
        }

        if (anchors.All(a => a.Count == 0))
        {
            return null;
        }

        int from = c.Round.FreezeEndTick + StratMiner.SetupThrowWindow.From * c.Rate;
        int to = Math.Min(contact - 1, c.Round.FreezeEndTick + StratMiner.SetupThrowWindow.To * c.Rate);
        return c.Signature(PatternKind.Setup, null, c.Round.FreezeEndTick, anchors, from, to);
    }

    private static RoundSignature? Execute(Context c)
    {
        (string? site, int? take) = Take(c);
        if (site is null || take is not { } anchor)
        {
            return null;
        }

        // An anchor in the first seconds after freeze end is everyone in spawn, which every rush shares.
        int earliest = c.Round.FreezeEndTick + SpawnSeconds * c.Rate;
        List<IReadOnlyList<MinedPawn>> anchors =
        [
            .. StratMiner.ExecuteAnchors.Select(o => anchor + o * c.Rate)
                .Select(tick => tick < earliest ? new List<MinedPawn>() : c.PawnsAt(tick))
        ];
        if (anchors.All(a => a.Count == 0))
        {
            return null;
        }

        int from = anchor + StratMiner.ExecuteThrowWindow.From * c.Rate;
        int to = anchor + StratMiner.ExecuteThrowWindow.To * c.Rate;
        return c.Signature(PatternKind.Execute, site, anchor, anchors, Math.Max(from, c.Round.FreezeEndTick), to);
    }

    // The site and the tick the take started, or nulls when the Ts never took a site.
    private static (string? Site, int? Tick) Take(Context c)
    {
        int last = c.Round.PlantTick ?? c.Round.EndTick ?? int.MaxValue;
        int steps = c.Stored.Pos.Count;
        if (c.Round.PlantSite is BombSite.A or BombSite.B)
        {
            string site = c.Round.PlantSite == BombSite.A ? "A" : "B";
            string place = site == "A" ? SiteAPlace : SiteBPlace;
            for (int step = 0; step < steps; step++)
            {
                int tick = c.Stored.FreezeEndTick + step * c.Positions.CadenceTicks;
                if (tick > last)
                {
                    break;
                }

                if (c.CountIn(step, place) >= TakeCount)
                {
                    return (site, tick);
                }
            }

            return c.Round.PlantTick is { } plant ? (site, plant) : (null, null);
        }

        for (int step = 0; step < steps; step++)
        {
            int tick = c.Stored.FreezeEndTick + step * c.Positions.CadenceTicks;
            if (tick > last)
            {
                break;
            }

            if (c.CountIn(step, SiteAPlace) >= UnplantedTakeCount)
            {
                return ("A", tick);
            }

            if (c.CountIn(step, SiteBPlace) >= UnplantedTakeCount)
            {
                return ("B", tick);
            }
        }

        return (null, null);
    }

    private sealed record Context(
        string Path,
        string? Sha,
        string Map,
        RoundFacts.RoundFacts Round,
        int Side,
        int Rate,
        RoundPositionsDocument Positions,
        RoundPositionsRound Stored,
        IReadOnlyList<MiningGrenade>? Grenades,
        Guid? Team)
    {
        private readonly HashSet<int> _ct = [.. Stored.Ct];

        private bool OnSide(int slot) => _ct.Contains(slot) == (Side == 3);

        public List<MinedPawn> PawnsAt(int tick)
        {
            int step = Positions.StepFor(Stored, tick);
            if (step < 0 || step >= Stored.Pos.Count)
            {
                return [];
            }

            return
            [
                .. Stored.At(step)
                    .Where(p => OnSide(p.Slot))
                    .OrderBy(p => p.Slot)
                    .Select(p => new MinedPawn(p.Slot, p.X, p.Y, p.Z, Positions.PlaceOf(p.PlaceId)))
            ];
        }

        public int CountIn(int step, string place) =>
            Stored.At(step).Count(p => OnSide(p.Slot) && string.Equals(Positions.PlaceOf(p.PlaceId), place, StringComparison.Ordinal));

        public RoundSignature Signature(PatternKind kind, string? site, int anchor, List<IReadOnlyList<MinedPawn>> anchors,
            int throwFrom, int throwTo)
        {
            SideFacts facts = Side == 3 ? Round.Ct : Round.T;
            return new RoundSignature
            {
                DemoPath = Path,
                Sha256 = Sha,
                Round = Round.Number,
                Map = Map,
                Side = Side,
                Kind = kind,
                Site = site,
                Buy = facts.BuyType,
                Won = Round.WinnerSide is 2 or 3 ? Round.WinnerSide == Side : null,
                TeamId = Team,
                TickRate = Rate,
                FreezeEndTick = Round.FreezeEndTick,
                AnchorTick = anchor,
                Anchors = anchors,
                Throws = Grenades is null ? null : Throws(anchor, throwFrom, throwTo)
            };
        }

        private List<MinedThrow> Throws(int anchor, int from, int to) =>
        [
            .. Grenades!
                .Where(g => g.Grenade.Row.RoundNumber == Round.Number && g.Grenade.Row.ThrowerTeam == Side
                                                                    && g.Grenade.Row.ReleaseTick >= from && g.Grenade.Row.ReleaseTick <= to)
                .OrderBy(g => g.Grenade.Row.ReleaseTick)
                .ThenBy(g => g.Grenade.Row.Id, StringComparer.Ordinal)
                .Select(g => new MinedThrow(g.Grenade.Kind, (g.Grenade.Row.ReleaseTick - anchor) / (double)Rate, g.Grenade.Landing,
                    g.Grenade.LandingPlace, g.Grenade.Origin, g.Grenade.Row.ThrowerSlot, g.LineupId))
        ];
    }
}

#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook;
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

    /// <summary>Part of every cache key: bump it when the anchors, the windows or the take rule change.</summary>
    public const int SignatureVersion = 1;

    private const string SiteAPlace = "BombsiteA";
    private const string SiteBPlace = "BombsiteB";
    private const int FallbackTickRate = 64;

    private readonly SignatureCache? _cache;
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
    /// <param name="cache">Signatures from earlier builds; null reads every demo's files on every build.</param>
    public RoundSignatureBuilder(DemoCacheStore demoCache, RoundIndexStore positions, Func<string?, string> fingerprintFor,
        Func<string, IReadOnlyList<MiningGrenade>>? grenades, TeamIdentityService? teams, SignatureCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fingerprintFor);
        _demoCache = demoCache;
        _positions = positions;
        _fingerprintFor = fingerprintFor;
        _grenades = grenades;
        _teams = teams;
        _cache = cache;
    }

    /// <summary>Drops the signature cache's in-memory entries (written first when dirty); the next build reads the file.</summary>
    public void ReleaseCache() => _cache?.Release();

    /// <summary>Demos the last <see cref="Build" /> took from the cache, and demos it read files for.</summary>
    public (int Reused, int Built) LastBuild { get; private set; }

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
        BuildSession session = Begin();
        for (int i = 0; i < session.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Step(session, i, 1);
        }

        return Finish(session);
    }

    /// <summary>
    ///     The demos a build will read, in build order: one per hash, grouped by map, each with its grenades.
    ///     Reads only memory. <see cref="Step" /> then reads them in batches and <see cref="Finish" /> ends it.
    /// </summary>
    public BuildSession Begin()
    {
        BuildSession session = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (IGrouping<string, DemoCacheIndexEntry> map in _demoCache.Index
                     .Where(e => e.Map is { Length: > 0 })
                     .GroupBy(e => e.Map!, StringComparer.OrdinalIgnoreCase))
        {
            ILookup<string, MiningGrenade> grenades = (_grenades?.Invoke(map.Key) ?? [])
                .ToLookup(g => g.Grenade.Demo.Path, StringComparer.OrdinalIgnoreCase);
            foreach (DemoCacheIndexEntry entry in map)
            {
                if (seen.Add(entry.Sha256 ?? entry.Path))
                {
                    session.Items.Add((entry, map.Key, grenades.Contains(entry.Path) ? [.. grenades[entry.Path]] : null));
                }
            }
        }

        return session;
    }

    /// <summary>Reads demos <paramref name="from" /> to <paramref name="from" /> + <paramref name="count" /> of the session.</summary>
    public void Step(BuildSession session, int from, int count)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (int i = from; i < Math.Min(session.Count, from + count); i++)
        {
            (DemoCacheIndexEntry entry, string map, IReadOnlyList<MiningGrenade>? grenades) = session.Items[i];
            session.Present.Add(entry.Path);
            DemoSignatures? demo;
            string? key = _cache is null ? null : KeyFor(entry, map);
            if (key is not null && _cache!.TryGet(entry.Path, key) is { } hit)
            {
                demo = hit;
                session.Reused++;
            }
            else
            {
                demo = Read(entry.Path, map);
                session.Built++;
                // A demo whose files could not be read is not cached: the next mine tries it again.
                if (key is not null && demo is not null)
                {
                    _cache!.Put(entry.Path, key, demo);
                }
            }

            if (demo is not null)
            {
                session.Signatures.AddRange(Attach(entry.Path, map, demo, grenades));
            }
        }
    }

    /// <summary>Drops cache entries for demos the session did not see, writes the cache, and returns the signatures.</summary>
    public IReadOnlyList<RoundSignature> Finish(BuildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_cache is not null)
        {
            _cache.Retain(session.Present);
            _cache.Save();
        }

        LastBuild = (session.Reused, session.Built);
        return session.Signatures;
    }

    /// <summary>One build in progress; see <see cref="Begin" />.</summary>
    public sealed class BuildSession
    {
        internal List<(DemoCacheIndexEntry Entry, string Map, IReadOnlyList<MiningGrenade>? Grenades)> Items { get; } = [];

        internal List<RoundSignature> Signatures { get; } = [];

        internal HashSet<string> Present { get; } = new(StringComparer.Ordinal);

        internal int Reused { get; set; }

        internal int Built { get; set; }

        /// <summary>Demos to read.</summary>
        public int Count => Items.Count;
    }

    /// <summary>One demo's signatures.</summary>
    /// <param name="path">The demo.</param>
    /// <param name="map">Its map.</param>
    /// <param name="grenades">Its grenades, or null when it has no grenade rows.</param>
    public IEnumerable<RoundSignature> ForDemo(string path, string map, IReadOnlyList<MiningGrenade>? grenades) =>
        Read(path, map) is { } demo ? Attach(path, map, demo, grenades) : [];

    // Everything a demo's signatures are read from, so a change to any of them rebuilds it. Grenades are not in
    // it: throws are attached fresh on every build. The file stamps catch a rewrite the index row does not show.
    private string KeyFor(DemoCacheIndexEntry entry, string map)
    {
        string teams = "-";
        if (_teams is not null && _teams.GetAssignment(entry.Path) is { } assignment)
        {
            teams = string.Create(CultureInfo.InvariantCulture,
                $"{_teams.TeamOnSide(entry.Path, 2)?.Id}:{assignment.T.TeamId}:{string.Join(',', assignment.T.Key)}/{_teams.TeamOnSide(entry.Path, 3)?.Id}:{assignment.Ct.TeamId}:{string.Join(',', assignment.Ct.Key)}");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{SignatureVersion}|{entry.Sha256}|{entry.Size}|{entry.ModifiedTicks}|{entry.RoundFactsStamp()?.Schema ?? 0}|{entry.RoundFactsStamp()?.Fingerprint}|{entry.RoundIndexComputedAtTicks()}|{_fingerprintFor(map)}|{FileStamp(_demoCache.SidecarPathFor(entry.Path))}|{FileStamp(_positions.PositionsPathFor(entry.Path))}|{teams}");
    }

    private static string FileStamp(string? file)
    {
        if (file is null)
        {
            return "-";
        }

        FileInfo info = new(file);
        return info.Exists ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}") : "none";
    }

    // Null when the record has no Round Facts rows or the positions file is missing or stale.
    private DemoSignatures? Read(string path, string map)
    {
        if (_demoCache.TryLoadWithRoundFacts(path) is not ({ } record, { } rows)
            || _positions.TryReadPositions(path, _fingerprintFor(map), record.Sha256) is not { } positions)
        {
            return null;
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

        List<CachedSignature> signatures = [];
        foreach (RoundFacts.RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
        {
            if (!round.IsLive || positions.Round(round.Number) is not { } stored)
            {
                continue;
            }

            foreach (int side in (int[]) [2, 3])
            {
                Guid? team = teams.FirstOrDefault(t => _teams!.SideAtRound(path, t.Team.Id, round.Number) == side).Team?.Id;
                Context context = new(round, side, rate, positions, stored, team);
                if (Setup(context) is { } setup)
                {
                    signatures.Add(setup);
                }

                if (side == 2 && Execute(context) is { } execute)
                {
                    signatures.Add(execute);
                }
            }
        }

        return new DemoSignatures(record.Sha256, signatures);
    }

    private static IEnumerable<RoundSignature> Attach(string path, string map, DemoSignatures demo,
        IReadOnlyList<MiningGrenade>? grenades) =>
        demo.Signatures.Select(s => new RoundSignature
        {
            DemoPath = path,
            Sha256 = demo.Sha256,
            Round = s.Round,
            Map = map,
            Side = s.Side,
            Kind = s.Kind,
            Site = s.Site,
            Buy = s.Buy,
            Won = s.Won,
            TeamId = s.TeamId,
            TickRate = s.TickRate,
            FreezeEndTick = s.FreezeEndTick,
            AnchorTick = s.AnchorTick,
            Anchors = s.Anchors,
            Throws = grenades is null ? null : Throws(grenades, s)
        });

    private static List<MinedThrow> Throws(IReadOnlyList<MiningGrenade> grenades, CachedSignature s) =>
    [
        .. grenades
            .Where(g => g.Grenade.Row.RoundNumber == s.Round && g.Grenade.Row.ThrowerTeam == s.Side
                                                          && g.Grenade.Row.ReleaseTick >= s.ThrowFrom && g.Grenade.Row.ReleaseTick <= s.ThrowTo)
            .OrderBy(g => g.Grenade.Row.ReleaseTick)
            .ThenBy(g => g.Grenade.Row.Id, StringComparer.Ordinal)
            .Select(g => new MinedThrow(g.Grenade.Kind, (g.Grenade.Row.ReleaseTick - s.AnchorTick) / (double)s.TickRate, g.Grenade.Landing,
                g.Grenade.LandingPlace, g.Grenade.Origin, g.Grenade.Row.ThrowerSteamId ?? 0, g.Grenade.Row.ThrowerName, g.LineupId))
    ];

    private static CachedSignature? Setup(Context c)
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

    private static CachedSignature? Execute(Context c)
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
        RoundFacts.RoundFacts Round,
        int Side,
        int Rate,
        RoundPositionsDocument Positions,
        RoundPositionsRound Stored,
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

        public CachedSignature Signature(PatternKind kind, string? site, int anchor, List<IReadOnlyList<MinedPawn>> anchors,
            int throwFrom, int throwTo)
        {
            SideFacts facts = Side == 3 ? Round.Ct : Round.T;
            return new CachedSignature(Round.Number, Side, kind, site, facts.BuyType,
                Round.WinnerSide is 2 or 3 ? Round.WinnerSide == Side : null, Team, Rate, Round.FreezeEndTick, anchor, anchors,
                throwFrom, throwTo);
        }
    }
}

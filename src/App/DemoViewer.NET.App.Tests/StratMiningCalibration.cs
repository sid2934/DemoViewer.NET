#region

using System.Diagnostics;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.Services.Teams;
using TUnit.Core.Exceptions;
using DemoViewer.NET.Services.Zones;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Prints the miner's numbers over a copy of a real library. Runs only when <c>STRAT_MINE_CONFIG</c> names
///     a config root; never point it at a live one.
/// </summary>
[NotInParallel]
public class StratMiningCalibration
{
    [Test]
    [Category("Calibration")]
    public async Task PrintTheNumbers()
    {
        string? root = Environment.GetEnvironmentVariable("STRAT_MINE_CONFIG");
        if (string.IsNullOrEmpty(root))
        {
            throw new SkipTestException("STRAT_MINE_CONFIG not set");
        }

        string cache = Path.Combine(root, "cache");
        DemoCacheStore demoCache = new(cache);
        RoundIndexStore positions = new(cache, demoCache);
        AssetZonePlaceResolverSource zones = new();
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn, zones);
        using GrenadeIndex grenades = new(demoCache, zones);
        grenades.Load();
        TeamIdentityService teams = new(root, demoCache, new CachedFacts(demoCache));
        await teams.StartAsync();

        Console.WriteLine($"[mine] teams={teams.AllTeams.Count} " + string.Join(", ", teams.AllTeams.Select(t => $"{t.Name}:{teams.DemosOf(t.Id).Count}")));
        Stopwatch watch = Stopwatch.StartNew();
        RoundSignatureBuilder builder = new(demoCache, positions, sources.FingerprintFor, RoundSignatureBuilder.FromIndex(grenades), teams);
        IReadOnlyList<RoundSignature> signatures = builder.Build();
        Console.WriteLine($"[mine] demos={demoCache.Index.Count} grenadeDemos={grenades.DemoCount} signatures={signatures.Count} in {watch.ElapsedMilliseconds} ms");
        foreach (IGrouping<(PatternKind, int), RoundSignature> g in signatures.GroupBy(s => (s.Kind, s.Side)).OrderBy(g => g.Key))
        {
            Console.WriteLine($"[mine]   {g.Key.Item1} side={g.Key.Item2}: {g.Count()} rounds, {g.Count(s => s.Throws is not null)} with grenade rows, {g.Count(s => s.TeamId is not null)} with a team");
        }

        foreach (double cutoff in (double[]) [0.35, 0.45, 0.55, 0.65])
        {
            watch.Restart();
            IReadOnlyList<MinedPattern> patterns = StratMiner.Mine(signatures, cutoff);
            long ms = watch.ElapsedMilliseconds;
            int backToBack = patterns.Count(p => p.Support == 2 && p.Demos == 1 && Math.Abs(p.Members[0].Round - p.Members[1].Round) == 1);
            string line = string.Join(" ", patterns.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g =>
                $"{g.Key}={g.Count()} (support 2: {g.Count(p => p.Support == 2)}, 3-4: {g.Count(p => p.Support is 3 or 4)}, 5+: {g.Count(p => p.Support >= 5)}, utility compared: {g.Count(p => p.UtilityCompared)}, multi-demo: {g.Count(p => p.Demos > 1)}, multi-team: {g.Count(p => p.Teams.Count > 1)})"));
            Console.WriteLine($"[mine] cutoff {cutoff:0.00}: {patterns.Count} patterns in {ms} ms, same-demo back-to-back pairs {backToBack}; {line}");
        }

        IReadOnlyList<MinedPattern> chosen = StratMiner.Mine(signatures);
        foreach (MinedPattern p in chosen.Where(p => p.Spread < 0.05).Take(4))
        {
            Console.WriteLine($"[mine] tight {p.Spread:0.000}: " + string.Join(" | ", p.Members.Select(m => $"{Path.GetFileName(m.DemoPath)} r{m.Round} sha={m.Sha256?[..8]}")));
        }

        foreach (MinedPattern p in chosen.Where(p => p.UtilityCompared).Take(15).Concat(chosen.Where(p => !p.UtilityCompared).Take(8)))
        {
            string common = string.Join(", ", p.CommonThrows.Select(c => $"{c.Throw.Kind}@{c.Throw.LandingPlace ?? "?"} {c.Throw.Seconds:+0;-0}s ({c.Rounds})"));
            string places = string.Join(",", p.Medoid.Anchors[0].Select(a => a.Place ?? "?").Order());
            string teamNames = string.Join("/", p.Teams.Select(t => teams.AllTeams.FirstOrDefault(x => x.Id == t)?.Name ?? "?"));
            Console.WriteLine($"[mine] {p.Map} {(p.Side == 2 ? "T" : "CT")} {p.Kind} {p.Site} support={p.Support} demos={p.Demos} won={p.Wins} spread={p.Spread:0.00} util={p.UtilityCompared} teams={teamNames} take={p.Medoid.AnchorSeconds:0}s places=[{places}] common=[{common}]");
        }
    }

    private sealed class CachedFacts(DemoCacheStore cache) : IRoundFactsSource
    {
        public int Schema => 0;

        public RoundFactsRows? TryGet(string demoPath) => cache.TryLoadRecord(demoPath)?.RoundFacts;

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }
    }
}

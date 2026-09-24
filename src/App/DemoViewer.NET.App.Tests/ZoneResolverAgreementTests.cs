#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The design's baseline measurement, re-run through the real reader and resolver: for every
///     placed sample on every 32nd frame of every demo under <c>DEMO_PATH</c> whose map has a
///     <c>zones.json</c>, the cascade's answer is compared with the pawn's own
///     <c>m_szLastPlaceName</c>. The samples of every demo of a map are POOLED and the pooled
///     agreement must clear that map's floor (decision D7, taken 2026-09-24: pooled, not per demo).
///     The per-demo and per-place tables still go to the test output so a map that slips can be read
///     off, demo by demo and place by place.
///     <para>
///         Pooled because a single demo is not a measurement of the resolver: an abandoned 9.4-minute
///         mirage replay scored 90.35 percent on its own while the ten mirage demos together scored
///         92.83, and the difference was the sticky pawn field (Underpass held while the pawn stood on
///         Catwalk), not the cascade. The field is the last place the pawn was inside, so part of the
///         residual is the field, not the resolver; the floors state a miss rate rather than pretend to
///         separate the two. Skipped unless <c>DEMO_PATH</c> names a demo or a folder of them. A
///         replays folder holds hundreds of matches, so <c>DEMO_ZONES_PER_MAP</c> (unset means every
///         demo) caps how many are measured per map, smallest first; the map is read off the header,
///         not a parse.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ZoneResolverAgreementTests
{
    private const int FrameStride = 32;
    private const double DefaultFloor = 90;
    private const string PerMapEnvVar = "DEMO_ZONES_PER_MAP";

    // Each floor is the pooled agreement measured over the smallest ten demos of the map in the Steam
    // replays folder (DEMO_ZONES_PER_MAP=10, 83 demos, 2026-09-24), less one point, rounded down to
    // the whole number. A map with only one demo would keep the design's floor (section 7.2); every
    // map with a zones.json had at least two. The pooled values, with the per-demo spread they hide:
    //   de_ancient   96.95 % over ten demos  (96.22 to 98.44)   floor 95
    //   de_anubis    97.99 % over ten demos  (97.64 to 98.36)   floor 96
    //   de_cache     98.82 % over four demos (98.72 to 98.93)   floor 97
    //   de_dust2     99.43 % over ten demos  (99.22 to 99.56)   floor 98
    //   de_inferno   97.28 % over ten demos  (94.59 to 98.40)   floor 96
    //   de_mirage    92.91 % over ten demos  (90.35 to 94.42)   floor 91
    //                (92.83 in the Phase 0 fix pass; the low demo is the abandoned 9.4-minute replay,
    //                and with DEMO_ZONES_PER_MAP=2 it is half the pool: 91.67 over the smallest two,
    //                which is why the floor is measured minus one and not the 92 first proposed)
    //   de_nuke      96.79 % over ten demos  (96.02 to 97.77)   floor 95
    //   de_overpass  94.66 % over ten demos  (92.56 to 96.09)   floor 93
    //   de_train     98.41 % over six demos  (98.23 to 98.56)   floor 97
    //   de_vertigo   97.09 % over two demos  (96.48 to 97.27)   floor 96
    // de_dogtown has no zones.json and is skipped; DefaultFloor covers a map baked after this table.
    private static readonly Dictionary<string, double> _floors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_ancient"] = 95,
        ["de_anubis"] = 96,
        ["de_cache"] = 97,
        ["de_dust2"] = 98,
        ["de_inferno"] = 96,
        ["de_mirage"] = 91,
        ["de_nuke"] = 95,
        ["de_overpass"] = 93,
        ["de_train"] = 97,
        ["de_vertigo"] = 96
    };

    [Test]
    public async Task CascadeAgreement_ClearsThePerMapFloor_OnThePooledSamplesUnderDemoPath()
    {
        string[] demos = ResolveDemos();
        Dictionary<string, Pool> pools = new(StringComparer.OrdinalIgnoreCase);
        List<string> failures = [];

        foreach (string path in demos)
        {
            ParsedDemo demo = DemoTestHelper.GetOrParse(path);
            string? map = demo.MapName;
            string? bundleDir = MapAssetBundleReader.FindBundleDirectory(map);
            PlaceResolver? resolver = ZoneAssetPipeline.TryLoad(bundleDir, null);
            if (resolver is null)
            {
                Console.WriteLine($"[agreement] {Path.GetFileName(path)}: {map ?? "?"} has no zones.json, skipped");
                continue;
            }

            Measurement m = Measure(demo, resolver);
            double floor = _floors.GetValueOrDefault(map!, DefaultFloor);
            Console.WriteLine(m.Report(Path.GetFileName(path), map!, floor));

            if (m.Placed == 0)
            {
                // Pooling would hide a demo the sampler found nothing in, and that is a defect of
                // its own (the walk, or the field), not a low agreement.
                failures.Add($"{Path.GetFileName(path)}: no placed samples");
                continue;
            }

            if (!pools.TryGetValue(map!, out Pool? pool))
            {
                pool = new Pool();
                pools[map!] = pool;
            }

            pool.Add(Path.GetFileName(path), m);
        }

        if (pools.Count == 0 && failures.Count == 0)
        {
            throw new SkipTestException("no demo under DEMO_PATH is on a map with a zones.json");
        }

        foreach ((string map, Pool pool) in pools.OrderBy(p => p.Key))
        {
            double floor = _floors.GetValueOrDefault(map, DefaultFloor);
            Console.WriteLine(pool.Report(map, floor));
            if (pool.AgreementPercent < floor)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{map}: {pool.AgreementPercent:F2} % pooled agreement over {pool.Demos} demo(s) " +
                    $"is under the {floor} % floor (per demo: {pool.PerDemo()})"));
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    private static Measurement Measure(ParsedDemo demo, PlaceResolver resolver)
    {
        Measurement m = new(resolver.Zones.EffectiveVersion);
        foreach (PositionSample sample in PositionSampler.Walk(demo, FrameStride, demo.Frames.Count))
        {
            m.Samples++;
            if (string.IsNullOrEmpty(sample.Place))
            {
                continue;
            }

            m.Add(sample.Place, resolver.Resolve(sample.Position));
        }

        return m;
    }

    // DEMO_PATH is a folder for this test (the Steam replays directory, read in place), or one demo.
    // Smallest first so a failing map is reported before the largest match has parsed, and grouped
    // by the header's map name so the per-map cap applies before anything is parsed.
    private static string[] ResolveDemos()
    {
        string? env = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (string.IsNullOrWhiteSpace(env))
        {
            throw new SkipTestException("DEMO_PATH is not set; it names a .dem or a folder of them");
        }

        if (File.Exists(env))
        {
            return [env];
        }

        if (!Directory.Exists(env))
        {
            throw new SkipTestException($"DEMO_PATH does not exist: {env}");
        }

        string[] all = [.. new DirectoryInfo(env).EnumerateFiles("*.dem").OrderBy(f => f.Length).Select(f => f.FullName)];
        if (all.Length == 0)
        {
            throw new SkipTestException($"DEMO_PATH folder holds no .dem: {env}");
        }

        int perMap = int.TryParse(Environment.GetEnvironmentVariable(PerMapEnvVar), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int cap) && cap > 0
            ? cap
            : int.MaxValue;

        Dictionary<string, int> taken = new(StringComparer.OrdinalIgnoreCase);
        List<string> chosen = [];
        foreach (string path in all)
        {
            string map = DownstreamUtilities.TryReadQuickInfo(path, out DownstreamUtilities.DemoQuickInfo info) &&
                         !string.IsNullOrEmpty(info.MapName)
                ? info.MapName
                : "?";
            int count = taken.GetValueOrDefault(map);
            if (count >= perMap)
            {
                continue;
            }

            taken[map] = count + 1;
            chosen.Add(path);
        }

        Console.WriteLine($"[agreement] {chosen.Count} of {all.Length} demos selected " +
                          $"({(perMap == int.MaxValue ? "no" : perMap.ToString(CultureInfo.InvariantCulture))} per-map cap): " +
                          string.Join(", ", taken.OrderBy(t => t.Key).Select(t => $"{t.Key} x{t.Value}")));
        return [.. chosen];
    }

    // One map's demos summed: the assertion reads this, the per-demo tables are for reading.
    private sealed class Pool
    {
        private readonly List<(string Demo, double Percent)> _demos = [];

        public int Demos => _demos.Count;
        public int Placed { get; private set; }
        public int Agreed { get; private set; }
        public double AgreementPercent => Placed == 0 ? 0 : 100.0 * Agreed / Placed;

        public void Add(string demo, Measurement m)
        {
            Placed += m.Placed;
            Agreed += m.Agreed;
            _demos.Add((demo, m.AgreementPercent));
        }

        public string PerDemo() => string.Join(", ",
            _demos.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Demo} {d.Percent:F2}")));

        public string Report(string map, double floor) => string.Create(CultureInfo.InvariantCulture,
            $"[agreement] POOLED {map}  demos={Demos}  placed={Placed}  agreed={Agreed}  " +
            $"agree={AgreementPercent:F2} %  min={_demos.Min(d => d.Percent):F2} %  max={_demos.Max(d => d.Percent):F2} %  " +
            $"floor={floor} %  {(AgreementPercent >= floor ? "OK" : "UNDER")}");
    }

    private sealed class Measurement(string zonesVersion)
    {
        private readonly Dictionary<string, PlaceStat> _byPlace = new(StringComparer.Ordinal);

        public int Samples { get; set; }
        public int Placed { get; private set; }
        public int Agreed { get; private set; }
        public int Unresolved { get; private set; }
        public double AgreementPercent => Placed == 0 ? 0 : 100.0 * Agreed / Placed;

        public void Add(string pawnPlace, PlaceHit hit)
        {
            Placed++;
            if (!_byPlace.TryGetValue(pawnPlace, out PlaceStat? stat))
            {
                stat = new PlaceStat();
                _byPlace[pawnPlace] = stat;
            }

            stat.Total++;
            if (string.Equals(hit.Name, pawnPlace, StringComparison.Ordinal))
            {
                Agreed++;
                stat.Agreed++;
                return;
            }

            if (!hit.IsHit)
            {
                Unresolved++;
            }

            string other = hit.Name ?? "(none)";
            stat.Confusions[other] = stat.Confusions.GetValueOrDefault(other) + 1;
        }

        public string Report(string demo, string map, double floor)
        {
            StringBuilder sb = new();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"[agreement] {demo}  map={map}  zones={zonesVersion}  samples={Samples}  placed={Placed}  " +
                $"agree={AgreementPercent:F2} %  unresolved={Unresolved}  floor={floor} %  " +
                $"{(AgreementPercent >= floor ? "OK" : "UNDER")}"));
            sb.AppendLine("[agreement]   place                    samples   agree%   top miss");
            foreach ((string place, PlaceStat stat) in _byPlace.OrderByDescending(p => p.Value.Total))
            {
                string miss = stat.Confusions.Count == 0
                    ? "-"
                    : stat.Confusions.OrderByDescending(c => c.Value).Select(c => $"{c.Key} ({c.Value})").First();
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"[agreement]   {place,-24} {stat.Total,7}   {100.0 * stat.Agreed / stat.Total,6:F1}   {miss}"));
            }

            return sb.ToString().TrimEnd();
        }

        private sealed class PlaceStat
        {
            public int Total;
            public int Agreed;
            public Dictionary<string, int> Confusions { get; } = new(StringComparer.Ordinal);
        }
    }
}

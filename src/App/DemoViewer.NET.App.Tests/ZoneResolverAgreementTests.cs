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
///     <c>m_szLastPlaceName</c>, and the agreement must clear a per-map floor set one point under
///     what was measured (decision D7). The per-place table goes to the test output so a map that
///     slips can be read off, place by place.
///     <para>
///         The pawn field is sticky (it is the last place the pawn was inside), so part of the residual
///         is the field, not the resolver; the floors state a miss rate rather than pretend to separate
///         the two. Skipped unless <c>DEMO_PATH</c> names a demo or a folder of them. A replays folder
///         holds hundreds of matches, so <c>DEMO_ZONES_PER_MAP</c> (unset means every demo) caps how
///         many are measured per map, smallest first; the map is read off the header, not a parse.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ZoneResolverAgreementTests
{
    private const int FrameStride = 32;
    private const double DefaultFloor = 90;
    private const string PerMapEnvVar = "DEMO_ZONES_PER_MAP";

    private static readonly Dictionary<string, double> _floors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_nuke"] = 96,
        ["de_mirage"] = 92,
        ["de_dust2"] = 99,
        ["de_inferno"] = 96
    };

    [Test]
    public async Task CascadeAgreement_ClearsThePerMapFloor_OnEveryDemoUnderDemoPath()
    {
        string[] demos = ResolveDemos();
        List<string> failures = [];
        int measured = 0;

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
            measured++;
            double floor = _floors.GetValueOrDefault(map!, DefaultFloor);
            Console.WriteLine(m.Report(Path.GetFileName(path), map!, floor));

            if (m.Placed == 0)
            {
                failures.Add($"{Path.GetFileName(path)}: no placed samples");
            }
            else if (m.AgreementPercent < floor)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)} ({map}): {m.AgreementPercent:F1} % agreement is under the {floor} % floor"));
            }
        }

        if (measured == 0)
        {
            throw new SkipTestException("no demo under DEMO_PATH is on a map with a zones.json");
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

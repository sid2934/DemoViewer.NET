#region

using System.Globalization;
using System.Numerics;
using System.Text;
using SteamDatabase.ValvePak;
using ValveResourceFormat.NavMesh;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Derives real walkable <b>floor bands</b> from a map's CS2 nav mesh (the headline of the baked bundle). The
///     baker owns this via VRF's <see cref="NavMeshFile" /> — the app never parses nav. Each nav area is a
///     walkable polygon carrying true world Z; clustering the area-Z distribution (weighted by walkable area,
///     so big flat floors dominate and ramps/catwalks don't spawn phantom bands) yields the storeys. Output
///     feeds the app's <c>FloorSplitter.SetSectionHeights</c> as an authoritative override of its Z-histogram
///     heuristic — nav Z is stable from frame 1, needs no warmup, and doesn't collapse under stair traffic.
/// </summary>
public static class NavFloors
{
    // Clustering tuning (world units / fractions). Calibrated against real nav data (see --diag).
    private const double BucketWidth = 32; // nav Z is precise; finer than the player-Z histogram's 64u.
    private const double MinPeakFraction = 0.05; // a floor holds ≥5% of total walkable area (filters catwalks).

    private const double ValleyDepthFraction = 0.35; // two peaks are separate floors iff the valley drops below

    // this fraction of the smaller peak.
    private const double MinFloorSeparation = 190; // two density peaks are the SAME floor if their Z is closer

    // than this (world u). Real storeys are well separated;
    // within-floor sub-peaks (adjacent rooms at slightly
    // different heights) must not each spawn a band.
    private const double OuterEdge = 100_000; // outermost bands extend here so any player Z maps to a floor.

    // Footprint-overlap gate. The Z histogram can't tell a real stacked storey (nuke's lower level UNDER the
    // upper; vertigo's floors) from one sloped floor whose low and high ends sit in DIFFERENT parts of the map
    // (de_ancient): both look bimodal with the same peak separation and valley depth. The discriminator is
    // horizontal: a genuine split has the two bands occupying the same XY footprint (you can stand at the same
    // X,Y on both levels); a slope does not. We rasterize each band's area bounding-boxes onto a coarse XY grid
    // and require their overlap (∩ / smaller band) to clear the threshold, else the boundary is a slope → merged.
    // Measured (∩/smaller): de_ancient 0.05 (reject) vs de_vertigo 0.59 / de_nuke 0.97 (keep).
    private const double FootprintCell = 64; // XY grid cell (world u) for footprint rasterization.
    private const double MinFloorFootprintOverlap = 0.25; // below this, the two bands don't stack → same floor.

    /// <summary>Reads <c>maps/&lt;map&gt;.nav</c> out of the per-map vpk.</summary>
    public static byte[] ExtractNav(string vpkPath, string mapName)
    {
        using Package package = new();
        package.Read(vpkPath);
        string navPath = $"maps/{mapName}.nav";
        PackageEntry entry = package.FindEntry(navPath)
                             ?? throw new FileNotFoundException($"{navPath} not found in {vpkPath}");
        package.ReadEntry(entry, out byte[] bytes);
        return bytes;
    }

    public static Result ComputeFloors(byte[] navBytes)
    {
        NavMeshFile nav = new();
        using (MemoryStream ms = new(navBytes))
        {
            nav.Read(ms);
        }

        // Per-area representative Z (avg corner Z) weighted by walkable area (shoelace on XY); also the area's
        // XY bounding box, so the footprint-overlap gate can tell a stacked storey from a sloped single floor.
        List<AreaSample> samples = new(nav.Areas.Count);
        foreach (NavMeshArea area in nav.Areas.Values)
        {
            Vector3[]? corners = area.Corners;
            if (corners is null || corners.Length < 3)
            {
                continue;
            }

            double zSum = 0;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int c = 0; c < corners.Length; c++)
            {
                zSum += corners[c].Z;
                minX = Math.Min(minX, corners[c].X);
                maxX = Math.Max(maxX, corners[c].X);
                minY = Math.Min(minY, corners[c].Y);
                maxY = Math.Max(maxY, corners[c].Y);
            }

            double repZ = zSum / corners.Length;
            double weight = Math.Max(PolygonAreaXy(corners), 1.0);
            samples.Add(new AreaSample(repZ, weight, minX, minY, maxX, maxY));
        }

        if (samples.Count == 0)
        {
            return new Result(new List<FloorBand>
            {
                new(-OuterEdge, OuterEdge)
            }, 0, 0, 0, "(no nav areas)");
        }

        double minZ = samples.Min(s => s.Z);
        double maxZ = samples.Max(s => s.Z);

        // Area-weighted histogram.
        SortedDictionary<int, double> hist = new();
        double total = 0;
        foreach (AreaSample s in samples)
        {
            int b = (int)Math.Floor(s.Z / BucketWidth);
            hist.TryGetValue(b, out double cur);
            hist[b] = cur + s.Weight;
            total += s.Weight;
        }

        int lo = hist.Keys.First();
        int hi = hist.Keys.Last();
        int n = hi - lo + 1;
        double[] counts = new double[n];
        double peakW = 0;
        foreach (KeyValuePair<int, double> kv in hist)
        {
            counts[kv.Key - lo] = kv.Value;
            peakW = Math.Max(peakW, kv.Value);
        }

        List<double> candidates = FindBoundaries(counts, lo, total);
        List<double> boundaries = FilterByFootprintOverlap(candidates, samples, out string overlapDiag);
        List<FloorBand> floors = BandsFromBoundaries(boundaries);

        string diag = BuildDiagnostic(counts, lo, peakW, samples.Count, minZ, maxZ, boundaries, floors) + overlapDiag;
        return new Result(floors, samples.Count, minZ, maxZ, diag);
    }

    // Density-valley clustering (mirrors the proven FloorSplitter, area-weighted). Returns interior boundary
    // Z values (world units); empty ⇒ a single floor.
    private static List<double> FindBoundaries(double[] counts, int lo, double total)
    {
        int n = counts.Length;
        double minPeak = MinPeakFraction * total;

        List<int> peaks = new();
        for (int b = 0; b < n; b++)
        {
            if (counts[b] < minPeak)
            {
                continue;
            }

            bool geLeft = b == 0 || counts[b] >= counts[b - 1];
            bool gtLeft = b == 0 || counts[b] > counts[b - 1];
            bool geRight = b == n - 1 || counts[b] >= counts[b + 1];
            bool gtRight = b == n - 1 || counts[b] > counts[b + 1];
            if (geLeft && gtRight || gtLeft && geRight)
            {
                peaks.Add(b);
            }
        }

        List<double> boundaries = new();
        if (peaks.Count <= 1)
        {
            return boundaries;
        }

        int lastPeak = peaks[0];
        for (int i = 1; i < peaks.Count; i++)
        {
            int a = lastPeak, c = peaks[i];
            double valley = double.MaxValue;
            int valleyIdx = a;
            for (int j = a; j <= c; j++)
            {
                if (counts[j] < valley)
                {
                    valley = counts[j];
                    valleyIdx = j;
                }
            }

            double smaller = Math.Min(counts[a], counts[c]);
            double peakSeparation = (c - a) * BucketWidth;
            if (peakSeparation >= MinFloorSeparation && valley < ValleyDepthFraction * smaller)
            {
                boundaries.Add((lo + valleyIdx + 0.5) * BucketWidth); // world Z at the valley bucket centre
                lastPeak = c;
            }
            else if (counts[c] > counts[lastPeak])
            {
                // Same floor (sub-peak too close, or valley too shallow): track the taller peak as this
                // floor's representative so the NEXT peak's separation is measured from the floor's crest.
                lastPeak = c;
            }
        }

        return boundaries;
    }

    // Drops any candidate boundary whose two adjacent Z bands don't share enough XY footprint to be a real
    // stacked storey (they're the low and high ends of one sloped floor instead). Bands are the ranges between
    // neighbouring candidates (±∞ at the outermost); a coarse XY grid rasterizes each band's area boxes and we
    // keep the boundary only if ∩/smaller ≥ MinFloorFootprintOverlap. Emits a per-boundary diagnostic line.
    private static List<double> FilterByFootprintOverlap(
        List<double> candidates, List<AreaSample> samples, out string diag)
    {
        if (candidates.Count == 0)
        {
            diag = string.Empty;
            return candidates;
        }

        StringBuilder sb = new();
        List<double> kept = new();
        for (int i = 0; i < candidates.Count; i++)
        {
            double b = candidates[i];
            double lowEdge = i == 0 ? double.NegativeInfinity : candidates[i - 1];
            double highEdge = i == candidates.Count - 1 ? double.PositiveInfinity : candidates[i + 1];
            double overlap = FootprintOverlap(samples, lowEdge, b, highEdge);
            bool keep = overlap >= MinFloorFootprintOverlap;
            if (keep)
            {
                kept.Add(b);
            }

            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  → footprint gate: boundary {b:F0}  overlap(∩/smaller)={overlap:F2}  " +
                $"{(keep ? "KEEP (stacked storey)" : "DROP (sloped single floor)")}"));
        }

        diag = sb.ToString();
        return kept;
    }

    // Rasterizes the XY bounding boxes of the areas in the band below the boundary and the band above it onto a
    // FootprintCell grid, and returns intersection-over-smaller of the two occupied cell sets (0 if either empty).
    private static double FootprintOverlap(
        List<AreaSample> samples, double lowEdge, double boundary, double highEdge)
    {
        HashSet<(int, int)> below = new();
        HashSet<(int, int)> above = new();
        foreach (AreaSample s in samples)
        {
            HashSet<(int, int)>? set =
                s.Z >= lowEdge && s.Z < boundary ? below
                : s.Z >= boundary && s.Z < highEdge ? above
                : null;
            if (set is null)
            {
                continue;
            }

            int gx0 = (int)Math.Floor(s.MinX / FootprintCell), gx1 = (int)Math.Floor(s.MaxX / FootprintCell);
            int gy0 = (int)Math.Floor(s.MinY / FootprintCell), gy1 = (int)Math.Floor(s.MaxY / FootprintCell);
            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    set.Add((gx, gy));
                }
            }
        }

        if (below.Count == 0 || above.Count == 0)
        {
            return 0;
        }

        int inter = 0;
        foreach ((int, int) cell in below)
        {
            if (above.Contains(cell))
            {
                inter++;
            }
        }

        return inter / (double)Math.Min(below.Count, above.Count);
    }

    private static List<FloorBand> BandsFromBoundaries(List<double> boundaries)
    {
        List<FloorBand> floors = new();
        double lower = -OuterEdge;
        foreach (double b in boundaries)
        {
            floors.Add(new FloorBand(lower, b));
            lower = b;
        }

        floors.Add(new FloorBand(lower, OuterEdge));
        return floors;
    }

    private static double PolygonAreaXy(Vector3[] corners)
    {
        double area = 0;
        int n = corners.Length;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = corners[i];
            Vector3 b = corners[(i + 1) % n];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }

        return Math.Abs(area) / 2;
    }

    private static string BuildDiagnostic(double[] counts, int lo, double peakW, int areaCount,
        double minZ, double maxZ, List<double> boundaries, List<FloorBand> floors)
    {
        StringBuilder sb = new();
        sb.AppendLine($"  areas={areaCount}  Z[{minZ:F0}..{maxZ:F0}]  buckets={counts.Length}");
        for (int b = 0; b < counts.Length; b++)
        {
            double z = (lo + b) * BucketWidth;
            int bars = peakW > 0 ? (int)Math.Round(counts[b] / peakW * 40) : 0;
            if (counts[b] > 0)
            {
                sb.AppendLine($"    z {z,7:F0}  {new string('#', bars),-40}  {counts[b],10:F0}");
            }
        }

        sb.AppendLine($"  → boundaries: [{string.Join(", ", boundaries.Select(x => x.ToString("F0", CultureInfo.InvariantCulture)))}]");
        sb.AppendLine($"  → floors: {floors.Count}  " +
                      string.Join("  ", floors.Select(f => $"[{f.MinZ:F0}..{f.MaxZ:F0}]")));
        return sb.ToString();
    }

    public sealed record Result(
        IReadOnlyList<FloorBand> Floors,
        int AreaCount,
        double MinZ,
        double MaxZ,
        string Diagnostic);

    // One walkable nav area reduced to what the clusterer needs: a representative world Z, its walkable
    // weight, and its XY bounding box (for the footprint-overlap gate).
    private readonly record struct AreaSample(
        double Z,
        double Weight,
        float MinX,
        float MinY,
        float MaxX,
        float MaxY);
}

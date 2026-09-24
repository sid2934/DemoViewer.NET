#region

using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>One queried pair: <paramref name="Count" /> players in <paramref name="Place" />.</summary>
/// <param name="Place">A raw place name as the rows spell it.</param>
/// <param name="Count">How many; what "how many" means depends on the tolerance.</param>
public sealed record PlaceQuery(string Place, int Count);

/// <summary>
///     How loosely a queried pair matches a token. <see cref="Exact" /> means exactly N; the wider
///     levels mean at least N over a growing neighbourhood, so the count never falls as the slider
///     loosens (the Tolerance Slider's done bar). A neighbourhood sum cannot be exact without becoming
///     non-monotone, which is why the two semantics differ on purpose.
/// </summary>
public enum SituationTolerance
{
    /// <summary>The token holds the place with exactly the count.</summary>
    Exact,

    /// <summary>
    ///     The sum over the place and its neighbours is at least the count, and the side's alive total
    ///     is at least the sum of the queried counts (so two places sharing a neighbourhood cannot count
    ///     one player twice). Needs an adjacency graph.
    /// </summary>
    Adjacent,

    /// <summary>The same over the place, its neighbours and their neighbours. Needs an adjacency graph.</summary>
    TwoHops,

    /// <summary>The side's alive total is at least the sum of the queried counts.</summary>
    AnyPlace
}

/// <summary>
///     A cross-demo situation query. A side with no pairs is unconstrained; both unconstrained
///     matches every indexed round of the map. The optional filters narrow the same hit set, so the
///     count after filtering equals the filtered result count.
/// </summary>
/// <param name="Map">The map, as the demo header spells it.</param>
/// <param name="Ct">The CT side's pairs; empty leaves the side unconstrained.</param>
/// <param name="T">The T side's pairs; empty leaves the side unconstrained.</param>
/// <param name="Tolerance">How loosely the pairs match.</param>
/// <param name="Facts">A Round Facts filter joined by (path, round number); null applies none.</param>
/// <param name="Demos">Stable keys to restrict to, from Team Identity or the Library filter; null means every demo.</param>
/// <param name="IndexedAfterTicks">Only demos whose index stamp is newer than this (Watched Situations); null means all.</param>
public sealed record SituationQuery(
    string Map,
    IReadOnlyList<PlaceQuery> Ct,
    IReadOnlyList<PlaceQuery> T,
    SituationTolerance Tolerance = SituationTolerance.Exact,
    RoundFactsFilter? Facts = null,
    IReadOnlySet<string>? Demos = null,
    long? IndexedAfterTicks = null);

/// <summary>One matching (demo, round). Every tick is frame clock.</summary>
/// <param name="DemoPath">The demo's path as the library knows it.</param>
/// <param name="DemoStableKey">The derived-store join key.</param>
/// <param name="DemoSha256">The content hash when known.</param>
/// <param name="Map">The map.</param>
/// <param name="RoundNumber">The Round Facts round number.</param>
/// <param name="FreezeEndTick">The round's freeze end.</param>
/// <param name="FirstMatchTick">The first sampled tick that matched; Result Cards seek ten seconds before it.</param>
/// <param name="LastMatchTick">The last sampled tick that matched.</param>
/// <param name="MatchedSteps">How many sampled steps matched, so Overlay View can weight by duration.</param>
public sealed record SituationHit(
    string DemoPath,
    string DemoStableKey,
    string? DemoSha256,
    string Map,
    int RoundNumber,
    int FreezeEndTick,
    int FirstMatchTick,
    int LastMatchTick,
    int MatchedSteps);

/// <summary>A demo's sidecar was merged into the in-memory index: the Watched Situations hook.</summary>
/// <param name="DemoPath">The demo's path.</param>
/// <param name="DemoStableKey">The derived-store join key.</param>
/// <param name="DemoSha256">The content hash when known.</param>
/// <param name="Map">The map.</param>
/// <param name="ComputedAtTicks">The index stamp (UTC ticks); a watch's watermark compares against this.</param>
public sealed record RoundIndexedEvent(
    string DemoPath,
    string DemoStableKey,
    string? DemoSha256,
    string Map,
    long ComputedAtTicks);

/// <summary>One place's alive samples across the loaded library, per Z bucket.</summary>
/// <param name="Place">The raw place name.</param>
/// <param name="SampleCount">Alive samples in the place over every loaded demo.</param>
/// <param name="Buckets">The per-bucket centroids, bucket ascending.</param>
public sealed record PlaceSummary(string Place, int SampleCount, IReadOnlyList<PlaceZBucket> Buckets);

/// <summary>The centroid of one place's samples on one 64-unit Z bucket (<c>MapSpace.QuantizeZ</c> of the sample Z, never a level key).</summary>
/// <param name="ZBucket">The quantized sample Z.</param>
/// <param name="Count">Samples in the bucket.</param>
/// <param name="CentroidX">Mean X.</param>
/// <param name="CentroidY">Mean Y.</param>
public sealed record PlaceZBucket(int ZBucket, int Count, double CentroidX, double CentroidY);

/// <summary>
///     A place-adjacency graph: which places touch. The zone graph is authoritative when the map has
///     zones; the empirical graph the index folds from its own transitions is the fallback that gives
///     the Tolerance Slider its middle stops before Zone Baking's resolver lands.
/// </summary>
public interface IPlaceAdjacency
{
    /// <summary><c>zones:&lt;zonesVersion&gt;</c> or <c>index:&lt;demoCount&gt;</c>; the slider names it in its tooltip.</summary>
    string Source { get; }

    /// <summary>The places adjacent to <paramref name="place" />; empty for an unknown place.</summary>
    /// <param name="place">A raw place name.</param>
    IReadOnlySet<string> Neighbours(string place);
}

/// <summary>
///     The graph the index already has: two places are adjacent when an alive player crossed between
///     them in one second at least <see cref="Threshold" /> times over the library. Measured on nuke
///     that keeps the real callout neighbours and drops the four skip-throughs; it is wrong in two known
///     ways (a fast player crossing a small place links its two neighbours, and a place nobody walks
///     through has no edges), both erring towards a looser match, the direction the slider loosens in.
/// </summary>
public sealed class EmpiricalPlaceAdjacency : IPlaceAdjacency
{
    /// <summary>Transition observations a pair needs before it is an edge.</summary>
    public const int Threshold = 3;

    private static readonly IReadOnlySet<string> _none = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _neighbours = new(StringComparer.Ordinal);

    /// <param name="transitions">Unordered pairs with their library-wide counts, already folded.</param>
    /// <param name="demoCount">How many demos the counts came from; part of <see cref="Source" />.</param>
    public EmpiricalPlaceAdjacency(IEnumerable<PlaceTransition> transitions, int demoCount)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        foreach (PlaceTransition transition in transitions)
        {
            if (transition.Count < Threshold
                || string.Equals(transition.A, transition.B, StringComparison.Ordinal))
            {
                continue;
            }

            Link(transition.A, transition.B);
            Link(transition.B, transition.A);
        }

        Source = $"index:{demoCount}";
    }

    /// <inheritdoc />
    public string Source { get; }

    /// <summary>How many places have at least one edge.</summary>
    public int PlaceCount => _neighbours.Count;

    /// <inheritdoc />
    public IReadOnlySet<string> Neighbours(string place) =>
        _neighbours.TryGetValue(place, out HashSet<string>? set) ? set : _none;

    private void Link(string from, string to)
    {
        if (!_neighbours.TryGetValue(from, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _neighbours[from] = set;
        }

        set.Add(to);
    }
}

/// <summary>Zone Baking's graph, through the resolver seam. Authoritative when the map has zones.</summary>
public sealed class ZonePlaceAdjacency : IPlaceAdjacency
{
    private readonly IZonePlaceResolver _resolver;

    /// <param name="resolver">The map's resolver over its effective zone set.</param>
    public ZonePlaceAdjacency(IZonePlaceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        Source = $"zones:{resolver.ZonesVersion}";
    }

    /// <inheritdoc />
    public string Source { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> Neighbours(string place) => _resolver.Adjacent(place);
}

/// <summary>
///     The Query Canvas snap without Zone Baking: the nearest place centroid on a Z band. Buckets are
///     by sample Z, so nuke's <c>Ramp</c>, which spans both storeys, gets a centroid per storey and a
///     click on either pane snaps to the right one. With zones present the canvas prefers
///     <c>PlaceResolver.ResolveOnFloor</c>; both produce a raw name, so the token is the same either way.
/// </summary>
public static class PlaceSnap
{
    /// <summary>
    ///     Folds the buckets inside <c>[minZ, maxZ)</c> and returns the nearest place centroid on that
    ///     band, or null when nothing is within <paramref name="maxDistance" /> or the band has no samples.
    /// </summary>
    /// <param name="places">The map's place summaries.</param>
    /// <param name="x">The click's world X.</param>
    /// <param name="y">The click's world Y.</param>
    /// <param name="minZ">The pane's band lower bound.</param>
    /// <param name="maxZ">The pane's band upper bound, exclusive.</param>
    /// <param name="maxDistance">World units beyond which no place is near enough.</param>
    public static (string Place, double Distance)? Nearest(
        IReadOnlyList<PlaceSummary> places,
        double x,
        double y,
        double minZ,
        double maxZ,
        double maxDistance = 512)
    {
        ArgumentNullException.ThrowIfNull(places);

        (string Place, double Distance)? best = null;
        foreach (PlaceSummary place in places)
        {
            int count = 0;
            double sumX = 0;
            double sumY = 0;
            foreach (PlaceZBucket bucket in place.Buckets)
            {
                if (bucket.ZBucket < minZ || bucket.ZBucket >= maxZ)
                {
                    continue;
                }

                count += bucket.Count;
                sumX += bucket.CentroidX * bucket.Count;
                sumY += bucket.CentroidY * bucket.Count;
            }

            if (count == 0)
            {
                continue;
            }

            double dx = sumX / count - x;
            double dy = sumY / count - y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= maxDistance && (best is null || distance < best.Value.Distance))
            {
                best = (place.Place, distance);
            }
        }

        return best;
    }
}

/// <summary>
///     The read API over the in-memory situation index. A DI singleton delegate-injected into the
///     Situations module; queries run in microseconds, so the live count can run one per token drag.
/// </summary>
public interface ISituationIndex
{
    /// <summary>False until the startup load has finished; queries before that return nothing.</summary>
    bool IsReady { get; }

    /// <summary>Demos whose sidecar is loaded.</summary>
    int IndexedDemoCount { get; }

    /// <summary>Loaded demos whose index was built under another fingerprint than the current one.</summary>
    int StaleDemoCount { get; }

    /// <summary>One hit per matching (demo, round), newest demo first then round number. Safe off the UI thread.</summary>
    /// <param name="query">The query.</param>
    IReadOnlyList<SituationHit> Query(SituationQuery query);

    /// <summary>Equals <c>Query(query).Count</c> by construction: the same path with the materialisation skipped.</summary>
    /// <param name="query">The query.</param>
    int Count(SituationQuery query);

    /// <summary>The map's observed places with their per-bucket centroids, for the snap.</summary>
    /// <param name="map">The map.</param>
    IReadOnlyList<PlaceSummary> Places(string map);

    /// <summary>The zone graph when the map has zones, else the empirical one when it has an indexed demo, else null.</summary>
    /// <param name="map">The map.</param>
    IPlaceAdjacency? Adjacency(string map);

    /// <summary>The load finished, or a demo was merged or dropped. Raised through the post delegate.</summary>
    event Action? Changed;

    /// <summary>A demo's sidecar was merged. Raised through the post delegate after the merge.</summary>
    event Action<RoundIndexedEvent>? Indexed;
}

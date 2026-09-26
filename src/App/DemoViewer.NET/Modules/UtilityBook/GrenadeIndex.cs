#region

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     One grenade in the library: a Grenade Walk row with the demo it came from and where it landed.
///     The cross-demo key is the demo's hash plus <see cref="GrenadeRow.Id" />.
/// </summary>
/// <param name="Demo">The demo, by path, stable key and hash.</param>
/// <param name="Map">The map, as the demo header spells it.</param>
/// <param name="Row">The walk's row, as read from the rows sibling.</param>
/// <param name="Origin">Where it was thrown from: the release position, else the thrower at the spawn.</param>
/// <param name="Landing">Where it went off.</param>
/// <param name="LandingPlace">The zone the landing point falls in, or null when the map has no zones or no zone holds it.</param>
/// <param name="PlaceSource"><c>zones:&lt;zonesVersion&gt;</c> when the zones answered, else null.</param>
/// <param name="TickRate">The demo's tick rate, from the rows sibling's frame-clock header; 64 when it read none.</param>
public sealed record IndexedGrenade(
    DemoRef Demo,
    string Map,
    GrenadeRow Row,
    WorldPoint Origin,
    WorldPoint Landing,
    string? LandingPlace,
    string? PlaceSource,
    int TickRate = 64)
{
    public GrenadeKind Kind => Row.Kind;

    /// <summary>The demo's hash plus the row id, or the stable key plus the row id when the demo has no hash yet.</summary>
    public string Key => (Demo.Sha256 ?? Demo.StableKey) + "/" + Row.Id;

    /// <summary><see cref="GrenadeRow.AirTimeTicks" /> at <see cref="TickRate" />: what a Lineup Card prints.</summary>
    public float AirTimeSeconds => Row.AirTimeTicks / (float)Math.Max(1, TickRate);
}

/// <summary>
///     What to look for. Every filter left null matches everything; <see cref="Map" /> is the one
///     required field, because a grid cell means nothing across maps.
/// </summary>
/// <param name="Map">The map, compared without case.</param>
/// <param name="Kinds">The kinds to keep; null keeps all.</param>
/// <param name="LandingPlaces">The landing places to keep, compared without case; null keeps every landing, placed or not.</param>
/// <param name="ThrowerTeam">2 = T, 3 = CT; null keeps both and the unread.</param>
/// <param name="DemoPaths">The demos to look in, compared without case; null looks in the whole library.</param>
public sealed record GrenadeQuery(
    string Map,
    IReadOnlySet<GrenadeKind>? Kinds = null,
    IReadOnlySet<string>? LandingPlaces = null,
    int? ThrowerTeam = null,
    IReadOnlySet<string>? DemoPaths = null);

/// <summary>
///     One throw position: every grenade of a cluster whose origin rounds to the same point and that was
///     thrown the same way (jump-throw or not). What a lineup card will print once.
/// </summary>
/// <param name="Origin">The members' mean origin.</param>
/// <param name="JumpThrow">Whether the members were jump-throws.</param>
/// <param name="Throws">The members, oldest demo first, then by release tick.</param>
/// <param name="Id">
///     A stable identity for this throw position (Lineup On A Strat Step, plan.md §3, Phase 4), computed by
///     <see cref="GrenadeIndex.LineupId" /> over the map, kind, landing cell and rounded origin rather than
///     any one member: the representative throw (oldest demo first) can change as an older demo is indexed
///     later, but the position it names does not. This is the value Strat Model's <c>utility.lineupId</c>
///     stores (strat-model.md §3.3.3): that design assumed the Utility Book would mint a persisted
///     <c>Lineup.Id</c> row; there is none, so the deterministic key stands in for it (docs/strat-format.md).
/// </param>
public sealed record GrenadeLineup(WorldPoint Origin, bool JumpThrow, IReadOnlyList<IndexedGrenade> Throws, Guid Id)
{
    /// <summary>Distinct demos among the members, by content where the hash is known.</summary>
    public int DemoCount => Throws.Select(t => t.Demo.Sha256 ?? t.Demo.StableKey).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>
///     What a Strat Book step or another consumer needs to show for a lineup without holding the whole
///     cluster: its identity, its cluster's card title, and enough of the console line to be useful at a
///     glance. Looked up by <see cref="GrenadeIndex.DescribeLineup" />.
/// </summary>
/// <param name="Id"><see cref="GrenadeLineup.Id" />.</param>
/// <param name="Title">The cluster's card title (<see cref="LineupClipPlanner.Title" />'s wording).</param>
/// <param name="Kind">What is thrown.</param>
/// <param name="LandingPlace">The cluster's landing place, or null when none resolved.</param>
/// <param name="ThrowCount">How many recorded throws stood at this position.</param>
/// <param name="ConsoleText">The representative throw's <c>setpos</c>/<c>setang</c> line, or null when its release state was not read.</param>
public sealed record LineupSummary(Guid Id, string Title, GrenadeKind Kind, string? LandingPlace, int ThrowCount, string? ConsoleText);

/// <summary>
///     The grenades of one kind that landed in one coarse grid cell, their origins deduplicated into
///     <see cref="Lineups" />.
/// </summary>
/// <param name="Kind">What was thrown.</param>
/// <param name="Cell">The landing cell: X and Y over <see cref="GrenadeIndex.LandingCellSize" />, Z over <see cref="GrenadeIndex.LandingCellHeight" />.</param>
/// <param name="Landing">The members' mean landing point.</param>
/// <param name="LandingPlace">The landing place most members share, or null when none is placed.</param>
/// <param name="Lineups">The deduplicated origins, most thrown first.</param>
public sealed record GrenadeCluster(
    GrenadeKind Kind,
    (int X, int Y, int Z) Cell,
    WorldPoint Landing,
    string? LandingPlace,
    IReadOnlyList<GrenadeLineup> Lineups)
{
    /// <summary>Every grenade in the cluster.</summary>
    public int ThrowCount => Lineups.Sum(l => l.Throws.Count);
}

/// <summary>
///     The Grenade Index (plan.md §3, Phase 4): every Grenade Walk row in the library, one per grenade,
///     with its landing place from Zone Baking's resolver, queried by map, kind, landing place, side and
///     demo set and returned as clusters on a coarse landing grid with the origins deduplicated.
///     <para>
///         <b>Loading.</b> The same shape as the situation index: a startup load off the UI thread reads
///         every rows sibling the index row says is current, the evaluator's <c>Indexed</c> merges one demo
///         as it is written, and a cache change drops a demo whose row lost its stamp. A demo whose content
///         hash another loaded path already carries (Content Identity: a copy of the same file) counts once.
///     </para>
///     <para>
///         <b>Landing place.</b> Resolved at merge time through <see cref="IZonePlaceResolverSource" /> and
///         stored with its <c>zones:&lt;zonesVersion&gt;</c> stamp (correction 13); a query re-resolves a
///         map whose zones version moved since. A map without zones, and every map on the browser host,
///         leaves the place empty (zone-baking.md §3.5): the grid still clusters, a place filter finds nothing.
///     </para>
/// </summary>
public sealed class GrenadeIndex : IDisposable
{
    /// <summary>World units per landing cell in X and Y: about a doorway and its approach.</summary>
    public const float LandingCellSize = 256f;

    /// <summary>World units per landing cell in Z: two 64-unit levels, so a smoke on a ledge and one below it part.</summary>
    public const float LandingCellHeight = 128f;

    /// <summary>World units an origin is rounded to before two throws count as the same position.</summary>
    public const float OriginRounding = 16f;

    private static ILogger? _diagLog;

    private readonly DemoCacheStore _demoCache;
    private readonly GrenadeIndexEvaluator? _evaluator;
    private readonly object _gate = new();
    private readonly Dictionary<string, LoadedDemo> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<Action> _post;
    private readonly IZonePlaceResolverSource _zones;
    private bool _disposed;
    private bool _ready;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category);

    /// <param name="demoCache">The index rows and the siblings.</param>
    /// <param name="zones">Where a map's zone resolver comes from; none when omitted.</param>
    /// <param name="evaluator">The writer, whose <c>Indexed</c> merges a demo; null in a read-only host.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    public GrenadeIndex(DemoCacheStore demoCache, IZonePlaceResolverSource? zones = null,
        GrenadeIndexEvaluator? evaluator = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        _demoCache = demoCache;
        _zones = zones ?? NoZonePlaceResolverSource.Instance;
        _evaluator = evaluator;
        _post = post ?? (a => a());
        if (_evaluator is not null)
        {
            _evaluator.Indexed += OnIndexed;
        }

        _demoCache.Changed += OnCacheChanged;
    }

    /// <summary>True once the startup load finished; a query before then answers from what is loaded.</summary>
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    /// <summary>Demos whose rows are loaded, a copied demo counted once.</summary>
    public int DemoCount
    {
        get
        {
            lock (_gate)
            {
                return DistinctDemosLocked().Count();
            }
        }
    }

    /// <summary>Grenades across the loaded demos, a copied demo counted once.</summary>
    public int GrenadeCount
    {
        get
        {
            lock (_gate)
            {
                return DistinctDemosLocked().Sum(d => d.Grenades.Count);
            }
        }
    }

    /// <summary>The index changed: a load finished, a demo merged or one left. Raised through the post delegate.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_evaluator is not null)
        {
            _evaluator.Indexed -= OnIndexed;
        }

        _demoCache.Changed -= OnCacheChanged;
    }

    /// <summary>The startup load on a worker: the composition root calls this and never awaits it on the UI thread.</summary>
    public Task StartLoadAsync() => Task.Run(Load);

    /// <summary>Loads every demo whose index row says its grenades are current. Synchronous; call it off the UI thread.</summary>
    public void Load()
    {
        Stopwatch watch = Stopwatch.StartNew();
        foreach (DemoCacheIndexEntry entry in _demoCache.Index)
        {
            if (IsLoadable(entry))
            {
                Merge(entry);
            }
        }

        int demos;
        lock (_gate)
        {
            _ready = true;
            demos = _loaded.Count;
        }

        GrenadeIndexLog.Loaded(Log, demos, watch.ElapsedMilliseconds);
        _post(() => Changed?.Invoke());
    }

    /// <summary>The maps with at least one loaded grenade, sorted.</summary>
    public IReadOnlyList<string> Maps()
    {
        lock (_gate)
        {
            return
            [
                .. DistinctDemosLocked().Where(d => d.Grenades.Count > 0).Select(d => d.Map)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            ];
        }
    }

    /// <summary>The landing places grenades on a map resolved to, sorted.</summary>
    /// <param name="map">The map, compared without case.</param>
    public IReadOnlyList<string> LandingPlaces(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (_gate)
        {
            RefreshPlacesLocked(map);
            return
            [
                .. DistinctDemosLocked().Where(d => string.Equals(d.Map, map, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(d => d.Grenades).Select(g => g.LandingPlace).OfType<string>()
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            ];
        }
    }

    /// <summary>Every grenade the query keeps, one per grenade, a copied demo counted once.</summary>
    /// <param name="query">The filters.</param>
    public IReadOnlyList<IndexedGrenade> Rows(GrenadeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            RefreshPlacesLocked(query.Map);
            return [.. Filter(DistinctDemosLocked().SelectMany(d => d.Grenades), query)];
        }
    }

    /// <summary>The query's grenades clustered by landing cell with their origins deduplicated.</summary>
    /// <param name="query">The filters.</param>
    public IReadOnlyList<GrenadeCluster> Query(GrenadeQuery query) => Cluster(Rows(query));

    /// <summary>
    ///     The grenades the query keeps, in input order. Pure, so a caller holding rows of its own (a test,
    ///     a single demo's panel) filters them the way the index does.
    /// </summary>
    /// <param name="grenades">The rows to filter.</param>
    /// <param name="query">The filters.</param>
    public static IEnumerable<IndexedGrenade> Filter(IEnumerable<IndexedGrenade> grenades, GrenadeQuery query)
    {
        ArgumentNullException.ThrowIfNull(grenades);
        ArgumentNullException.ThrowIfNull(query);
        HashSet<string>? places = query.LandingPlaces is null
            ? null
            : new HashSet<string>(query.LandingPlaces, StringComparer.OrdinalIgnoreCase);
        HashSet<string>? demos = query.DemoPaths is null
            ? null
            : new HashSet<string>(query.DemoPaths, StringComparer.OrdinalIgnoreCase);
        return grenades.Where(g =>
            string.Equals(g.Map, query.Map, StringComparison.OrdinalIgnoreCase)
            && (query.Kinds is null || query.Kinds.Contains(g.Kind))
            && (places is null || (g.LandingPlace is { } place && places.Contains(place)))
            && (query.ThrowerTeam is not { } team || g.Row.ThrowerTeam == team)
            && (demos is null || demos.Contains(g.Demo.Path)));
    }

    /// <summary>
    ///     Groups grenades by kind and landing cell, then within a cell by rounded origin and jump-throw.
    ///     Clusters come most thrown first, lineups within a cluster likewise; ties break on position so the
    ///     order is the same on every run.
    /// </summary>
    /// <param name="grenades">The rows to cluster, usually the output of <see cref="Filter" />.</param>
    public static IReadOnlyList<GrenadeCluster> Cluster(IEnumerable<IndexedGrenade> grenades)
    {
        ArgumentNullException.ThrowIfNull(grenades);
        List<GrenadeCluster> clusters = [];
        foreach (IGrouping<(GrenadeKind Kind, (int, int, int) Cell), IndexedGrenade> cell in grenades
                     .GroupBy(g => (g.Kind, CellOf(g.Landing))))
        {
            List<GrenadeLineup> lineups =
            [
                .. cell.GroupBy(g => (RoundedOrigin: RoundedOrigin(g.Origin), g.Row.JumpThrow))
                    .Select(group =>
                    {
                        List<IndexedGrenade> throws =
                        [
                            .. group.OrderBy(g => g.Demo.Path, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(g => g.Row.ReleaseTick)
                        ];
                        Guid id = LineupId(throws[0].Map, cell.Key.Kind, cell.Key.Cell, group.Key.RoundedOrigin, group.Key.JumpThrow);
                        return new GrenadeLineup(Mean(throws.Select(t => t.Origin)), group.Key.JumpThrow, throws, id);
                    })
                    .OrderByDescending(l => l.Throws.Count)
                    .ThenBy(l => l.Origin.X).ThenBy(l => l.Origin.Y).ThenBy(l => l.Origin.Z)
                    .ThenBy(l => l.JumpThrow)
            ];
            string? place = cell.Select(g => g.LandingPlace).OfType<string>()
                .GroupBy(p => p, StringComparer.Ordinal)
                .OrderByDescending(p => p.Count()).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key).FirstOrDefault();
            clusters.Add(new GrenadeCluster(cell.Key.Kind, cell.Key.Cell, Mean(cell.Select(g => g.Landing)), place, lineups));
        }

        return
        [
            .. clusters.OrderByDescending(c => c.ThrowCount)
                .ThenBy(c => c.Kind)
                .ThenBy(c => c.Cell.X).ThenBy(c => c.Cell.Y).ThenBy(c => c.Cell.Z)
        ];
    }

    /// <summary>
    ///     A walk row as an index row, or null when it has no origin or no landing point to cluster on (a
    ///     grenade that never went off, or a thrower that was never read).
    /// </summary>
    /// <param name="demo">The demo.</param>
    /// <param name="map">The demo's map.</param>
    /// <param name="row">The walk's row.</param>
    /// <param name="zones">The map's zone resolver, or null when it has none.</param>
    /// <param name="tickRate">The demo's tick rate; 64 when the caller has none.</param>
    public static IndexedGrenade? From(DemoRef demo, string map, GrenadeRow row, IZonePlaceResolver? zones, int tickRate = 64)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(row);
        if ((row.ReleasePosition ?? row.ThrowerPositionAtSpawn) is not { } origin || row.DetonationPosition is not { } landing)
        {
            return null;
        }

        (string? place, string? source) = Resolve(zones, landing);
        return new IndexedGrenade(demo, map, row, origin, landing, place, source, tickRate > 0 ? tickRate : 64);
    }

    /// <summary>The landing cell a point falls in.</summary>
    /// <param name="point">A world point.</param>
    public static (int X, int Y, int Z) CellOf(WorldPoint point) =>
        ((int)MathF.Floor(point.X / LandingCellSize),
            (int)MathF.Floor(point.Y / LandingCellSize),
            (int)MathF.Floor(point.Z / LandingCellHeight));

    /// <summary>The origin rounded to <see cref="OriginRounding" />: two throws with the same value stood in the same spot.</summary>
    /// <param name="point">A world point.</param>
    public static (int X, int Y, int Z) RoundedOrigin(WorldPoint point) =>
        ((int)MathF.Round(point.X / OriginRounding, MidpointRounding.AwayFromZero),
            (int)MathF.Round(point.Y / OriginRounding, MidpointRounding.AwayFromZero),
            (int)MathF.Round(point.Z / OriginRounding, MidpointRounding.AwayFromZero));

    /// <summary>
    ///     <see cref="GrenadeLineup.Id" />: deterministic over the map, the kind, the landing cell and the
    ///     rounded origin, so the same throw position gets the same id from every process and every reindex,
    ///     with no row to persist. The first 16 bytes of a SHA-256 over the canonical string, the
    ///     <see cref="LineupClipPlanner.FileStem" /> idiom, read back as a <see cref="Guid" />.
    /// </summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="kind">What is thrown.</param>
    /// <param name="cell">The landing cell (<see cref="CellOf" />).</param>
    /// <param name="roundedOrigin">The origin, rounded (<see cref="RoundedOrigin" />).</param>
    /// <param name="jumpThrow">Whether the position is a jump-throw.</param>
    public static Guid LineupId(string map, GrenadeKind kind, (int X, int Y, int Z) cell,
        (int X, int Y, int Z) roundedOrigin, bool jumpThrow)
    {
        ArgumentNullException.ThrowIfNull(map);
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{map.ToLowerInvariant()}|{kind}|{cell.X},{cell.Y},{cell.Z}|{roundedOrigin.X},{roundedOrigin.Y},{roundedOrigin.Z}|{jumpThrow}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Every lineup on a map, across every kind unless <paramref name="kinds" /> narrows it, most thrown first.</summary>
    /// <param name="map">The map, compared without case.</param>
    /// <param name="kinds">The kinds to keep; null keeps all.</param>
    public IReadOnlyList<LineupSummary> Lineups(string map, IReadOnlySet<GrenadeKind>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        return
        [
            .. Query(new GrenadeQuery(map, kinds))
                .SelectMany(c => c.Lineups.Select(l => Describe(c, l)))
        ];
    }

    /// <summary>
    ///     One lineup by <see cref="GrenadeLineup.Id" />, or null when the map has none matching (never
    ///     indexed, or a reindex moved every throw off the position). <paramref name="map" /> is required:
    ///     the id alone does not say which map it names.
    /// </summary>
    /// <param name="map">The map the lineup is on.</param>
    /// <param name="lineupId"><see cref="GrenadeLineup.Id" />.</param>
    public LineupSummary? DescribeLineup(string map, Guid lineupId)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (GrenadeCluster cluster in Query(new GrenadeQuery(map)))
        {
            if (cluster.Lineups.FirstOrDefault(l => l.Id == lineupId) is { } lineup)
            {
                return Describe(cluster, lineup);
            }
        }

        return null;
    }

    private static LineupSummary Describe(GrenadeCluster cluster, GrenadeLineup lineup) =>
        new(lineup.Id, LineupClipPlanner.Title(cluster), cluster.Kind, cluster.LandingPlace, lineup.Throws.Count,
            GrenadeConsole.Format(lineup.Throws[0].Row));

    // ── Merge and remove ──────────────────────────────────────────────────────

    private static bool IsLoadable(DemoCacheIndexEntry? entry) =>
        entry is not null && entry.IsGrenadesCurrent(GrenadeWalker.Version);

    private static (string? Place, string? Source) Resolve(IZonePlaceResolver? zones, WorldPoint landing) =>
        zones?.Resolve(landing.ToVector()) is { Length: > 0 } place
            ? (place, $"zones:{zones.ZonesVersion}")
            : (null, null);

    private static WorldPoint Mean(IEnumerable<WorldPoint> points)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (WorldPoint point in points)
        {
            sum += point.ToVector();
            count++;
        }

        return count == 0 ? default : WorldPoint.From(sum / count);
    }

    // Reads one rows sibling and replaces whatever the demo contributed before. False when the file is
    // missing, unreadable, or another demo's (the sidecar reader's rules).
    private bool Merge(DemoCacheIndexEntry entry)
    {
        if (GrenadeSidecar.TryReadRows(_demoCache, entry.Path) is not { } document)
        {
            GrenadeIndexLog.RowsIgnored(Log, Path.GetFileName(entry.Path));
            return false;
        }

        string map = entry.Map ?? "";
        DemoRef demo = DemoRef.From(entry);
        IZonePlaceResolver? zones = map.Length > 0 ? _zones.TryGet(map) : null;
        int tickRate = document.Clock.TickRate > 0 ? document.Clock.TickRate : 64;
        List<IndexedGrenade> grenades = [];
        foreach (GrenadeRow row in document.Grenades)
        {
            if (From(demo, map, row, zones, tickRate) is { } grenade)
            {
                grenades.Add(grenade);
            }
        }

        lock (_gate)
        {
            _loaded[entry.Path] = new LoadedDemo(demo, map, zones?.ZonesVersion, grenades);
        }

        return true;
    }

    // Re-resolves a map's landing places when its zones version is no longer the one they were resolved
    // under: an overlay edit or a re-bake. Under _gate.
    private void RefreshPlacesLocked(string map)
    {
        IZonePlaceResolver? zones = null;
        bool looked = false;
        foreach ((string path, LoadedDemo demo) in _loaded.ToList())
        {
            if (!string.Equals(demo.Map, map, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!looked)
            {
                zones = _zones.TryGet(demo.Map);
                looked = true;
            }

            if (string.Equals(demo.ZonesVersion, zones?.ZonesVersion, StringComparison.Ordinal))
            {
                continue;
            }

            List<IndexedGrenade> replaced = [];
            foreach (IndexedGrenade grenade in demo.Grenades)
            {
                (string? place, string? source) = Resolve(zones, grenade.Landing);
                replaced.Add(grenade with { LandingPlace = place, PlaceSource = source });
            }

            _loaded[path] = demo with { ZonesVersion = zones?.ZonesVersion, Grenades = replaced };
        }
    }

    // One demo per content hash: the first path in ordinal order wins, so a copy never counts twice and
    // the winner is the same on every run. A demo with no hash yet is its own. Under _gate.
    private IEnumerable<LoadedDemo> DistinctDemosLocked()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (LoadedDemo demo in _loaded.Values.OrderBy(d => d.Demo.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (demo.Demo.Sha256 is { Length: > 0 } sha && !seen.Add(sha))
            {
                continue;
            }

            yield return demo;
        }
    }

    private void OnIndexed(string path)
    {
        if (_demoCache.TryGetIndex(path) is { } entry && IsLoadable(entry) && Merge(entry))
        {
            _post(() => Changed?.Invoke());
        }
    }

    // Removals only: new rows arrive through the evaluator's Indexed, so no sibling is read here.
    private void OnCacheChanged(string? path)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_ready)
            {
                return;
            }

            IEnumerable<string> candidates = path is null ? _loaded.Keys.ToList() : [path];
            foreach (string loaded in candidates)
            {
                if (_loaded.ContainsKey(loaded) && !IsLoadable(_demoCache.TryGetIndex(loaded)))
                {
                    changed |= _loaded.Remove(loaded);
                }
            }
        }

        if (changed)
        {
            _post(() => Changed?.Invoke());
        }
    }

    private sealed record LoadedDemo(DemoRef Demo, string Map, string? ZonesVersion, List<IndexedGrenade> Grenades);
}

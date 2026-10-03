#region

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
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
    /// <summary>
    ///     Every grid id this lineup absorbed when neighbouring grid positions were merged, <see cref="Id" />
    ///     included, so a strat step that stored a smaller neighbour's id still finds the lineup.
    /// </summary>
    public IReadOnlyList<Guid> AliasIds { get; init; } = [];

    /// <summary>The ways this lineup is thrown, most thrown first; one per lineup under the grid grouping.</summary>
    public IReadOnlyList<LineupTechnique> Techniques { get; init; } = [];

    /// <summary>The throw a card and a clip use: the most thrown technique's medoid, or the first throw.</summary>
    public IndexedGrenade Representative => Techniques.Count > 0 ? Techniques[0].Representative : Throws[0];

    /// <summary>True when <paramref name="id" /> names this lineup, directly or through a merged neighbour.</summary>
    /// <param name="id">A stored lineup id.</param>
    public bool Answers(Guid id) => Id == id || AliasIds.Contains(id);

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
public sealed class GrenadeIndex : IPackResident, IDisposable
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
    private readonly GrenadeLineupStore _lineups;
    private TaskCompletionSource _loadedOnce = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, Dictionary<string, Guid>> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<Guid, List<Guid>>> _reverseAliases = new(StringComparer.OrdinalIgnoreCase);
    private bool _attached;
    private bool _disposed;
    private bool _ready;
    private readonly CoalescedWriter<GrenadeLineupDocument> _lineupWriter;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category);

    /// <param name="demoCache">The index rows and the siblings.</param>
    /// <param name="zones">Where a map's zone resolver comes from; none when omitted.</param>
    /// <param name="evaluator">The writer, whose <c>Indexed</c> merges a demo; null in a read-only host.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    /// <param name="lineups">The lineup store; the one in the cache root when null.</param>
    /// <param name="scheduleSave">Runs a lineup save later: a processing-queue item in the app, the pool when null.</param>
    public GrenadeIndex(DemoCacheStore demoCache, IZonePlaceResolverSource? zones = null,
        GrenadeIndexEvaluator? evaluator = null, Action<Action>? post = null, GrenadeLineupStore? lineups = null,
        Func<Action, Task>? scheduleSave = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        _demoCache = demoCache;
        _lineups = lineups ?? GrenadeLineupStore.For(demoCache);
        _lineupWriter = new CoalescedWriter<GrenadeLineupDocument>(_lineups.Write,
            ex => GrenadeIndexLog.LineupsNotSaved(Log, ex), scheduleSave ?? (drain => Task.Run(drain)));
        _zones = zones ?? NoZonePlaceResolverSource.Instance;
        _evaluator = evaluator;
        _post = post ?? (a => a());
        Attach();
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

    /// <summary>Whether <paramref name="demoPath" />'s rows are loaded.</summary>
    /// <param name="demoPath">The demo.</param>
    public bool IsLoaded(string demoPath)
    {
        lock (_gate)
        {
            return _loaded.ContainsKey(demoPath);
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
        Detach();
        FlushLineups(TimeSpan.FromSeconds(10));
    }

    /// <inheritdoc />
    public void Attach()
    {
        lock (_gate)
        {
            if (_attached || _disposed)
            {
                return;
            }

            _attached = true;
        }

        if (_evaluator is not null)
        {
            _evaluator.Indexed += OnIndexed;
        }

        _demoCache.Changed += OnCacheChanged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Pending lineup saves are written first. Every loaded demo, assignment and the lineup document go;
    ///     <see cref="IsReady" /> is false and <see cref="WhenLoaded" /> pending again until the next <see cref="Load" />.
    /// </remarks>
    public void Release()
    {
        if (!Detach())
        {
            return;
        }

        FlushLineups(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            _loaded.Clear();
            _assignments.Clear();
            _reverseAliases.Clear();
            _lineups.Unload();
            _ready = false;
            _loadedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _post(() => Changed?.Invoke());
    }

    private bool Detach()
    {
        lock (_gate)
        {
            if (!_attached)
            {
                return false;
            }

            _attached = false;
        }

        if (_evaluator is not null)
        {
            _evaluator.Indexed -= OnIndexed;
        }

        _demoCache.Changed -= OnCacheChanged;
        return true;
    }

    /// <summary>The startup load on a worker: the composition root calls this and never awaits it on the UI thread.</summary>
    public Task StartLoadAsync() => Task.Run(Load);

    /// <summary>Loads every demo whose index row says its grenades are current. Synchronous; call it off the UI thread.</summary>
    public void Load()
    {
        Attach();
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
            InvalidateLocked(null);
            foreach (string map in DistinctDemosLocked().Select(d => d.Map).Where(m => m.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                EnsureAssignedLocked(map);
            }
        }

        GrenadeIndexLog.Loaded(Log, demos, watch.ElapsedMilliseconds);
        WhenLoadedSource().TrySetResult();
        _post(() => Changed?.Invoke());
    }

    /// <summary>Completes when the first <see cref="Load" /> has finished.</summary>
    public Task WhenLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loadedOnce.Task;
            }
        }
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
    public IReadOnlyList<GrenadeCluster> Query(GrenadeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Grouping == GrenadeGrouping.Grid)
        {
            return Cluster(Rows(query));
        }

        lock (_gate)
        {
            RefreshPlacesLocked(query.Map);
            Dictionary<string, Guid> assignment = EnsureAssignedLocked(query.Map);
            Dictionary<Guid, List<Guid>> aliases = _reverseAliases.GetValueOrDefault(query.Map) ?? [];
            return GrenadeLineups.Group(Filter(DistinctDemosLocked().SelectMany(d => d.Grenades), query),
                g => assignment.TryGetValue(g.Key, out Guid id) ? id : GrenadeLineups.SingleId(g.Key),
                id => aliases.TryGetValue(id, out List<Guid>? old) ? old : []);
        }
    }

    /// <summary>Which grouping <see cref="Query" /> runs: stored lineups, or the v1 grid for a comparison.</summary>
    public GrenadeGrouping Grouping { get; init; } = GrenadeGrouping.Lineups;

    /// <summary>The stored representative flight of a lineup's technique (or the lineup), or null.</summary>
    /// <param name="lineup">The lineup.</param>
    /// <param name="technique">The technique, or null for the lineup's most thrown one.</param>
    public IReadOnlyList<TrajectoryPoint>? PathFor(GrenadeLineup lineup, LineupTechnique? technique)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        if ((technique ?? (lineup.Techniques.Count > 0 ? lineup.Techniques[0] : null)) is not { } chosen
            || lineup.Throws.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            return _lineups.For(lineup.Throws[0].Map).Paths.TryGetValue(PathKey(lineup.Id, chosen.Key), out LineupPath? path)
                ? path.Points
                : null;
        }
    }

    /// <summary>The store key of a lineup technique's representative flight.</summary>
    public static string PathKey(Guid lineupId, string technique) =>
        lineupId.ToString("N", CultureInfo.InvariantCulture) + "/" + technique;

    /// <summary>The lineup store this index assigns against.</summary>
    public GrenadeLineupStore LineupStore => _lineups;

    // A map's throw-to-lineup assignment, minting anchors and building the alias map once the library has
    // loaded; cached until a demo on the map comes or goes. Under _gate.
    private Dictionary<string, Guid> EnsureAssignedLocked(string map)
    {
        if (_assignments.TryGetValue(map, out Dictionary<string, Guid>? cached))
        {
            return cached;
        }

        List<IndexedGrenade> all =
        [
            .. DistinctDemosLocked().Where(d => string.Equals(d.Map, map, StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => d.Grenades)
        ];
        MapLineups lineups = _lineups.For(map);
        (Dictionary<string, Guid> assignment, bool changed) = GrenadeLineups.Assign(map, all, lineups, _ready);
        if (_ready)
        {
            changed |= FillPaths(all, assignment, lineups);
        }

        if (_ready && !lineups.AliasesBuilt && all.Count > 0)
        {
            foreach ((Guid old, Guid now) in GrenadeLineups.BuildAliases(all, assignment))
            {
                lineups.Aliases.TryAdd(old, now);
            }

            lineups.AliasesBuilt = true;
            changed = true;
        }

        if (changed)
        {
            SaveLineupsLocked();
        }

        _assignments[map] = assignment;
        _reverseAliases[map] = lineups.Aliases.GroupBy(a => a.Value)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Key).Order().ToList());
        return assignment;
    }

    // Anchored lineup techniques that have no stored flight, with their throws and mean origin.
    private static IEnumerable<(string Key, WorldPoint Origin, List<IndexedGrenade> Throws)> Unpathed(
        IReadOnlyList<IndexedGrenade> all, Dictionary<string, Guid> assignment, MapLineups lineups)
    {
        HashSet<Guid> anchored = [.. lineups.Anchors.Select(a => a.Id)];
        foreach (IGrouping<string, IndexedGrenade> group in all
                     .Where(g => assignment.TryGetValue(g.Key, out Guid id) && anchored.Contains(id))
                     .GroupBy(g => PathKey(assignment[g.Key], GrenadeLineups.TechniqueKey(g.Row)), StringComparer.Ordinal))
        {
            if (!lineups.Paths.ContainsKey(group.Key))
            {
                List<IndexedGrenade> throws = [.. group];
                yield return (group.Key, Mean(throws.Select(t => t.Origin)), throws);
            }
        }
    }

    // Stores one in-memory flight per anchored technique that has none: the one released nearest the mean.
    private static bool FillPaths(IReadOnlyList<IndexedGrenade> all, Dictionary<string, Guid> assignment, MapLineups lineups)
    {
        bool changed = false;
        foreach ((string key, WorldPoint origin, List<IndexedGrenade> throws) in Unpathed(all, assignment, lineups).ToList())
        {
            List<IndexedGrenade> flown = [.. throws.Where(t => t.Row.Trajectory.Count >= 2)];
            if (flown.Count > 0)
            {
                IndexedGrenade best = GrenadeLineups.Medoid(flown, origin);
                lineups.Paths[key] = new LineupPath(best.Key, [.. best.Row.Trajectory]);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    ///     Fills the store's missing flights from the demos' old paths siblings (the one-off migration), trying
    ///     each technique's three throws released nearest its mean. Reads every snapshot demo's file once,
    ///     outside the lock, and saves the store.
    /// </summary>
    /// <param name="snapshot">The demos whose paths files are read.</param>
    /// <param name="readPaths">A demo's paths sibling, or null when it is missing or does not read.</param>
    /// <returns>How many positions got a flight, and the snapshot demos whose file read.</returns>
    public (int Filled, HashSet<string> Readable) HarvestStoredPaths(IReadOnlyList<string> snapshot,
        Func<string, GrenadePathsDocument?> readPaths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(readPaths);
        List<(string Map, string Key, List<IndexedGrenade> Candidates)> needs = [];
        lock (_gate)
        {
            foreach (string map in DistinctDemosLocked().Select(d => d.Map).Where(m => m.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                Dictionary<string, Guid> assignment = EnsureAssignedLocked(map);
                List<IndexedGrenade> all =
                [
                    .. DistinctDemosLocked().Where(d => string.Equals(d.Map, map, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(d => d.Grenades)
                ];
                foreach ((string key, WorldPoint origin, List<IndexedGrenade> throws) in Unpathed(all, assignment, _lineups.For(map)))
                {
                    needs.Add((map, key, [.. throws.OrderBy(t => Distance(t.Origin, origin)).Take(3)]));
                }
            }
        }

        Dictionary<string, HashSet<string>> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach (IndexedGrenade g in needs.SelectMany(n => n.Candidates))
        {
            if (!wanted.TryGetValue(g.Demo.Path, out HashSet<string>? ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                wanted[g.Demo.Path] = ids;
            }

            ids.Add(g.Row.Id);
        }

        Dictionary<string, List<TrajectoryPoint>> found = new(StringComparer.Ordinal);
        HashSet<string> readable = new(StringComparer.OrdinalIgnoreCase);
        foreach (string demo in snapshot.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (readPaths(demo) is not { } document)
            {
                continue;
            }

            readable.Add(demo);
            if (!wanted.TryGetValue(demo, out HashSet<string>? ids))
            {
                continue;
            }

            foreach (string id in ids)
            {
                if (document.Paths.TryGetValue(id, out List<TrajectoryPoint>? points) && points.Count >= 2)
                {
                    found[demo + "|" + id] = points;
                }
            }
        }

        int filled = 0;
        lock (_gate)
        {
            foreach ((string map, string key, List<IndexedGrenade> candidates) in needs)
            {
                if (candidates.FirstOrDefault(c => found.ContainsKey(c.Demo.Path + "|" + c.Row.Id)) is { } best)
                {
                    _lineups.For(map).Paths[key] = new LineupPath(best.Key, found[best.Demo.Path + "|" + best.Row.Id]);
                    filled++;
                }
            }

            SaveLineupsLocked();
        }

        // The migration reads the file back right after this returns.
        FlushLineups();
        return (filled, readable);
    }

    /// <summary>
    ///     True when every anchored lineup technique a demo's throws belong to has its flight in the store:
    ///     the demo's paths file holds nothing the store still needs.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    public bool FlightsCovered(string demoPath)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        lock (_gate)
        {
            if (!_loaded.TryGetValue(demoPath, out LoadedDemo? demo) || demo.Map.Length == 0)
            {
                return true;
            }

            Dictionary<string, Guid> assignment = EnsureAssignedLocked(demo.Map);
            MapLineups lineups = _lineups.For(demo.Map);
            HashSet<Guid> anchored = [.. lineups.Anchors.Select(a => a.Id)];
            return demo.Grenades.All(g => !assignment.TryGetValue(g.Key, out Guid id) || !anchored.Contains(id)
                                          || lineups.Paths.ContainsKey(PathKey(id, GrenadeLineups.TechniqueKey(g.Row))));
        }
    }

    private static float Distance(WorldPoint a, WorldPoint b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    // Under _gate. Only the copy is taken here; the gzip and write of the whole file (over a second on a
    // big library) run as the writer's scheduled item, outside the lock.
    private void SaveLineupsLocked() => _lineupWriter.Post(_lineups.Snapshot());

    /// <summary>
    ///     Writes any pending lineup save on the calling thread, waiting at most <paramref name="timeout" />
    ///     for one in progress. Never throws; false when it could not.
    /// </summary>
    /// <param name="timeout">The wait for a write in progress; infinite when null.</param>
    public bool FlushLineups(TimeSpan? timeout = null) => _lineupWriter.Flush(timeout);

    // Drops cached assignments: one map's, or every map's when null. Under _gate.
    private void InvalidateLocked(string? map)
    {
        if (map is null)
        {
            _assignments.Clear();
        }
        else
        {
            _assignments.Remove(map);
        }
    }

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

        // 1. Grid positions: the stable ids, one per landing cell, rounded origin and throw style.
        List<GridPosition> grid =
        [
            .. grenades.GroupBy(g => (g.Kind, Cell: CellOf(g.Landing), RoundedOrigin: RoundedOrigin(g.Origin), g.Row.JumpThrow))
                .Select(group => new GridPosition(group.Key.Kind, group.Key.Cell, group.Key.JumpThrow,
                    LineupId(group.First().Map, group.Key.Kind, group.Key.Cell, group.Key.RoundedOrigin, group.Key.JumpThrow),
                    [.. group]))
                .OrderByDescending(p => p.Throws.Count).ThenBy(p => p.Id)
        ];

        // 2. Lineups: a fixed 16-unit grid splits one standing spot across a cell edge, so neighbouring
        // positions merge into the biggest one near them (seeded, never chained): close origin, close
        // landing, same aim within a couple of degrees. The seed's grid id stays the lineup's id.
        // Seeds are bucketed on a merge-radius grid, so a position only compares against the nine cells
        // around it; first-come order within a bucket keeps the most thrown seed winning.
        List<LineupSeed> seeds = [];
        Dictionary<(GrenadeKind, bool, int, int), List<LineupSeed>> seedCells = [];
        foreach (GridPosition position in grid)
        {
            (WorldPoint origin, WorldPoint landing, float? yaw, float? pitch) = Centre(position.Throws);
            (int cx, int cy) = BucketOf(origin, OriginMergeRadius);
            LineupSeed? home = null;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (seedCells.TryGetValue((position.Kind, position.JumpThrow, cx + dx, cy + dy), out List<LineupSeed>? near)
                        && near.FirstOrDefault(s => Near(s.Origin, origin, OriginMergeRadius, OriginMergeHeight)
                                                    && Near(s.Landing, landing, LineupLandingRadius, LineupLandingHeight)
                                                    && AimNear(s.Yaw, yaw, s.Pitch, pitch)) is { } found
                        && (home is null || found.Order < home.Order))
                    {
                        home = found;
                    }
                }
            }

            if (home is null)
            {
                LineupSeed seed = new(position.Kind, position.JumpThrow, position.Cell, position.Id, origin, landing, yaw, pitch,
                    [.. position.Throws], [position.Id], seeds.Count);
                seeds.Add(seed);
                (int, int) key = BucketOf(origin, OriginMergeRadius);
                if (!seedCells.TryGetValue((position.Kind, position.JumpThrow, key.Item1, key.Item2), out List<LineupSeed>? bucket))
                {
                    bucket = [];
                    seedCells[(position.Kind, position.JumpThrow, key.Item1, key.Item2)] = bucket;
                }

                bucket.Add(seed);
            }
            else
            {
                home.Throws.AddRange(position.Throws);
                home.Aliases.Add(position.Id);
            }
        }

        // 3. Landing groups: lineups of one kind whose landings sit together, seeded by the most thrown, so
        // one smoke spot is one group even where the 256-unit grid cuts through it.
        List<LandingSeed> landings = [];
        Dictionary<(GrenadeKind, int, int), List<LandingSeed>> landingCells = [];
        foreach (LineupSeed seed in seeds.OrderByDescending(s => s.Throws.Count).ThenBy(s => s.Id))
        {
            WorldPoint landing = Mean(seed.Throws.Select(t => t.Landing));
            (int cx, int cy) = BucketOf(landing, LandingMergeRadius);
            LandingSeed? home = null;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (landingCells.TryGetValue((seed.Kind, cx + dx, cy + dy), out List<LandingSeed>? near)
                        && near.FirstOrDefault(l => Near(l.Landing, landing, LandingMergeRadius, LandingMergeHeight)) is { } found
                        && (home is null || found.Order < home.Order))
                    {
                        home = found;
                    }
                }
            }

            if (home is null)
            {
                LandingSeed group = new(seed.Kind, seed.Cell, landing, [seed], landings.Count);
                landings.Add(group);
                if (!landingCells.TryGetValue((seed.Kind, cx, cy), out List<LandingSeed>? bucket))
                {
                    bucket = [];
                    landingCells[(seed.Kind, cx, cy)] = bucket;
                }

                bucket.Add(group);
            }
            else
            {
                home.Lineups.Add(seed);
            }
        }

        List<GrenadeCluster> clusters = [];
        foreach (LandingSeed group in landings)
        {
            List<GrenadeLineup> lineups =
            [
                .. group.Lineups.Select(seed =>
                    {
                        List<IndexedGrenade> throws =
                        [
                            .. seed.Throws.OrderBy(g => g.Demo.Path, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(g => g.Row.ReleaseTick)
                        ];
                        return new GrenadeLineup(Mean(throws.Select(t => t.Origin)), seed.JumpThrow, throws, seed.Id)
                        {
                            AliasIds = [.. seed.Aliases]
                        };
                    })
                    .OrderByDescending(l => l.Throws.Count)
                    .ThenBy(l => l.Origin.X).ThenBy(l => l.Origin.Y).ThenBy(l => l.Origin.Z)
                    .ThenBy(l => l.JumpThrow)
            ];
            List<IndexedGrenade> members = [.. lineups.SelectMany(l => l.Throws)];
            string? place = members.Select(g => g.LandingPlace).OfType<string>()
                .GroupBy(p => p, StringComparer.Ordinal)
                .OrderByDescending(p => p.Count()).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key).FirstOrDefault();
            clusters.Add(new GrenadeCluster(group.Kind, group.Cell, Mean(members.Select(g => g.Landing)), place, lineups));
        }

        return
        [
            .. clusters.OrderByDescending(c => c.ThrowCount)
                .ThenBy(c => c.Kind)
                .ThenBy(c => c.Cell.X).ThenBy(c => c.Cell.Y).ThenBy(c => c.Cell.Z)
        ];
    }

    /// <summary>
    ///     Two grid positions within this distance of each other's mean origin, in the plane, are one
    ///     standing spot. Measured on the owner's 53 indexed demos (16,374 grenades): the fixed grid alone
    ///     finds 1,243 lineups thrown twice or more covering 3,923 throws; merging within 16 units and 2
    ///     degrees of aim finds 1,408 covering 5,201, with the largest lineup at 40 throws (no chaining).
    /// </summary>
    public const float OriginMergeRadius = 16f;

    /// <summary>...and this far apart in height, a crouch plus a step.</summary>
    public const float OriginMergeHeight = 24f;

    /// <summary>The same lineup lands within this distance in the plane.</summary>
    public const float LineupLandingRadius = 128f;

    /// <summary>...and this far apart in height.</summary>
    public const float LineupLandingHeight = 96f;

    /// <summary>The same lineup is aimed within this many degrees of yaw and of pitch.</summary>
    public const float AimToleranceDegrees = 2f;

    /// <summary>Lineups of one kind whose landings are this close in the plane are one landing group on the map.</summary>
    public const float LandingMergeRadius = 96f;

    /// <summary>...and this close in height.</summary>
    public const float LandingMergeHeight = 96f;

    private static (int X, int Y) BucketOf(WorldPoint point, float size) =>
        ((int)MathF.Floor(point.X / size), (int)MathF.Floor(point.Y / size));

    private static bool Near(WorldPoint a, WorldPoint b, float radius, float height) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) <= radius && MathF.Abs(a.Z - b.Z) <= height;

    private static bool AimNear(float? yawA, float? yawB, float? pitchA, float? pitchB)
    {
        if (yawA is { } ya && yawB is { } yb && MathF.Abs(((ya - yb) % 360 + 540) % 360 - 180) > AimToleranceDegrees)
        {
            return false;
        }

        return pitchA is not { } pa || pitchB is not { } pb || MathF.Abs(pa - pb) <= AimToleranceDegrees;
    }

    // The mean origin and landing, and the mean aim (yaw on the circle), of one grid position's throws.
    private static (WorldPoint Origin, WorldPoint Landing, float? Yaw, float? Pitch) Centre(IReadOnlyList<IndexedGrenade> throws)
    {
        List<float> yaws = [.. throws.Select(t => t.Row.ReleaseEyeYaw).OfType<float>()];
        List<float> pitches = [.. throws.Select(t => t.Row.ReleaseEyePitch).OfType<float>()];
        float? yaw = yaws.Count == 0
            ? null
            : MathF.Atan2(yaws.Sum(y => MathF.Sin(y * MathF.PI / 180)), yaws.Sum(y => MathF.Cos(y * MathF.PI / 180))) * 180 / MathF.PI;
        float? pitch = pitches.Count == 0 ? null : pitches.Average();
        return (Mean(throws.Select(t => t.Origin)), Mean(throws.Select(t => t.Landing)), yaw, pitch);
    }

    private sealed record GridPosition(GrenadeKind Kind, (int X, int Y, int Z) Cell, bool JumpThrow, Guid Id, IReadOnlyList<IndexedGrenade> Throws);

    private sealed record LineupSeed(GrenadeKind Kind, bool JumpThrow, (int X, int Y, int Z) Cell, Guid Id,
        WorldPoint Origin, WorldPoint Landing, float? Yaw, float? Pitch, List<IndexedGrenade> Throws, List<Guid> Aliases, int Order);

    private sealed record LandingSeed(GrenadeKind Kind, (int X, int Y, int Z) Cell, WorldPoint Landing, List<LineupSeed> Lineups, int Order);

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
    ///     <see cref="LineupClipPlanner.LegacyFileStem" /> idiom, read back as a <see cref="Guid" />.
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
            if (cluster.Lineups.FirstOrDefault(l => l.Answers(lineupId)) is { } lineup)
            {
                return Describe(cluster, lineup);
            }
        }

        return null;
    }

    private static LineupSummary Describe(GrenadeCluster cluster, GrenadeLineup lineup) =>
        new(lineup.Id, LineupClipPlanner.Title(cluster), cluster.Kind, cluster.LandingPlace, lineup.Throws.Count,
            GrenadeConsole.Format(lineup.Representative.Row));

    // ── Merge and remove ──────────────────────────────────────────────────────

    private static bool IsLoadable(DemoCacheIndexEntry? entry) =>
        entry is not null && entry.IsGrenadesCurrent(GrenadeWalker.Version);

    private static (string? Place, string? Source) Resolve(IZonePlaceResolver? zones, WorldPoint landing) =>
        zones?.Resolve(landing.ToVector()) is { Length: > 0 } place
            ? (place, PlaceSourceFor(zones.ZonesVersion))
            : (null, null);

    // One string per zones version, shared by every grenade resolved under it. A version changes only on
    // an overlay edit or a re-bake, so the map stays a handful of entries.
    private static readonly ConcurrentDictionary<string, string> _placeSources = new(StringComparer.Ordinal);

    private static string PlaceSourceFor(string zonesVersion) =>
        _placeSources.GetOrAdd(zonesVersion, static version => $"zones:{version}");

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
    private TaskCompletionSource WhenLoadedSource()
    {
        lock (_gate)
        {
            return _loadedOnce;
        }
    }

    private bool Merge(DemoCacheIndexEntry entry, IReadOnlyDictionary<string, List<TrajectoryPoint>>? flights = null)
    {
        lock (_gate)
        {
            // A merge posted before a release lands after it: released means empty.
            if (!_attached)
            {
                return false;
            }
        }

        if (GrenadeSidecar.TryReadRows(_demoCache, entry.Path) is not { } document)
        {
            string fileName = Path.GetFileName(entry.Path);
            GrenadeIndexLog.RowsIgnored(Log, fileName);
            return false;
        }

        // Rows written before names were stored (the JSON the grenades migration has not converted yet)
        // take them from the record, as the migration does.
        if (document.Grenades.Any(r => r.ThrowerName is null))
        {
            GrenadeSidecar.Name(document, _demoCache.TryLoadRecord(entry.Path)?.Players);
        }

        string map = entry.Map ?? "";
        DemoRef demo = DemoRef.From(entry);
        IZonePlaceResolver? zones = map.Length > 0 ? _zones.TryGet(map) : null;
        int tickRate = document.Clock.TickRate > 0 ? document.Clock.TickRate : 64;
        List<IndexedGrenade> grenades = [];
        foreach (GrenadeRow row in document.Grenades)
        {
            // A demo walked this session keeps its flights in memory; the lineup store takes one per position.
            if (flights is not null && flights.TryGetValue(row.Id, out List<TrajectoryPoint>? flight))
            {
                row.Trajectory = flight;
            }

            if (From(demo, map, row, zones, tickRate) is { } grenade)
            {
                grenades.Add(grenade);
            }
        }

        lock (_gate)
        {
            _loaded[entry.Path] = new LoadedDemo(demo, map, zones?.ZonesVersion, grenades);
            InvalidateLocked(map);
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
        if (_demoCache.TryGetIndex(path) is { } entry && IsLoadable(entry) && Merge(entry, _evaluator?.TakeFlights(path)))
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
                    InvalidateLocked(null);
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

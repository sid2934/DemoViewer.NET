#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     Watched Situations (plan §3, round-index.md §3.12): the saved queries, their persistence and
///     the "N new" badge. A DI singleton like <see cref="TeamIdentityService" />, delegate-injected
///     into the Situations module.
///     <para>
///         <b>New is a watermark, not a list.</b> Each watch carries <see cref="WatchedSituation.WatermarkTicks" />;
///         a hit is new when its demo's index stamp is above it. Two paths keep the badge right: the
///         index's <see cref="ISituationIndex.Indexed" /> hook runs the watch over that one demo the
///         moment its sidecar merges, which is what makes the badge move without a restart; and a full
///         pass with <see cref="SituationQuery.IndexedAfterTicks" /> at the watermark runs at load and
///         after any change to the index, the teams or the labels, which is what makes it survive a
///         restart and a drop. Both read the same stamp, so they never disagree. Marking a watch seen
///         moves the watermark to now, or to the newest stamp it counted when a clock runs behind.
///     </para>
///     <para>
///         <b>The query is rebuilt on every evaluation.</b> The stored filter holds a team id, a label
///         and a date range; the demo set they narrow to is derived against the library as it stands,
///         so a demo against the opponent indexed after the watch was saved is inside the set. A stored
///         set of keys would have frozen the watch at the day it was made.
///     </para>
///     <para>
///         Persistence follows <c>teams.json</c>: whole-file atomic writes under the config root, a file
///         that cannot be read is refused and never overwritten, and a null root (the browser, tests)
///         keeps the watches for the session only.
///     </para>
/// </summary>
public sealed class WatchedSituationsService : IDisposable
{
    /// <summary>The file under the config root.</summary>
    public const string FileName = "watched-situations.json";

    private readonly DemoCacheStore _demoCache;
    private readonly object _gate = new();
    private readonly ISituationIndex _index;

    // Per watch, per demo stable key: the demo's index stamp and the hits that are new in it.
    private readonly Dictionary<Guid, Dictionary<string, NewGroup>> _new = new();
    private readonly Func<long> _now;
    private readonly string? _path;
    private readonly Action<Action> _post;
    private readonly IDemoProvenanceSource? _provenance;
    private readonly TeamIdentityService? _teams;

    private bool _disposed;
    private WatchedSituationsFile _file = new();
    private bool _refused;

    /// <param name="configRoot">The app config root, or null for a session-only store (the browser, tests).</param>
    /// <param name="index">The in-memory situation index the watches run against.</param>
    /// <param name="demoCache">The index rows the filter's demo set and the stamps read.</param>
    /// <param name="teams">Team Identity, for the opponent and our-side fields; null leaves both inert.</param>
    /// <param name="provenance">Demo Provenance Labels, for the source field; null leaves it inert.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    /// <param name="now">The clock a watermark is set from, UTC ticks; defaults to <see cref="DateTime.UtcNow" />. Tests pin it.</param>
    public WatchedSituationsService(
        string? configRoot,
        ISituationIndex index,
        DemoCacheStore demoCache,
        TeamIdentityService? teams = null,
        IDemoProvenanceSource? provenance = null,
        Action<Action>? post = null,
        Func<long>? now = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(demoCache);
        _index = index;
        _demoCache = demoCache;
        _teams = teams;
        _provenance = provenance;
        _post = post ?? (action => action());
        _now = now ?? (() => DateTime.UtcNow.Ticks);
        _path = configRoot is null ? null : Path.Combine(configRoot, FileName);

        Load();

        // The hook first, then the full pass: both are posted by the index in that order after a merge,
        // and the pass finds nothing to change after the hook has already counted the demo.
        _index.Indexed += OnIndexed;
        _index.Changed += Reevaluate;
        if (_teams is not null)
        {
            _teams.Changed += Reevaluate;
        }

        if (_provenance is not null)
        {
            _provenance.Changed += Reevaluate;
        }

        Reevaluate();
    }

    /// <summary>True when nothing persists: the browser host, and tests without a root.</summary>
    public bool IsSessionOnly => _path is null;

    /// <summary>
    ///     Why the file could not be read, or null. While set the file is never written: a user's saved
    ///     queries are not something to overwrite with an empty file because one read failed.
    /// </summary>
    public string? FileProblem { get; private set; }

    /// <summary>The watches in creation order.</summary>
    public IReadOnlyList<WatchedSituation> Watches
    {
        get
        {
            lock (_gate)
            {
                return [.. _file.Watches];
            }
        }
    }

    /// <summary>New hits over every watch: the tab badge.</summary>
    public int NewCount
    {
        get
        {
            lock (_gate)
            {
                return _new.Values.Sum(groups => groups.Values.Sum(g => g.Hits.Count));
            }
        }
    }

    /// <summary>Raised (through the post delegate) after the list or any badge changed.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _index.Indexed -= OnIndexed;
        _index.Changed -= Reevaluate;
        if (_teams is not null)
        {
            _teams.Changed -= Reevaluate;
        }

        if (_provenance is not null)
        {
            _provenance.Changed -= Reevaluate;
        }
    }

    /// <summary>New hits of one watch: its own badge.</summary>
    /// <param name="id">The watch.</param>
    public int NewCountOf(Guid id)
    {
        lock (_gate)
        {
            return _new.TryGetValue(id, out Dictionary<string, NewGroup>? groups) ? groups.Values.Sum(g => g.Hits.Count) : 0;
        }
    }

    /// <summary>How many demos the new hits of one watch fall in.</summary>
    /// <param name="id">The watch.</param>
    public int NewDemoCountOf(Guid id)
    {
        lock (_gate)
        {
            return _new.TryGetValue(id, out Dictionary<string, NewGroup>? groups) ? groups.Count : 0;
        }
    }

    /// <summary>The new hits of one watch, newest demo stamp first then the index's order.</summary>
    /// <param name="id">The watch.</param>
    public IReadOnlyList<SituationHit> NewHitsOf(Guid id)
    {
        lock (_gate)
        {
            return _new.TryGetValue(id, out Dictionary<string, NewGroup>? groups)
                ? [.. groups.Values.OrderByDescending(g => g.ComputedAtTicks).SelectMany(g => g.Hits)]
                : [];
        }
    }

    /// <summary>
    ///     Saves the canvas's query as a watch. The watermark starts at now: the user has just seen
    ///     what the query matches, so only demos indexed from here on are new.
    /// </summary>
    /// <param name="name">The display name; empty falls back to the query's own line.</param>
    /// <param name="map">The map the query runs over.</param>
    /// <param name="tokens">The placed tokens, resolved or not.</param>
    /// <param name="tolerance">How loosely the pairs match.</param>
    /// <param name="filters">The rail's values.</param>
    public WatchedSituation Watch(string name, string map, IEnumerable<QueryToken> tokens, SituationTolerance tolerance,
        SearchFilterValues filters)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(filters);

        long now = _now();
        WatchedSituation watch = new()
        {
            Id = Guid.NewGuid(),
            Map = map,
            CreatedTicks = now,
            WatermarkTicks = now,
            Tolerance = tolerance,
            Tokens = [.. tokens.Select(WatchedToken.From)],
            Filters = filters
        };
        watch.Name = name.Trim().Length > 0 ? name.Trim() : QueryLine(watch);

        lock (_gate)
        {
            _file.Watches.Add(watch);
            _new[watch.Id] = new Dictionary<string, NewGroup>(StringComparer.Ordinal);
            Save();
        }

        RaiseChanged();
        return watch;
    }

    /// <summary>Drops a watch.</summary>
    /// <param name="id">The watch.</param>
    public void Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _file.Watches.RemoveAll(w => w.Id == id) > 0;
            _new.Remove(id);
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    ///     Clears a watch's badge by moving its watermark to now, or to the newest stamp it counted when
    ///     the clock runs behind the stamps (a machine whose clock moved back; a test's pinned clock).
    /// </summary>
    /// <param name="id">The watch.</param>
    public void MarkSeen(Guid id)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_file.Watches.FirstOrDefault(w => w.Id == id) is { } watch)
            {
                changed = MarkSeenLocked(watch);
                if (changed)
                {
                    Save();
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Clears every badge.</summary>
    public void MarkAllSeen()
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (WatchedSituation watch in _file.Watches)
            {
                changed |= MarkSeenLocked(watch);
            }

            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    ///     The query a watch runs, over the library as it stands: the pairs from the saved tokens, the
    ///     Round Facts filter and the demo set re-derived from the saved fields, then narrowed to
    ///     <paramref name="demos" /> and stamped after <paramref name="indexedAfter" /> when given.
    /// </summary>
    /// <param name="watch">The watch.</param>
    /// <param name="demos">Stable keys to restrict to on top of the filter's own set; null for the filter's set alone.</param>
    /// <param name="indexedAfter">Only demos whose index stamp is newer; null for all.</param>
    public SituationQuery ToQuery(WatchedSituation watch, IReadOnlySet<string>? demos = null, long? indexedAfter = null)
    {
        ArgumentNullException.ThrowIfNull(watch);
        IReadOnlySet<string>? filtered = watch.Filters.ToDemos(_demoCache, _teams, _provenance);
        IReadOnlySet<string>? scope = demos is null ? filtered
            : filtered is null ? demos
            : new HashSet<string>(demos.Where(filtered.Contains), StringComparer.Ordinal);
        return new SituationQuery(watch.Map, watch.PairsFor(QuerySide.Ct), watch.PairsFor(QuerySide.T), watch.Tolerance,
            watch.Filters.ToFacts(_teams), scope, indexedAfter);
    }

    /// <summary>"CT BombsiteA:2|Outside:3 vs T Lobby:3|Ramp:2 · adjacent · CT buy full": the query as text, the fallback name.</summary>
    /// <param name="watch">The watch.</param>
    public static string QueryLine(WatchedSituation watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        string ct = watch.TokenFor(QuerySide.Ct);
        string t = watch.TokenFor(QuerySide.T);
        List<string> parts =
        [
            $"CT {(ct.Length > 0 ? ct : "any")} vs T {(t.Length > 0 ? t : "any")}"
        ];
        if (watch.Tolerance != SituationTolerance.Exact)
        {
            parts.Add(watch.Tolerance switch
            {
                SituationTolerance.Adjacent => "adjacent",
                SituationTolerance.TwoHops => "two hops",
                _ => "any place"
            });
        }

        parts.AddRange(FilterParts(watch.Filters));
        return string.Join(" · ", parts);
    }

    // The rail's ActiveLine over stored values: the same words, minus the team's display name, which
    // only the rail can look up.
    private static IEnumerable<string> FilterParts(SearchFilterValues filters)
    {
        if (filters.Side is int side)
        {
            yield return side == 3 ? "us on CT" : "us on T";
        }

        if (filters.BuyCt is { } buyCt)
        {
            yield return $"CT buy {RoundFactsValues.LowerCamel(buyCt)}";
        }

        if (filters.BuyT is { } buyT)
        {
            yield return $"T buy {RoundFactsValues.LowerCamel(buyT)}";
        }

        if (filters.Phase is { } phase)
        {
            yield return RoundFactsValues.LowerCamel(phase);
        }

        if (filters.Clock is { } clock)
        {
            yield return RoundFactsValues.LowerCamel(clock);
        }

        if (filters.ManCount is { } manCount)
        {
            yield return RoundFactsValues.LowerCamel(manCount);
        }

        if (filters.Score is { } score)
        {
            yield return RoundFactsValues.LowerCamel(score);
        }

        if (filters.Opponent is not null)
        {
            yield return "vs opponent";
        }

        if (filters.From is { } from)
        {
            yield return $"from {from:yyyy-MM-dd}";
        }

        if (filters.To is { } to)
        {
            yield return $"to {to:yyyy-MM-dd}";
        }

        if (filters.Source is { } source)
        {
            yield return source;
        }
    }

    // ── Evaluation ────────────────────────────────────────────────────────────

    // The hook: one demo merged. Only its map's watches can hit, and only when its stamp is above the
    // watermark; the result replaces what the demo contributed before, so a rebuild counts once.
    private void OnIndexed(RoundIndexedEvent indexed)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (WatchedSituation watch in _file.Watches)
            {
                if (!string.Equals(watch.Map, indexed.Map, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Dictionary<string, NewGroup> groups = GroupsLocked(watch.Id);
                if (indexed.ComputedAtTicks <= watch.WatermarkTicks)
                {
                    changed |= groups.Remove(indexed.DemoStableKey);
                    continue;
                }

                HashSet<string> only = new([indexed.DemoStableKey], StringComparer.Ordinal);
                IReadOnlyList<SituationHit> hits = _index.Query(ToQuery(watch, only));
                changed |= Replace(groups, indexed.DemoStableKey, hits, indexed.ComputedAtTicks);
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    // The full pass: every watch over every demo stamped after its watermark. Idempotent, and the
    // restart path by construction; also the answer to a drop, which has no event of its own.
    private void Reevaluate()
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (WatchedSituation watch in _file.Watches)
            {
                Dictionary<string, NewGroup> groups = GroupsLocked(watch.Id);
                Dictionary<string, NewGroup> next = new(StringComparer.Ordinal);
                if (_index.IsReady)
                {
                    foreach (IGrouping<string, SituationHit> demo in _index.Query(ToQuery(watch, indexedAfter: watch.WatermarkTicks))
                                 .GroupBy(h => h.DemoStableKey, StringComparer.Ordinal))
                    {
                        long stamp = _demoCache.TryGetIndex(demo.First().DemoPath)?.RoundIndexComputedAtTicks ?? 0;
                        next[demo.Key] = new NewGroup(stamp, [.. demo]);
                    }
                }

                if (!SameShape(groups, next))
                {
                    _new[watch.Id] = next;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private static bool SameShape(Dictionary<string, NewGroup> a, Dictionary<string, NewGroup> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach ((string key, NewGroup group) in a)
        {
            if (!b.TryGetValue(key, out NewGroup? other) || other.Hits.Count != group.Hits.Count
                || other.ComputedAtTicks != group.ComputedAtTicks)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Replace(Dictionary<string, NewGroup> groups, string key, IReadOnlyList<SituationHit> hits, long stamp)
    {
        if (hits.Count == 0)
        {
            return groups.Remove(key);
        }

        bool same = groups.TryGetValue(key, out NewGroup? previous) && previous.Hits.Count == hits.Count
                    && previous.ComputedAtTicks == stamp;
        groups[key] = new NewGroup(stamp, hits);
        return !same;
    }

    private Dictionary<string, NewGroup> GroupsLocked(Guid id)
    {
        if (!_new.TryGetValue(id, out Dictionary<string, NewGroup>? groups))
        {
            groups = new Dictionary<string, NewGroup>(StringComparer.Ordinal);
            _new[id] = groups;
        }

        return groups;
    }

    private bool MarkSeenLocked(WatchedSituation watch)
    {
        Dictionary<string, NewGroup> groups = GroupsLocked(watch.Id);
        long newest = groups.Count == 0 ? 0 : groups.Values.Max(g => g.ComputedAtTicks);
        long watermark = Math.Max(_now(), newest);
        bool changed = groups.Count > 0 || watermark != watch.WatermarkTicks;
        watch.WatermarkTicks = watermark;
        groups.Clear();
        return changed;
    }

    private void RaiseChanged() => _post(() =>
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    });

    // ── The file ──────────────────────────────────────────────────────────────

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            WatchedSituationsFile? file = JsonSerializer.Deserialize<WatchedSituationsFile>(File.ReadAllText(_path), WatchedSituationsFile.JsonOptions);
            if (file is null || file.SchemaVersion > WatchedSituationsFile.CurrentSchema)
            {
                Refuse($"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            _file = file;
            foreach (WatchedSituation watch in _file.Watches)
            {
                _new[watch.Id] = new Dictionary<string, NewGroup>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Refuse($"{FileName} could not be read: {ex.Message}");
        }
    }

    private void Refuse(string problem)
    {
        _refused = true;
        FileProblem = problem;
        _file = new WatchedSituationsFile();
    }

    private void Save()
    {
        if (_path is null || _refused)
        {
            return;
        }

        try
        {
            DemoCacheStore.WriteAtomic(_path, JsonSerializer.Serialize(_file, WatchedSituationsFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory list stands for the session and the next write retries.
        }
    }

    /// <summary>What one demo contributes to a watch's badge: its stamp and the hits in it.</summary>
    private sealed record NewGroup(long ComputedAtTicks, IReadOnlyList<SituationHit> Hits);
}

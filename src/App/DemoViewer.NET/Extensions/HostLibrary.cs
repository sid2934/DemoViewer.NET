#region

using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The demo cache's index as the SDK's <see cref="LibraryDemo" /> rows, shared by every extension and the
///     Library tab. One instance per store (<see cref="For" />), one subscription to the store. A row is
///     projected once per index row object, which the store replaces on every write, so an unchanged demo is
///     the same <see cref="LibraryDemo" /> instance however often it is read.
///     <para>
///         One row per demo, under its primary path, however many paths hold its bytes: a lookup by any of
///         them answers that row, and a write through any of them is reported under it.
///     </para>
/// </summary>
internal sealed class HostLibrary : IExtensionLibrary
{
    private static readonly ConditionalWeakTable<DemoCacheStore, HostLibrary> Shared = new();

    // The shown key of a row projected while the packs contributed: never a key the facts answer.
    private const string DeferredKey = "\0deferred";

    private readonly ConditionalWeakTable<DemoCacheIndexEntry, LibraryDemo> _projected = new();
    private string? _shownKey;
    private bool _deferred;
    private int _recheckPosted;
    private volatile bool _hasFactsStamps;
    private readonly object _gate = new();
    private readonly Func<bool> _onUiThread;
    private readonly Action<Action> _post;
    private readonly IDemoProcessingQueue? _queue;
    // The rows last reported, by primary path, and the primary each of their paths was reported under.
    private readonly Dictionary<string, DemoCacheIndexEntry> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shownAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly DemoCacheStore _store;
    private IReadOnlyList<LibraryDemo> _demos = [];
    private long _demosVersion = -1;

    /// <param name="store">The demo cache.</param>
    /// <param name="queue">The processing queue a UI-thread detail read runs on; null reads inline.</param>
    /// <param name="onUiThread">True on the UI thread; the dispatcher's check when null.</param>
    /// <param name="post">Runs an action on the UI thread later; the dispatcher's post when null.</param>
    /// <param name="facts">Resolves the analysis facts on first read; null until <see cref="For" /> is handed one.</param>
    internal HostLibrary(DemoCacheStore store, IDemoProcessingQueue? queue, Func<bool>? onUiThread = null,
        Action<Action>? post = null, Func<IAnalysisFacts>? facts = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue;
        _onUiThread = onUiThread ?? (static () => Dispatcher.UIThread.CheckAccess());
        _post = post ?? (static action => Dispatcher.UIThread.Post(action));
        _factsSource = facts;
        foreach (DemoCacheIndexEntry entry in store.Contents)
        {
            Remember(entry);
            _hasFactsStamps |= HasFactsStamp(entry);
        }

        store.Changed += OnStoreChanged;
    }

    /// <summary>The one host library over <paramref name="store" />, built on first use.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="queue">The processing queue; read by the first caller only.</param>
    /// <param name="facts">Resolves the analysis facts on first read; the first caller that passes one wins.</param>
    public static HostLibrary For(DemoCacheStore store, IDemoProcessingQueue? queue, Func<IAnalysisFacts>? facts = null)
    {
        HostLibrary library = Shared.GetValue(store, s => new HostLibrary(s, queue, facts: facts));
        if (facts is not null)
        {
            Interlocked.CompareExchange(ref library._factsSource, facts, null);
        }

        return library;
    }

    private Func<IAnalysisFacts>? _factsSource;

    /// <summary>The analysis outputs over the same store; empty until the composition root hands them over.</summary>
    public IAnalysisFacts Facts => _factsSource?.Invoke() ?? NoFacts.Instance;

    // Which facts stamps a row shows moves with the rulesets that are on, which no index write signals, so a
    // move drops every projection and the cached list. The facts are not resolved before a row carries a facts
    // stamp: resolving them reads the rules, which must not happen inside an early store event.
    // While the packs contribute on this thread the rulesets are not known yet, so a row shows every stamp
    // and one recheck is posted to drop an off ruleset's stamps, with a change, once they are.
    private (IFactsVisibility? Visibility, string Key) Shown()
    {
        if (!_hasFactsStamps || _factsSource is null)
        {
            return (null, "");
        }

        if (PackContributionSet.Collecting)
        {
            if (Interlocked.Exchange(ref _recheckPosted, 1) == 0)
            {
                _post(RecheckFacts);
            }

            return (null, DeferredKey);
        }

        return _factsSource() is IFactsVisibility visibility ? (visibility, visibility.ShownKey) : (null, "");
    }

    private static bool HasFactsStamp(DemoCacheIndexEntry entry) =>
        entry.PackStamps.Any(s => StampedFacts.RulesetOf(s.Id) is not null);

    private IFactsVisibility? Revalidate()
    {
        (IFactsVisibility? visibility, string key) = Shown();
        lock (_gate)
        {
            _deferred |= string.Equals(key, DeferredKey, StringComparison.Ordinal);
            if (!string.Equals(key, _shownKey, StringComparison.Ordinal))
            {
                _shownKey = key;
                _projected.Clear();
                _demosVersion = -1;
            }
        }

        return visibility;
    }

    /// <summary>
    ///     Raises a library-wide change when the rulesets whose facts the rows show moved since the last read, so a
    ///     reader re-reads rows that drop or regain an extension's facts. Call it when a feature gate changes.
    /// </summary>
    internal void RecheckFacts()
    {
        Interlocked.Exchange(ref _recheckPosted, 0);
        string key = Shown().Key;
        if (string.Equals(key, DeferredKey, StringComparison.Ordinal))
        {
            return;
        }

        // A row handed out while the packs contributed is reported moved even when a later read already
        // resolved the rulesets: its reader never heard.
        lock (_gate)
        {
            bool moved = _deferred || (_shownKey is not null && !string.Equals(key, _shownKey, StringComparison.Ordinal));
            _deferred = false;
            if (!moved)
            {
                return;
            }
        }

        Revalidate();
        Changed?.Invoke(new LibraryChange(null, LibraryChangeKind.Updated));
    }

    /// <summary>The store's index as rows. Rebuilt only when the index moved since the last read.</summary>
    public IReadOnlyList<LibraryDemo> Demos
    {
        get
        {
            IFactsVisibility? visibility = Revalidate();
            long version = _store.IndexVersion;
            lock (_gate)
            {
                if (version == _demosVersion)
                {
                    return _demos;
                }
            }

            List<LibraryDemo> demos = [.. _store.Contents.Select(e => Project(e, visibility))];
            lock (_gate)
            {
                _demos = demos;
                _demosVersion = version;
                return demos;
            }
        }
    }

    /// <summary>Raised on the thread the store raises its own change on, the UI thread in the app.</summary>
    public event Action<LibraryChange>? Changed;

    public LibraryDemo? Find(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return _store.TryGetPrimary(path) is { } entry ? Project(entry, Revalidate()) : null;
    }

    public LibraryDemo? FindBySha256(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        return _store.TryGetIndexBySha256(sha256) is { } entry ? Project(entry, Revalidate()) : null;
    }

    public LibraryPage Query(LibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<LibraryDemo> matches = Demos.Where(d => Matches(d, query));
        List<LibraryDemo> all = query.Sort switch
        {
            LibrarySort.OldestFirst => [.. matches.OrderBy(d => d.Modified).ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)],
            LibrarySort.FileName => [.. matches.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)],
            LibrarySort.Map => [.. matches.OrderBy(d => d.MapName ?? "", StringComparer.OrdinalIgnoreCase).ThenByDescending(d => d.Modified)],
            _ => [.. matches.OrderByDescending(d => d.Modified).ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)]
        };
        int skip = Math.Clamp(query.Skip, 0, all.Count);
        int take = query.Take is { } t ? Math.Clamp(t, 0, all.Count - skip) : all.Count - skip;
        return new LibraryPage(all.GetRange(skip, take), all.Count);
    }

    /// <summary>
    ///     The demo's detail from its record. Read before returning off the UI thread and in a host with no
    ///     queue; a light queue job on the UI thread. The read leaves the store's capacity-1 cache alone.
    /// </summary>
    public Task<LibraryDemoDetail?> GetDetailAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (_queue is null || QueueWork.RunsOnPool(_queue) || !_onUiThread())
        {
            return Task.FromResult(Detail(path));
        }

        return QueueWork.RunAsync<LibraryDemoDetail?>(_queue, QueueJobKind.StoreLoad, "Load: demo record", "library",
            () => Detail(path), null, DemoJobPriority.UserRequested).WaitAsync(cancellationToken);
    }

    /// <summary>The detail of a demo whose record the caller already read.</summary>
    /// <param name="record">The record.</param>
    public LibraryDemoDetail DetailOf(DemoCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        IFactsVisibility? visibility = Revalidate();
        DemoCacheIndexEntry? entry = _store.TryGetIndex(record.Path);
        LibraryDemo demo = entry is not null && entry.MatchesFile(record.Size, record.ModifiedTicks)
                                             && _store.TryGetPrimary(record.Path) is { } primary
            ? Project(primary, visibility)
            : ProjectRow(record.ToIndexEntry(), visibility);
        return new LibraryDemoDetail(demo, record.TickRate, record.TickCount, record.ServerStartTick,
            [.. record.Players.Select(p => new LibraryPlayer(p.Slot, p.Name, SteamIdOf(p.SteamId64), p.Team, p.IsBot, p.IsCoach))],
            [.. record.Rounds.Select(r => new LibraryRound(r.Number, r.StartTickFrameClock))]);
    }

    private LibraryDemoDetail? Detail(string path) =>
        _store.TryLoadRecord(path, false) is { } record ? DetailOf(record) : null;

    /// <summary>The row for an index entry: the same instance for the same entry object while the shown facts hold.</summary>
    public LibraryDemo Project(DemoCacheIndexEntry entry) => Project(entry, Revalidate());

    private LibraryDemo Project(DemoCacheIndexEntry entry, IFactsVisibility? visibility) =>
        _projected.GetValue(entry, e => ProjectRow(e, visibility));

    private static LibraryDemo ProjectRow(DemoCacheIndexEntry entry, IFactsVisibility? visibility) =>
        new(entry.Path, Path.GetFileName(entry.Path), entry.Map, new DateTime(entry.ModifiedTicks, DateTimeKind.Local), entry.Size)
        {
            Sha256 = entry.Sha256,
            Server = entry.Server,
            SourceKind = entry.SourceKind,
            DurationSeconds = entry.DurationSeconds,
            RoundCount = entry.RoundCount,
            CtScore = entry.CtScore,
            TScore = entry.TScore,
            CtClan = entry.CtClan,
            TClan = entry.TClan,
            PlayerNames = [.. entry.PlayerNames],
            CtPlayers = Side(entry.CtPlayers),
            TPlayers = Side(entry.TPlayers),
            CtSideWins = entry.CtSideWins,
            TSideWins = entry.TSideWins,
            State = entry.Tier switch
            {
                DemoCacheTier.Analysis => LibraryDemoState.Analyzed,
                DemoCacheTier.Parse => LibraryDemoState.Parsed,
                DemoCacheTier.Header => LibraryDemoState.HeaderRead,
                _ => LibraryDemoState.Found
            },
            Facts = [.. entry.PackStamps.Where(s => visibility?.Shows(s.Id) ?? true).Select(s => new LibraryFactState(s.Id, s.Schema, s.Fingerprint, s.State switch
            {
                DemoAnalysisState.Indexed => DemoDataState.Written,
                DemoAnalysisState.Failed => DemoDataState.Failed,
                _ => DemoDataState.Pending
            }, s.Count))]
        };

    // An id the row cannot carry as a number is not a SteamID64; the side lists it never.
    private static List<LibrarySidePlayer>? Side(List<IndexSidePlayer>? players) =>
        players is null
            ? null
            : [.. players.Select(p => (Id: SteamIdOf(p.SteamId64), p.Name, p.Slots)).Where(p => p.Id != 0)
                .Select(p => new LibrarySidePlayer(p.Id, p.Name) { Slots = [.. p.Slots] })];

    private static ulong SteamIdOf(string steamId64) =>
        ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;

    private static bool Matches(LibraryDemo demo, LibraryQuery query) =>
        (query.Map is null || string.Equals(demo.MapName, query.Map, StringComparison.OrdinalIgnoreCase))
        && (query.ModifiedFrom is not { } from || demo.Modified >= from)
        && (query.ModifiedBefore is not { } before || demo.Modified < before)
        && (query.Clan is null || string.Equals(demo.CtClan, query.Clan, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(demo.TClan, query.Clan, StringComparison.OrdinalIgnoreCase))
        && (query.PlayerName is null || demo.PlayerNames.Any(n => n.Contains(query.PlayerName, StringComparison.OrdinalIgnoreCase)))
        && (query.SteamId64 is not { } id || (demo.CtPlayers?.Any(p => p.SteamId64 == id) ?? false)
                                          || (demo.TPlayers?.Any(p => p.SteamId64 == id) ?? false))
        && (query.AtLeast is not { } state || demo.State >= state)
        && (query.HasFact is null || demo.Fact(query.HasFact) is { IsWritten: true });

    // The store raises with the changed path, or null for a batch. The kind is read off the row the
    // library last saw for the demo, so a stamp-only write tells apart from a parse. A write that moved a
    // path between demos, or a demo's paths, may have changed which rows exist and under which path, so it is
    // reported library-wide.
    private void OnStoreChanged(string? path)
    {
        if (!_hasFactsStamps)
        {
            _hasFactsStamps = path is null
                ? _store.Contents.Any(HasFactsStamp)
                : _store.TryGetIndex(path) is { } written && HasFactsStamp(written);
        }

        IFactsVisibility? visibility = Revalidate();
        LibraryChange change;
        lock (_gate)
        {
            DemoCacheIndexEntry? after = path is null ? null : _store.TryGetPrimary(path);
            DemoCacheIndexEntry? before = path is not null && _shownAt.TryGetValue(path, out string? shown)
                ? _seen.GetValueOrDefault(shown)
                : null;
            if (path is null || !OneRowMoved(before, after))
            {
                _seen.Clear();
                _shownAt.Clear();
                foreach (DemoCacheIndexEntry entry in _store.Contents)
                {
                    Remember(entry);
                }

                change = new LibraryChange(null, LibraryChangeKind.Updated);
            }
            else
            {
                if (before is not null)
                {
                    Forget(before);
                }

                if (after is not null)
                {
                    Remember(after);
                }

                change = new LibraryChange(after?.Path ?? before?.Path ?? path, (before, after) switch
                {
                    (null, null) => LibraryChangeKind.Removed,
                    (null, _) => LibraryChangeKind.Added,
                    (_, null) => LibraryChangeKind.Removed,
                    _ => OnlyFactsMoved(Project(before, visibility), Project(after, visibility)) ? LibraryChangeKind.FactsUpdated : LibraryChangeKind.Updated
                });
            }
        }

        Changed?.Invoke(change);
    }

    // True when the write added, removed or changed one row whose paths stayed as they were.
    private static bool OneRowMoved(DemoCacheIndexEntry? before, DemoCacheIndexEntry? after) =>
        (before, after) switch
        {
            (null, null) => true,
            (null, _) => after.Locations.Count <= 1,
            (_, null) => before.Locations.Count <= 1,
            _ => string.Equals(before.Path, after.Path, StringComparison.OrdinalIgnoreCase)
                 && before.Locations.Select(l => l.Path).SequenceEqual(after.Locations.Select(l => l.Path), StringComparer.OrdinalIgnoreCase)
        };

    // Under _gate.
    private void Remember(DemoCacheIndexEntry entry)
    {
        _seen[entry.Path] = entry;
        _shownAt[entry.Path] = entry.Path;
        foreach (DemoLocation location in entry.Locations)
        {
            _shownAt[location.Path] = entry.Path;
        }
    }

    // Under _gate.
    private void Forget(DemoCacheIndexEntry entry)
    {
        _seen.Remove(entry.Path);
        _shownAt.Remove(entry.Path);
        foreach (DemoLocation location in entry.Locations)
        {
            _shownAt.Remove(location.Path);
        }
    }

    private static bool OnlyFactsMoved(LibraryDemo before, LibraryDemo after) =>
        !before.Facts.SequenceEqual(after.Facts)
        && before with { PlayerNames = after.PlayerNames, CtPlayers = after.CtPlayers, TPlayers = after.TPlayers, Facts = after.Facts } == after
        && before.PlayerNames.SequenceEqual(after.PlayerNames, StringComparer.Ordinal)
        && SameSide(before.CtPlayers, after.CtPlayers)
        && SameSide(before.TPlayers, after.TPlayers);

    private static bool SameSide(IReadOnlyList<LibrarySidePlayer>? a, IReadOnlyList<LibrarySidePlayer>? b) =>
        a is null
            ? b is null
            : b is not null && a.Count == b.Count && a.Zip(b).All(p => p.First.SteamId64 == p.Second.SteamId64
                                                                      && string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)
                                                                      && p.First.Slots.SequenceEqual(p.Second.Slots));
}

/// <summary>
///     The library as one extension sees it. Each of the extension's change handlers runs as the extension's,
///     so one that throws is reported against it and the next still runs.
/// </summary>
internal sealed class ExtensionLibraryView : IExtensionLibrary
{
    private readonly ExtensionGuard _guard;
    private readonly HostLibrary _host;
    private readonly object _gate = new();
    private Action<LibraryChange>? _changed;
    private bool _subscribed;

    public ExtensionLibraryView(HostLibrary host, ExtensionGuard guard)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public IReadOnlyList<LibraryDemo> Demos => _host.Demos;

    public LibraryDemo? Find(string path) => _host.Find(path);

    public LibraryDemo? FindBySha256(string sha256) => _host.FindBySha256(sha256);

    public LibraryPage Query(LibraryQuery query) => _host.Query(query);

    public Task<LibraryDemoDetail?> GetDetailAsync(string path, CancellationToken cancellationToken = default) =>
        _host.GetDetailAsync(path, cancellationToken);

    public IAnalysisFacts Facts
    {
        get
        {
            IAnalysisFacts inner = _host.Facts;
            lock (_gate)
            {
                if (_facts is null || !ReferenceEquals(_facts.Inner, inner))
                {
                    _facts = new ExtensionFactsView(inner, _guard);
                }

                return _facts;
            }
        }
    }

    private ExtensionFactsView? _facts;

    public event Action<LibraryChange>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (!_subscribed)
                {
                    _subscribed = true;
                    _host.Changed += Forward;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    private void Forward(LibraryChange change)
    {
        Action<LibraryChange>? handlers;
        lock (_gate)
        {
            handlers = _changed;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<LibraryChange> handler in handlers.GetInvocationList().Cast<Action<LibraryChange>>())
        {
            _guard.Run("library change handler", () => handler(change));
        }
    }
}

/// <summary>The analysis facts as one extension sees them: each of its Round Facts handlers runs as the extension's.</summary>
internal sealed class ExtensionFactsView(IAnalysisFacts inner, ExtensionGuard guard) : IAnalysisFacts
{
    public IAnalysisFacts Inner => inner;

    public IReadOnlyList<FactKey> Declared => inner.Declared;

    public IRoundFacts RoundFacts { get; } = new GuardedRoundFacts(inner.RoundFacts, guard);

    public FactStatus Status(string demoPath, FactKey key) => inner.Status(demoPath, key);

    public bool IsCurrent(string demoPath, FactKey key) => inner.IsCurrent(demoPath, key);

    public FactTable? TryGet(string demoPath, FactKey key) => inner.TryGet(demoPath, key);

    public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => inner.Highlights(demoPath);

    private sealed class GuardedRoundFacts(IRoundFacts rows, ExtensionGuard guard) : IRoundFacts
    {
        private readonly List<(Action<string> Handler, Action<string> Guarded)> _handlers = [];

        public int Schema => rows.Schema;

        public RoundFactsRows? TryGet(string demoPath) => rows.TryGet(demoPath);

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) => rows.RoundAt(demoPath, frameClockTick);

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => rows.FactsFor(demoPath, round, atTick);

        public event Action<string>? Updated
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                Action<string> guarded = path => guard.Run("round facts handler", () => value(path));
                lock (_handlers)
                {
                    _handlers.Add((value, guarded));
                }

                rows.Updated += guarded;
            }
            remove
            {
                Action<string>? guarded = null;
                lock (_handlers)
                {
                    int i = _handlers.FindIndex(h => h.Handler.Equals(value));
                    if (i >= 0)
                    {
                        guarded = _handlers[i].Guarded;
                        _handlers.RemoveAt(i);
                    }
                }

                if (guarded is not null)
                {
                    rows.Updated -= guarded;
                }
            }
        }
    }
}

/// <summary>The facts of a host that keeps none: nothing declared, nothing written.</summary>
internal sealed class NoFacts : IAnalysisFacts, IRoundFacts
{
    public static NoFacts Instance { get; } = new();

    public IReadOnlyList<FactKey> Declared => [];

    public IRoundFacts RoundFacts => this;

    public FactStatus Status(string demoPath, FactKey key) => FactStatus.Absent;

    public bool IsCurrent(string demoPath, FactKey key) => false;

    public FactTable? TryGet(string demoPath, FactKey key) => null;

    public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => [];

    public int Schema => RoundFactsRows.CurrentSchema;

    public RoundFactsRows? TryGet(string demoPath) => null;

    public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

    public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];

    public event Action<string>? Updated
    {
        add { }
        remove { }
    }
}

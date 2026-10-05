#region

using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The demo cache's index as the SDK's <see cref="LibraryDemo" /> rows, shared by every extension and the
///     Library tab. One instance per store (<see cref="For" />), one subscription to the store. A row is
///     projected once per index row object, which the store replaces on every write, so an unchanged demo is
///     the same <see cref="LibraryDemo" /> instance however often it is read.
/// </summary>
internal sealed class HostLibrary
{
    private static readonly ConditionalWeakTable<DemoCacheStore, HostLibrary> Shared = new();

    private readonly ConditionalWeakTable<DemoCacheIndexEntry, LibraryDemo> _projected = new();
    private readonly object _gate = new();
    private readonly Func<bool> _onUiThread;
    private readonly IDemoProcessingQueue? _queue;
    private readonly Dictionary<string, DemoCacheIndexEntry> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly DemoCacheStore _store;
    private IReadOnlyList<LibraryDemo> _demos = [];
    private long _demosVersion = -1;

    /// <param name="store">The demo cache.</param>
    /// <param name="queue">The processing queue a UI-thread detail read runs on; null reads inline.</param>
    /// <param name="onUiThread">True on the UI thread; the dispatcher's check when null.</param>
    internal HostLibrary(DemoCacheStore store, IDemoProcessingQueue? queue, Func<bool>? onUiThread = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue;
        _onUiThread = onUiThread ?? (static () => Dispatcher.UIThread.CheckAccess());
        foreach (DemoCacheIndexEntry entry in store.Index)
        {
            _seen[entry.Path] = entry;
        }

        store.Changed += OnStoreChanged;
    }

    /// <summary>The one host library over <paramref name="store" />, built on first use.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="queue">The processing queue; read by the first caller only.</param>
    public static HostLibrary For(DemoCacheStore store, IDemoProcessingQueue? queue) =>
        Shared.GetValue(store, s => new HostLibrary(s, queue));

    /// <summary>The store's index as rows. Rebuilt only when the index moved since the last read.</summary>
    public IReadOnlyList<LibraryDemo> Demos
    {
        get
        {
            long version = _store.IndexVersion;
            lock (_gate)
            {
                if (version == _demosVersion)
                {
                    return _demos;
                }
            }

            List<LibraryDemo> demos = [.. _store.Index.Select(Project)];
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
        return _store.TryGetIndex(path) is { } entry ? Project(entry) : null;
    }

    public LibraryDemo? FindBySha256(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        return _store.TryGetIndexBySha256(sha256) is { } entry ? Project(entry) : null;
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
    public Task<LibraryDemoDetail?> GetDetailAsync(string path, CancellationToken cancellationToken)
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
        DemoCacheIndexEntry? entry = _store.TryGetIndex(record.Path);
        LibraryDemo demo = entry is not null && entry.MatchesFile(record.Size, record.ModifiedTicks)
            ? Project(entry)
            : ProjectRow(record.ToIndexEntry());
        return new LibraryDemoDetail(demo, record.TickRate, record.TickCount, record.ServerStartTick,
            [.. record.Players.Select(p => new LibraryPlayer(p.Slot, p.Name, SteamIdOf(p.SteamId64), p.Team, p.IsBot, p.IsCoach))],
            [.. record.Rounds.Select(r => new LibraryRound(r.Number, r.StartTickFrameClock))]);
    }

    private LibraryDemoDetail? Detail(string path) =>
        _store.TryLoadRecord(path, false) is { } record ? DetailOf(record) : null;

    /// <summary>The row for an index entry: the same instance for the same entry object.</summary>
    public LibraryDemo Project(DemoCacheIndexEntry entry) => _projected.GetValue(entry, ProjectRow);

    private static LibraryDemo ProjectRow(DemoCacheIndexEntry entry) =>
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
            State = entry.Tier switch
            {
                DemoCacheTier.Analysis => LibraryDemoState.Analyzed,
                DemoCacheTier.Parse => LibraryDemoState.Parsed,
                DemoCacheTier.Header => LibraryDemoState.HeaderRead,
                _ => LibraryDemoState.Found
            },
            Facts = [.. entry.PackStamps.Select(s => new LibraryFactState(s.Id, s.Schema, s.Fingerprint, s.State switch
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
            : [.. players.Select(p => (Id: SteamIdOf(p.SteamId64), p.Name)).Where(p => p.Id != 0).Select(p => new LibrarySidePlayer(p.Id, p.Name))];

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
    // library last saw for the path, so a stamp-only write tells apart from a parse.
    private void OnStoreChanged(string? path)
    {
        LibraryChange change;
        lock (_gate)
        {
            if (path is null)
            {
                _seen.Clear();
                foreach (DemoCacheIndexEntry entry in _store.Index)
                {
                    _seen[entry.Path] = entry;
                }

                change = new LibraryChange(null, LibraryChangeKind.Updated);
            }
            else
            {
                _seen.TryGetValue(path, out DemoCacheIndexEntry? before);
                DemoCacheIndexEntry? after = _store.TryGetIndex(path);
                if (after is null)
                {
                    _seen.Remove(path);
                }
                else
                {
                    _seen[path] = after;
                }

                change = new LibraryChange(path, (before, after) switch
                {
                    (null, null) => LibraryChangeKind.Removed,
                    (null, _) => LibraryChangeKind.Added,
                    (_, null) => LibraryChangeKind.Removed,
                    _ => OnlyFactsMoved(Project(before), Project(after)) ? LibraryChangeKind.FactsUpdated : LibraryChangeKind.Updated
                });
            }
        }

        Changed?.Invoke(change);
    }

    private static bool OnlyFactsMoved(LibraryDemo before, LibraryDemo after) =>
        !before.Facts.SequenceEqual(after.Facts)
        && before with { PlayerNames = after.PlayerNames, CtPlayers = after.CtPlayers, TPlayers = after.TPlayers, Facts = after.Facts } == after
        && before.PlayerNames.SequenceEqual(after.PlayerNames, StringComparer.Ordinal)
        && SameSide(before.CtPlayers, after.CtPlayers)
        && SameSide(before.TPlayers, after.TPlayers);

    private static bool SameSide(IReadOnlyList<LibrarySidePlayer>? a, IReadOnlyList<LibrarySidePlayer>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
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

#region

using System.Collections.Concurrent;
using System.Globalization;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>An account's demos with their stats, as <see cref="PlayerRowCache" /> last filled them.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="IndexVersion">The index version the rows were filled at.</param>
/// <param name="Demos">Every demo of the account, newest match first.</param>
public sealed record PlayerRows(ulong SteamId64, long IndexVersion, IReadOnlyList<PlayerDemo> Demos)
{
    /// <summary>The pool of the demos that pass <paramref name="filter" />. Reads nothing: a filter change is free.</summary>
    /// <param name="filter">Which demos; all when null.</param>
    public PlayerAggregate Aggregate(PlayerFilter? filter = null) => PlayerAggregate.Of(PlayerAggregate.Window(Demos, filter ?? PlayerFilter.All));
}

/// <summary>Every account's demos with their stats, as <see cref="PlayerRowCache.LoadLibraryAsync" /> last filled them.</summary>
/// <param name="IndexVersion">The index version the rows were filled at.</param>
/// <param name="Accounts">Each account's demos, newest match first, as <see cref="PlayerRows.Demos" /> holds one account's.</param>
public sealed record LibraryRows(long IndexVersion, IReadOnlyDictionary<ulong, IReadOnlyList<PlayerDemo>> Accounts);

/// <summary>How far a fill has got: sidecars read of those it has to read.</summary>
public readonly record struct PlayerRowsProgress(int Done, int Total);

/// <summary>
///     The facet rows of the accounts the Player Overview shows, held in memory so a filter change reads no file.
///     A demo's row is kept under its content id and its <c>facts:player_match</c> stamp (fingerprint, state and
///     write time), so after the index moves a fill reads back only the demos whose facet was written since or
///     that are new to the account; a facts write moves the stamp, and a re-walk under a new ruleset moves the
///     fingerprint. The last few accounts are kept.
///     <para>
///         The library fill (<see cref="LoadLibraryAsync" />) serves every account at once, for the benchmarks. Both
///         fills go through one read per demo and stamp, which merges the rows of every account in the demo: the
///         tab's fill and the benchmarks' fill running side by side read each sidecar once between them.
///     </para>
/// </summary>
public sealed class PlayerRowCache
{
    private const int Capacity = 4;

    private readonly PlayerDirectory _directory;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<(string Id, PackStamp Stamp), Lazy<Held>> _held = new();
    private readonly object _libraryGate = new();
    private readonly Func<string, FactTable?> _read;
    private readonly LinkedList<Subject> _subjects = [];
    private readonly DemoCacheStore _store;
    private LibraryRows? _library;

    /// <param name="store">The demo cache.</param>
    /// <param name="directory">The player directory over <paramref name="store" />.</param>
    /// <param name="read">Reads a demo's <c>player_match</c> table by path, current or stale; null when none is written.</param>
    public PlayerRowCache(DemoCacheStore store, PlayerDirectory directory, Func<string, FactTable?> read)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    /// <summary>The account's rows when they are filled at the index's current version, else null: call <see cref="LoadAsync" />.</summary>
    /// <param name="steamId64">The account.</param>
    public PlayerRows? TryGet(ulong steamId64)
    {
        long version = _store.IndexVersion;
        lock (_gate)
        {
            return Find(steamId64)?.Rows is { } rows && rows.IndexVersion == version ? rows : null;
        }
    }

    /// <summary>
    ///     Fills the account's rows off the calling thread, reading each sidecar it does not hold under the demo's
    ///     current stamp, and reports each read. Returns at once when the rows are current.
    /// </summary>
    /// <param name="steamId64">The account.</param>
    /// <param name="progress">Told the count of sidecars to read, then each one read.</param>
    /// <param name="cancellationToken">Stops between reads; what was read is kept.</param>
    public Task<PlayerRows> LoadAsync(ulong steamId64, IProgress<PlayerRowsProgress>? progress = null, CancellationToken cancellationToken = default) =>
        TryGet(steamId64) is { } current
            ? Task.FromResult(current)
            : Task.Run(() => Fill(steamId64, progress, cancellationToken), cancellationToken);

    /// <summary>Every account's rows when they are filled at the index's current version, else null: call <see cref="LoadLibraryAsync" />.</summary>
    public LibraryRows? TryGetLibrary()
    {
        long version = _store.IndexVersion;
        lock (_gate)
        {
            return _library is { } library && library.IndexVersion == version ? library : null;
        }
    }

    /// <summary>
    ///     Fills every account's rows off the calling thread, reading each sidecar it does not hold under the demo's
    ///     current stamp once for all the accounts in it, and reports each read. Returns at once when the rows are
    ///     current.
    /// </summary>
    /// <param name="progress">Told the count of sidecars to read, then each one read.</param>
    /// <param name="cancellationToken">Stops between reads; what was read is kept.</param>
    public Task<LibraryRows> LoadLibraryAsync(IProgress<PlayerRowsProgress>? progress = null, CancellationToken cancellationToken = default) =>
        TryGetLibrary() is { } current
            ? Task.FromResult(current)
            : Task.Run(() => FillLibrary(progress, cancellationToken), cancellationToken);

    private Subject? Find(ulong steamId64)
    {
        for (LinkedListNode<Subject>? node = _subjects.First; node is not null; node = node.Next)
        {
            if (node.Value.SteamId64 == steamId64)
            {
                return node.Value;
            }
        }

        return null;
    }

    private Subject Use(ulong steamId64)
    {
        lock (_gate)
        {
            Subject subject = Find(steamId64) ?? new Subject(steamId64);
            _subjects.Remove(subject);
            _subjects.AddFirst(subject);
            while (_subjects.Count > Capacity)
            {
                _subjects.RemoveLast();
            }

            return subject;
        }
    }

    private PlayerRows Fill(ulong steamId64, IProgress<PlayerRowsProgress>? progress, CancellationToken cancellationToken)
    {
        Subject subject = Use(steamId64);

        // One fill per account at a time: a second caller waits and finds the rows current.
        lock (subject)
        {
            // Read before the rows: a write in between leaves the rows one version behind, filled again next load.
            long version = _store.IndexVersion;
            if (subject.Rows?.IndexVersion == version)
            {
                return subject.Rows;
            }

            IReadOnlyList<PlayerMatch> matches = _directory.Matches(steamId64);
            Dictionary<string, Entry> entries = new(matches.Count, StringComparer.Ordinal);
            List<(PlayerMatch Match, string Id, PackStamp Stamp)> toRead = [];
            foreach (PlayerMatch match in matches)
            {
                string id = ContentIdOf(match.Row);
                PackStamp? stamp = match.Row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId));
                if (stamp is not { State: DemoAnalysisState.Indexed })
                {
                    entries[id] = new Entry(stamp, false, null);
                }
                else if (subject.Entries.TryGetValue(id, out Entry? held) && held.Stamp == stamp)
                {
                    entries[id] = held;
                }
                else
                {
                    toRead.Add((match, id, stamp));
                }
            }

            progress?.Report(new PlayerRowsProgress(0, toRead.Count));
            for (int i = 0; i < toRead.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Keep what was read: the next load reads only the rest.
                    foreach ((string read, Entry entry) in entries)
                    {
                        subject.Entries[read] = entry;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                (PlayerMatch match, string id, PackStamp stamp) = toRead[i];
                Held held = Read(match.Row, id, stamp);
                entries[id] = new Entry(stamp, held.HasFacet, held.Stats?.GetValueOrDefault(steamId64));
                progress?.Report(new PlayerRowsProgress(i + 1, toRead.Count));
            }

            subject.Entries = entries;
            List<PlayerDemo> demos = new(matches.Count);
            foreach (PlayerMatch match in matches)
            {
                Entry entry = entries[ContentIdOf(match.Row)];
                demos.Add(new PlayerDemo(match, entry.HasFacet, entry.Stats));
            }

            return subject.Rows = new PlayerRows(steamId64, version, demos);
        }
    }

    private LibraryRows FillLibrary(IProgress<PlayerRowsProgress>? progress, CancellationToken cancellationToken)
    {
        // One library fill at a time: a second caller waits and finds the rows current.
        lock (_libraryGate)
        {
            long version = _store.IndexVersion;
            if (TryGetLibrary() is { } current)
            {
                return current;
            }

            IReadOnlyList<PlayerEntry> players = _directory.Players;
            Dictionary<ulong, IReadOnlyList<PlayerMatch>> matches = new(players.Count);
            Dictionary<string, Held> entries = new(StringComparer.Ordinal);
            HashSet<(string Id, PackStamp Stamp)> live = [];
            List<(DemoCacheIndexEntry Row, string Id, PackStamp Stamp)> toRead = [];
            foreach (PlayerEntry player in players)
            {
                IReadOnlyList<PlayerMatch> theirs = _directory.Matches(player.SteamId64);
                matches[player.SteamId64] = theirs;
                foreach (PlayerMatch match in theirs)
                {
                    string id = ContentIdOf(match.Row);
                    if (entries.ContainsKey(id))
                    {
                        continue;
                    }

                    PackStamp? stamp = match.Row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId));
                    if (stamp is not { State: DemoAnalysisState.Indexed })
                    {
                        entries[id] = Held.None;
                        continue;
                    }

                    live.Add((id, stamp));
                    if (_held.TryGetValue((id, stamp), out Lazy<Held>? held) && held.IsValueCreated)
                    {
                        entries[id] = held.Value;
                    }
                    else
                    {
                        // Claimed now, so the demo's other accounts do not queue it again.
                        entries[id] = Held.None;
                        toRead.Add((match.Row, id, stamp));
                    }
                }
            }

            progress?.Report(new PlayerRowsProgress(0, toRead.Count));
            for (int i = 0; i < toRead.Count; i++)
            {
                // What was read stays held: the next fill reads only the rest.
                cancellationToken.ThrowIfCancellationRequested();
                (DemoCacheIndexEntry row, string id, PackStamp stamp) = toRead[i];
                entries[id] = Read(row, id, stamp);
                progress?.Report(new PlayerRowsProgress(i + 1, toRead.Count));
            }

            // A stamp that moved, or a demo that left the library, leaves its old read behind.
            foreach ((string Id, PackStamp Stamp) key in _held.Keys)
            {
                if (!live.Contains(key))
                {
                    _held.TryRemove(key, out _);
                }
            }

            Dictionary<ulong, IReadOnlyList<PlayerDemo>> accounts = new(matches.Count);
            foreach ((ulong steamId64, IReadOnlyList<PlayerMatch> theirs) in matches)
            {
                List<PlayerDemo> demos = new(theirs.Count);
                foreach (PlayerMatch match in theirs)
                {
                    Held held = entries[ContentIdOf(match.Row)];
                    demos.Add(new PlayerDemo(match, held.HasFacet, held.Stats?.GetValueOrDefault(steamId64)));
                }

                accounts[steamId64] = demos;
            }

            LibraryRows library = new(version, accounts);
            lock (_gate)
            {
                _library = library;
            }

            return library;
        }
    }

    // A demo's facet merged for every account in it, read once per stamp by whichever fill asks first; a read
    // that throws is not kept, so the next fill tries again.
    private Held Read(DemoCacheIndexEntry row, string id, PackStamp stamp)
    {
        Lazy<Held> held = _held.GetOrAdd((id, stamp),
            _ => new Lazy<Held>(() => _read(row.Path) is { } table ? new Held(true, MergeAll(table.Rows)) : Held.None));
        try
        {
            return held.Value;
        }
        catch (Exception)
        {
            _held.TryRemove(new KeyValuePair<(string, PackStamp), Lazy<Held>>((id, stamp), held));
            throw;
        }
    }

    // Every account in one demo's facet, each merged as PlayerAggregate.Merge merges one.
    private static Dictionary<ulong, IReadOnlyDictionary<string, double>> MergeAll(IReadOnlyList<FactRow> rows)
    {
        HashSet<ulong> ids = [];
        foreach (FactRow row in rows)
        {
            if (ulong.TryParse(row.Dimensions.GetValueOrDefault("steam_id")?.AsText(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        Dictionary<ulong, IReadOnlyDictionary<string, double>> merged = new(ids.Count);
        foreach (ulong id in ids)
        {
            if (PlayerAggregate.Merge(rows, id) is { } stats)
            {
                merged[id] = stats;
            }
        }

        return merged;
    }

    // What a demo is across copies and renames: its content hash, or its one path before it is hashed.
    private static string ContentIdOf(DemoCacheIndexEntry row) => string.IsNullOrEmpty(row.Sha256) ? row.Path : row.Sha256;

    private sealed record Entry(PackStamp? Stamp, bool HasFacet, IReadOnlyDictionary<string, double>? Stats);

    private sealed record Held(bool HasFacet, IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, double>>? Stats)
    {
        public static readonly Held None = new(false, null);
    }

    private sealed class Subject(ulong steamId64)
    {
        public ulong SteamId64 { get; } = steamId64;

        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);

        public PlayerRows? Rows { get; set; }
    }
}

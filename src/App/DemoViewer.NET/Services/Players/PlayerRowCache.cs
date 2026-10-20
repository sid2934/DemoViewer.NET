#region

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

/// <summary>How far a fill has got: sidecars read of those it has to read.</summary>
public readonly record struct PlayerRowsProgress(int Done, int Total);

/// <summary>
///     The facet rows of the accounts the Player Overview shows, held in memory so a filter change reads no file.
///     A demo's row is kept under its content id and its <c>facts:player_match</c> stamp (fingerprint, state and
///     write time), so after the index moves a fill reads back only the demos whose facet was written since or
///     that are new to the account; a facts write moves the stamp, and a re-walk under a new ruleset moves the
///     fingerprint. Each demo also keeps the rating inputs of every other account in it, which the teammates
///     section reads, so a teammate costs no read of their own. The last few accounts are kept.
/// </summary>
public sealed class PlayerRowCache
{
    private const int Capacity = 4;

    private readonly PlayerDirectory _directory;
    private readonly object _gate = new();
    private readonly Func<string, FactTable?> _read;
    private readonly LinkedList<Subject> _subjects = [];
    private readonly DemoCacheStore _store;

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
                    entries[id] = new Entry(stamp, false, null, null);
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
                FactTable? table = _read(match.Row.Path);
                entries[id] = table is null
                    ? new Entry(stamp, false, null, null)
                    : new Entry(stamp, true, PlayerAggregate.Merge(table.Rows, steamId64),
                        PlayerAggregate.MergeOthers(table.Rows, steamId64, PlayerDemo.OtherColumns));
                progress?.Report(new PlayerRowsProgress(i + 1, toRead.Count));
            }

            subject.Entries = entries;
            List<PlayerDemo> demos = new(matches.Count);
            foreach (PlayerMatch match in matches)
            {
                Entry entry = entries[ContentIdOf(match.Row)];
                demos.Add(new PlayerDemo(match, entry.HasFacet, entry.Stats) { Others = entry.Others });
            }

            return subject.Rows = new PlayerRows(steamId64, version, demos);
        }
    }

    // What a demo is across copies and renames: its content hash, or its one path before it is hashed.
    private static string ContentIdOf(DemoCacheIndexEntry row) => string.IsNullOrEmpty(row.Sha256) ? row.Path : row.Sha256;

    private sealed record Entry(PackStamp? Stamp, bool HasFacet, IReadOnlyDictionary<string, double>? Stats,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, double>>? Others);

    private sealed class Subject(ulong steamId64)
    {
        public ulong SteamId64 { get; } = steamId64;

        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);

        public PlayerRows? Rows { get; set; }
    }
}

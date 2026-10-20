#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>An account's rank row in each of its demos, as <see cref="PlayerRankCache" /> last filled them.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="IndexVersion">The index version the rows were filled at.</param>
/// <param name="Demos">Every demo of the account, newest match first.</param>
public sealed record PlayerRanks(ulong SteamId64, long IndexVersion, IReadOnlyList<PlayerRank> Demos)
{
    private readonly Lazy<Dictionary<string, MatchRankRow?>> _byContent = new(() =>
    {
        Dictionary<string, MatchRankRow?> map = new(Demos.Count, StringComparer.Ordinal);
        foreach (PlayerRank demo in Demos)
        {
            map[PlayerRankCache.ContentIdOf(demo.Match.Row)] = demo.Rank;
        }

        return map;
    });

    /// <summary>The account's rank row in the demo <paramref name="row" /> is, or null when it has none or it is not read.</summary>
    /// <param name="row">The demo's index row.</param>
    public MatchRankRow? For(DemoCacheIndexEntry row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _byContent.Value.GetValueOrDefault(PlayerRankCache.ContentIdOf(row));
    }
}

/// <summary>One demo of an account with the account's rank row in it.</summary>
/// <param name="Match">The demo.</param>
/// <param name="Rank">The account's row from <see cref="DemoMatchMeta.Players" />; null when the demo carries no rank for it.</param>
public sealed record PlayerRank(PlayerMatch Match, MatchRankRow? Rank);

/// <summary>
///     The rank rows of the accounts the Player Overview shows. The rows stay on each demo's record, not its index
///     row, so a fill reads one sidecar per demo; a demo's row is kept under its content id and its
///     <c>matchrank</c> stamp, so after the index moves only the demos whose rank was written since, or that are
///     new to the account, are read again. The last few accounts are kept.
/// </summary>
public sealed class PlayerRankCache
{
    private const int Capacity = 4;

    private readonly PlayerDirectory _directory;
    private readonly object _gate = new();
    private readonly Func<string, DemoMatchMeta?> _read;
    private readonly DemoCacheStore _store;
    private readonly LinkedList<Subject> _subjects = [];

    /// <param name="store">The demo cache.</param>
    /// <param name="directory">The player directory over <paramref name="store" />.</param>
    /// <param name="read">Reads a demo's match metadata by path; the record's, without evicting the open demo's, when null.</param>
    public PlayerRankCache(DemoCacheStore store, PlayerDirectory directory, Func<string, DemoMatchMeta?>? read = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _read = read ?? (path => store.TryLoadRecord(path, false)?.MatchMeta);
    }

    /// <summary>The account's rows when they are filled at the index's current version, else null: call <see cref="LoadAsync" />.</summary>
    /// <param name="steamId64">The account.</param>
    public PlayerRanks? TryGet(ulong steamId64)
    {
        long version = _store.IndexVersion;
        lock (_gate)
        {
            return Find(steamId64)?.Ranks is { } ranks && ranks.IndexVersion == version ? ranks : null;
        }
    }

    /// <summary>Fills the account's rows off the calling thread. Returns at once when they are current.</summary>
    /// <param name="steamId64">The account.</param>
    public Task<PlayerRanks> LoadAsync(ulong steamId64) =>
        TryGet(steamId64) is { } current ? Task.FromResult(current) : Task.Run(() => Fill(steamId64));

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

    private PlayerRanks Fill(ulong steamId64)
    {
        Subject subject = Use(steamId64);
        lock (subject)
        {
            long version = _store.IndexVersion;
            if (subject.Ranks?.IndexVersion == version)
            {
                return subject.Ranks;
            }

            string id64 = steamId64.ToString(CultureInfo.InvariantCulture);
            IReadOnlyList<PlayerMatch> matches = _directory.Matches(steamId64);
            Dictionary<string, Entry> entries = new(matches.Count, StringComparer.Ordinal);
            List<PlayerRank> demos = new(matches.Count);
            foreach (PlayerMatch match in matches)
            {
                string id = ContentIdOf(match.Row);
                PackStamp? stamp = match.Row.Stamp(MatchMetaEvaluator.StampId);
                if (!entries.TryGetValue(id, out Entry? entry))
                {
                    if (stamp is not { State: DemoAnalysisState.Indexed })
                    {
                        // Not captured yet: nothing on the record to read.
                        entry = new Entry(stamp, null);
                    }
                    else if (!subject.Entries.TryGetValue(id, out entry) || entry.Stamp != stamp)
                    {
                        MatchRankRow? rank = _read(match.Row.Path)?.Players?.Find(p => string.Equals(p.SteamId64, id64, StringComparison.Ordinal));
                        entry = new Entry(stamp, rank);
                    }

                    entries[id] = entry;
                }

                demos.Add(new PlayerRank(match, entry.Rank));
            }

            subject.Entries = entries;
            return subject.Ranks = new PlayerRanks(steamId64, version, demos);
        }
    }

    // What a demo is across copies and renames: its content hash, or its one path before it is hashed.
    internal static string ContentIdOf(DemoCacheIndexEntry row) => string.IsNullOrEmpty(row.Sha256) ? row.Path : row.Sha256;

    private sealed record Entry(PackStamp? Stamp, MatchRankRow? Rank);

    private sealed class Subject(ulong steamId64)
    {
        public ulong SteamId64 { get; } = steamId64;

        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);

        public PlayerRanks? Ranks { get; set; }
    }
}

#region

using System.Globalization;
using System.Runtime.InteropServices;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>
///     Every account in the library and the demos it is in, projected from the cache's index: no sidecar is read
///     and nothing is persisted. The projection is rebuilt on the first read after <see cref="DemoCacheStore.IndexVersion" />
///     moves and is shared until it moves again.
///     <para>
///         An account is in a demo when the demo's side lists have it (<see cref="DemoCacheRecord.IsSidePlayer" />:
///         no bot, no coach, a real SteamID) or its <c>player_match</c> facet does (<see cref="DemoCacheIndexEntry.FacetPlayers" />);
///         one the facet alone has left early. A demo is one row however many paths hold its bytes, and an
///         orphaned row is not listed until a path holding its bytes comes back.
///     </para>
/// </summary>
public sealed class PlayerDirectory
{
    private readonly object _gate = new();
    private readonly Func<string, bool> _fileExists;
    private readonly DemoCacheStore _store;
    private Projection _projection = Projection.Empty;
    private long _version = -1;

    /// <param name="store">The demo cache.</param>
    /// <param name="fileExists">Whether a demo file is on disk; <see cref="File.Exists" /> when null.</param>
    public PlayerDirectory(DemoCacheStore store, Func<string, bool>? fileExists = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileExists = fileExists ?? File.Exists;
        store.Changed += _ => Changed?.Invoke();
    }

    /// <summary>Raised on the thread the store raises its own change on, the UI thread in the app.</summary>
    public event Action? Changed;

    /// <summary>Every account, the one in the most demos first. The same instance until the index moves.</summary>
    public IReadOnlyList<PlayerEntry> Players => Current().Players;

    /// <summary>How many demos hold any account, a demo with two copies once.</summary>
    public int DemoCount => Current().Demos;

    /// <summary>The account, or null when no demo holds it.</summary>
    /// <param name="steamId64">The account.</param>
    public PlayerEntry? Get(ulong steamId64) =>
        Current().Accounts.TryGetValue(steamId64, out Account? account) ? account.Entry : null;

    /// <summary>
    ///     The accounts <paramref name="text" /> names: the one whose SteamID64 it is, else every account with a
    ///     name containing it, ignoring case. The one in the most demos first.
    /// </summary>
    /// <param name="text">A SteamID64 or a part of a name.</param>
    public IReadOnlyList<PlayerEntry> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string query = text.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        Projection projection = Current();
        if (ulong.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
        {
            return projection.Accounts.TryGetValue(id, out Account? account) ? [account.Entry] : [];
        }

        return [.. projection.Players.Where(p => p.Aliases.Any(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>The account's demos that pass <paramref name="filter" />, newest match first.</summary>
    /// <param name="steamId64">The account.</param>
    /// <param name="filter">Which demos; all when null.</param>
    public IReadOnlyList<PlayerMatch> Matches(ulong steamId64, PlayerFilter? filter = null)
    {
        if (!Current().Accounts.TryGetValue(steamId64, out Account? account))
        {
            return [];
        }

        if (filter is null || filter == PlayerFilter.All)
        {
            return account.Matches;
        }

        IEnumerable<PlayerMatch> matches = account.Matches.Where(m => PlayerAggregate.Passes(m, filter));
        return filter.Last is { } last ? [.. matches.Take(Math.Max(last, 0))] : [.. matches];
    }

    /// <summary>
    ///     A path the demo can be opened from: its primary path, else another path known to hold its bytes, the
    ///     first that is on disk. Null when none is ("file missing").
    /// </summary>
    /// <param name="match">One of the account's demos.</param>
    public string? OpenPath(PlayerMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        DemoCacheIndexEntry row = match.Row;
        if (_fileExists(row.Path))
        {
            return row.Path;
        }

        // A row with no hash vouches for no other path.
        return string.IsNullOrEmpty(row.Sha256)
            ? null
            : row.Locations
                .Where(l => l.Confirmed && !string.Equals(l.Path, row.Path, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Path)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(_fileExists);
    }

    private Projection Current()
    {
        // Read before the rows: a write in between leaves the projection one version behind, rebuilt next read.
        long version = _store.IndexVersion;
        lock (_gate)
        {
            if (version == _version)
            {
                return _projection;
            }
        }

        Projection projection = Project(_store.Contents);
        lock (_gate)
        {
            if (version > _version)
            {
                _projection = projection;
                _version = version;
            }

            return _projection;
        }
    }

    /// <summary>
    ///     The projection of <paramref name="rows" />: one per demo, as <see cref="DemoCacheStore.Contents" /> lists
    ///     them. An account keeps its demos' rows; what it did in each is read back on its first <see cref="Matches" />.
    /// </summary>
    internal static Projection Project(IReadOnlyList<DemoCacheIndexEntry> rows)
    {
        Dictionary<ulong, Builder> builders = [];
        InDemo.Set inDemo = new();
        int demoCount = 0;
        foreach (DemoCacheIndexEntry row in rows.Order(NewestMatchFirst.Instance))
        {
            inDemo.Collect(row, null);
            if (inDemo.Accounts.Count > 0)
            {
                demoCount++;
            }

            long time = TimeOf(row);
            foreach ((ulong id, InDemo here) in inDemo.Accounts)
            {
                if (!builders.TryGetValue(id, out Builder? builder))
                {
                    builders[id] = builder = new Builder();
                }

                builder.Rows.Add(row);
                foreach (string name in here.Names)
                {
                    builder.Seen(name, time);
                }
            }
        }

        Dictionary<ulong, Account> accounts = new(builders.Count);
        foreach ((ulong id, Builder builder) in builders)
        {
            List<PlayerAlias> aliases = [.. builder.Aliases
                .Select(a => new PlayerAlias(a.Key, a.Value.First, a.Value.Last, a.Value.Demos))
                .OrderByDescending(a => a.LastSeenTicks)
                .ThenByDescending(a => a.Demos)
                .ThenBy(a => a.Name, StringComparer.Ordinal)];
            List<DemoCacheIndexEntry> demos = builder.Rows;
            accounts[id] = new Account(new PlayerEntry(id, aliases, demos.Count, TimeOf(demos[^1]), TimeOf(demos[0])), demos);
        }

        List<PlayerEntry> players = [.. accounts.Values.Select(a => a.Entry)
            .OrderByDescending(p => p.DemoCount)
            .ThenByDescending(p => p.LastSeenTicks)
            .ThenBy(p => p.SteamId64)];
        return new Projection(accounts, players, demoCount);
    }

    private static long TimeOf(DemoCacheIndexEntry row) => row.MatchTimeUtcTicks ?? row.ModifiedTicks;

    // An id the row cannot carry as a number is not a SteamID64, and 0 is no account.
    private static ulong? IdOf(string steamId64) =>
        ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0 ? id : null;

    internal sealed class Account(PlayerEntry entry, List<DemoCacheIndexEntry> rows)
    {
        private readonly object _gate = new();
        private List<PlayerMatch>? _matches;

        public PlayerEntry Entry { get; } = entry;

        /// <summary>The account in each of its demos, newest match first, read from the rows on first use.</summary>
        public List<PlayerMatch> Matches
        {
            get
            {
                lock (_gate)
                {
                    if (_matches is not null)
                    {
                        return _matches;
                    }

                    InDemo.Set inDemo = new();
                    List<PlayerMatch> matches = new(rows.Count);
                    foreach (DemoCacheIndexEntry row in rows)
                    {
                        inDemo.Collect(row, Entry.SteamId64);
                        InDemo here = inDemo.Accounts[Entry.SteamId64];
                        here.Slots.Sort();
                        matches.Add(new PlayerMatch(row, here.Side, [.. here.Names], [.. here.Slots], here.Side is null));
                    }

                    return _matches = matches;
                }
            }
        }
    }

    internal sealed record Projection(Dictionary<ulong, Account> Accounts, List<PlayerEntry> Players, int Demos)
    {
        public static readonly Projection Empty = new([], [], 0);
    }

    // One account in one demo: the side it ended on (null when only the facet has it), its names and slots.
    private sealed class InDemo
    {
        public int? Side;
        public readonly List<string> Names = [];
        public readonly List<int> Slots = [];

        private void Add(string name)
        {
            if (name.Length > 0 && !Names.Contains(name))
            {
                Names.Add(name);
            }
        }

        private void Add(List<int> slots)
        {
            foreach (int slot in slots)
            {
                if (!Slots.Contains(slot))
                {
                    Slots.Add(slot);
                }
            }
        }

        // One demo's accounts. Reused from demo to demo: a library is ~100k account-demo pairs.
        public sealed class Set
        {
            private readonly List<InDemo> _spare = [];

            public Dictionary<ulong, InDemo> Accounts { get; } = [];

            /// <summary>Fills <see cref="Accounts" /> from <paramref name="row" />: every account, or <paramref name="only" />.</summary>
            public void Collect(DemoCacheIndexEntry row, ulong? only)
            {
                foreach (InDemo used in Accounts.Values)
                {
                    used.Side = null;
                    used.Names.Clear();
                    used.Slots.Clear();
                    _spare.Add(used);
                }

                Accounts.Clear();
                AddSide(row.CtPlayers, 3, only);
                AddSide(row.TPlayers, 2, only);
                foreach (FacetPlayer player in row.FacetPlayers ?? [])
                {
                    if (IdOf(player.SteamId64) is { } id && (only is null || id == only))
                    {
                        InDemo here = At(id, null);
                        foreach (string name in player.Names)
                        {
                            here.Add(name);
                        }

                        here.Add(player.Slots);
                    }
                }
            }

            private void AddSide(List<IndexSidePlayer>? side, int team, ulong? only)
            {
                foreach (IndexSidePlayer player in side ?? [])
                {
                    if (IdOf(player.SteamId64) is { } id && (only is null || id == only))
                    {
                        InDemo here = At(id, team);
                        here.Add(player.Name);
                        here.Add(player.Slots);
                    }
                }
            }

            private InDemo At(ulong id, int? side)
            {
                if (!Accounts.TryGetValue(id, out InDemo? here))
                {
                    if (_spare.Count > 0)
                    {
                        here = _spare[^1];
                        _spare.RemoveAt(_spare.Count - 1);
                    }
                    else
                    {
                        here = new InDemo();
                    }

                    Accounts[id] = here;
                }

                here.Side ??= side;
                return here;
            }
        }
    }

    private sealed class Builder
    {
        public readonly Dictionary<string, (long First, long Last, int Demos)> Aliases = new(StringComparer.Ordinal);
        public readonly List<DemoCacheIndexEntry> Rows = [];

        public void Seen(string name, long time)
        {
            ref (long First, long Last, int Demos) seen = ref CollectionsMarshal.GetValueRefOrAddDefault(Aliases, name, out bool exists);
            seen = exists ? (Math.Min(seen.First, time), Math.Max(seen.Last, time), seen.Demos + 1) : (time, time, 1);
        }
    }
}

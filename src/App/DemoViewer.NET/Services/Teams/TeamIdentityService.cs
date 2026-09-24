#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     Teams as data (design §3): who played in which demo, which team is us, and who the opponent was.
///     <para>
///         <b>Two files, one rule.</b> What the user authored is truth and lives in
///         <c>&lt;config&gt;/teams.json</c> beside <c>settings.json</c>: names, the us mark, rosters with
///         their fixed five and extended-core snapshot, merges, tombstones, overrides, the me accounts.
///         What clustering derived is a cache and lives in <c>&lt;config&gt;/cache/team-index.json</c>:
///         every side key, its assignment, the members table. The index is rebuilt from the sidecars when
///         it is missing or behind; the user file is refused, never overwritten, when it cannot be read.
///     </para>
///     <para>
///         <b>Assignment is a replay.</b> Every change, one demo indexed or a merge, re-runs
///         <see cref="TeamClusterer" /> over the side keys already in the index, seeded from the user
///         file, in <c>orderTicks</c> order. The keys are lifted out of each record once, on
///         <see cref="DemoCacheStore.Changed" />, so nothing here opens a sidecar again until a rebuild;
///         a replay over a few hundred demos costs milliseconds and is the same pass a rebuild runs, which
///         is what keeps the incremental and the rebuilt index identical.
///     </para>
///     <para>
///         <b>Browser host.</b> No config root, so both files are session-only and the Teams panel says so.
///     </para>
/// </summary>
public sealed class TeamIdentityService : IDisposable
{
    /// <summary>The words the Teams panel shows on the browser host, the annotations panel's shape.</summary>
    public const string BrowserNote = "session only: this browser tab forgets teams when it reloads";

    /// <summary>The library size from which the me suggestion is offered.</summary>
    public const int MeSuggestionMinDemos = 20;

    private const string TeamsFileName = "teams.json";
    private const string IndexFileName = "team-index.json";

    private readonly DemoCacheStore _demoCache;
    private readonly object _gate = new();
    private readonly string? _indexPath;
    private readonly Dictionary<string, DemoSideInput> _inputs = new(StringComparer.Ordinal);
    private readonly Action<Action> _post;
    private readonly IRoundFactsSource? _roundFacts;
    private readonly Func<Action, Task> _run;
    private readonly string? _teamsPath;
    private bool _disposed;
    private TeamIndexFile _index = new();
    private TeamsFile _teams = new();
    private bool _teamsRefused;
    private Task _work = Task.CompletedTask;

    /// <param name="configRoot">The app config root, or null for a session-only store (the browser, tests).</param>
    /// <param name="demoCache">The unified demo cache the side keys come from.</param>
    /// <param name="roundFacts">The per-round rows <see cref="SideAtRound" /> joins; null answers null.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    /// <param name="run">Runs a replay off the caller's thread; defaults to the thread pool. Tests pass an inline runner.</param>
    public TeamIdentityService(
        string? configRoot,
        DemoCacheStore demoCache,
        IRoundFactsSource? roundFacts = null,
        Action<Action>? post = null,
        Func<Action, Task>? run = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        _demoCache = demoCache;
        _roundFacts = roundFacts;
        _post = post ?? (action => action());
        _run = run ?? (action => Task.Run(action));
        if (configRoot is not null)
        {
            _teamsPath = Path.Combine(configRoot, TeamsFileName);
            _indexPath = Path.Combine(configRoot, "cache", IndexFileName);
        }

        Load();
        _demoCache.Changed += OnCacheChanged;
    }

    /// <summary>True when nothing persists: the browser host, and tests without a root.</summary>
    public bool IsSessionOnly => _teamsPath is null;

    /// <summary>
    ///     Why <c>teams.json</c> could not be read, or null. While set the file is never written: a user's
    ///     names and merges are not something to overwrite with an empty file because one read failed.
    /// </summary>
    public string? TeamsFileProblem { get; private set; }

    /// <summary>The index was missing, corrupt or behind at load: <see cref="StartAsync" /> rebuilds from the sidecars.</summary>
    public bool NeedsRebuild { get; private set; }

    /// <summary>The account the share heuristic proposes as me, or null (design §3.5). Written only by confirmation.</summary>
    public MeSuggestion? MeSuggestion { get; private set; }

    /// <summary>Visible teams, in file order.</summary>
    public IReadOnlyList<Team> Teams
    {
        get
        {
            lock (_gate)
            {
                return [.. _teams.Teams.Where(t => !t.Hidden)];
            }
        }
    }

    /// <summary>Every team, hidden included.</summary>
    public IReadOnlyList<Team> AllTeams
    {
        get
        {
            lock (_gate)
            {
                return [.. _teams.Teams];
            }
        }
    }

    /// <summary>The team marked as us, or null.</summary>
    public Team? Us
    {
        get
        {
            lock (_gate)
            {
                return _teams.Teams.FirstOrDefault(t => t.IsUs);
            }
        }
    }

    /// <summary>The owner's accounts.</summary>
    public IReadOnlyList<string> MyAccounts
    {
        get
        {
            lock (_gate)
            {
                return [.. _teams.Me.SteamIds];
            }
        }
    }

    /// <summary>Demos in the index that carry no side key on either side: no <c>player_team</c> reached the parse.</summary>
    public int UnclusterableCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Demos.Values.Count(d => !(d.Side(2)?.IsClusterable ?? false) && !(d.Side(3)?.IsClusterable ?? false));
            }
        }
    }

    /// <summary>Demos the index knows.</summary>
    public int DemoCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Demos.Count;
            }
        }
    }

    /// <summary>The replay in flight, for callers that must observe its result.</summary>
    public Task Idle => _work;

    /// <summary>Raised (through the post delegate) after any change to either file.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _demoCache.Changed -= OnCacheChanged;
    }

    // ── Startup and the incremental hook ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Brings the index up to date at startup: a rebuild from the sidecars when it is missing or
    ///     behind, else the index-versus-cache diff a batch runs. Off the UI thread.
    /// </summary>
    public Task StartAsync() => NeedsRebuild ? RebuildAsync() : Schedule(SyncWithIndex);

    /// <summary>
    ///     The id-preserving rebuild (design §3.6): one <see cref="DemoCacheStore.LoadRecords" /> pass to
    ///     collect every side key, then the seeded replay. Off the UI thread; the documented cold cost.
    /// </summary>
    public Task RebuildAsync(CancellationToken ct = default) => Schedule(() =>
    {
        ct.ThrowIfCancellationRequested();
        List<DemoSideInput> inputs = [];
        foreach (DemoCacheRecord record in _demoCache.LoadRecords(e => e.ParseSchema > 0))
        {
            if (SideKeys.From(record) is { } input)
            {
                inputs.Add(input);
            }
        }

        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _inputs.Clear();
            foreach (DemoSideInput input in inputs)
            {
                _inputs[input.StableKey] = input;
            }

            NeedsRebuild = false;
            Recompute();
        }
    });

    private void OnCacheChanged(string? path)
    {
        if (path is null)
        {
            _ = Schedule(SyncWithIndex);
            return;
        }

        _ = Schedule(() => SyncOne(path));
    }

    // A batch or a startup: every parsed row the index does not know, or knows under another file
    // identity, is lifted; rows the cache forgot are dropped.
    private void SyncWithIndex()
    {
        Dictionary<string, DemoCacheIndexEntry> entries = new(StringComparer.Ordinal);
        foreach (DemoCacheIndexEntry entry in _demoCache.Index)
        {
            if (entry.ParseSchema > 0)
            {
                entries[DemoCacheStore.StableKey(entry.Path)] = entry;
            }
        }

        List<string> toLoad = [];
        List<string> toDrop = [];
        lock (_gate)
        {
            foreach ((string key, DemoCacheIndexEntry entry) in entries)
            {
                if (!_inputs.TryGetValue(key, out DemoSideInput? held)
                    || held.OrderTicks != entry.ModifiedTicks
                    || !string.Equals(held.Sha256, entry.Sha256, StringComparison.Ordinal))
                {
                    toLoad.Add(entry.Path);
                }
            }

            toDrop.AddRange(_inputs.Keys.Where(key => !entries.ContainsKey(key)));
        }

        List<DemoSideInput> loaded = [];
        foreach (string path in toLoad)
        {
            if (_demoCache.TryLoadRecord(path) is { } record && SideKeys.From(record) is { } input)
            {
                loaded.Add(input);
            }
        }

        lock (_gate)
        {
            bool changed = false;
            foreach (string key in toDrop)
            {
                changed |= _inputs.Remove(key);
            }

            foreach (DemoSideInput input in loaded)
            {
                if (!_inputs.TryGetValue(input.StableKey, out DemoSideInput? held) || !held.SameSides(input))
                {
                    _inputs[input.StableKey] = input;
                    changed = true;
                }
            }

            if (changed || _index.Demos.Count != _inputs.Count)
            {
                Recompute();
            }
        }
    }

    // One demo changed: a removal drops it, a parsed row is lifted (a capacity-1 hit right after the
    // upsert), and nothing runs when the sides are the ones already held.
    private void SyncOne(string path)
    {
        string key = DemoCacheStore.StableKey(path);
        DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
        if (entry is null)
        {
            lock (_gate)
            {
                if (_inputs.Remove(key))
                {
                    Recompute();
                }
            }

            return;
        }

        if (entry.ParseSchema == 0)
        {
            return;
        }

        DemoSideInput? input = _demoCache.TryLoadRecord(path) is { } record ? SideKeys.From(record) : null;
        if (input is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_inputs.TryGetValue(key, out DemoSideInput? held) && held.SameSides(input))
            {
                return;
            }

            _inputs[key] = input;
            Recompute();
        }
    }

    private Task Schedule(Action work)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            // One replay at a time, in the order the changes arrived; a failure never stops the chain.
            // An idle chain starts the work through the runner directly, so an inline runner (tests)
            // finishes before the caller continues.
            Task previous = _work;
            _work = previous.IsCompleted
                ? _run(work)
                : previous.ContinueWith(_ => _run(work), TaskScheduler.Default).Unwrap();
            return _work;
        }
    }

    // ── Queries ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The assignment of a demo, or null when it is unknown or unclusterable.</summary>
    /// <param name="demoPath">The demo.</param>
    public TeamAssignment? GetAssignment(string demoPath)
    {
        lock (_gate)
        {
            return _index.Demos.TryGetValue(DemoCacheStore.StableKey(demoPath), out TeamIndexDemo? row)
                ? ToAssignment(row)
                : null;
        }
    }

    /// <summary>Demos with either side assigned to the team, newest first.</summary>
    /// <param name="teamId">The team.</param>
    public IReadOnlyList<DemoRef> DemosOf(Guid teamId)
    {
        lock (_gate)
        {
            return [.. Ordered(_index.Demos.Where(d => OnEitherSide(d.Value, teamId))).Select(ToRef)];
        }
    }

    /// <summary>Demos where our side is resolved and the other side is the team, newest first.</summary>
    /// <param name="opponentTeamId">The opponent.</param>
    public IReadOnlyList<DemoRef> DemosAgainst(Guid opponentTeamId)
    {
        lock (_gate)
        {
            return
            [
                .. Ordered(_index.Demos.Where(d => d.Value.OurSide is not null && d.Value.OpponentTeamId == opponentTeamId))
                    .Select(ToRef)
            ];
        }
    }

    /// <summary>Every demo with our side resolved, newest first.</summary>
    public IReadOnlyList<DemoRef> OurDemos()
    {
        lock (_gate)
        {
            return [.. Ordered(_index.Demos.Where(d => d.Value.OurSide is not null)).Select(ToRef)];
        }
    }

    /// <summary>The team assigned to an end-of-demo side, or null.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="endSide">2 = T, 3 = CT.</param>
    public Team? TeamOnSide(string demoPath, int endSide)
    {
        lock (_gate)
        {
            return _index.Demos.TryGetValue(DemoCacheStore.StableKey(demoPath), out TeamIndexDemo? row)
                   && row.Side(endSide)?.TeamId is { } id
                ? _teams.Find(id)
                : null;
        }
    }

    /// <summary>
    ///     The side a team played in round N (overview correction 9): the Round Facts slots of that round
    ///     joined against the record's slot-to-SteamID map and the team's side key, at the same three-of-
    ///     five continuity. Null without rows, without the round, or when neither side holds the key.
    /// </summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="roundNumber"><c>ClipRound.Number</c>.</param>
    public int? SideAtRound(string demoPath, Guid teamId, int roundNumber)
    {
        List<string>? key;
        lock (_gate)
        {
            if (!_index.Demos.TryGetValue(DemoCacheStore.StableKey(demoPath), out TeamIndexDemo? row))
            {
                return null;
            }

            key = row.Side(2)?.TeamId == teamId ? row.Side(2)!.Key
                : row.Side(3)?.TeamId == teamId ? row.Side(3)!.Key
                : null;
        }

        if (key is null || key.Count == 0 || _roundFacts?.TryGet(demoPath) is not { } rows)
        {
            return null;
        }

        RoundFacts.RoundFacts? round = rows.Rounds.FirstOrDefault(r => r.Number == roundNumber);
        if (round is null || _demoCache.TryLoadRecord(demoPath) is not { } record)
        {
            return null;
        }

        Dictionary<int, string> bySlot = [];
        foreach (CachedPlayerInfo player in record.Players)
        {
            bySlot[player.Slot] = player.SteamId64;
        }

        HashSet<string> members = new(key, StringComparer.Ordinal);
        int k = Math.Min(TeamClusterer.Continuity, members.Count);
        int ct = round.Ct.Slots.Count(s => bySlot.TryGetValue(s, out string? id) && members.Contains(id));
        int t = round.T.Slots.Count(s => bySlot.TryGetValue(s, out string? id) && members.Contains(id));
        if (ct >= k && ct > t)
        {
            return 3;
        }

        return t >= k && t > ct ? 2 : null;
    }

    /// <summary>The members table of one roster: SteamID64 to appearances, last name, first and last seen.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="rosterId">The roster.</param>
    public IReadOnlyDictionary<string, TeamIndexMember> MembersOf(Guid teamId, string rosterId)
    {
        lock (_gate)
        {
            return _index.Members.TryGetValue(teamId.ToString(), out Dictionary<string, Dictionary<string, TeamIndexMember>>? perRoster)
                   && perRoster.TryGetValue(rosterId, out Dictionary<string, TeamIndexMember>? members)
                ? new Dictionary<string, TeamIndexMember>(members, StringComparer.Ordinal)
                : new Dictionary<string, TeamIndexMember>(StringComparer.Ordinal);
        }
    }

    /// <summary>The sides of a team, newest first: what the Teams panel lists per team.</summary>
    /// <param name="teamId">The team.</param>
    public IReadOnlyList<(DemoRef Demo, int Side, TeamAssignment Assignment)> SidesOf(Guid teamId)
    {
        lock (_gate)
        {
            List<(DemoRef, int, TeamAssignment)> sides = [];
            foreach ((string key, TeamIndexDemo row) in Ordered(_index.Demos))
            {
                foreach (int side in (int[]) [2, 3])
                {
                    if (row.Side(side)?.TeamId == teamId)
                    {
                        sides.Add((ToRef(new KeyValuePair<string, TeamIndexDemo>(key, row)), side, ToAssignment(row)));
                    }
                }
            }

            return sides;
        }
    }

    /// <summary>The newest order stamp of a team's demos, or 0.</summary>
    /// <param name="teamId">The team.</param>
    public long LastSeenTicks(Guid teamId)
    {
        lock (_gate)
        {
            return _index.Demos.Values.Where(d => OnEitherSide(d, teamId)).Select(d => d.OrderTicks).DefaultIfEmpty(0).Max();
        }
    }

    /// <summary>The unaffiliated one-side candidates: the count the panel footer reports.</summary>
    public int UnaffiliatedCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Unaffiliated.Count;
            }
        }
    }

    /// <summary>True when at least one demo has our side resolved and an assigned opponent.</summary>
    public bool HasRecurringOpponent
    {
        get
        {
            lock (_gate)
            {
                return _index.Demos.Values.Any(d => d.OurSide is not null && d.OpponentTeamId is not null);
            }
        }
    }

    // ── Writes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Renames a team. A user name is never overwritten by a later tag. Touches nothing derived.</summary>
    public void Rename(Guid teamId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_teams.Find(teamId) is not { } team)
            {
                return;
            }

            team.Name = name.Trim();
            team.NameSource = TeamNameSource.User;
            SaveTeams();
        }

        RaiseChanged();
    }

    /// <summary>Marks a team as us, clearing the previous one; null clears the mark. Re-derives our side everywhere.</summary>
    public void SetUs(Guid? teamId)
    {
        lock (_gate)
        {
            foreach (Team team in _teams.Teams)
            {
                team.IsUs = teamId is not null && team.Id == teamId;
            }

            ResolveOurSides();
            SaveTeams();
            SaveIndex();
        }

        RaiseChanged();
    }

    /// <summary>Replaces the me accounts. Re-derives our side everywhere.</summary>
    public void SetMyAccounts(IReadOnlyList<string> steamIds)
    {
        ArgumentNullException.ThrowIfNull(steamIds);
        lock (_gate)
        {
            _teams.Me.SteamIds =
            [
                .. steamIds.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)
            ];
            MeSuggestion = null;
            ResolveOurSides();
            SaveTeams();
            SaveIndex();
        }

        RaiseChanged();
    }

    /// <summary>Writes the suggested account as me. The one path by which the suggestion reaches the file.</summary>
    public void ConfirmMeSuggestion()
    {
        if (MeSuggestion is { } suggestion)
        {
            SetMyAccounts([suggestion.SteamId64]);
        }
    }

    /// <summary>Folds <paramref name="from" /> into <paramref name="into" />: rosters concatenate, the id is tombstoned.</summary>
    public Guid Merge(Guid into, Guid from)
    {
        lock (_gate)
        {
            Team? target = _teams.Find(into);
            Team? source = _teams.Find(from);
            if (target is null || source is null || into == from)
            {
                return into;
            }

            foreach (Roster roster in source.Rosters)
            {
                roster.Id = UniqueRosterId(target, roster.Id);
                target.Rosters.Add(roster);
            }

            target.MergedFrom.Add(source.Id);
            target.MergedFrom.AddRange(source.MergedFrom);
            target.IsUs |= source.IsUs;
            _teams.Teams.Remove(source);
            _teams.Tombstones.Add(source.Id);
            foreach (TeamOverride o in _teams.Overrides.Where(o => o.TeamId == source.Id))
            {
                o.TeamId = target.Id;
            }

            Recompute();
        }

        return into;
    }

    /// <summary>
    ///     Moves sides out of a team into a new one: a new roster whose five is established from them
    ///     when the same five is among them, the sides pinned by override so a rebuild keeps both.
    /// </summary>
    public Guid Split(Guid teamId, IReadOnlyList<DemoSideRef> sides, string? name)
    {
        ArgumentNullException.ThrowIfNull(sides);
        Guid id = Guid.NewGuid();
        lock (_gate)
        {
            if (_teams.Find(teamId) is null || sides.Count == 0)
            {
                return teamId;
            }

            List<(TeamIndexDemo Row, TeamIndexSide Side)> picked = [];
            foreach (DemoSideRef side in sides)
            {
                if (_index.Demos.TryGetValue(DemoCacheStore.StableKey(side.DemoPath), out TeamIndexDemo? row)
                    && row.Side(side.Side) is { IsClusterable: true } s)
                {
                    picked.Add((row, s));
                }
            }

            if (picked.Count == 0)
            {
                return teamId;
            }

            Team team = new()
            {
                Id = id,
                Name = name?.Trim() ?? "",
                NameSource = string.IsNullOrWhiteSpace(name) ? TeamNameSource.Auto : TeamNameSource.User
            };
            Roster roster = new()
            {
                Id = "r1",
                Since = TeamClusterer.DateOf(picked.Min(p => p.Row.OrderTicks))
            };
            List<string>? five = picked.Select(p => p.Side.Key).Where(k => k.Count == 5)
                .GroupBy(k => string.Join(",", k), StringComparer.Ordinal)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count())
                .Select(g => g.First())
                .FirstOrDefault();
            roster.CoreLineup = five is null ? null : [.. five];
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            foreach ((TeamIndexDemo _, TeamIndexSide side) in picked)
            {
                foreach (string member in side.Key)
                {
                    counts[member] = counts.GetValueOrDefault(member) + 1;
                }
            }

            roster.ExtendedCore =
            [
                .. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
                    .Take(TeamClusterer.ExtendedCoreCap).Select(c => c.Key)
            ];
            team.Rosters.Add(roster);
            _teams.Teams.Add(team);
            foreach ((TeamIndexDemo row, TeamIndexSide _) in picked)
            {
                int side = sides.First(s => string.Equals(DemoCacheStore.StableKey(s.DemoPath),
                    DemoCacheStore.StableKey(row.Path), StringComparison.Ordinal)).Side;
                UpsertOverride(row, side, id);
            }

            Recompute();
        }

        return id;
    }

    /// <summary>Starts a new roster in a team at a date: the team's sides from then on establish their own five.</summary>
    public void StartRoster(Guid teamId, DateOnly since, string? label)
    {
        lock (_gate)
        {
            if (_teams.Find(teamId) is not { } team)
            {
                return;
            }

            team.Rosters.Add(new Roster
            {
                Id = UniqueRosterId(team, $"r{team.Rosters.Count + 1}"),
                Since = since,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                UserStarted = true
            });
            Recompute();
        }
    }

    /// <summary>Pins a side to a team, or with null says the side is not a team. Keyed by hash when the demo has one.</summary>
    public void Override(string demoPath, int endSide, Guid? teamId)
    {
        lock (_gate)
        {
            if (!_index.Demos.TryGetValue(DemoCacheStore.StableKey(demoPath), out TeamIndexDemo? row))
            {
                return;
            }

            UpsertOverride(row, endSide, teamId);
            Recompute();
        }
    }

    /// <summary>Removes a side's override, if any, so clustering decides again.</summary>
    public void ClearOverride(string demoPath, int endSide)
    {
        lock (_gate)
        {
            string key = DemoCacheStore.StableKey(demoPath);
            string? sha = _index.Demos.GetValueOrDefault(key)?.Sha256;
            int removed = _teams.Overrides.RemoveAll(o => o.Side == endSide && Matches(o, key, sha));
            if (removed == 0)
            {
                return;
            }

            Recompute();
        }
    }

    // ── The provenance override store ────────────────────────────────────────────────────────────
    // Demo Provenance Labels owns the vocabulary and the heuristic; this service owns the file the
    // overrides live in (overview correction 24), so the writes come through here and share one
    // atomic write, one refusal rule and one Changed with the rest of teams.json.

    /// <summary>A snapshot of every provenance override in the file.</summary>
    public IReadOnlyList<ProvenanceOverride> ProvenanceOverrides
    {
        get
        {
            lock (_gate)
            {
                return [.. _teams.Provenance.Overrides];
            }
        }
    }

    /// <summary>The provenance override on a demo, or null when the heuristic decides.</summary>
    /// <param name="demoPath">The demo.</param>
    public ProvenanceOverride? ProvenanceOverrideFor(string demoPath)
    {
        string key = DemoCacheStore.StableKey(demoPath);
        string? sha = _demoCache.TryGetIndex(demoPath)?.Sha256;
        lock (_gate)
        {
            sha ??= _index.Demos.GetValueOrDefault(key)?.Sha256;
            return _teams.Provenance.Overrides.FirstOrDefault(o => Matches(o, key, sha));
        }
    }

    /// <summary>
    ///     Pins a demo's provenance label, or with null removes the pin so the heuristic decides again.
    ///     Keyed by hash when the demo has one. Touches nothing derived.
    /// </summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="label">One of <see cref="Provenance.DemoProvenanceLabel.All" />, or null.</param>
    public void SetProvenanceOverride(string demoPath, string? label)
    {
        if (label is not null && !Provenance.DemoProvenanceLabel.IsKnown(label))
        {
            throw new ArgumentException($"'{label}' is not a provenance label", nameof(label));
        }

        string key = DemoCacheStore.StableKey(demoPath);
        string? sha = _demoCache.TryGetIndex(demoPath)?.Sha256;
        lock (_gate)
        {
            sha ??= _index.Demos.GetValueOrDefault(key)?.Sha256;
            int removed = _teams.Provenance.Overrides.RemoveAll(o => Matches(o, key, sha));
            if (label is null && removed == 0)
            {
                return;
            }

            if (label is not null)
            {
                _teams.Provenance.Overrides.Add(new ProvenanceOverride
                {
                    DemoSha256 = sha,
                    DemoStableKey = sha is null ? key : null,
                    Label = label
                });
            }

            SaveTeams();
        }

        RaiseChanged();
    }

    /// <summary>Hides or shows a team in lists. Touches nothing derived.</summary>
    public void SetHidden(Guid teamId, bool hidden)
    {
        lock (_gate)
        {
            if (_teams.Find(teamId) is not { } team)
            {
                return;
            }

            team.Hidden = hidden;
            SaveTeams();
        }

        RaiseChanged();
    }

    // ── The replay ───────────────────────────────────────────────────────────────────────────────

    // Under _gate. Seeds a copy of the user file, replays every held side in order, and only then
    // swaps both files in, so a throw midway leaves the previous state whole.
    private void Recompute()
    {
        TeamsFile teams = Clone(_teams);
        TeamClusterer clusterer = new(teams);
        foreach (DemoSideInput input in _inputs.Values.OrderBy(i => i.OrderTicks).ThenBy(i => i.Path, StringComparer.Ordinal))
        {
            clusterer.Assign(input, OverridesFor(teams, input));
        }

        TeamIndexFile index = clusterer.Finish(DateTime.UtcNow.Ticks);
        _teams = teams;
        _index = index;
        UpgradeOverrides();
        ResolveOurSides();
        SuggestMe();
        SaveTeams();
        SaveIndex();
        RaiseChanged();
    }

    private static Dictionary<int, Guid?>? OverridesFor(TeamsFile teams, DemoSideInput input)
    {
        Dictionary<int, Guid?>? map = null;
        foreach (TeamOverride o in teams.Overrides)
        {
            if (Matches(o, input.StableKey, input.Sha256))
            {
                map ??= [];
                map[o.Side] = o.TeamId;
            }
        }

        return map;
    }

    /// <summary>
    ///     Whether a <c>teams.json</c> entry names a demo: by hash when the entry carries one, else by the
    ///     cache's stable key. Public so Demo Provenance Labels applies the same rule to its overrides.
    /// </summary>
    /// <param name="o">The entry.</param>
    /// <param name="stableKey"><see cref="DemoCacheStore.StableKey" /> of the demo.</param>
    /// <param name="sha256">The demo's content hash, or null before it is hashed.</param>
    public static bool Matches(IDemoKeyedOverride o, string stableKey, string? sha256) =>
        (o.DemoSha256 is not null && string.Equals(o.DemoSha256, sha256, StringComparison.Ordinal))
        || (o.DemoSha256 is null && string.Equals(o.DemoStableKey, stableKey, StringComparison.Ordinal));

    // An override written before the demo was hashed moves onto the hash the moment it appears, so a
    // moved file keeps it. Both override lists, one rule.
    private void UpgradeOverrides()
    {
        foreach (IDemoKeyedOverride o in _teams.Overrides.Cast<IDemoKeyedOverride>().Concat(_teams.Provenance.Overrides))
        {
            if (o.DemoSha256 is null && o.DemoStableKey is not null
                && _index.Demos.TryGetValue(o.DemoStableKey, out TeamIndexDemo? row) && row.Sha256 is not null)
            {
                o.DemoSha256 = row.Sha256;
                o.DemoStableKey = null;
            }
        }
    }

    private void UpsertOverride(TeamIndexDemo row, int side, Guid? teamId)
    {
        string key = DemoCacheStore.StableKey(row.Path);
        _teams.Overrides.RemoveAll(o => o.Side == side && Matches(o, key, row.Sha256));
        _teams.Overrides.Add(new TeamOverride
        {
            DemoSha256 = row.Sha256,
            DemoStableKey = row.Sha256 is null ? key : null,
            Side = side,
            TeamId = teamId
        });
    }

    // The order of §3.5: an override naming us, a roster of the us team, a me account on a side. A
    // pure pass over the index; the opponent is whatever team sits on the other side.
    private void ResolveOurSides()
    {
        Team? us = _teams.Teams.FirstOrDefault(t => t.IsUs);
        HashSet<string> me = new(_teams.Me.SteamIds, StringComparer.Ordinal);
        foreach (TeamIndexDemo row in _index.Demos.Values)
        {
            int? ourSide = null;
            OurSideSource source = OurSideSource.None;
            if (us is not null)
            {
                foreach (int side in (int[]) [2, 3])
                {
                    if (row.Side(side) is { Override: true } s && s.TeamId == us.Id)
                    {
                        (ourSide, source) = (side, OurSideSource.Override);
                        break;
                    }
                }

                if (ourSide is null)
                {
                    foreach (int side in (int[]) [2, 3])
                    {
                        if (row.Side(side)?.TeamId == us.Id)
                        {
                            (ourSide, source) = (side, OurSideSource.Team);
                            break;
                        }
                    }
                }
            }

            if (ourSide is null && me.Count > 0)
            {
                bool onT = row.Side(2)?.Key.Any(me.Contains) ?? false;
                bool onCt = row.Side(3)?.Key.Any(me.Contains) ?? false;
                if (onT != onCt)
                {
                    (ourSide, source) = (onT ? 2 : 3, OurSideSource.Me);
                }
            }

            row.OurSide = ourSide;
            row.OurSideSource = source;
            row.OpponentTeamId = ourSide is { } o ? row.Side(o == 2 ? 3 : 2)?.TeamId : null;
        }
    }

    // The account with the highest demo share, offered when me is empty, the library has twenty or
    // more clusterable demos and the share is above one half. Nothing is written here.
    private void SuggestMe()
    {
        MeSuggestion = null;
        if (_teams.Me.SteamIds.Count > 0)
        {
            return;
        }

        Dictionary<string, (int Count, string Name)> counts = new(StringComparer.Ordinal);
        int clusterable = 0;
        foreach (TeamIndexDemo row in _index.Demos.Values)
        {
            bool any = false;
            foreach (int side in (int[]) [2, 3])
            {
                if (row.Side(side) is not { IsClusterable: true } s)
                {
                    continue;
                }

                any = true;
                for (int i = 0; i < s.Key.Count; i++)
                {
                    (int seen, string _) = counts.GetValueOrDefault(s.Key[i]);
                    counts[s.Key[i]] = (seen + 1, i < s.Names.Count ? s.Names[i] : s.Key[i]);
                }
            }

            if (any)
            {
                clusterable++;
            }
        }

        if (clusterable < MeSuggestionMinDemos)
        {
            return;
        }

        (string id, (int count, string name)) = counts.OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key, StringComparer.Ordinal).First();
        double share = (double) count / clusterable;
        if (share > 0.5)
        {
            MeSuggestion = new MeSuggestion(id, name, count, share);
        }
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────

    private void Load()
    {
        if (_teamsPath is null)
        {
            NeedsRebuild = true;
            return;
        }

        try
        {
            if (File.Exists(_teamsPath))
            {
                TeamsFile? teams = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(_teamsPath), TeamsFile.JsonOptions);
                if (teams is null || teams.SchemaVersion > TeamsFile.CurrentSchema)
                {
                    Refuse($"{TeamsFileName} is at schema {teams?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                }
                else
                {
                    _teams = teams;
                }
            }
        }
        catch (Exception ex)
        {
            Refuse($"{TeamsFileName} could not be read: {ex.Message}");
        }

        try
        {
            if (_indexPath is not null && File.Exists(_indexPath))
            {
                TeamIndexFile? index = JsonSerializer.Deserialize<TeamIndexFile>(File.ReadAllText(_indexPath), TeamsFile.JsonOptions);
                if (index is { SchemaVersion: TeamIndexFile.CurrentSchema })
                {
                    _index = index;
                    foreach ((string key, TeamIndexDemo row) in index.Demos)
                    {
                        _inputs[key] = SideKeys.From(key, row);
                    }

                    SuggestMe();
                    return;
                }
            }
        }
        catch (Exception)
        {
            // A corrupt index is a cache: rebuild it, never crash on it.
        }

        NeedsRebuild = true;
    }

    private void Refuse(string problem)
    {
        _teamsRefused = true;
        TeamsFileProblem = problem;
        _teams = new TeamsFile();
    }

    private void SaveTeams()
    {
        if (_teamsPath is null || _teamsRefused)
        {
            return;
        }

        try
        {
            DemoCacheStore.WriteAtomic(_teamsPath, JsonSerializer.Serialize(_teams, TeamsFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory truth stands for the session and the next write retries.
        }
    }

    private void SaveIndex()
    {
        if (_indexPath is null)
        {
            return;
        }

        try
        {
            DemoCacheStore.WriteAtomic(_indexPath, JsonSerializer.Serialize(_index, TeamsFile.JsonOptions));
        }
        catch (Exception)
        {
            // Rebuildable cache: persistence noise is never surfaced.
        }
    }

    private static TeamsFile Clone(TeamsFile teams) =>
        JsonSerializer.Deserialize<TeamsFile>(JsonSerializer.Serialize(teams, TeamsFile.JsonOptions), TeamsFile.JsonOptions)
        ?? new TeamsFile();

    private static string UniqueRosterId(Team team, string wanted)
    {
        string id = wanted;
        int n = team.Rosters.Count + 1;
        while (team.Rosters.Any(r => string.Equals(r.Id, id, StringComparison.Ordinal)))
        {
            id = $"r{n++}";
        }

        return id;
    }

    private void RaiseChanged() => _post(() => Changed?.Invoke());

    private static bool OnEitherSide(TeamIndexDemo row, Guid teamId) =>
        row.Side(2)?.TeamId == teamId || row.Side(3)?.TeamId == teamId;

    private static IEnumerable<KeyValuePair<string, TeamIndexDemo>> Ordered(IEnumerable<KeyValuePair<string, TeamIndexDemo>> rows) =>
        rows.OrderByDescending(d => d.Value.OrderTicks).ThenBy(d => d.Value.Path, StringComparer.Ordinal);

    private static DemoRef ToRef(KeyValuePair<string, TeamIndexDemo> row) =>
        new(row.Value.Path, row.Key, row.Value.Sha256);

    private static TeamAssignment ToAssignment(TeamIndexDemo row) =>
        new(row.Path, row.Sha256, ToSide(row.Side(2)), ToSide(row.Side(3)), row.OurSide, row.OurSideSource, row.OpponentTeamId);

    private static SideAssignment ToSide(TeamIndexSide? side) =>
        side is null
            ? new SideAssignment([], null, null, 0, 0, false)
            : new SideAssignment([.. side.Key], side.TeamId, side.RosterId, side.Overlap, side.Tier, side.StandIn);
}

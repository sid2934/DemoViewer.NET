#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Strats.Mining;

/// <summary>A pattern as the inbox lists it: whether the user dismissed it, and the strat it became.</summary>
public sealed record DetectedPattern(MinedPattern Pattern, bool Dismissed, Guid? StratId)
{
    /// <summary>Dismissed wins over promoted: a promoted pattern can still be dismissed from the inbox.</summary>
    public GeneratedState State => Dismissed ? GeneratedState.Dismissed
        : StratId is not null ? GeneratedState.Accepted
        : GeneratedState.New;
}

/// <summary>A previewed strat's save: the strat, or null; PatternChanged when the pattern moved since the preview.</summary>
public sealed record PromoteResult(StratDocument? Document, bool PatternChanged);

/// <summary>
///     Strat Mining's inbox (owner, 2026-09-27): mines the library from cached files, keeps what it found, and turns
///     a pattern into a strat only when the user promotes it.
///     <para>
///         Two files. <c>&lt;cache&gt;/strat-mining/detected.json</c> is derived and rebuilt by every mine;
///         <c>&lt;config&gt;/strat-mining.json</c> is user truth (dismissed and promoted pattern keys), so a re-mine
///         never brings back what the user put away. Null roots keep both in memory.
///     </para>
///     <para>
///         After the first mine, a change to the demo cache or the Grenade Index re-mines once things go quiet
///         for <see cref="QuietDelay" />: the quiet re-mine after new demos finish indexing. It waits while the
///         processing queue has demo parses and is re-armed when the queue drains. Every mine is an item of the
///         processing queue, stepping aside for a demo open between batches, and reads files only for demos whose
///         inputs changed (<see cref="SignatureCache" />).
///     </para>
/// </summary>
public sealed class StratMiningService : IDisposable
{
    /// <summary>The detected file's shape version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The schema of the user file, <c>strat-mining.json</c>.</summary>
    public const int StateSchemaVersion = 1;

    /// <summary>The tag code a mined T setup's runs carry; an execute's is the palette's site code.</summary>
    public const string DefaultCode = "Default";

    /// <summary>The tag code a mined CT setup's runs carry.</summary>
    public const string SetupCode = "Setup";

    /// <summary>The provenance detector name on a promoted pattern's runs.</summary>
    public const string Detector = "strat-mining";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly DemoCacheStore _demoCache;
    private readonly string? _detectedPath;
    private readonly Func<string?, string> _fingerprintFor;
    private readonly object _gate = new();
    private readonly GrenadeIndex? _grenadeIndex;
    private readonly RoundIndexStore _positions;
    private readonly Action<Action> _post;
    private readonly IDemoProcessingQueue? _queue;
    private readonly Func<Action, Task> _run;
    private readonly RoundSignatureBuilder _signatures;
    private readonly string? _statePath;
    private readonly StratStore _strats;
    private readonly TagStore? _tags;
    private readonly TeamIdentityService? _teams;
    private bool _deferred;
    private Timer? _quiet;
    private bool _rerun;
    private bool _running;
    private MiningState _state = new();
    private bool _stateRefused;
    private bool _stateUnread;

    /// <param name="demoCache">Records, Round Facts and the cache events a quiet re-mine follows.</param>
    /// <param name="positions">The round positions files.</param>
    /// <param name="fingerprintFor">The positions fingerprint per map.</param>
    /// <param name="grenadeIndex">The Grenade Index; null mines positions only.</param>
    /// <param name="teams">Team Identity; null leaves every pattern unowned.</param>
    /// <param name="strats">Where a promoted pattern is saved.</param>
    /// <param name="tags">Where a promoted pattern's runs are written; null writes none.</param>
    /// <param name="cacheRoot">The demo cache directory; null keeps the detected patterns in memory.</param>
    /// <param name="configRoot">The config root; null keeps dismissals and promotions in memory.</param>
    /// <param name="post">UI-thread marshal for <see cref="Changed" />.</param>
    /// <param name="run">Runs a mine off the UI thread; defaults to <see cref="Task.Run(Action)" />.</param>
    /// <param name="queue">
    ///     The processing queue a mine runs in, and whose pending demo parses hold back the quiet re-mine; null mines
    ///     on <paramref name="run" /> directly.
    /// </param>
    public StratMiningService(DemoCacheStore demoCache, RoundIndexStore positions, Func<string?, string> fingerprintFor,
        GrenadeIndex? grenadeIndex, TeamIdentityService? teams, StratStore strats, TagStore? tags, string? cacheRoot,
        string? configRoot, Action<Action>? post = null, Func<Action, Task>? run = null,
        IDemoProcessingQueue? queue = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fingerprintFor);
        ArgumentNullException.ThrowIfNull(strats);
        _demoCache = demoCache;
        _positions = positions;
        _fingerprintFor = fingerprintFor;
        _grenadeIndex = grenadeIndex;
        _teams = teams;
        _strats = strats;
        _tags = tags;
        _detectedPath = cacheRoot is null ? null : Path.Combine(cacheRoot, "strat-mining", "detected.json");
        _statePath = configRoot is null ? null : Path.Combine(configRoot, "strat-mining.json");
        _post = post ?? (action => action());
        _run = run ?? Task.Run;
        _queue = queue;
        _signatures = new RoundSignatureBuilder(demoCache, positions, fingerprintFor,
            grenadeIndex is null ? null : RoundSignatureBuilder.FromIndex(grenadeIndex), teams,
            new SignatureCache(cacheRoot is null ? null : Path.Combine(cacheRoot, "strat-mining", "signatures.json.gz")));
        Load();
        _strats.Deleted += OnStratDeleted;
        _demoCache.Changed += OnSourceChanged;
        if (_grenadeIndex is not null)
        {
            _grenadeIndex.Changed += OnSourceChanged;
        }

        if (_queue is not null)
        {
            _queue.Changed += OnQueueChanged;
        }
    }

    /// <summary>The builder, for its cache counts.</summary>
    internal RoundSignatureBuilder Signatures => _signatures;

    /// <summary>How long the cache has to stay quiet before a re-mine; <see cref="Timeout.InfiniteTimeSpan" /> turns it off.</summary>
    public TimeSpan QuietDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Every pattern the last mine found, the largest first; a dismissed one is flagged, not removed.</summary>
    public IReadOnlyList<DetectedPattern> Patterns { get; private set; } = [];

    /// <summary>
    ///     Why <c>strat-mining.json</c> is not in use, or null. A file that does not parse or is at a newer schema is
    ///     refused for the session; one that could not be opened is tried again on the next change or mine. Either
    ///     way it is never overwritten: dismissals stay in memory and promotion is refused.
    /// </summary>
    public string? StateProblem { get; private set; }

    /// <summary>When the last mine finished; null before the first.</summary>
    public DateTime? MinedUtc { get; private set; }

    /// <summary>True while a mine is running.</summary>
    public bool IsMining
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Demos the last mine read and rounds it compared.</summary>
    public (int Demos, int Rounds) LastRead { get; private set; }

    public void Dispose()
    {
        _strats.Deleted -= OnStratDeleted;
        _demoCache.Changed -= OnSourceChanged;
        if (_grenadeIndex is not null)
        {
            _grenadeIndex.Changed -= OnSourceChanged;
        }

        if (_queue is not null)
        {
            _queue.Changed -= OnQueueChanged;
        }

        _quiet?.Dispose();
    }

    /// <summary>Raised on the UI thread when the patterns or their flags change.</summary>
    public event Action? Changed;

    /// <summary>
    ///     Mines the library for the user: a user-requested queue item, so it starts after the job in flight rather
    ///     than the whole queue. A call while one is still queued joins it; one while a mine runs queues another.
    /// </summary>
    public Task MineAsync() => MineAsync(user: true);

    /// <summary>Demos read per gate slot.</summary>
    internal int BatchSize { get; set; } = 16;

    internal Task MineAsync(bool user)
    {
        lock (_gate)
        {
            if (user)
            {
                StateUsable();
            }

            if (_queue is null && _running)
            {
                _rerun = true;
                return Task.CompletedTask;
            }

            _running = true;
        }

        if (_queue is null)
        {
            return _run(MineLoop);
        }

        IDemoQueueHandle handle = _queue.SubmitJob(new QueueJobRequest(QueueJobKind.StratMining, "Strat mining: library",
            "strat-mining", user ? DemoJobPriority.UserRequested : DemoJobPriority.Background, MineQueuedAsync,
            Key: "strat-mining"));
        return handle.Completion.ContinueWith(_ =>
        {
            lock (_gate)
            {
                _running = _queue.ActiveCount(QueueJobKind.StratMining) > 0;
            }

            _post(() => Changed?.Invoke());
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // One pass as a queue item. Steps aside between batches, so a demo open waits for one batch at most.
    private async Task MineQueuedAsync(IQueueJobContext job)
    {
        IReadOnlyList<RoundSignature> signatures = [];
        IReadOnlyList<MinedPattern> patterns = [];
        try
        {
            RoundSignatureBuilder.BuildSession? session = null;
            await _run(() => session = _signatures.Begin()).ConfigureAwait(false);
            int count = session!.Count;
            for (int from = 0; from < count; from += BatchSize)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                job.Report(from, count, $"{from} of {count} demos");
                if (from > 0)
                {
                    await job.StepAsideAsync().ConfigureAwait(false);
                }

                int start = from;
                await _run(() => _signatures.Step(session, start, BatchSize)).ConfigureAwait(false);
            }

            job.Report(count, count, "grouping rounds");
            await job.StepAsideAsync().ConfigureAwait(false);
            await _run(() =>
            {
                signatures = _signatures.Finish(session);
                patterns = StratMiner.Mine(signatures);
                Save(patterns);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file mid-write: the next mine reads it.
        }

        Published(signatures, patterns);
    }

    private void MineLoop()
    {
        while (true)
        {
            IReadOnlyList<RoundSignature> signatures = [];
            IReadOnlyList<MinedPattern> patterns = [];
            try
            {
                signatures = _signatures.Build();
                patterns = StratMiner.Mine(signatures);
                Save(patterns);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file mid-write: the next mine reads it.
            }

            if (!Completed(signatures, patterns))
            {
                return;
            }
        }
    }

    private void Published(IReadOnlyList<RoundSignature> signatures, IReadOnlyList<MinedPattern> patterns)
    {
        int demos = signatures.Select(s => s.DemoPath).Distinct(StringComparer.Ordinal).Count();
        _post(() =>
        {
            MinedUtc = DateTime.UtcNow;
            LastRead = (demos, signatures.Count);
            CarryState([.. Patterns.Select(p => p.Pattern)], patterns);
            Publish(patterns);
        });
    }

    // Publishes a pass; true when another pass was asked for while it ran.
    private bool Completed(IReadOnlyList<RoundSignature> signatures, IReadOnlyList<MinedPattern> patterns)
    {
        Published(signatures, patterns);
        lock (_gate)
        {
            if (!_rerun)
            {
                _running = false;
                return false;
            }

            _rerun = false;
            return true;
        }
    }

    /// <summary>Hides a pattern from the inbox for good, across re-mines.</summary>
    /// <param name="key"><see cref="MinedPattern.Key" />.</param>
    public void Dismiss(string key) => Mutate(state => state.Dismissed.Add(key));

    /// <summary>Brings a dismissed pattern back.</summary>
    /// <param name="key"><see cref="MinedPattern.Key" />.</param>
    public void Restore(string key) => Mutate(state => state.Dismissed.Remove(key));

    /// <summary>
    ///     Turns a pattern into a strat in <paramref name="owner" />'s book: its medoid round's positions, grenades
    ///     and plant as steps, then its runs written as accepted suggestions labelled <c>strat: &lt;id&gt;</c> in each
    ///     member demo that has a hash, so the record panel counts them. Null when the medoid's files are gone or
    ///     the save is refused.
    /// </summary>
    /// <param name="key"><see cref="MinedPattern.Key" />.</param>
    /// <param name="owner">The book.</param>
    /// <param name="nowUtc">The creation time; now when null.</param>
    public StratDocument? Promote(string key, StratOwner owner, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Patterns.FirstOrDefault(p => p.Pattern.Key == key)?.Pattern is not { } pattern
            || Build(pattern, owner, nowUtc ?? DateTime.UtcNow) is not { } doc)
        {
            return null;
        }

        return Commit(pattern, doc);
    }

    /// <summary>
    ///     Saves a previewed strat as it was shown, with a fresh id and stamps, in <paramref name="owner" />'s book.
    ///     Refused with <see cref="PromoteResult.PatternChanged" /> when the pattern under the preview's key no
    ///     longer has the medoid and rounds the preview was built from.
    /// </summary>
    /// <param name="previewed">The pattern as it was when the preview was built.</param>
    /// <param name="built">The previewed document.</param>
    /// <param name="owner">The book.</param>
    /// <param name="nowUtc">The creation time; now when null.</param>
    public PromoteResult Promote(MinedPattern previewed, StratDocument built, StratOwner owner, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(previewed);
        ArgumentNullException.ThrowIfNull(built);
        ArgumentNullException.ThrowIfNull(owner);
        if (Patterns.FirstOrDefault(p => p.Pattern.Key == previewed.Key)?.Pattern is not { } current
            || !SameRounds(previewed, current))
        {
            return new PromoteResult(null, true);
        }

        DateTime now = nowUtc ?? DateTime.UtcNow;
        StratDocument doc = built.Clone();
        doc.Id = Guid.NewGuid();
        doc.CreatedUtc = now;
        doc.ModifiedUtc = now;
        doc.Owner = owner.Clone();
        return new PromoteResult(Commit(current, doc), false);
    }

    private static bool SameRounds(MinedPattern a, MinedPattern b) =>
        a.Medoid.DemoPath == b.Medoid.DemoPath && a.Medoid.Round == b.Medoid.Round
                                               && a.Members.Select(RoundOf).Order(StringComparer.Ordinal)
                                                   .SequenceEqual(b.Members.Select(RoundOf).Order(StringComparer.Ordinal));

    private static string RoundOf(MinedMember m) => $"{m.DemoPath}#{m.Round}";

    private StratDocument? Commit(MinedPattern pattern, StratDocument doc)
    {
        // A promotion the state file cannot record would be offered again and promoted twice.
        lock (_gate)
        {
            if (!StateUsable())
            {
                return null;
            }
        }

        StratSaveResult saved = _strats.Save(doc, [], $"promoted from {pattern.Support} mined rounds");
        if (!saved.Saved)
        {
            return null;
        }

        WriteRuns(pattern, doc);
        Mutate(state => state.Promoted[pattern.Key] = doc.Id);
        return doc;
    }

    /// <summary>
    ///     <see cref="Build" /> for the Detected preview, as a user-requested queue item off the UI thread. Writes
    ///     nothing. Null when the medoid's files are gone or the preview was cancelled.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="owner">The book it would go into.</param>
    /// <param name="nowUtc">The creation time.</param>
    /// <param name="cancellationToken">Cancels a preview the user moved away from.</param>
    public async Task<StratDocument?> PreviewAsync(MinedPattern pattern, StratOwner owner, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(owner);
        StratDocument? built = null;
        if (_queue is null)
        {
            try
            {
                await _run(() => built = Build(pattern, owner, nowUtc, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return cancellationToken.IsCancellationRequested ? null : built;
        }

        IDemoQueueHandle handle = _queue.SubmitJob(new QueueJobRequest(QueueJobKind.StratPreview,
            $"Strat preview: {MinedStratBuilder.Name(pattern)}", "strat-mining", DemoJobPriority.UserRequested,
            async job =>
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                await _run(() => built = Build(pattern, owner, nowUtc, job.CancellationToken)).ConfigureAwait(false);
            }));
        await using (cancellationToken.Register(handle.Cancel))
        {
            await handle.Completion.ConfigureAwait(false);
        }

        return cancellationToken.IsCancellationRequested ? null : built;
    }

    /// <summary>The strat a pattern would become, without saving it. Null when the medoid's files are gone.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="owner">The book.</param>
    /// <param name="nowUtc">The creation time.</param>
    /// <param name="cancellationToken">Checked between the file reads; a cancelled build throws.</param>
    public StratDocument? Build(MinedPattern pattern, StratOwner owner, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        RoundSignature medoid = pattern.Medoid;
        cancellationToken.ThrowIfCancellationRequested();
        if (_demoCache.TryLoadRecord(medoid.DemoPath) is not { RoundFacts: { } rows } record
            || rows.Rounds.FirstOrDefault(r => r.Number == medoid.Round) is not { } facts
            || cancellationToken.IsCancellationRequested
            || _positions.TryReadPositions(medoid.DemoPath, _fingerprintFor(pattern.Map), record.Sha256) is not { } positions
            || positions.Round(medoid.Round) is not { } stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<GrenadeRow> grenades =
        [
            .. _grenadeIndex?.Rows(new GrenadeQuery(pattern.Map, DemoPaths: new HashSet<string>([medoid.DemoPath])))
                   .Select(g => g.Row)
                   .Where(r => r.RoundNumber == medoid.Round)
               ?? []
        ];
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<int, (ulong, string)> players = [];
        foreach (CachedPlayerInfo player in record.Players)
        {
            players[player.Slot] = (ulong.TryParse(player.SteamId64, out ulong id) ? id : 0, player.Name);
        }

        RoundCapture capture = CachedRoundCapture.Build(positions, stored, facts, grenades, players, medoid.TickRate,
            MinedStratBuilder.WindowEnd(pattern, facts));
        return MinedStratBuilder.Document(pattern, capture, facts, owner, Path.GetFileName(medoid.DemoPath),
            id => _teams?.AllTeams.FirstOrDefault(t => t.Id == id)?.Name, nowUtc);
    }

    private void WriteRuns(MinedPattern pattern, StratDocument doc)
    {
        if (_tags is null)
        {
            return;
        }

        string code = pattern.Kind == PatternKind.Execute ? $"{pattern.Site} execute"
            : pattern.Side == 2 ? DefaultCode : SetupCode;
        foreach (MinedMember member in pattern.Members)
        {
            string? sha = member.Sha256 ?? _demoCache.TryGetIndex(member.DemoPath)?.Sha256;
            if (sha is null || _demoCache.TryLoadRecord(member.DemoPath) is not { } record)
            {
                continue;
            }

            (int from, int to) = RunSpan(pattern, member, record.RoundFacts?.Rounds.FirstOrDefault(r => r.Number == member.Round));
            TagInstance run = new()
            {
                Id = Guid.NewGuid(),
                Code = code,
                FromTick = from,
                ToTick = to,
                Round = member.Round,
                CreatedUtc = doc.CreatedUtc,
                ModifiedUtc = doc.CreatedUtc,
                Source = TagSources.Suggested,
                Provenance = new JsonObject { ["detector"] = Detector, ["pattern"] = pattern.Key },
                Labels =
                [
                    new TagLabel(TagStore.StratGroup, doc.Id.ToString()),
                    new TagLabel(StratEvidence.RevisionGroup, "1")
                ]
            };
            _tags.Append(new DemoIdentity(sha, Path.GetFileName(member.DemoPath), record.Size),
                record.RoundFacts?.Clock?.ToIdentity() ?? ClockIdentity.Unknown, run);
        }
    }

    /// <summary>The last run removal a deleted strat started; for tests.</summary>
    internal Task LastRunRemoval { get; private set; } = Task.CompletedTask;

    // A promoted strat deleted from its book: the pattern is new again, and the runs its promotion wrote go.
    private void OnStratDeleted(Guid id)
    {
        string? key;
        lock (_gate)
        {
            key = _state.Promoted.FirstOrDefault(p => p.Value == id).Key;
        }

        if (key is null)
        {
            return;
        }

        Mutate(state => state.Promoted.Remove(key));
        LastRunRemoval = _queue is null
            ? _run(() => RemoveRuns(id))
            : _queue.SubmitJob(new QueueJobRequest(QueueJobKind.StratMining, "Strat mining: remove a deleted strat's runs",
                "strat-mining", DemoJobPriority.UserRequested, _ =>
                {
                    RemoveRuns(id);
                    return Task.CompletedTask;
                }, Key: "strat-runs:" + id.ToString("N"))).Completion;
    }

    /// <summary>
    ///     Removes the runs a promotion wrote for <paramref name="stratId" />: suggested instances whose provenance
    ///     detector is <see cref="Detector" /> and whose strat label is that strat. Nothing else is touched.
    /// </summary>
    /// <returns>How many were removed.</returns>
    internal int RemoveRuns(Guid stratId)
    {
        if (_tags is null)
        {
            return 0;
        }

        string value = stratId.ToString();
        int removed = 0;
        foreach (TagDocument document in _tags.LoadDocuments(e => e.StratIds.Contains(value, StringComparer.Ordinal)))
        {
            _tags.Update(document.Demo.Sha256, d => removed += d.Instances.RemoveAll(i => IsRunOf(i, value)));
        }

        return removed;
    }

    private static bool IsRunOf(TagInstance instance, string stratId) =>
        instance.Source == TagSources.Suggested
        && instance.Provenance?["detector"] is JsonValue detector && detector.TryGetValue(out string? name) && name == Detector
        && instance.Labels.Any(l => l.Group == TagStore.StratGroup && l.Value == stratId);

    // The window the strat covers in that round: the setup's opening, or the take from 10 s before to the plant.
    private static (int From, int To) RunSpan(MinedPattern pattern, MinedMember member, RoundFacts.RoundFacts? facts)
    {
        int rate = Math.Max(1, member.TickRate);
        int end = facts?.EndTick ?? int.MaxValue;
        if (pattern.Kind == PatternKind.Setup)
        {
            int to = member.FreezeEndTick + MinedStratBuilder.SetupSeconds * rate;
            return (member.FreezeEndTick, Math.Min(to, end));
        }

        int take = member.AnchorTick;
        int stop = facts?.PlantTick ?? take + MinedStratBuilder.ExecuteTailSeconds * rate;
        return (Math.Max(member.FreezeEndTick, take - 10 * rate), Math.Min(Math.Max(stop, take), end));
    }

    /// <summary>
    ///     Old key to new key for each pattern that has user state and whose key the re-mine did not keep: the new
    ///     pattern holding at least half of its rounds, when one does and has no state of its own. A new demo can
    ///     move a pattern's medoid or its common throws, and with them its key.
    /// </summary>
    /// <param name="previous">The patterns before the re-mine.</param>
    /// <param name="next">The patterns after it.</param>
    /// <param name="hasState">Whether a key is dismissed or promoted.</param>
    public static Dictionary<string, string> KeyMoves(IReadOnlyList<MinedPattern> previous, IReadOnlyList<MinedPattern> next,
        Func<string, bool> hasState)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(hasState);
        HashSet<string> kept = [.. next.Select(p => p.Key)];
        Dictionary<string, string> moves = new(StringComparer.Ordinal);
        foreach (MinedPattern old in previous.Where(p => hasState(p.Key) && !kept.Contains(p.Key)))
        {
            HashSet<string> rounds = [.. old.Members.Select(RoundKey)];
            MinedPattern? heir = next
                .Where(p => !hasState(p.Key) && !moves.ContainsValue(p.Key))
                .Select(p => (Pattern: p, Shared: p.Members.Count(m => rounds.Contains(RoundKey(m)))))
                .Where(x => x.Shared * 2 >= rounds.Count)
                .OrderByDescending(x => x.Shared)
                .ThenBy(x => x.Pattern.Key, StringComparer.Ordinal)
                .Select(x => x.Pattern)
                .FirstOrDefault();
            if (heir is not null)
            {
                moves[old.Key] = heir.Key;
            }
        }

        return moves;

        static string RoundKey(MinedMember m) => $"{m.Sha256 ?? m.DemoPath}#{m.Round}";
    }

    private void CarryState(IReadOnlyList<MinedPattern> previous, IReadOnlyList<MinedPattern> next)
    {
        Dictionary<string, string> moves;
        lock (_gate)
        {
            MiningState state = _state;
            moves = KeyMoves(previous, next, k => state.Dismissed.Contains(k) || state.Promoted.ContainsKey(k));
        }

        if (moves.Count == 0)
        {
            return;
        }

        Mutate(state =>
        {
            foreach ((string from, string to) in moves)
            {
                if (state.Dismissed.Remove(from))
                {
                    state.Dismissed.Add(to);
                }

                if (state.Promoted.Remove(from, out Guid id))
                {
                    state.Promoted[to] = id;
                }
            }
        }, publish: false);
    }

    private void OnSourceChanged(string? _) => OnSourceChanged();

    private void OnSourceChanged()
    {
        if (MinedUtc is null || QuietDelay == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        lock (_gate)
        {
            _quiet ??= new Timer(_ => OnQuiet());
            _quiet.Change(QuietDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // Demo parses only: the queue's other jobs (clips, this mine) must not hold the re-mine back forever.
    private bool QueueBusy => _queue is { } queue && queue.ActiveCount(QueueJobKind.DemoProcessing) > 0;

    /// <summary>The quiet timer: re-mines unless the processing queue has work, in which case it waits for the drain.</summary>
    internal void OnQuiet()
    {
        lock (_gate)
        {
            if (QueueBusy)
            {
                _deferred = true;
                return;
            }

            _deferred = false;
        }

        _ = MineAsync(user: false);
    }

    private void OnQueueChanged()
    {
        lock (_gate)
        {
            if (!_deferred || QueueBusy)
            {
                return;
            }

            _deferred = false;
        }

        OnSourceChanged();
    }

    private void Publish(IReadOnlyList<MinedPattern> patterns)
    {
        MiningState state;
        lock (_gate)
        {
            state = _state;
        }

        Patterns =
        [
            .. patterns.Select(p => new DetectedPattern(p, state.Dismissed.Contains(p.Key),
                state.Promoted.TryGetValue(p.Key, out Guid id) ? id : null))
        ];
        Changed?.Invoke();
    }

    private void Mutate(Action<MiningState> change, bool publish = true)
    {
        lock (_gate)
        {
            change(_state);
            if (_statePath is not null && StateUsable())
            {
                try
                {
                    WriteAtomic(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The in-memory state stands for the session; the next change writes it again.
                }
            }
        }

        if (publish)
        {
            Publish([.. Patterns.Select(p => p.Pattern)]);
        }
    }

    private void Save(IReadOnlyList<MinedPattern> patterns)
    {
        if (_detectedPath is not null)
        {
            WriteAtomic(_detectedPath, JsonSerializer.Serialize(
                new DetectedFile(SchemaVersion, DateTime.UtcNow, [.. patterns]), JsonOptions));
        }
    }

    private void Load()
    {
        LoadState();
        try
        {
            if (_detectedPath is not null && File.Exists(_detectedPath)
                                          && JsonSerializer.Deserialize<DetectedFile>(File.ReadAllText(_detectedPath), JsonOptions) is
                                              { SchemaVersion: SchemaVersion } file)
            {
                MinedUtc = file.MinedUtc;
                Publish(file.Patterns);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable detected file is rebuilt by the next mine.
        }
    }

    // Under _gate, or from the constructor. Retries a file that could not be opened; a retry that reads it keeps
    // what changed in memory meanwhile on top of it.
    private bool StateUsable()
    {
        if (_stateUnread && !_stateRefused)
        {
            LoadState();
        }

        return !_stateRefused && !_stateUnread;
    }

    private void LoadState()
    {
        if (_statePath is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_statePath))
            {
                _stateUnread = false;
                StateProblem = null;
                return;
            }

            string json = File.ReadAllText(_statePath);
            MiningState? state;
            try
            {
                state = JsonSerializer.Deserialize<MiningState>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                RefuseState($"{_statePath} is not readable ({ex.Message}). Move it aside and restart to start a new one.");
                return;
            }

            if (state is null || state.SchemaVersion > StateSchemaVersion)
            {
                RefuseState($"{_statePath} is at schema {state?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads. Use the newer build, or move the file aside and restart.");
                return;
            }

            if (_stateUnread)
            {
                state.Dismissed.UnionWith(_state.Dismissed);
                foreach ((string key, Guid id) in _state.Promoted)
                {
                    state.Promoted.TryAdd(key, id);
                }
            }

            _state = state;
            _stateUnread = false;
            StateProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stateUnread = true;
            StateProblem = $"{_statePath} could not be opened ({ex.Message}). Changes are kept for this session and saved once it opens.";
        }
    }

    private void RefuseState(string problem)
    {
        _stateRefused = true;
        _stateUnread = false;
        StateProblem = problem;
        _state = new MiningState();
    }

    private static void WriteAtomic(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".mining-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    private sealed record DetectedFile(int SchemaVersion, DateTime MinedUtc, List<MinedPattern> Patterns);

    private sealed class MiningState
    {
        // Missing in files written before it existed, which read as schema 1.
        public int SchemaVersion { get; set; } = StateSchemaVersion;

        public HashSet<string> Dismissed { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, Guid> Promoted { get; set; } = new(StringComparer.Ordinal);
    }
}

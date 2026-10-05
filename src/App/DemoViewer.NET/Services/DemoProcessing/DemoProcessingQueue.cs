#region

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     The global demo-processing queue (demo-processing-queue.md). The single source all background
///     demo parse/analyse work is pulled from, plus the awaitable highest-priority foreground open.
///     <para>
///         <b>Two authorities, one live number.</b> This pump is the PRIMARY limiter: it starts
///         up to <see cref="MaxConcurrency" /> background worker loops, owns priority ordering,
///         coalescing, the size cap, and pause/disable. <see cref="HeavyJobGate" /> is the hard SAFETY
///         BACKSTOP that cannot be exceeded even if the pump miscounts; both read the same live
///         concurrency, so a pump bug can never OOM the machine.
///     </para>
///     <para>
///         <b>Foreground never depends on the pump.</b> <see cref="RequestForegroundAsync" />
///         acquires the interactive gate slot and parses the caller's in-hand bytes directly; it
///         bypasses pause/disable/size-cap by construction. Coalescing onto an in-flight parse is a
///         best-effort optimisation only.
///     </para>
///     <para>
///         <b>Threading.</b> Authoritative state lives in <c>_entries</c> under <c>_sync</c>. The
///         UI-bindable <see cref="Items" /> mirror is reconciled by id via the injected <c>post</c>
///         delegate (the dispatcher in-app; inline in tests). All waits are <c>await</c>: WASM-safe.
///     </para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The user-facing feature IS a processing queue; the 'Queue' suffix is intentional.")]
public sealed class DemoProcessingQueue : IDemoProcessingQueue, IDisposable
{
    // How many terminal items linger in the mirror for UI feedback before the oldest are pruned.
    private const int TerminalHistoryCap = 30;

    // Throttles the drain compaction after non-parse jobs only; a finished parse compacts at once.
    private static readonly TimeSpan MinDrainCompactInterval = TimeSpan.FromSeconds(30);

    /// <summary>The shared background plan when no owner on the entry reads user commands.</summary>
    public static DecodePlan WithoutUserCommands { get; } =
        DecodePlan.Everything with { Categories = MessageCategories.All & ~MessageCategories.UserCmds };

    // Diagnostics-pillar logger (v0.6.0: replaced Console.WriteLine). Lazy (the ambient factory is
    // wired after construction) and static so the static SafeInvoke helper can log through it.
    private static ILogger? _diagLog;

    private readonly Func<Task> _compactHeap;
    private readonly List<Entry> _entries = [];
    private readonly HeavyJobGate _gate;
    private readonly ObservableCollection<DemoQueueItem> _items = [];
    private readonly Func<ReadOnlyMemory<byte>, ParsedDemo> _parseBytes; // foreground: parse in-hand bytes
    private readonly Func<string, DecodePlan, ParsedDemo> _parseFile; // background: read file at path → parse
    private readonly Func<string, ForwardNeeds, Action<double>, CancellationToken, ForwardDemoResult>? _forwardPass;
    private readonly Func<string, string?>? _contentHash;
    private readonly Dictionary<string, int> _parsesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly JobKindRegistry _jobKinds;
    private readonly Action<ParsedDemo>? _parseReleased;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _shutdown = new();

    // Captured once so a worker never touches _shutdown.Token AFTER Dispose disposes the source (which
    // would throw ObjectDisposedException). The captured struct stays valid post-dispose.
    private readonly CancellationToken _shutdownToken;
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private int _activeWorkers;
    private int _activeLightWorkers;
    private bool _backgroundEnabled = true;
    private bool _disposed;
    private bool _compacting;
    private int _jobsSinceCompact;
    private int _parsesSinceCompact;
    private ITimer? _deferredCompact;
    private long _deferredGeneration;
    private DateTimeOffset? _lastCompact;

    private int _maxConcurrency = 1;
    private int _maxQueueSize = 200;
    private bool _paused;
    private long _seq;

    // The demo of the heavy item that started last: its remaining work goes before other demos'.
    private string? _lastStartedPath;

    /// <param name="gate">The machine-wide heavy-parse gate (concurrency backstop + reel/interactive).</param>
    /// <param name="post">Marshals mirror mutations to the UI thread (inline in tests).</param>
    /// <param name="parseFile">
    ///     Test seam: the background read+parse step (default <see cref="ParseFileDefault" />).
    /// </param>
    /// <param name="parseBytes">
    ///     Test seam: the foreground in-hand-bytes parse (default
    ///     <c>DemoParser.Parse</c>).
    /// </param>
    /// <param name="compactHeap">
    ///     Test seam: the compaction run when the queue drains, as a queue item (default
    ///     <see cref="HeapCompactor.CompactAsync" />).
    /// </param>
    /// <param name="timeProvider">Test seam: the clock for the drain-compaction throttle.</param>
    /// <param name="parseFileWithPlan">
    ///     Test seam: the background parse given the entry's plan; wins over <paramref name="parseFile" />.
    /// </param>
    /// <param name="parseReleased">Called once every owner of a background parse has run on it.</param>
    /// <param name="forwardPass">
    ///     The forward read for an entry whose every owner can take one. Null keeps every entry on the
    ///     retained parse.
    /// </param>
    /// <param name="jobKinds">
    ///     Resolves a kind's scheduling rank and light-slot flag. Defaults to
    ///     <see cref="JobKindRegistry.Default" />, the core table plus the one compiled-in pack's kinds,
    ///     so a test or a bare construction sees the same scheduling every kind had before the registry.
    /// </param>
    /// <param name="contentHash">
    ///     A demo's content hash when the library knows it, so <see cref="ParseCounts" /> counts copies of one
    ///     demo together. Null counts by path.
    /// </param>
    public DemoProcessingQueue(
        HeavyJobGate gate,
        Action<Action>? post = null,
        Func<string, ParsedDemo>? parseFile = null,
        Func<ReadOnlyMemory<byte>, ParsedDemo>? parseBytes = null,
        Func<Task>? compactHeap = null,
        TimeProvider? timeProvider = null,
        Func<string, DecodePlan, ParsedDemo>? parseFileWithPlan = null,
        Func<string, ForwardNeeds, Action<double>, CancellationToken, ForwardDemoResult>? forwardPass = null,
        Action<ParsedDemo>? parseReleased = null,
        JobKindRegistry? jobKinds = null,
        Func<string, string?>? contentHash = null)
    {
        _contentHash = contentHash;
        _gate = gate;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _parseFile = parseFileWithPlan ?? (parseFile is null ? ParseFileDefault : (path, _) => parseFile(path));
        _parseBytes = parseBytes ?? (bytes => DemoParser.Parse(bytes));
        _forwardPass = forwardPass;
        _parseReleased = parseReleased;
        _compactHeap = compactHeap ?? HeapCompactor.CompactAsync;
        _time = timeProvider ?? TimeProvider.System;
        _jobKinds = jobKinds ?? JobKindRegistry.Default;
        _shutdownToken = _shutdown.Token;
        Items = new ReadOnlyObservableCollection<DemoQueueItem>(_items);
        _gate.MaxConcurrency = _maxConcurrency;
    }

    private static ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger(AppLog.QueueCategory);

    /// <summary>
    ///     The background parse. Maps the file instead of reading it into one LOH-sized array; the
    ///     mapping is released before this returns and the <see cref="ParsedDemo" /> holds no view of it.
    ///     Browser has no memory mapping, and a file that may still be written is read into a byte[]:
    ///     truncating a mapped file under the parse is a fatal access violation, not an exception.
    /// </summary>
    private ParsedDemo ParseFileDefault(string path, DecodePlan plan)
    {
        ParseOptions options = new() { Plan = plan };
        return !OperatingSystem.IsBrowser() && MappedParsePolicy.IsSettled(path, _time, MappedParsePolicy.StatFile)
            ? MemoryMappedDemoSource.ParseFile(path, options)
            : DemoParser.Parse(File.ReadAllBytes(path).AsMemory(), options);
    }

    public ReadOnlyObservableCollection<DemoQueueItem> Items { get; }

    // Test seam: a worker decrements this only after its last drain check.
    internal int ActiveWorkerCount
    {
        get
        {
            lock (_sync)
            {
                return _activeWorkers;
            }
        }
    }

    public event Action? Changed;
    public event Action? CapacityAvailable;

    /// <inheritdoc />
    public IShellDemoLease? ShellDemo { get; set; }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _paused;
            }
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count(e => e.State == DemoQueueItemState.Queued);
            }
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count(e => e.State == DemoQueueItemState.Running);
            }
        }
    }

    public int ActiveCount(QueueJobKind kind)
    {
        lock (_sync)
        {
            return _entries.Count(e => e.Kind == kind && IsActive(e));
        }
    }

    public int ActiveCount(string extensionKind)
    {
        lock (_sync)
        {
            return _entries.Count(e => e.Kind == QueueJobKind.Extension
                                       && string.Equals(e.ExtensionKind, extensionKind, StringComparison.Ordinal)
                                       && IsActive(e));
        }
    }

    /// <summary>
    ///     Every demo read this session, by content hash when the library knows it and by path otherwise, with
    ///     how many times it was read: opens, retained parses and forward reads, a stopped read included.
    /// </summary>
    public IReadOnlyDictionary<string, int> ParseCounts()
    {
        KeyValuePair<string, int>[] byPath;
        lock (_sync)
        {
            byPath = [.. _parsesByPath];
        }

        // Folded at read time: a first import learns its hash during the visit that read it.
        Dictionary<string, int> byContent = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, int count) in byPath)
        {
            string key = ContentKey(path);
            byContent[key] = byContent.GetValueOrDefault(key) + count;
        }

        return byContent;
    }

    /// <summary>How many times the content at <paramref name="path" /> was read this session, through any path.</summary>
    public int ParseCount(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ParseCounts().GetValueOrDefault(ContentKey(path));
    }

    private string ContentKey(string path)
    {
        try
        {
            return _contentHash?.Invoke(path) is { Length: > 0 } hash ? hash : path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
            return path;
        }
    }

    // Called right before every read of a demo, so a read that fails or is stopped still counts.
    private void NoteParse(string? path, string read)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (_sync)
        {
            _parsesByPath[path] = _parsesByPath.GetValueOrDefault(path) + 1;
        }

        string name = System.IO.Path.GetFileName(path);
        int count = ParseCount(path);
        AppLog.DemoRead(DiagLog, name, read, count);
    }

    public int MaxConcurrency
    {
        get
        {
            lock (_sync)
            {
                return _maxConcurrency;
            }
        }
        set
        {
            _gate.MaxConcurrency = value; // clamps to [1, HardCap]
            int applied = _gate.MaxConcurrency;
            lock (_sync)
            {
                _maxConcurrency = applied;
                PumpLocked(); // grow → spawn more workers
            }
        }
    }

    public int MaxQueueSize
    {
        get
        {
            lock (_sync)
            {
                return _maxQueueSize;
            }
        }
        set
        {
            bool grew;
            lock (_sync)
            {
                int next = Math.Max(1, value);
                grew = next > _maxQueueSize;
                _maxQueueSize = next;
            }

            if (grew)
            {
                RaiseCapacityAvailable(); // more room → let feeders top up
            }
        }
    }

    public bool BackgroundEnabled
    {
        get
        {
            lock (_sync)
            {
                return _backgroundEnabled;
            }
        }
        set
        {
            lock (_sync)
            {
                _backgroundEnabled = value;
                PumpLocked();
            }

            RaiseChanged();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _paused = true;
        }

        RaiseChanged();
    }

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Pause/Resume is the domain vocabulary for the queue control.")]
    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            PumpLocked();
        }

        RaiseChanged();
    }

    // ── Foreground fast-path ─────────────────────────────────────────────

    public async Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        // Best-effort coalesce: if this exact path is already being parsed, await THAT result rather
        // than starting a redundant multi-GB parse. Never blocks on the pump: the fallback below
        // always runs a direct parse under the interactive slot.
        if (path is not null)
        {
            Task<ParsedDemo>? inFlight = null;
            lock (_sync)
            {
                if (JoinableParseLocked(path) is { } running)
                {
                    TaskCompletionSource<ParsedDemo> waiter = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    running.ForegroundWaiters.Add(waiter);
                    inFlight = waiter.Task;
                }
            }

            if (inFlight is not null)
            {
                // WaitAsync so a foreground cancel abandons the wait; the shared parse still completes.
                return await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Fast-path: the interactive slot preempts background and refuses during a reel.
        using (await _gate.AcquireInteractiveAsync(cancellationToken).ConfigureAwait(false))
        {
            NoteParse(path, "open");
            return await Task.Run(() => _parseBytes(bytes), cancellationToken).ConfigureAwait(false);
        }
    }

    // A running retained parse of this demo that decodes user commands: an open can take its result.
    private Entry? JoinableParseLocked(string path) => _entries.FirstOrDefault(e =>
        e.Kind == QueueJobKind.DemoProcessing && e.State == DemoQueueItemState.Running && !e.Finalizing
        && !e.Forward && e.UserCommands && PathEquals(e.Path, path));

    // ── User opens ───────────────────────────────────────────────────────

    public IDemoOpenTicket BeginOpen(string? path, string fileName)
    {
        List<CancellationTokenSource> replaced = [];
        CancellationTokenSource? preempted;
        OpenTicket ticket;
        lock (_sync)
        {
            if (_disposed)
            {
                return new PassThroughDemoOpen((bytes, ct) => Task.Run(() => _parseBytes(bytes), ct));
            }

            // The last user choice wins. A queued open ends here; a running one stops at its next check.
            foreach (Entry old in _entries.Where(e => e.Kind == QueueJobKind.DemoOpen && IsActive(e)).ToList())
            {
                old.Superseded = true;
                old.CancelRequested = true;
                if (old.Cancel is { } c)
                {
                    replaced.Add(c);
                }

                if (old.State == DemoQueueItemState.Queued)
                {
                    SetTerminalLocked(old, DemoQueueItemState.Cancelled, null);
                }
                else if (old.ParsingOpen)
                {
                    old.Detail = "Replaced, finishing its parse";
                }
            }

            CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            Entry entry = new()
            {
                Kind = QueueJobKind.DemoOpen,
                JobOwner = "open",
                Path = path ?? "",
                DisplayName = "Open demo: " + fileName,
                FileName = fileName,
                Priority = DemoJobPriority.Foreground,
                Seq = _seq++,
                Cancel = cancel,
                Detail = "Reading the file"
            };
            _entries.Insert(0, entry);
            // A queued visit of this demo waits for the open and runs on its parse instead of reading the file.
            if (entry.Path.Length > 0)
            {
                foreach (Entry visit in _entries.Where(e =>
                             e.Kind == QueueJobKind.DemoProcessing && e.State == DemoQueueItemState.Queued && PathEquals(e.Path, entry.Path)))
                {
                    visit.ParkedBehind = entry;
                }
            }

            preempted = PreemptForLocked(entry);
            PumpLocked();
            ticket = new OpenTicket(this, entry, cancel);
        }

        foreach (CancellationTokenSource c in replaced)
        {
            CancelQuietly(c);
        }

        CancelQuietly(preempted);
        RaiseChanged();
        return ticket;
    }

    // Joins a running parse of the same demo, else waits for the interactive slot. A retained parse takes no
    // token, so an open cannot stop one; the list names it while the open waits.
    private async Task<ParsedDemo> ParseForOpenAsync(Entry entry, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Task<ParsedDemo>? joined = null;
        lock (_sync)
        {
            ThrowIfOpenEndedLocked(entry, ct);
            if (entry.Path.Length > 0 && JoinableParseLocked(entry.Path) is { } running)
            {
                TaskCompletionSource<ParsedDemo> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                running.ForegroundWaiters.Add(waiter);
                joined = waiter.Task;
                entry.State = DemoQueueItemState.Running;
                entry.Detail = "Parsing with the background parse of this demo";
            }
            else
            {
                entry.WaitingForSlot = true;
            }
        }

        RaiseChanged();
        if (joined is not null)
        {
            return await joined.WaitAsync(ct).ConfigureAwait(false);
        }

        IDisposable slot;
        try
        {
            slot = await _gate.AcquireInteractiveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                entry.WaitingForSlot = false;
            }
        }

        using (slot)
        {
            lock (_sync)
            {
                ThrowIfOpenEndedLocked(entry, ct);
                entry.State = DemoQueueItemState.Running;
                entry.Detail = "Parsing";
                entry.ParsingOpen = true;
            }

            RaiseChanged();
            ParsedDemo parsed;
            try
            {
                // The parser takes no token: a replaced open still runs to the end of its parse.
                NoteParse(entry.Path, "open");
                parsed = await Task.Run(() => _parseBytes(bytes)).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    entry.ParsingOpen = false;
                }
            }

            bool dropped;
            lock (_sync)
            {
                dropped = entry.CancelRequested || !IsActive(entry);
            }

            if (dropped)
            {
                // Ended here, not by the caller, so the list clears as soon as the slot frees.
                EndOpen(entry, DemoQueueItemState.Cancelled, null);
                throw new OperationCanceledException(ct);
            }

            return parsed;
        }
    }

    // Under _sync. A replaced open is ended here before its token is cancelled, outside the lock.
    private static void ThrowIfOpenEndedLocked(Entry entry, CancellationToken ct)
    {
        if (entry.CancelRequested || !IsActive(entry))
        {
            throw new OperationCanceledException(ct);
        }
    }

    private void EndOpen(Entry entry, DemoQueueItemState state, string? error)
    {
        lock (_sync)
        {
            entry.Cancel = null;
            if (!IsActive(entry))
            {
                return;
            }

            if (state == DemoQueueItemState.Completed && entry.CancelRequested)
            {
                state = DemoQueueItemState.Cancelled;
            }
        }

        SetTerminal(entry, state, error);
    }

    // Under _sync. What a waiting open shows: the open or heavy item holding the slot.
    private string OpenWaitDetailLocked(Entry self)
    {
        if (_entries.FirstOrDefault(e => e.ParsingOpen && !ReferenceEquals(e, self)) is { } open)
        {
            return open.CancelRequested
                ? $"Waiting for {open.FileName} to finish parsing (cancelled)"
                : $"Waiting for {open.FileName} to finish parsing";
        }

        Entry? holder = _entries.FirstOrDefault(e =>
            e.State == DemoQueueItemState.Running && e.Kind != QueueJobKind.DemoOpen && !IsLight(e));
        if (holder is null)
        {
            return "Waiting for the parse slot";
        }

        string name = holder.DisplayName ?? System.IO.Path.GetFileName(holder.Path);
        if (holder.Kind == QueueJobKind.DemoProcessing && !holder.Forward)
        {
            return $"Waiting for {name} to finish parsing";
        }

        return holder.Preempted || holder.CancelRequested ? $"Waiting for {name} to stop" : $"Waiting for {name} to finish";
    }

    private sealed class OpenTicket(DemoProcessingQueue queue, Entry entry, CancellationTokenSource cancel)
        : IDemoOpenTicket
    {
        private readonly CancellationToken _token = cancel.Token;
        private int _ended;

        public CancellationToken CancellationToken => _token;

        public bool IsSuperseded
        {
            get
            {
                lock (queue._sync)
                {
                    return entry.Superseded;
                }
            }
        }

        public Task<ParsedDemo> ParseAsync(ReadOnlyMemory<byte> bytes) => queue.ParseForOpenAsync(entry, bytes, _token);

        public void Report(double progress, string stage) =>
            queue.ReportProgress(entry, (int)(Math.Clamp(progress, 0, 1) * 100), 100, stage);

        public Task RunPassesAsync(ParsedDemo parsed, Action? plan = null) =>
            queue.RunOpenPassesAsync(entry, parsed, plan, _token);

        public void Complete() => End(DemoQueueItemState.Completed, null);

        public void Fail(Exception failure) => End(DemoQueueItemState.Failed, failure.Message);

        public void Dispose() => End(DemoQueueItemState.Cancelled, null);

        private void End(DemoQueueItemState state, string? error)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return;
            }

            queue.EndOpen(entry, state, error);
            cancel.Dispose();
        }
    }

    // ── Background submit + coalescing ───────────────────────────────────

    public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Submit(request.Path, LevelOf(request.Priority), request.Priority, request.OrderHint, request.DisplayName,
            [new CallbackPass(request)], null);
    }

    public IDemoQueueHandle SubmitVisit(DemoVisitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        return Submit(request.Path, request.Level, PriorityOf(request.Level), request.OrderHint, request.DisplayName,
            request.Passes, request.PassEnded);
    }

    // An open demo's passes and a user's request outrank the backlog; the finer levels order later.
    private static DemoJobPriority PriorityOf(PassLevel level) =>
        level >= PassLevel.OpenDemo ? DemoJobPriority.UserRequested : DemoJobPriority.Background;

    private static PassLevel LevelOf(DemoJobPriority priority) =>
        priority >= DemoJobPriority.UserRequested ? PassLevel.UserRequested : PassLevel.Background;

    private Handle Submit(string path, PassLevel level, DemoJobPriority priority, long orderHint, string? displayName,
        IReadOnlyList<IDemoPass> passes, Action<IDemoPass, PassOutcome, Exception?>? ended)
    {
        VisitedDemo demo = new(path);
        // Asked once, outside the lock: the answer fixes how this pass may join and what the visit reads.
        List<(IDemoPass Pass, PassNeeds Needs)> joining = passes.Select(p => (p, SafeNeeds(p, demo))).ToList();
        List<string> owners = passes.Select(p => p.Id).Distinct(StringComparer.Ordinal).ToList();

        Handle handle;
        CancellationTokenSource? supersededCancel = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return RejectedHandle(path, owners);
            }

            // A running parse without user commands cannot serve a pass that reads them, and a running
            // forward read serves only forward passes whose needs it already covers.
            Entry? existing = _entries.FirstOrDefault(e =>
                e.Kind == QueueJobKind.DemoProcessing && IsActive(e) && !e.Finalizing && PathEquals(e.Path, path)
                && (e.State == DemoQueueItemState.Queued
                    || joining.TrueForAll(j => DemoVisit.CanJoinRunning(j.Needs, e.Forward, e.Needs, e.UserCommands))));
            if (existing is not null)
            {
                // Coalesce: one read, every pass; bump level, priority and order to the max seen.
                foreach ((IDemoPass pass, PassNeeds needs) in joining)
                {
                    existing.Visit!.Add(pass, needs, ended);
                }

                existing.Visit!.Raise(level);
                if (priority > existing.Priority)
                {
                    existing.Priority = priority;
                }

                if (orderHint > existing.OrderHint)
                {
                    existing.OrderHint = orderHint;
                }

                existing.DisplayName ??= displayName;
                if (existing.State == DemoQueueItemState.Queued)
                {
                    existing.ParkedBehind ??= OpenForLocked(path);
                    supersededCancel = existing.ParkedBehind is null ? PreemptForLocked(existing) : null;
                }

                PumpLocked(); // priority may have changed the pick order
                handle = new Handle(this, existing.Id, owners, path, existing.Completion.Task, entry: existing);
            }
            else if (_entries.FirstOrDefault(e =>
                         e.Kind == QueueJobKind.DemoProcessing && e.State == DemoQueueItemState.Running && e.Forward
                         && !e.Finalizing && !e.CancelRequested && PathEquals(e.Path, path)) is { } forward)
            {
                // The running forward read cannot serve this pass: stop it and move its passes onto one entry
                // that runs next, so the demo is still read once and no pass waits behind other demos.
                Entry entry = new()
                {
                    Path = path,
                    DisplayName = forward.DisplayName ?? displayName,
                    Priority = priority > forward.Priority ? priority : forward.Priority,
                    OrderHint = Math.Max(orderHint, forward.OrderHint),
                    Seq = forward.Seq,
                    Front = true,
                    Visit = new DemoVisit(demo, level),
                    ParkedBehind = OpenForLocked(path)
                };
                entry.Visit.TakeFrom(forward.Visit!);
                foreach ((IDemoPass pass, PassNeeds needs) in joining)
                {
                    entry.Visit.Add(pass, needs, ended);
                }

                forward.CancelRequested = true;
                forward.Superseded = true;
                supersededCancel = forward.Cancel;
                _entries.Add(entry);
                PumpLocked();
                handle = new Handle(this, entry.Id, owners, path, entry.Completion.Task, entry: entry);
            }
            else if (priority < DemoJobPriority.UserRequested && BackgroundTierCountLocked() >= _maxQueueSize)
            {
                // The size cap governs the BACKGROUND tier only; UserRequested/Foreground bypass it.
                return RejectedHandle(path, owners);
            }
            else
            {
                Entry entry = new()
                {
                    Path = path,
                    DisplayName = displayName,
                    Priority = priority,
                    OrderHint = orderHint,
                    Seq = _seq++,
                    Visit = new DemoVisit(demo, level),
                    ParkedBehind = OpenForLocked(path)
                };
                foreach ((IDemoPass pass, PassNeeds needs) in joining)
                {
                    entry.Visit.Add(pass, needs, ended);
                }

                _entries.Add(entry);
                // A visit that waits for the open stops nothing: the open did that when it began.
                supersededCancel = entry.ParkedBehind is null ? PreemptForLocked(entry) : null;
                PumpLocked();
                handle = new Handle(this, entry.Id, owners, path, entry.Completion.Task, entry: entry);
            }
        }

        CancelQuietly(supersededCancel);
        RaiseChanged();
        return handle;
    }

    // Under _sync. The open of this demo that a queued visit waits for, if one is active and has not yet run
    // its passes.
    private Entry? OpenForLocked(string path) =>
        path.Length == 0
            ? null
            : _entries.FirstOrDefault(e =>
                e.Kind == QueueJobKind.DemoOpen && IsActive(e) && !e.Superseded && !e.PassesStarted && PathEquals(e.Path, path));

    // Under _sync. A visit waits while the open it is parked behind is active and has not run its passes; an
    // open that ended or was replaced releases it without a separate step.
    private static bool ParkedLocked(Entry e) =>
        e.ParkedBehind is { } open && IsActive(open) && !open.Superseded && !open.PassesStarted;

    // A pass that cannot say what it needs gets the widest read, which serves whatever it turns out to do.
    private static PassNeeds SafeNeeds(IDemoPass pass, VisitedDemo demo)
    {
        try
        {
            return pass.Needs(demo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
            return PassNeeds.RetainedParse;
        }
    }

    public IDemoQueueHandle SubmitJob(QueueJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == QueueJobKind.DemoProcessing)
        {
            throw new ArgumentException("A demo parse goes through SubmitBackground.", nameof(request));
        }

        Handle handle;
        CancellationTokenSource? preempted = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return new Handle(this, Guid.Empty, [request.OwnerTag], "", Task.CompletedTask, true);
            }

            Entry? existing = request.Key is null
                ? null
                : _entries.FirstOrDefault(e => e.Kind == request.Kind && e.State == DemoQueueItemState.Queued
                                               && string.Equals(e.Key, request.Key, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.DisplayName = request.Title;
                existing.Priority = (DemoJobPriority)Math.Max((int)existing.Priority, (int)request.Priority);
                existing.JobLevel = (PassLevel)Math.Max((int)existing.JobLevel, (int)JobLevelOf(request));
                existing.OrderHint = Math.Max(existing.OrderHint, request.OrderHint);
                if (request.ReplacePending)
                {
                    existing.Job = request.RunAsync;
                }

                preempted = PreemptForLocked(existing);
                PumpLocked();
                handle = new Handle(this, existing.Id, [request.OwnerTag], existing.Path, existing.Completion.Task, entry: existing);
            }
            else
            {
                Entry entry = SubmitJobLocked(request);
                preempted = PreemptForLocked(entry);
                handle = new Handle(this, entry.Id, [request.OwnerTag], entry.Path, entry.Completion.Task, entry: entry);
            }
        }

        CancelQuietly(preempted);
        RaiseChanged();
        return handle;
    }

    private static PassLevel JobLevelOf(QueueJobRequest request) =>
        request.Level ?? (request.Priority >= DemoJobPriority.UserRequested ? PassLevel.UserRequested : PassLevel.Background);

    private Entry SubmitJobLocked(QueueJobRequest request)
    {
        Entry entry = new()
        {
            Kind = request.Kind,
            ExtensionKind = request.ExtensionKind,
            Key = request.Key,
            Job = request.RunAsync,
            JobOwner = request.OwnerTag,
            Path = request.Target ?? "",
            DisplayName = request.Title,
            Priority = request.Priority,
            JobLevel = JobLevelOf(request),
            OrderHint = request.OrderHint,
            Seq = _seq++,
            Preemptible = request.Preemptible,
            Serial = request.Serial,
            ReplacePending = request.ReplacePending
        };
        _entries.Add(entry);
        PumpLocked();
        return entry;
    }

    // ── Removal ─────────────────────────────────────────────────────────

    public void RemoveByUser(Guid itemId)
    {
        bool freedQueueSlot = false;
        CancellationTokenSource? cancel = null;
        List<VisitPass> removed = [];
        lock (_sync)
        {
            Entry? e = _entries.FirstOrDefault(x => x.Id == itemId);
            if (e is null)
            {
                return;
            }

            if (e.Kind == QueueJobKind.DemoOpen)
            {
                cancel = e.Cancel;
            }

            if (e.State == DemoQueueItemState.Queued)
            {
                e.CancelRequested = true;
                if (e.Visit is { } visit)
                {
                    visit.RemoveWhere(p =>
                    {
                        removed.Add(p);
                        return true;
                    });
                }

                SetTerminalLocked(e, DemoQueueItemState.Cancelled, null);
                freedQueueSlot = e.Kind != QueueJobKind.HeapCompaction;
            }
            else if (e.State == DemoQueueItemState.Running)
            {
                // A retained parse is not abortable: FinishEntry discards its result and runs no
                // post-processing. A forward pass and a job are told through their token.
                e.CancelRequested = true;
                cancel = e.Cancel;
            }
        }

        EndPasses(removed, PassOutcome.Cancelled);
        CancelQuietly(cancel);
        RaiseChanged();
        if (freedQueueSlot)
        {
            RaiseCapacityAvailable(); // a queued slot opened → feeders may re-submit their backlog
            CompactIfDue();
        }
    }

    public void CancelOwned(string ownerTag, string path)
    {
        bool freedQueueSlot = false;
        CancellationTokenSource? cancel = null;
        List<VisitPass> removed = [];
        lock (_sync)
        {
            Entry? e = _entries.FirstOrDefault(x =>
                x.Kind == QueueJobKind.DemoProcessing && IsActive(x) && !x.Finalizing && PathEquals(x.Path, path));
            if (e is null)
            {
                return;
            }

            RemovePassesLocked(e, ownerTag, removed);
            if (e.Visit!.Count > 0 || e.ForegroundWaiters.Count > 0)
            {
                // A co-owner still wants it; only the owner chip changed.
            }
            else if (e.State == DemoQueueItemState.Queued)
            {
                SetTerminalLocked(e, DemoQueueItemState.Cancelled, null);
                freedQueueSlot = true;
            }
            else if (e.State == DemoQueueItemState.Running)
            {
                e.CancelRequested = true;
                cancel = e.Cancel;
            }
        }

        EndPasses(removed, PassOutcome.Cancelled);
        CancelQuietly(cancel);
        RaiseChanged();
        if (freedQueueSlot)
        {
            RaiseCapacityAvailable();
            CompactIfDue();
        }
    }

    public void CancelOwned(string ownerTag)
    {
        ArgumentNullException.ThrowIfNull(ownerTag);
        bool freedQueueSlot = false;
        bool changed = false;
        List<CancellationTokenSource> cancels = [];
        List<VisitPass> removed = [];
        lock (_sync)
        {
            foreach (Entry e in _entries)
            {
                if (!IsActive(e) || e.Finalizing || e.Kind == QueueJobKind.DemoOpen)
                {
                    continue;
                }

                if (e.Kind == QueueJobKind.DemoProcessing)
                {
                    if (RemovePassesLocked(e, ownerTag, removed) == 0)
                    {
                        continue;
                    }

                    changed = true;
                    if (e.Visit!.Count > 0 || e.ForegroundWaiters.Count > 0)
                    {
                        continue;
                    }
                }
                else if (!string.Equals(e.JobOwner, ownerTag, StringComparison.Ordinal))
                {
                    continue;
                }

                changed = true;
                e.CancelRequested = true;
                if (e.State == DemoQueueItemState.Queued)
                {
                    SetTerminalLocked(e, DemoQueueItemState.Cancelled, null);
                    freedQueueSlot |= e.Kind != QueueJobKind.HeapCompaction;
                }
                else if (e.Cancel is { } cancel)
                {
                    cancels.Add(cancel);
                }
            }
        }

        EndPasses(removed, PassOutcome.Cancelled);
        foreach (CancellationTokenSource cancel in cancels)
        {
            CancelQuietly(cancel);
        }

        if (!changed)
        {
            return;
        }

        RaiseChanged();
        if (freedQueueSlot)
        {
            RaiseCapacityAvailable();
            CompactIfDue();
        }
    }

    // Under _sync. Takes the owner's passes off the visit and collects them for the callbacks outside the lock.
    private static int RemovePassesLocked(Entry e, string ownerTag, List<VisitPass> removed) =>
        e.Visit!.RemoveWhere(p =>
        {
            if (!string.Equals(p.Pass.Id, ownerTag, StringComparison.Ordinal)
                && !string.Equals(p.Pass.Owner, ownerTag, StringComparison.Ordinal))
            {
                return false;
            }

            removed.Add(p);
            return true;
        });

    public void CancelPass(string path, IDemoPass pass)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pass);
        bool freedQueueSlot = false;
        CancellationTokenSource? cancel = null;
        List<VisitPass> removed = [];
        lock (_sync)
        {
            Entry? e = _entries.FirstOrDefault(x =>
                x.Kind == QueueJobKind.DemoProcessing && IsActive(x) && !x.Finalizing && PathEquals(x.Path, path)
                && x.Visit!.Contains(pass));
            if (e is null)
            {
                return;
            }

            e.Visit!.RemoveWhere(p =>
            {
                if (!ReferenceEquals(p.Pass, pass))
                {
                    return false;
                }

                removed.Add(p);
                return true;
            });
            if (e.Visit.Count > 0 || e.ForegroundWaiters.Count > 0)
            {
                // Another pass still wants the read.
            }
            else if (e.State == DemoQueueItemState.Queued)
            {
                SetTerminalLocked(e, DemoQueueItemState.Cancelled, null);
                freedQueueSlot = true;
            }
            else if (e.State == DemoQueueItemState.Running)
            {
                e.CancelRequested = true;
                cancel = e.Cancel;
            }
        }

        EndPasses(removed, PassOutcome.Cancelled);
        CancelQuietly(cancel);
        RaiseChanged();
        if (freedQueueSlot)
        {
            RaiseCapacityAvailable();
            CompactIfDue();
        }
    }

    public IReadOnlyList<DemoQueueItemSnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _entries.Select(e => e.Kind == QueueJobKind.DemoOpen && e.WaitingForSlot
                ? ToSnapshot(e) with { Detail = OpenWaitDetailLocked(e) }
                : ParkedLocked(e)
                    ? ToSnapshot(e) with { Detail = "Runs on the open of this demo" }
                    : ToSnapshot(e)).ToList();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelDeferredCompactLocked();
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
        if (ReferenceEquals(QueueWork.Ambient, this))
        {
            QueueWork.Ambient = null;
        }
    }

    // ── The pump + workers ───────────────────────────────────────────────

    // Ensures enough worker loops are alive: one per concurrently-runnable item, capped at
    // MaxConcurrency. A worker self-terminates when no work remains; the next submit/resume respawns.
    private void PumpLocked()
    {
        if (_disposed)
        {
            return;
        }

        // An open runs on its caller, not a worker; counting it would respawn idle workers forever.
        int running = _entries.Count(e => e.State == DemoQueueItemState.Running && !IsLight(e)
                                          && e.Kind != QueueJobKind.DemoOpen);
        // A job that gave its slot up keeps its worker busy without holding a slot: its own reads need another.
        int yielded = _entries.Count(e => e.State == DemoQueueItemState.Running && !IsLight(e) && e.SlotReleased);
        running -= yielded;
        int want = running;
        if (NextStartableLocked(false) is { } next)
        {
            want = next.Kind == QueueJobKind.DemoProcessing
                ? Math.Min(_maxConcurrency, running + _entries.Count(e => e.Kind == QueueJobKind.DemoProcessing && IsStartableLocked(e)))
                : 1;
        }

        want += yielded;
        while (_activeWorkers < want)
        {
            _activeWorkers++;
            _ = Task.Run(() => WorkerLoopAsync());
        }

        int lightRunning = _entries.Count(e => e.State == DemoQueueItemState.Running && IsLight(e));
        int wantLight = Math.Min(1 + MaxUserLight, lightRunning + (NextStartableLocked(true) is not null ? 1 : 0));
        while (_activeLightWorkers < wantLight)
        {
            _activeLightWorkers++;
            _ = Task.Run(() => LightWorkerLoopAsync());
        }
    }

    // The light lane: saves, loads, section builds and Team Identity commands. One at a time, beside the
    // heavy lane, with no heavy slot, so a store save or a section never waits behind a demo parse or a reel.
    private async Task LightWorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                Entry? entry;
                lock (_sync)
                {
                    entry = _disposed ? null : PickNextQueuedLocked(true);
                    PumpLocked(); // another user item may start beside this one
                }

                if (entry is null)
                {
                    return;
                }

                using SlotLease none = new(_gate, null, true);
                await RunJobAsync(entry, none).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_sync)
            {
                _activeLightWorkers--;
                PumpLocked();
            }
        }
    }

    // Light items a user is waiting on that may run at once; the pool gave a Dossier's four builds this.
    private const int MaxUserLight = 4;

    private bool IsLight(Entry e) => _jobKinds.IsLight(e.Kind, e.ExtensionKind);

    // Under _sync. A user's item stops the Background job or forward pass running in its lane; that item goes
    // back in the queue, first within its priority, so it runs again once the user's item is done. A retained
    // parse cannot be stopped (the parser takes no token), so the user's item simply runs next.
    private CancellationTokenSource? PreemptForLocked(Entry incoming)
    {
        if (incoming.Priority < DemoJobPriority.UserRequested || incoming.State != DemoQueueItemState.Queued)
        {
            return null;
        }

        bool light = IsLight(incoming);
        Entry? victim = _entries.FirstOrDefault(e =>
            e.State == DemoQueueItemState.Running && IsLight(e) == light && e.Priority == DemoJobPriority.Background
            && e.Preemptible && !e.Preempted && !e.CancelRequested && !e.Finalizing && e.Kind != QueueJobKind.HeapCompaction
            && (e.Kind != QueueJobKind.DemoProcessing || e.Forward));
        if (victim is null)
        {
            return null;
        }

        victim.Preempted = true;
        return victim.Cancel;
    }

    // Under _sync. Puts a preempted item back in the queue with the same id and an unfinished Completion.
    private void RequeueLocked(Entry entry)
    {
        entry.State = DemoQueueItemState.Queued;
        entry.Preempted = false;
        entry.Requeued = true;
        entry.Finalizing = false;
        entry.Cancel = null;
        if (entry.Kind == QueueJobKind.DemoProcessing)
        {
            // A forward read the open stopped comes back as the open's visit, not as a second read.
            entry.ParkedBehind = OpenForLocked(entry.Path);
        }

        PumpLocked();
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                bool user;
                lock (_sync)
                {
                    if (_disposed || NextStartableLocked(false) is not { } next)
                    {
                        return; // nothing to do → exit; respawned on next submit/resume/grow
                    }

                    user = next.Priority >= DemoJobPriority.UserRequested;
                }

                // Acquire a background slot (yields to interactive/reel; respects the hard cap). Between
                // demos the worker re-acquires, so it steps aside at each demo boundary, exactly like
                // the historical per-consumer loops. A user's item is not held back by an export session.
                using SlotLease slot = new(_gate, await _gate.AcquireBackgroundAsync(user, _shutdownToken).ConfigureAwait(false));

                Entry? entry;
                lock (_sync)
                {
                    entry = _disposed ? null : PickNextQueuedLocked(false, user && _gate.IsExportActive);
                }

                if (entry is null)
                {
                    continue; // work vanished / paused after the top check, re-evaluate, maybe exit
                }

                if (entry.Kind == QueueJobKind.DemoProcessing)
                {
                    RunEntry(entry);
                }
                else
                {
                    await RunJobAsync(entry, slot).ConfigureAwait(false);
                }

                // A compaction that failed must not queue the next one; the next job retries.
                if (entry.Kind != QueueJobKind.HeapCompaction)
                {
                    CompactIfDue();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown (token cancelled, or the CTS disposed at app exit): the durable backlogs keep
            // the work. Never surfaces as an unobserved task exception.
        }
        finally
        {
            lock (_sync)
            {
                _activeWorkers--;
                PumpLocked(); // work may have arrived during teardown
            }
        }
    }

    // Must stay synchronous: a local of the async worker loop is hoisted into its state machine, which
    // would root the ParsedDemo while the worker waits for the next slot.
    private void RunEntry(Entry entry)
    {
        if (TryHoldShellDemo(entry.Path) is { } held)
        {
            RunOnHeldParse(entry, held);
            return;
        }

        bool forward;
        lock (_sync)
        {
            // The visit's read is decided now, from every pass that joined, and fixed for later joiners.
            (forward, entry.Needs, entry.UserCommands) = entry.Visit!.ChooseMode(_forwardPass is not null);
            entry.Forward = forward;
        }

        if (forward)
        {
            RunForward(entry);
            return;
        }

        ParsedDemo? parsed = null;
        Exception? failure = null;
        DecodePlan plan;
        // The parser takes no token; the passes do, so a remove during their turn reaches them.
        CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        lock (_sync)
        {
            plan = entry.UserCommands ? DecodePlan.Everything : WithoutUserCommands;
            entry.Cancel = cancel;
            if (entry.CancelRequested)
            {
                cancel.Cancel();
            }
        }

        try
        {
            NoteParse(entry.Path, "retained");
            parsed = _parseFile(entry.Path, plan);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool didParse = parsed is not null;
        FinishEntry(entry, parsed, failure, cancel.Token); // runs passes OUTSIDE _sync, still inside the slot
        lock (_sync)
        {
            entry.Cancel = null;
        }

        cancel.Dispose();

        lock (_sync)
        {
            _jobsSinceCompact++;
            if (didParse)
            {
                _parsesSinceCompact++;
            }
        }
    }

    private IHeldParse? TryHoldShellDemo(string path)
    {
        if (ShellDemo is not { } shell || path.Length == 0)
        {
            return null;
        }

        try
        {
            return shell.TryHold(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
            return null;
        }
    }

    // The shell holds this demo's parse, decoded with every category: the passes read it and no file is
    // opened. The hold keeps the shell from releasing the parse until they are done. Synchronous like RunEntry.
    private void RunOnHeldParse(Entry entry, IHeldParse held)
    {
        CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        lock (_sync)
        {
            entry.Forward = false;
            entry.UserCommands = true;
            entry.Detail = "On the open demo's parse";
            entry.Cancel = cancel;
            if (entry.CancelRequested)
            {
                cancel.Cancel();
            }
        }

        RaiseChanged();
        try
        {
            FinishEntry(entry, held.Parsed, null, cancel.Token);
        }
        finally
        {
            held.Dispose();
            lock (_sync)
            {
                entry.Cancel = null;
                _jobsSinceCompact++;
            }

            cancel.Dispose();
        }
    }

    // The open's visit: every pass queued behind the open runs on the parse in hand, from a worker, while
    // the open item is still active and keeps other heavy starts off. No file is read.
    private Task RunOpenPassesAsync(Entry open, ParsedDemo parsed, Action? plan, CancellationToken ct) =>
        Task.Run(() => RunOpenPasses(open, parsed, plan, ct), CancellationToken.None);

    private void RunOpenPasses(Entry open, ParsedDemo parsed, Action? plan, CancellationToken ct)
    {
        if (plan is not null)
        {
            SafeInvoke(plan);
        }

        List<Entry> visits;
        bool cancelled;
        DemoVisit merged = new(new VisitedDemo(open.Path), PassLevel.OpenDemo);
        lock (_sync)
        {
            open.PassesStarted = true;
            cancelled = ct.IsCancellationRequested || !IsActive(open);
            visits = cancelled
                ? []
                : _entries.Where(e => e.Kind == QueueJobKind.DemoProcessing && e.State == DemoQueueItemState.Queued
                                      && ReferenceEquals(e.ParkedBehind, open)).ToList();
            foreach (Entry visit in visits)
            {
                visit.State = DemoQueueItemState.Running;
                visit.Finalizing = true;
                visit.Detail = "On the open demo's parse";
                merged.AddAll(visit.Visit!);
            }

            // An open that ended first leaves its visits to run on their own read.
            PumpLocked();
        }

        RaiseChanged();
        if (!cancelled)
        {
            foreach (VisitPass p in merged.Ordered())
            {
                RunPass(p, new PassInput(merged.Demo, PassLevel.OpenDemo, parsed, null, ct));
            }
        }

        if (_parseReleased is { } released)
        {
            SafeInvoke(() => released(parsed));
        }

        foreach (Entry visit in visits)
        {
            SetTerminal(visit, DemoQueueItemState.Completed, null);
        }
    }

    // A forward pass holds no frames, so no ParsedDemo can leak through a local here; kept synchronous
    // like RunEntry so the slot is held for the whole read.
    private void RunForward(Entry entry)
    {
        CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        ForwardNeeds needs;
        lock (_sync)
        {
            entry.Cancel = cancel;
            needs = entry.Needs;
            if (entry.CancelRequested || entry.Preempted)
            {
                cancel.Cancel();
            }
        }

        ForwardDemoResult? pass = null;
        Exception? failure = null;
        bool cancelled = false;
        int reported = -1;
        try
        {
            NoteParse(entry.Path, "forward");
            pass = _forwardPass!(entry.Path, needs, fraction =>
            {
                int percent = (int)(fraction * 100);
                if (percent != reported)
                {
                    reported = percent;
                    ReportProgress(entry, percent, 100, null);
                }
            }, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The source stays on the entry through the passes, so a remove during their turn still reaches them.
        FinishForward(entry, pass, failure, cancelled, cancel.Token);
        lock (_sync)
        {
            entry.Cancel = null;
        }

        cancel.Dispose();

        lock (_sync)
        {
            _jobsSinceCompact++;
            if (!entry.Superseded)
            {
                _parsesSinceCompact++;
            }
        }
    }

    // A cancelled read calls no pass: OnFailed would mark a demo that is fine as failed.
    private void FinishForward(Entry entry, ForwardDemoResult? pass, Exception? failure, bool cancelled, CancellationToken token)
    {
        IReadOnlyList<VisitPass> passes;
        VisitedDemo demo;
        PassLevel level;
        lock (_sync)
        {
            demo = entry.Visit!.Demo;
            level = entry.Visit.Level;
            if (cancelled && entry.Preempted && !entry.CancelRequested)
            {
                RequeueLocked(entry);
                passes = [];
            }
            else
            {
                entry.Finalizing = true;
                cancelled |= entry.CancelRequested;
                passes = entry.Visit.Ordered();
            }
        }

        if (entry.Requeued && entry.State == DemoQueueItemState.Queued)
        {
            RaiseChanged();
            return;
        }

        if (cancelled)
        {
            EndPasses(passes, PassOutcome.Cancelled);
            SetTerminal(entry, DemoQueueItemState.Cancelled, null);
            return;
        }

        if (failure is not null)
        {
            FailPasses(passes, demo, failure);
            SetTerminal(entry, DemoQueueItemState.Failed, failure.Message);
            return;
        }

        foreach (VisitPass p in passes)
        {
            RunPass(p, new PassInput(demo, level, null, pass!, token));
        }

        SetTerminal(entry, DemoQueueItemState.Completed, null);
    }

    // Asks the pass again right before its turn: an upstream pass on the same visit may have done its work,
    // or written what it was waiting for. A pass still waiting on upstream after upstream ran sits out.
    private static void RunPass(VisitPass p, PassInput input)
    {
        PassInterest interest;
        try
        {
            interest = p.Pass.Interest(input.Demo, input.Level);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
            EndPass(p, PassOutcome.Failed, ex);
            return;
        }

        switch (interest)
        {
            case PassInterest.No:
                EndPass(p, PassOutcome.Skipped, null);
                return;
            case PassInterest.IfUpstreamRuns:
                AppLog.PassStillWaitingOnUpstream(DiagLog, p.Pass.Id, input.Demo.FileName);
                EndPass(p, PassOutcome.Skipped, null);
                return;
        }

        try
        {
            p.Pass.Run(input);
        }
        catch (OperationCanceledException ex)
        {
            // A stopped pass is requeued or abandoned by whoever stopped it, not broken.
            EndPass(p, PassOutcome.Cancelled, ex);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
            EndPass(p, PassOutcome.Failed, ex);
            return;
        }

        EndPass(p, PassOutcome.Ran, null);
    }

    private static void FailPasses(IReadOnlyList<VisitPass> passes, VisitedDemo demo, Exception failure)
    {
        foreach (VisitPass p in passes)
        {
            SafeInvoke(() => p.Pass.OnFailed(demo, failure));
            EndPass(p, PassOutcome.ParseFailed, failure);
        }
    }

    private static void EndPasses(IReadOnlyList<VisitPass> passes, PassOutcome outcome)
    {
        foreach (VisitPass p in passes)
        {
            EndPass(p, outcome, null);
        }
    }

    // The submitter's callback must never break the slot either.
    private static void EndPass(VisitPass p, PassOutcome outcome, Exception? error)
    {
        try
        {
            p.Ended?.Invoke(p.Pass, outcome, error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
        }
    }

    private async Task RunJobAsync(Entry entry, SlotLease slot)
    {
        CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        Func<IQueueJobContext, Task>? job;
        string title;
        lock (_sync)
        {
            title = entry.DisplayName ?? "";
            entry.Cancel = cancel;
            job = entry.Job;
            if (entry.CancelRequested || entry.Preempted)
            {
                cancel.Cancel();
            }
        }

        DemoQueueItemState state = DemoQueueItemState.Completed;
        string? error = null;
        try
        {
            if (job is not null)
            {
                await job(new JobContext(this, entry, slot, cancel.Token)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            state = DemoQueueItemState.Cancelled;
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, title, ex);
            state = DemoQueueItemState.Failed;
            error = ex.Message;
        }

        bool requeued = false;
        lock (_sync)
        {
            if (state == DemoQueueItemState.Completed && entry.CancelRequested)
            {
                state = DemoQueueItemState.Cancelled;
            }

            entry.Cancel = null;
            if (entry.Kind != QueueJobKind.HeapCompaction && !IsLight(entry))
            {
                _jobsSinceCompact++;
                if (entry.ParsedDemo)
                {
                    _parsesSinceCompact++;
                }
            }

            // Stopped for a user's item, not by anyone asking it to stop: it runs again later. A job that
            // returned normally despite the stop is taken at its word and completes.
            if (state == DemoQueueItemState.Cancelled && entry.Preempted && !entry.CancelRequested)
            {
                // A newer build of the same key is already waiting and replaces this one's work.
                bool replaced = entry.ReplacePending && entry.Key is not null && _entries.Any(e =>
                    e.State == DemoQueueItemState.Queued && e.Kind == entry.Kind
                    && string.Equals(e.Key, entry.Key, StringComparison.Ordinal));
                if (!replaced)
                {
                    RequeueLocked(entry);
                    requeued = true;
                }
            }
        }

        cancel.Dispose();
        if (requeued)
        {
            RaiseChanged();
            return;
        }

        SetTerminal(entry, state, error);
    }

    // Never call from inside RunEntry or FinishEntry: the finished demo must be off every stack frame.
    // After a parse: at once, ahead of the next queued item. Otherwise only on a drain, at most once per
    // MinDrainCompactInterval; a drain inside the window schedules one compaction, a job start cancels it.
    private void CompactIfDue(long? deferredGeneration = null)
    {
        DateTimeOffset startedAt;
        lock (_sync)
        {
            bool deferredDue = deferredGeneration is not null;
            if (deferredDue)
            {
                // A callback that lost the race with a cancel or a reschedule is stale.
                if (_deferredCompact is null || deferredGeneration != _deferredGeneration)
                {
                    return;
                }

                CancelDeferredCompactLocked();
            }

            if (_disposed || _compacting || _jobsSinceCompact == 0)
            {
                return;
            }

            bool afterParse = _parsesSinceCompact > 0;
            if (!afterParse && _entries.Any(e => IsActive(e) && e.Kind != QueueJobKind.HeapCompaction))
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            if (!afterParse && !deferredDue && _lastCompact is { } last && now - last < MinDrainCompactInterval)
            {
                if (_deferredCompact is null)
                {
                    long generation = ++_deferredGeneration;
                    _deferredCompact = _time.CreateTimer(_ => CompactIfDue(generation), null,
                        last + MinDrainCompactInterval - now, Timeout.InfiniteTimeSpan);
                }

                return;
            }

            CancelDeferredCompactLocked();
            _compacting = true;
            startedAt = now;
            SubmitJobLocked(new QueueJobRequest(QueueJobKind.HeapCompaction, "Heap compaction", "queue",
                DemoJobPriority.Background, _ => CompactAsync(startedAt)));
        }

        RaiseChanged();
    }

    private void CancelDeferredCompactLocked()
    {
        _deferredCompact?.Dispose();
        _deferredCompact = null;
    }

    // The throttle is recorded only on success, so a failed compaction leaves the next drain free to run.
    // _compacting is cleared when the item goes terminal, which also covers a cancel before it ran.
    // Counts are taken when it starts: it runs alone, so every job counted by then has finished.
    private async Task CompactAsync(DateTimeOffset startedAt)
    {
        int jobs, parses;
        lock (_sync)
        {
            jobs = _jobsSinceCompact;
            parses = _parsesSinceCompact;
        }

        await _compactHeap().ConfigureAwait(false);
        lock (_sync)
        {
            _jobsSinceCompact -= jobs;
            _parsesSinceCompact -= parses;
            _lastCompact = startedAt;
        }
    }

    // Highest priority, then kind (demo parses first, so later kinds see a complete index), then newest
    // OrderHint, then FIFO seq; null when that item may not start yet. Demo parses may run side by side up to
    // MaxConcurrency, but any other job runs exclusively: it starts only on an idle queue, and nothing starts
    // while it runs, even after it hands its slot back.
    private Entry? NextStartableLocked(bool light)
    {
        string? resident = LoadedPathLocked();
        Entry? best = null;
        bool anyRunning = false, jobRunning = false, opening = false;
        int userRunning = 0;
        HashSet<string>? yielding = null;
        foreach (Entry e in _entries)
        {
            if (IsLight(e) != light)
            {
                continue;
            }

            if (e.Kind == QueueJobKind.DemoOpen)
            {
                opening |= IsActive(e);
                continue;
            }

            if (e.State == DemoQueueItemState.Running)
            {
                anyRunning = true;
                userRunning += e.Priority >= DemoJobPriority.UserRequested ? 1 : 0;
                jobRunning |= e.Kind != QueueJobKind.DemoProcessing;
                if (e.SlotReleased && e.JobOwner is { } owner)
                {
                    (yielding ??= new HashSet<string>(StringComparer.Ordinal)).Add(owner);
                }
            }
            else if (IsStartableLocked(e) && (best is null || Compare(e, best, resident) < 0))
            {
                best = e;
            }
        }

        if (light)
        {
            // Background light items run one at a time. Items a user is waiting on run beside them, up to
            // MaxUserLight at once: a save cannot stop mid-write, and a Dossier team builds four sections.
            return !anyRunning || best is { Priority: >= DemoJobPriority.UserRequested } && userRunning < MaxUserLight ? best : null;
        }

        if (jobRunning && !opening && yielding is not null)
        {
            return OwnReadLocked(yielding, resident);
        }

        return best is null || opening || jobRunning || (best.Kind != QueueJobKind.DemoProcessing && anyRunning) ? null : best;
    }

    // Under _sync. A job that gave its slot up still holds the lane, except for the demos it reads itself: its
    // own visits (a pack export's clips) start beside it, one heavy read at a time as ever.
    private Entry? OwnReadLocked(HashSet<string> owners, string? resident)
    {
        if (_entries.Any(e => e.State == DemoQueueItemState.Running && e.Kind == QueueJobKind.DemoProcessing && !IsLight(e)))
        {
            return null;
        }

        Entry? best = null;
        foreach (Entry e in _entries)
        {
            if (e.Kind == QueueJobKind.DemoProcessing && IsStartableLocked(e)
                && e.Visit!.HasJobOwnedBy(owners) && (best is null || Compare(e, best, resident) < 0))
            {
                best = e;
            }
        }

        return best;
    }

    // Marks the next startable item Running under the lock.
    private Entry? PickNextQueuedLocked(bool light, bool userOnly = false)
    {
        Entry? best = NextStartableLocked(light);
        if (best is not null && userOnly && best.Priority < DemoJobPriority.UserRequested)
        {
            best = null;
        }

        if (best is not null)
        {
            best.State = DemoQueueItemState.Running;
            if (!light && best.Path.Length > 0)
            {
                _lastStartedPath = best.Path;
            }

            CancelDeferredCompactLocked();
        }

        return best;
    }

    private void FinishEntry(Entry entry, ParsedDemo? parsed, Exception? failure, CancellationToken token)
    {
        bool cancelled;
        IReadOnlyList<VisitPass> passes;
        VisitedDemo demo;
        PassLevel level;
        List<TaskCompletionSource<ParsedDemo>> foreground;
        lock (_sync)
        {
            // Close the entry to further coalescing ATOMICALLY with capturing the pass snapshot: any
            // waiter or pass added after this point (during the run window below) would never be
            // signalled. Late callers coalesce onto nothing and start their own work instead.
            entry.Finalizing = true;
            cancelled = entry.CancelRequested;
            demo = entry.Visit!.Demo;
            level = entry.Visit.Level;
            passes = entry.Visit.Ordered();
            foreground = [.. entry.ForegroundWaiters];
        }

        if (failure is not null)
        {
            foreach (TaskCompletionSource<ParsedDemo> w in foreground)
            {
                w.TrySetException(failure);
            }

            FailPasses(passes, demo, failure);
            SetTerminal(entry, DemoQueueItemState.Failed, failure.Message);
            return;
        }

        // Success. Satisfy foreground waiters FIRST (responsiveness: they must not wait behind the
        // heavy background post-processing), THEN run each pass inside the slot, in After order.
        foreach (TaskCompletionSource<ParsedDemo> w in foreground)
        {
            w.TrySetResult(parsed!);
        }

        if (cancelled)
        {
            EndPasses(passes, PassOutcome.Cancelled);
        }
        else
        {
            foreach (VisitPass p in passes)
            {
                RunPass(p, new PassInput(demo, level, parsed!, null, token));
            }
        }

        if (_parseReleased is { } released)
        {
            SafeInvoke(() => released(parsed!));
        }

        SetTerminal(entry, cancelled ? DemoQueueItemState.Cancelled : DemoQueueItemState.Completed, null);
    }

    private void SetTerminal(Entry entry, DemoQueueItemState state, string? error)
    {
        lock (_sync)
        {
            SetTerminalLocked(entry, state, error);
        }

        RaiseChanged();
        RaiseCapacityAvailable(); // a background slot freed → feeders top up
    }

    private void SetTerminalLocked(Entry entry, DemoQueueItemState state, string? error)
    {
        entry.State = state;
        entry.Error = error;
        entry.Completion.TrySetResult();
        // A waiter's task holds the ParsedDemo, a job's body its closure; history must not keep either.
        entry.ForegroundWaiters.Clear();
        entry.Job = null;
        if (IsLight(entry) && state == DemoQueueItemState.Completed)
        {
            _entries.Remove(entry); // a finished save or section build is not history worth showing
        }

        if (entry.Kind == QueueJobKind.HeapCompaction)
        {
            _compacting = false;
            if (state == DemoQueueItemState.Completed)
            {
                _entries.Remove(entry); // one per drain; only a failure is worth keeping in the list
            }
        }

        PruneTerminalHistoryLocked();
        PumpLocked(); // an exclusive job ending may let several demo parses start
    }

    // Keep the mirror bounded: drop the oldest terminal entries beyond the history cap.
    private void PruneTerminalHistoryLocked()
    {
        List<Entry> terminal = _entries.Where(e => !IsActive(e)).OrderBy(e => e.Seq).ToList();
        int excess = terminal.Count - TerminalHistoryCap;
        for (int i = 0; i < excess; i++)
        {
            _entries.Remove(terminal[i]);
        }
    }

    // ── Handle ────────────────────────────────────────────────────────────────

    private Handle RejectedHandle(string path, IReadOnlyList<string> owners)
    {
        TaskCompletionSource done = new();
        done.SetResult();
        return new Handle(this, Guid.Empty, owners, path, done.Task, true);
    }

    private DemoQueueItemState StateOf(Entry entry)
    {
        lock (_sync)
        {
            return entry.State;
        }
    }

    private DemoQueueItemState GetState(Guid id)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Id == id)?.State ?? DemoQueueItemState.Cancelled;
        }
    }

    // ── UI mirror reconcile (posted) ──────────────────────────────────────────

    // One pending UI update covers every change made before it runs, since it reads the state when it
    // runs. A burst of section builds would otherwise post a mirror reconcile and a Changed per step.
    private int _changePending;

    private void RaiseChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
        {
            return;
        }

        _post(() =>
        {
            Interlocked.Exchange(ref _changePending, 0);
            Reconcile();
            Changed?.Invoke();
        });
    }

    private void RaiseCapacityAvailable()
    {
        bool hasRoom;
        lock (_sync)
        {
            hasRoom = BackgroundTierCountLocked() < _maxQueueSize;
        }

        if (hasRoom)
        {
            _post(() => CapacityAvailable?.Invoke());
        }
    }

    // Reconcile the bound mirror to the current snapshot by id (create/update/remove) so item identity
    // and selection survive. Runs on the post thread; guarded so concurrent inline posts (tests) are safe.
    private void Reconcile()
    {
        // Snapshot INSIDE the posted action, not before it. Posts run FIFO on the UI thread, so
        // taking the snapshot here makes the LAST-enqueued reconcile read the LATEST state:
        // capturing before the post let two concurrent RaiseChanged calls enqueue in one order
        // while their older/newer snapshots landed in the reverse, leaving the mirror stale.
        IReadOnlyList<DemoQueueItemSnapshot> snapshot = Snapshot();
        lock (_items)
        {
            Dictionary<Guid, DemoQueueItemSnapshot> wanted = snapshot.ToDictionary(s => s.Id);
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!wanted.ContainsKey(_items[i].Id))
                {
                    _items.RemoveAt(i);
                }
            }

            Dictionary<Guid, DemoQueueItem> present = _items.ToDictionary(x => x.Id);
            for (int index = 0; index < snapshot.Count; index++)
            {
                DemoQueueItemSnapshot s = snapshot[index];
                if (present.TryGetValue(s.Id, out DemoQueueItem? item))
                {
                    item.DisplayName = s.DisplayName;
                    item.Owners = string.Join(", ", s.Owners);
                    item.Priority = s.Priority;
                    item.State = s.State;
                    item.Error = s.Error;
                    item.Progress = s.Progress;
                    item.Detail = s.Detail;
                }
                else
                {
                    _items.Insert(Math.Min(index, _items.Count), new DemoQueueItem
                    {
                        Id = s.Id,
                        Path = s.Path,
                        DisplayName = s.DisplayName,
                        Owners = string.Join(", ", s.Owners),
                        Priority = s.Priority,
                        State = s.State,
                        Error = s.Error,
                        Kind = s.Kind,
                        ExtensionKind = s.ExtensionKind,
                        Progress = s.Progress,
                        Detail = s.Detail
                    });
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsActive(Entry e) =>
        e.State is DemoQueueItemState.Queued or DemoQueueItemState.Running;

    // The size cap is for demo parses; jobs are few and keyed.
    private int BackgroundTierCountLocked() =>
        _entries.Count(e => e.Kind == QueueJobKind.DemoProcessing && IsActive(e));

    // Pause stops every start; the disable switch stops only Background-priority work.
    private bool IsStartableLocked(Entry e) =>
        e.State == DemoQueueItemState.Queued
        && !ParkedLocked(e)
        && (!_paused || e.Priority >= DemoJobPriority.UserRequested)
        && (_backgroundEnabled || e.Priority >= DemoJobPriority.UserRequested || e.Kind == QueueJobKind.HeapCompaction
            || IsLight(e) || e.Kind == QueueJobKind.LibraryScan)
        && !BlockedLocked(e);

    // A keyed item waits while one with its key runs (a same-key submit then reruns once, never beside
    // it), and items sharing a serial never run together.
    private bool BlockedLocked(Entry e)
    {
        if (e.Key is null && e.Serial is null)
        {
            return false;
        }

        foreach (Entry r in _entries)
        {
            if (r.State == DemoQueueItemState.Running && !ReferenceEquals(r, e)
                && ((e.Key is not null && r.Kind == e.Kind && string.Equals(r.Key, e.Key, StringComparison.Ordinal))
                    || (e.Serial is not null && string.Equals(r.Serial, e.Serial, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }

    // Under _sync. The shell's loaded demo, whose work runs on the parse it already holds.
    private string? LoadedPathLocked()
    {
        try
        {
            return ShellDemo?.LoadedPath;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
            return null;
        }
    }

    // Negative when a runs before b. A compaction goes first: it is due now, and it holds _compacting. Then the
    // level (a user's request, the open demo's passes, the backlog, background), then a demo already in memory,
    // then the demo the last heavy item was about, so one demo's work runs back to back, then the kind's rank,
    // the order hint and arrival.
    private int Compare(Entry a, Entry b, string? resident)
    {
        bool aCompacts = a.Kind == QueueJobKind.HeapCompaction, bCompacts = b.Kind == QueueJobKind.HeapCompaction;
        if (aCompacts != bCompacts)
        {
            return aCompacts ? -1 : 1;
        }

        // Takes the place of the forward pass it stopped, which was already running.
        if (a.Front != b.Front)
        {
            return a.Front ? -1 : 1;
        }

        if (a.Priority != b.Priority)
        {
            return b.Priority.CompareTo(a.Priority);
        }

        if (a.Level != b.Level)
        {
            return b.Level.CompareTo(a.Level);
        }

        // A preempted item resumes before anything else of its priority.
        if (a.Requeued != b.Requeued)
        {
            return a.Requeued ? -1 : 1;
        }

        int byResident = SamePathFirst(a, b, resident);
        if (byResident != 0)
        {
            return byResident;
        }

        int byGroup = SamePathFirst(a, b, _lastStartedPath);
        if (byGroup != 0)
        {
            return byGroup;
        }

        int rank = _jobKinds.Rank(a.Kind, a.ExtensionKind).CompareTo(_jobKinds.Rank(b.Kind, b.ExtensionKind));
        if (rank != 0)
        {
            return rank;
        }

        return a.OrderHint != b.OrderHint ? b.OrderHint.CompareTo(a.OrderHint) : a.Seq.CompareTo(b.Seq);
    }

    private static int SamePathFirst(Entry a, Entry b, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        bool aMatches = PathEquals(a.Path, path), bMatches = PathEquals(b.Path, path);
        return aMatches == bMatches ? 0 : aMatches ? -1 : 1;
    }

    private static void CancelQuietly(CancellationTokenSource? cancel)
    {
        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished between the lookup and the cancel.
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DemoQueueItemSnapshot ToSnapshot(Entry e) => new(
        e.Id, e.Path, e.DisplayName,
        e.JobOwner is { } owner ? [owner] : e.Visit?.OwnerIds ?? [],
        e.Priority, e.State, e.Error, e.Kind, e.Progress, e.Detail, e.ExtensionKind);

    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // A single owner's post-processing failure never breaks the parse or another owner's
            // handler (mirrors the Tier2DemoParsed piggyback isolation). Diagnostics pillar, not
            // Console (v0.6.0): Console is invisible in a windowed Release build.
            AppLog.QueueOwnerHandlerFailed(DiagLog, ex);
        }
    }

    private sealed class Handle(
        DemoProcessingQueue queue,
        Guid id,
        IReadOnlyList<string> owners,
        string path,
        Task completion,
        bool rejected = false,
        Entry? entry = null) : IDemoQueueHandle
    {
        public Guid Id => id;
        public Task Completion => completion;

        // Read from the entry itself: a finished light item leaves the list, and is still completed.
        public DemoQueueItemState State => rejected ? DemoQueueItemState.Rejected
            : entry is null ? queue.GetState(id) : queue.StateOf(entry);
        public void Cancel()
        {
            if (queue.KindOf(id) is { } kind && kind != QueueJobKind.DemoProcessing)
            {
                queue.RemoveByUser(id);
                return;
            }

            foreach (string owner in owners)
            {
                queue.CancelOwned(owner, path);
            }
        }
    }

    // A pre-pass owner's callbacks as one pass: always interested, no order constraints of its own.
    private sealed class CallbackPass(DemoProcessingRequest request) : IDemoPass
    {
        public string Id => request.OwnerTag;

        public IReadOnlyList<string> After => [];

        public PassNeeds Needs(VisitedDemo demo) => request.OnForward is null
            ? new PassNeeds(ParseMode.Retained, ForwardNeeds.None, request.NeedsUserCommands)
            : new PassNeeds(ParseMode.Forward, request.ForwardNeeds, request.NeedsUserCommands);

        public PassInterest Interest(VisitedDemo demo, PassLevel level) => PassInterest.Yes;

        public void Run(PassInput input)
        {
            if (input.Forward is { } forward)
            {
                request.OnForward!(forward);
            }
            else
            {
                request.OnParsed(input.Retained!);
            }
        }

        public void OnFailed(VisitedDemo demo, Exception failure) => request.OnFailed?.Invoke(failure);
    }

    private QueueJobKind? KindOf(Guid id)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Id == id)?.Kind;
        }
    }

    private void NoteSlotReleased(Entry entry)
    {
        lock (_sync)
        {
            entry.SlotReleased = true;
            PumpLocked();
        }
    }

    private void NoteDemoParsed(Entry entry)
    {
        lock (_sync)
        {
            entry.ParsedDemo = true;
        }
    }

    private void ReportProgress(Entry entry, int done, int total, string? detail)
    {
        lock (_sync)
        {
            entry.Progress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : null;
            entry.Detail = detail;
        }

        RaiseChanged();
    }

    // The worker's slot, which a job may hand back for a moment or for good.
    private sealed class SlotLease(HeavyJobGate gate, IDisposable? held, bool light = false) : IDisposable
    {
        private IDisposable? _held = held;

        public void Dispose() => Release();

        public void Release() => Interlocked.Exchange(ref _held, null)?.Dispose();

        public async Task RetakeAsync(CancellationToken ct)
        {
            if (light)
            {
                return; // the light lane holds no slot to hand back
            }

            Release();
            _held = await gate.AcquireBackgroundAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class JobContext(DemoProcessingQueue queue, Entry entry, SlotLease slot, CancellationToken ct)
        : IQueueJobContext
    {
        private bool _released;

        public CancellationToken CancellationToken => ct;

        public void Report(int done, int total, string? detail = null) =>
            queue.ReportProgress(entry, done, total, detail);

        public Task StepAsideAsync() => _released ? Task.CompletedTask : slot.RetakeAsync(ct);

        public void ReleaseSlot()
        {
            _released = true;
            slot.Release();
            queue.NoteSlotReleased(entry);
        }

        public void NoteDemoParsed() => queue.NoteDemoParsed(entry);
    }

    private sealed class Entry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public QueueJobKind Kind { get; init; }
        public string? ExtensionKind { get; init; }
        public string? Key { get; init; }
        public string? JobOwner { get; init; }
        public Func<IQueueJobContext, Task>? Job { get; set; }
        public CancellationTokenSource? Cancel { get; set; }
        public double? Progress { get; set; }
        public string? Detail { get; set; }
        public required string Path { get; init; }
        public string? DisplayName { get; set; }
        public DemoJobPriority Priority { get; set; }

        // A job's place among items of its priority; a visit takes its own level.
        public PassLevel JobLevel { get; set; }

        public PassLevel Level => Visit?.Level ?? JobLevel;
        public long OrderHint { get; set; }
        public long Seq { get; init; }
        public DemoQueueItemState State { get; set; } = DemoQueueItemState.Queued;
        public string? Error { get; set; }
        public bool CancelRequested { get; set; }

        // Stopped for a user's item; it goes back in the queue instead of ending.
        public bool Preempted { get; set; }

        // Came back from a preemption; runs first within its priority.
        public bool Requeued { get; set; }

        public bool Preemptible { get; init; } = true;

        public string? Serial { get; init; }

        public bool ReplacePending { get; init; }

        // Whether this parse decodes user commands; fixed once it runs.
        public bool UserCommands { get; set; } = true;

        // Read forward instead of retained; fixed once it runs.
        public bool Forward { get; set; }

        // What a forward pass produces: the union of the owners' needs.
        public ForwardNeeds Needs { get; set; }

        // Stopped because an owner it could not serve arrived; its owners moved to the Front entry.
        public bool Superseded { get; set; }

        // Replaces a superseded forward pass and runs before any other demo.
        public bool Front { get; init; }

        // An open waiting for the interactive slot; its detail names what holds it.
        public bool WaitingForSlot { get; set; }

        // An open parsing under the interactive slot.
        public bool ParsingOpen { get; set; }

        // The file an open is for.
        public string? FileName { get; init; }

        // A job body reported a demo parse through its context.
        public bool ParsedDemo { get; set; }

        // A running job handed its heavy slot back; its own demo reads may start beside it.
        public bool SlotReleased { get; set; }

        // Set under _sync the instant FinishEntry captures its waiter/pass snapshot, BEFORE it
        // releases the lock to run the (multi-second) passes. The entry stays Running across that
        // window, so without this a foreground/background caller could coalesce onto it and append a
        // waiter AFTER the snapshot, which FinishEntry never re-reads, orphaning it forever (the
        // critical FinishEntry TOCTOU). Finalizing excludes the entry from all coalescing.
        public bool Finalizing { get; set; }

        // The demo's visit: the passes joined so far and the level. Set for every DemoProcessing entry.
        public DemoVisit? Visit { get; init; }
        public List<TaskCompletionSource<ParsedDemo>> ForegroundWaiters { get; } = [];

        // A queued visit of the demo an open is for: it does not start on its own, the open runs its passes
        // on the shell's parse. Cleared when that open ends without running them.
        public Entry? ParkedBehind { get; set; }

        // An open whose pass run has taken its parked visits; a visit submitted after this queues normally.
        public bool PassesStarted { get; set; }

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

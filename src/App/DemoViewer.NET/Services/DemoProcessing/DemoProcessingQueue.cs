#region

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
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
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _shutdown = new();

    // Captured once so a worker never touches _shutdown.Token AFTER Dispose disposes the source (which
    // would throw ObjectDisposedException). The captured struct stays valid post-dispose.
    private readonly CancellationToken _shutdownToken;
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private int _activeWorkers;
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
    /// <param name="forwardPass">
    ///     The forward read for an entry whose every owner can take one. Null keeps every entry on the
    ///     retained parse.
    /// </param>
    public DemoProcessingQueue(
        HeavyJobGate gate,
        Action<Action>? post = null,
        Func<string, ParsedDemo>? parseFile = null,
        Func<ReadOnlyMemory<byte>, ParsedDemo>? parseBytes = null,
        Func<Task>? compactHeap = null,
        TimeProvider? timeProvider = null,
        Func<string, DecodePlan, ParsedDemo>? parseFileWithPlan = null,
        Func<string, ForwardNeeds, Action<double>, CancellationToken, ForwardDemoResult>? forwardPass = null)
    {
        _gate = gate;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _parseFile = parseFileWithPlan ?? (parseFile is null ? ParseFileDefault : (path, _) => parseFile(path));
        _parseBytes = parseBytes ?? (bytes => DemoParser.Parse(bytes));
        _forwardPass = forwardPass;
        _compactHeap = compactHeap ?? HeapCompactor.CompactAsync;
        _time = timeProvider ?? TimeProvider.System;
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
                Entry? running = _entries.FirstOrDefault(e =>
                    e.Kind == QueueJobKind.DemoProcessing && e.State == DemoQueueItemState.Running && !e.Finalizing
                    && !e.Forward && e.UserCommands && PathEquals(e.Path, path));
                if (running is not null)
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
            return await Task.Run(() => _parseBytes(bytes), cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Background submit + coalescing ───────────────────────────────────

    public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Handle handle;
        lock (_sync)
        {
            if (_disposed)
            {
                return RejectedHandle(request);
            }

            // A running parse without user commands cannot serve an owner that reads them, and a running
            // forward pass serves only forward owners whose needs it already covers.
            Entry? existing = _entries.FirstOrDefault(e =>
                e.Kind == QueueJobKind.DemoProcessing && IsActive(e) && !e.Finalizing && PathEquals(e.Path, request.Path)
                && (e.State == DemoQueueItemState.Queued
                    || (e.Forward
                        ? request.OnForward is not null && (request.ForwardNeeds & ~e.Needs) == 0
                        : e.UserCommands || !request.NeedsUserCommands)));
            if (existing is not null)
            {
                // Coalesce: one parse, every owner's post-processing; bump priority/order to the max seen.
                existing.Attachments.Add(new Attachment(request.OwnerTag, request.OnParsed, request.OnFailed, request.OnForward));
                existing.UserCommands |= request.NeedsUserCommands;
                if (existing.State == DemoQueueItemState.Queued)
                {
                    existing.Needs |= request.ForwardNeeds;
                }
                if (request.Priority > existing.Priority)
                {
                    existing.Priority = request.Priority;
                }

                if (request.OrderHint > existing.OrderHint)
                {
                    existing.OrderHint = request.OrderHint;
                }

                existing.DisplayName ??= request.DisplayName;
                PumpLocked(); // priority may have changed the pick order
                handle = new Handle(this, existing.Id, request.OwnerTag, request.Path, existing.Completion.Task);
            }
            else if (request.Priority < DemoJobPriority.UserRequested && BackgroundTierCountLocked() >= _maxQueueSize)
            {
                // The size cap governs the BACKGROUND tier only; UserRequested/Foreground bypass it.
                return RejectedHandle(request);
            }
            else
            {
                Entry entry = new()
                {
                    Path = request.Path,
                    DisplayName = request.DisplayName,
                    Priority = request.Priority,
                    OrderHint = request.OrderHint,
                    Seq = _seq++,
                    UserCommands = request.NeedsUserCommands,
                    Needs = request.ForwardNeeds
                };
                entry.Attachments.Add(new Attachment(request.OwnerTag, request.OnParsed, request.OnFailed, request.OnForward));
                _entries.Add(entry);
                PumpLocked();
                handle = new Handle(this, entry.Id, request.OwnerTag, request.Path, entry.Completion.Task);
            }
        }

        RaiseChanged();
        return handle;
    }

    public IDemoQueueHandle SubmitJob(QueueJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == QueueJobKind.DemoProcessing)
        {
            throw new ArgumentException("A demo parse goes through SubmitBackground.", nameof(request));
        }

        Handle handle;
        lock (_sync)
        {
            if (_disposed)
            {
                return new Handle(this, Guid.Empty, request.OwnerTag, "", Task.CompletedTask, true);
            }

            Entry? existing = request.Key is null
                ? null
                : _entries.FirstOrDefault(e => e.Kind == request.Kind && e.State == DemoQueueItemState.Queued
                                               && string.Equals(e.Key, request.Key, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.DisplayName = request.Title;
                existing.Priority = (DemoJobPriority)Math.Max((int)existing.Priority, (int)request.Priority);
                existing.OrderHint = Math.Max(existing.OrderHint, request.OrderHint);
                PumpLocked();
                handle = new Handle(this, existing.Id, request.OwnerTag, existing.Path, existing.Completion.Task);
            }
            else
            {
                Entry entry = SubmitJobLocked(request);
                handle = new Handle(this, entry.Id, request.OwnerTag, entry.Path, entry.Completion.Task);
            }
        }

        RaiseChanged();
        return handle;
    }

    private Entry SubmitJobLocked(QueueJobRequest request)
    {
        Entry entry = new()
        {
            Kind = request.Kind,
            Key = request.Key,
            Job = request.RunAsync,
            JobOwner = request.OwnerTag,
            Path = request.Target ?? "",
            DisplayName = request.Title,
            Priority = request.Priority,
            OrderHint = request.OrderHint,
            Seq = _seq++
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
        lock (_sync)
        {
            Entry? e = _entries.FirstOrDefault(x => x.Id == itemId);
            if (e is null)
            {
                return;
            }

            if (e.State == DemoQueueItemState.Queued)
            {
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
        lock (_sync)
        {
            Entry? e = _entries.FirstOrDefault(x =>
                x.Kind == QueueJobKind.DemoProcessing && IsActive(x) && !x.Finalizing && PathEquals(x.Path, path));
            if (e is null)
            {
                return;
            }

            e.Attachments.RemoveAll(a => string.Equals(a.OwnerTag, ownerTag, StringComparison.Ordinal));
            if (e.Attachments.Count > 0 || e.ForegroundWaiters.Count > 0)
            {
                RaiseChanged(); // a co-owner still wants it; only the owner chip changed
                return;
            }

            if (e.State == DemoQueueItemState.Queued)
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
            return _entries.Select(ToSnapshot).ToList();
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
    }

    // ── The pump + workers ───────────────────────────────────────────────

    // Ensures enough worker loops are alive: one per concurrently-runnable item, capped at
    // MaxConcurrency. A worker self-terminates when no work remains; the next submit/resume respawns.
    private void PumpLocked()
    {
        if (_disposed || _paused)
        {
            return;
        }

        int running = _entries.Count(e => e.State == DemoQueueItemState.Running);
        int want = running;
        if (NextStartableLocked() is { } next)
        {
            want = next.Kind == QueueJobKind.DemoProcessing
                ? Math.Min(_maxConcurrency, running + _entries.Count(e => e.Kind == QueueJobKind.DemoProcessing && IsStartableLocked(e)))
                : 1;
        }
        while (_activeWorkers < want)
        {
            _activeWorkers++;
            _ = Task.Run(() => WorkerLoopAsync());
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_disposed || _paused || NextStartableLocked() is null)
                    {
                        return; // nothing to do → exit; respawned on next submit/resume/grow
                    }
                }

                // Acquire a background slot (yields to interactive/reel; respects the hard cap). Between
                // demos the worker re-acquires, so it steps aside at each demo boundary, exactly like
                // the historical per-consumer loops.
                using SlotLease slot = new(_gate, await _gate.AcquireBackgroundAsync(_shutdownToken).ConfigureAwait(false));

                Entry? entry;
                lock (_sync)
                {
                    entry = _disposed || _paused ? null : PickNextQueuedLocked();
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
        bool forward;
        lock (_sync)
        {
            forward = _forwardPass is not null && entry.Attachments.Count > 0
                                               && entry.Attachments.TrueForAll(a => a.OnForward is not null);
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
        lock (_sync)
        {
            plan = entry.UserCommands ? DecodePlan.Everything : WithoutUserCommands;
        }

        try
        {
            parsed = _parseFile(entry.Path, plan);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool didParse = parsed is not null;
        FinishEntry(entry, parsed, failure); // runs handlers OUTSIDE _sync, still inside the slot

        lock (_sync)
        {
            _jobsSinceCompact++;
            if (didParse)
            {
                _parsesSinceCompact++;
            }
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
            if (entry.CancelRequested)
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

        lock (_sync)
        {
            entry.Cancel = null;
        }

        cancel.Dispose();
        FinishForward(entry, pass, failure, cancelled);

        lock (_sync)
        {
            _jobsSinceCompact++;
            _parsesSinceCompact++;
        }
    }

    // A cancelled pass calls no owner: OnFailed would mark a demo that is fine as failed.
    private void FinishForward(Entry entry, ForwardDemoResult? pass, Exception? failure, bool cancelled)
    {
        List<Attachment> attachments;
        lock (_sync)
        {
            entry.Finalizing = true;
            cancelled |= entry.CancelRequested;
            attachments = [.. entry.Attachments];
        }

        if (cancelled)
        {
            SetTerminal(entry, DemoQueueItemState.Cancelled, null);
            return;
        }

        if (failure is not null)
        {
            foreach (Attachment a in attachments)
            {
                SafeInvoke(() => a.OnFailed?.Invoke(failure));
            }

            SetTerminal(entry, DemoQueueItemState.Failed, failure.Message);
            return;
        }

        foreach (Attachment a in attachments)
        {
            SafeInvoke(() => a.OnForward!(pass!));
        }

        SetTerminal(entry, DemoQueueItemState.Completed, null);
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
            if (entry.CancelRequested)
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

        lock (_sync)
        {
            if (state == DemoQueueItemState.Completed && entry.CancelRequested)
            {
                state = DemoQueueItemState.Cancelled;
            }

            entry.Cancel = null;
            if (entry.Kind != QueueJobKind.HeapCompaction)
            {
                _jobsSinceCompact++;
                if (entry.ParsedDemo)
                {
                    _parsesSinceCompact++;
                }
            }
        }

        cancel.Dispose();
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
    private Entry? NextStartableLocked()
    {
        Entry? best = null;
        bool anyRunning = false, jobRunning = false;
        foreach (Entry e in _entries)
        {
            if (e.State == DemoQueueItemState.Running)
            {
                anyRunning = true;
                jobRunning |= e.Kind != QueueJobKind.DemoProcessing;
            }
            else if (IsStartableLocked(e) && (best is null || Compare(e, best) < 0))
            {
                best = e;
            }
        }

        return best is null || jobRunning || (best.Kind != QueueJobKind.DemoProcessing && anyRunning) ? null : best;
    }

    // Marks the next startable item Running under the lock.
    private Entry? PickNextQueuedLocked()
    {
        Entry? best = NextStartableLocked();
        if (best is not null)
        {
            best.State = DemoQueueItemState.Running;
            CancelDeferredCompactLocked();
        }

        return best;
    }

    private void FinishEntry(Entry entry, ParsedDemo? parsed, Exception? failure)
    {
        bool cancelled;
        List<Attachment> attachments;
        List<TaskCompletionSource<ParsedDemo>> foreground;
        lock (_sync)
        {
            // Close the entry to further coalescing ATOMICALLY with capturing the handler snapshot:
            // any waiter/attachment added after this point (during the OnParsed window below) would
            // never be signalled. Late callers coalesce onto nothing and start their own work instead.
            entry.Finalizing = true;
            cancelled = entry.CancelRequested;
            attachments = [.. entry.Attachments];
            foreground = [.. entry.ForegroundWaiters];
        }

        if (failure is not null)
        {
            foreach (TaskCompletionSource<ParsedDemo> w in foreground)
            {
                w.TrySetException(failure);
            }

            foreach (Attachment a in attachments)
            {
                SafeInvoke(() => a.OnFailed?.Invoke(failure));
            }

            SetTerminal(entry, DemoQueueItemState.Failed, failure.Message);
            return;
        }

        // Success. Satisfy foreground waiters FIRST (responsiveness: they must not wait behind the
        // heavy background post-processing), THEN run each owner's OnParsed inside the slot.
        foreach (TaskCompletionSource<ParsedDemo> w in foreground)
        {
            w.TrySetResult(parsed!);
        }

        if (!cancelled)
        {
            foreach (Attachment a in attachments)
            {
                SafeInvoke(() => a.OnParsed(parsed!));
            }
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

    private Handle RejectedHandle(DemoProcessingRequest request)
    {
        TaskCompletionSource done = new();
        done.SetResult();
        return new Handle(this, Guid.Empty, request.OwnerTag, request.Path, done.Task,
            true);
    }

    private DemoQueueItemState GetState(Guid id)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Id == id)?.State ?? DemoQueueItemState.Cancelled;
        }
    }

    // ── UI mirror reconcile (posted) ──────────────────────────────────────────

    private void RaiseChanged()
    {
        PostReconcile();
        _post(() => Changed?.Invoke());
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
    private void PostReconcile()
    {
        _post(() =>
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
                foreach (DemoQueueItemSnapshot s in snapshot)
                {
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
                        _items.Add(new DemoQueueItem
                        {
                            Id = s.Id,
                            Path = s.Path,
                            DisplayName = s.DisplayName,
                            Owners = string.Join(", ", s.Owners),
                            Priority = s.Priority,
                            State = s.State,
                            Error = s.Error,
                            Kind = s.Kind,
                            Progress = s.Progress,
                            Detail = s.Detail
                        });
                    }
                }
            }
        });
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
        && (_backgroundEnabled || e.Priority >= DemoJobPriority.UserRequested || e.Kind == QueueJobKind.HeapCompaction);

    private static int KindRank(QueueJobKind kind) => kind switch
    {
        QueueJobKind.DemoProcessing or QueueJobKind.PackExport => 0,
        QueueJobKind.SidecarMigration => 1,
        QueueJobKind.StratMining => 2,
        QueueJobKind.LineupClips => 3,
        _ => 4
    };

    // Negative when a runs before b. A compaction goes first: it is due now, and it holds _compacting.
    private static int Compare(Entry a, Entry b)
    {
        bool aCompacts = a.Kind == QueueJobKind.HeapCompaction, bCompacts = b.Kind == QueueJobKind.HeapCompaction;
        if (aCompacts != bCompacts)
        {
            return aCompacts ? -1 : 1;
        }

        if (a.Priority != b.Priority)
        {
            return b.Priority.CompareTo(a.Priority);
        }

        int rank = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
        if (rank != 0)
        {
            return rank;
        }

        return a.OrderHint != b.OrderHint ? b.OrderHint.CompareTo(a.OrderHint) : a.Seq.CompareTo(b.Seq);
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
        e.JobOwner is { } owner ? [owner] : e.Attachments.Select(a => a.OwnerTag).Distinct(StringComparer.Ordinal).ToList(),
        e.Priority, e.State, e.Error, e.Kind, e.Progress, e.Detail);

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
        string ownerTag,
        string path,
        Task completion,
        bool rejected = false) : IDemoQueueHandle
    {
        public Guid Id => id;
        public Task Completion => completion;
        public DemoQueueItemState State => rejected ? DemoQueueItemState.Rejected : queue.GetState(id);
        public void Cancel()
        {
            if (queue.KindOf(id) is { } kind && kind != QueueJobKind.DemoProcessing)
            {
                queue.RemoveByUser(id);
            }
            else
            {
                queue.CancelOwned(ownerTag, path);
            }
        }
    }

    private QueueJobKind? KindOf(Guid id)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Id == id)?.Kind;
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
    private sealed class SlotLease(HeavyJobGate gate, IDisposable held) : IDisposable
    {
        private IDisposable? _held = held;

        public void Dispose() => Release();

        public void Release() => Interlocked.Exchange(ref _held, null)?.Dispose();

        public async Task RetakeAsync(CancellationToken ct)
        {
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
        }

        public void NoteDemoParsed() => queue.NoteDemoParsed(entry);
    }

    private sealed class Entry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public QueueJobKind Kind { get; init; }
        public string? Key { get; init; }
        public string? JobOwner { get; init; }
        public Func<IQueueJobContext, Task>? Job { get; set; }
        public CancellationTokenSource? Cancel { get; set; }
        public double? Progress { get; set; }
        public string? Detail { get; set; }
        public required string Path { get; init; }
        public string? DisplayName { get; set; }
        public DemoJobPriority Priority { get; set; }
        public long OrderHint { get; set; }
        public long Seq { get; init; }
        public DemoQueueItemState State { get; set; } = DemoQueueItemState.Queued;
        public string? Error { get; set; }
        public bool CancelRequested { get; set; }

        // Whether this parse decodes user commands; fixed once it runs.
        public bool UserCommands { get; set; } = true;

        // Read forward instead of retained; fixed once it runs.
        public bool Forward { get; set; }

        // What a forward pass produces: the union of the owners' needs.
        public ForwardNeeds Needs { get; set; }

        // A job body reported a demo parse through its context.
        public bool ParsedDemo { get; set; }

        // Set under _sync the instant FinishEntry captures its waiter/attachment snapshot, BEFORE it
        // releases the lock to run the (multi-second) handlers. The entry stays Running across that
        // window, so without this a foreground/background caller could coalesce onto it and append a
        // waiter AFTER the snapshot, which FinishEntry never re-reads, orphaning it forever (the
        // critical FinishEntry TOCTOU). Finalizing excludes the entry from all coalescing.
        public bool Finalizing { get; set; }
        public List<Attachment> Attachments { get; } = [];
        public List<TaskCompletionSource<ParsedDemo>> ForegroundWaiters { get; } = [];

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Attachment(
        string OwnerTag,
        Action<ParsedDemo> OnParsed,
        Action<Exception>? OnFailed,
        Action<ForwardDemoResult>? OnForward);
}

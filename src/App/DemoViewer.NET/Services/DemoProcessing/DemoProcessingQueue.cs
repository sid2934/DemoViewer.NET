#region

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;
using Cs2DemoKit.Analysis.Diagnostics;
using Cs2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     The global demo-processing queue (demo-processing-queue.md). The single source all background
///     demo parse/analyse work is pulled from, plus the awaitable highest-priority foreground open.
///     <para>
///         <b>Two authorities, one live number.</b> This pump is the PRIMARY limiter — it starts
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
///         delegate (the dispatcher in-app; inline in tests). All waits are <c>await</c> — WASM-safe.
///     </para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The user-facing feature IS a processing queue; the 'Queue' suffix is intentional.")]
public sealed class DemoProcessingQueue : IDemoProcessingQueue, IDisposable
{
    // How many terminal items linger in the mirror for UI feedback before the oldest are pruned.
    private const int TerminalHistoryCap = 30;

    private readonly List<Entry> _entries = [];
    private readonly HeavyJobGate _gate;
    private readonly ObservableCollection<DemoQueueItem> _items = [];
    private readonly Func<ReadOnlyMemory<byte>, ParsedDemo> _parseBytes; // foreground: parse in-hand bytes
    private readonly Func<string, ParsedDemo> _parseFile; // background: read file at path → parse
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _shutdown = new();

    // Captured once so a worker never touches _shutdown.Token AFTER Dispose disposes the source (which
    // would throw ObjectDisposedException). The captured struct stays valid post-dispose.
    private readonly CancellationToken _shutdownToken;
    private readonly object _sync = new();
    private int _activeWorkers;
    private bool _backgroundEnabled = true;
    private bool _disposed;

    private int _maxConcurrency = 1;
    private int _maxQueueSize = 200;
    private bool _paused;
    private long _seq;

    // Diagnostics-pillar logger (v0.6.0 — replaced Console.WriteLine). Lazy (the ambient factory is
    // wired after construction) and static so the static SafeInvoke helper can log through it.
    private static ILogger? _diagLog;
    private static ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger(AppLog.QueueCategory);

    /// <param name="gate">The machine-wide heavy-parse gate (concurrency backstop + reel/interactive).</param>
    /// <param name="post">Marshals mirror mutations to the UI thread (inline in tests).</param>
    /// <param name="parseFile">
    ///     Test seam: the background read+parse step (default reads the file and
    ///     calls <c>DemoParser.Parse</c>).
    /// </param>
    /// <param name="parseBytes">
    ///     Test seam: the foreground in-hand-bytes parse (default
    ///     <c>DemoParser.Parse</c>).
    /// </param>
    public DemoProcessingQueue(
        HeavyJobGate gate,
        Action<Action>? post = null,
        Func<string, ParsedDemo>? parseFile = null,
        Func<ReadOnlyMemory<byte>, ParsedDemo>? parseBytes = null)
    {
        _gate = gate;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _parseFile = parseFile ?? (path => DemoParser.Parse(File.ReadAllBytes(path).AsMemory()));
        _parseBytes = parseBytes ?? (bytes => DemoParser.Parse(bytes));
        _shutdownToken = _shutdown.Token;
        Items = new ReadOnlyObservableCollection<DemoQueueItem>(_items);
        _gate.MaxConcurrency = _maxConcurrency;
    }

    public ReadOnlyObservableCollection<DemoQueueItem> Items { get; }

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
                if (value)
                {
                    PumpLocked();
                }
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
        // than starting a redundant multi-GB parse. Never blocks on the pump — the fallback below
        // always runs a direct parse under the interactive slot.
        if (path is not null)
        {
            Task<ParsedDemo>? inFlight = null;
            lock (_sync)
            {
                Entry? running = _entries.FirstOrDefault(e =>
                    e.State == DemoQueueItemState.Running && !e.Finalizing && PathEquals(e.Path, path));
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

            Entry? existing = _entries.FirstOrDefault(e =>
                IsActive(e) && !e.Finalizing && PathEquals(e.Path, request.Path));
            if (existing is not null)
            {
                // Coalesce: one parse, every owner's post-processing; bump priority/order to the max seen.
                existing.Attachments.Add(new Attachment(request.OwnerTag, request.OnParsed, request.OnFailed));
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
                    Seq = _seq++
                };
                entry.Attachments.Add(new Attachment(request.OwnerTag, request.OnParsed, request.OnFailed));
                _entries.Add(entry);
                PumpLocked();
                handle = new Handle(this, entry.Id, request.OwnerTag, request.Path, entry.Completion.Task);
            }
        }

        RaiseChanged();
        return handle;
    }

    // ── Removal ─────────────────────────────────────────────────────────

    public void RemoveByUser(Guid itemId)
    {
        bool freedQueueSlot = false;
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
                freedQueueSlot = true;
            }
            else if (e.State == DemoQueueItemState.Running)
            {
                // The parse is not abortable — mark it so FinishEntry discards the result and runs no
                // post-processing when it completes.
                e.CancelRequested = true;
            }
        }

        RaiseChanged();
        if (freedQueueSlot)
        {
            RaiseCapacityAvailable(); // a queued slot opened → feeders may re-submit their backlog
        }
    }

    public void CancelOwned(string ownerTag, string path)
    {
        bool freedQueueSlot = false;
        lock (_sync)
        {
            Entry? e = _entries.FirstOrDefault(x => IsActive(x) && !x.Finalizing && PathEquals(x.Path, path));
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
            }
        }

        RaiseChanged();
        if (freedQueueSlot)
        {
            RaiseCapacityAvailable();
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
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    // ── The pump + workers ───────────────────────────────────────────────

    // Ensures enough worker loops are alive: one per concurrently-runnable item, capped at
    // MaxConcurrency. A worker self-terminates when no work remains; the next submit/resume respawns.
    private void PumpLocked()
    {
        if (_disposed || _paused || !_backgroundEnabled)
        {
            return;
        }

        int runnable = _entries.Count(IsActive); // Queued + Running
        int want = Math.Min(_maxConcurrency, runnable);
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
                    if (_disposed || _paused || !_backgroundEnabled || !_entries.Any(e => e.State == DemoQueueItemState.Queued))
                    {
                        return; // nothing to do → exit; respawned on next submit/resume/grow
                    }
                }

                // Acquire a background slot (yields to interactive/reel; respects the hard cap). Between
                // demos the worker re-acquires, so it steps aside at each demo boundary — exactly like
                // the historical per-consumer loops.
                using IDisposable slot = await _gate.AcquireBackgroundAsync(_shutdownToken).ConfigureAwait(false);

                Entry? entry;
                lock (_sync)
                {
                    entry = _disposed || _paused || !_backgroundEnabled ? null : PickNextQueuedLocked();
                }

                if (entry is null)
                {
                    continue; // work vanished / paused after the top check — re-evaluate, maybe exit
                }

                ParsedDemo? parsed = null;
                Exception? failure = null;
                try
                {
                    parsed = _parseFile(entry.Path);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                FinishEntry(entry, parsed, failure); // runs handlers OUTSIDE _sync, still inside the slot
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown (token cancelled, or the CTS disposed at app exit) — the durable backlogs keep
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

    // Highest priority, then newest OrderHint, then FIFO seq. Marks the winner Running under the lock.
    private Entry? PickNextQueuedLocked()
    {
        Entry? best = null;
        foreach (Entry e in _entries)
        {
            if (e.State != DemoQueueItemState.Queued)
            {
                continue;
            }

            if (best is null
                || e.Priority > best.Priority
                || e.Priority == best.Priority && e.OrderHint > best.OrderHint
                || e.Priority == best.Priority && e.OrderHint == best.OrderHint && e.Seq < best.Seq)
            {
                best = e;
            }
        }

        if (best is not null)
        {
            best.State = DemoQueueItemState.Running;
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
            // Close the entry to further coalescing ATOMICALLY with capturing the handler snapshot —
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

        // Success. Satisfy foreground waiters FIRST (responsiveness — they must not wait behind the
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
        PruneTerminalHistoryLocked();
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
            // taking the snapshot here makes the LAST-enqueued reconcile read the LATEST state —
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
                            Error = s.Error
                        });
                    }
                }
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsActive(Entry e) =>
        e.State is DemoQueueItemState.Queued or DemoQueueItemState.Running;

    private int BackgroundTierCountLocked() => _entries.Count(IsActive);

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DemoQueueItemSnapshot ToSnapshot(Entry e) => new(
        e.Id, e.Path, e.DisplayName,
        e.Attachments.Select(a => a.OwnerTag).Distinct(StringComparer.Ordinal).ToList(),
        e.Priority, e.State, e.Error);

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
            // Console (v0.6.0) — Console is invisible in a windowed Release build.
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
        public void Cancel() => queue.CancelOwned(ownerTag, path);
    }

    private sealed class Entry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string Path { get; init; }
        public string? DisplayName { get; set; }
        public DemoJobPriority Priority { get; set; }
        public long OrderHint { get; set; }
        public long Seq { get; init; }
        public DemoQueueItemState State { get; set; } = DemoQueueItemState.Queued;
        public string? Error { get; set; }
        public bool CancelRequested { get; set; }

        // Set under _sync the instant FinishEntry captures its waiter/attachment snapshot, BEFORE it
        // releases the lock to run the (multi-second) handlers. The entry stays Running across that
        // window, so without this a foreground/background caller could coalesce onto it and append a
        // waiter AFTER the snapshot — which FinishEntry never re-reads, orphaning it forever (the
        // critical FinishEntry TOCTOU). Finalizing excludes the entry from all coalescing.
        public bool Finalizing { get; set; }
        public List<Attachment> Attachments { get; } = [];
        public List<TaskCompletionSource<ParsedDemo>> ForegroundWaiters { get; } = [];

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Attachment(string OwnerTag, Action<ParsedDemo> OnParsed, Action<Exception>? OnFailed);
}

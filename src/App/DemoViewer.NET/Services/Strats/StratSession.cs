#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The live strat in the Strat Book tab (strat-model.md §3.12): the one undo stack over <see cref="PatchOp" />,
///     the commit rule, and the autosaved working copy.
///     <para>
///         <b>One history, and it is this one</b> (overview correction 18). Every edit reaches the document through
///         <see cref="Apply(IReadOnlyList{PatchOp})" />, whether the metadata editor made it or the Step Authoring
///         canvas did, and every applied op, undo and redo included, is announced on <see cref="OpsApplied" /> so a
///         projection can follow without keeping a stack of its own. The session fills each op's <c>from</c> from
///         the document and resolves an RFC 6902 <c>-</c> to the index it lands on, so an inverse always says which
///         element it removes.
///     </para>
///     <para>
///         <b>Two writes, two clocks.</b> The working copy is written debounced (<see cref="AutoSaveDelay" />, the
///         annotation controller's rules: snapshot on the UI thread, write off it, a stale snapshot stands down),
///         at the previous revision with <see cref="StratStore.PendingField" /> set, so a crash loses at most half
///         a second. The history line is written at a commit, which is when the revision moves: an explicit save,
///         a tab deactivate, a demo swap, shutdown, or <see cref="IdleCommitDelay" /> after the last edit, with
///         same-path ops merged by <see cref="StratCommitBuffer" />.
///     </para>
///     <para>
///         UI-thread affine like the tag session: every mutating call is made on one thread, and the timers come
///         back through the post delegate. <see cref="Changed" /> is raised on that thread.
///     </para>
/// </summary>
public sealed class StratSession : IDisposable
{
    /// <summary>Undo entries kept; the oldest is dropped. The annotation document's cap.</summary>
    public const int MaxHistoryEntries = 200;

    private readonly StratCommitBuffer _buffer = new();
    private readonly Func<bool> _isBrowser;
    private readonly Action<Action> _post;
    private readonly List<IReadOnlyList<PatchOp>> _redo = [];

    // Working-copy writes and commits are serialized: two in flight could land the older one last.
    private readonly SemaphoreSlim _saveSerializer = new(1, 1);
    private readonly StratStore _store;
    private readonly List<IReadOnlyList<PatchOp>> _undo = [];
    private readonly Func<DateTime> _utcNow;

    private CancellationTokenSource? _autosave;
    private IDisposable? _checkOut;
    private string? _commitFailure;
    private bool _disposed;
    private CancellationTokenSource? _idle;
    private int _lastSavedVersion;
    private bool _writeFailed;

    /// <param name="store">The store the strat is checked out of.</param>
    /// <param name="post">Marshals the timers and the write completions onto the UI thread; defaults to synchronous.</param>
    public StratSession(StratStore store, Action<Action>? post = null)
        : this(store, post, OperatingSystem.IsBrowser, () => DateTime.UtcNow)
    {
    }

    /// <summary>Test seam: the host predicate and the clock injected.</summary>
    internal StratSession(StratStore store, Action<Action>? post, Func<bool> isBrowser, Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(isBrowser);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _post = post ?? (action => action());
        _isBrowser = isBrowser;
        _utcNow = utcNow;
        StatusText = Describe();
    }

    /// <summary>The open strat, or null. Its revision is the last committed one; edits since sit in the commit buffer.</summary>
    public StratDocument? Document { get; private set; }

    /// <summary>The validator's findings at open or at the last commit.</summary>
    public IReadOnlyList<StratIssue> Issues { get; private set; } = [];

    /// <summary>Bumped on every edit, undo and redo. Never goes backwards.</summary>
    public int Version { get; private set; }

    /// <summary>How long edits coalesce before the working copy is written. Shortened by tests.</summary>
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Idle time after the last edit at which the pending ops are committed (decision 2).</summary>
    public TimeSpan IdleCommitDelay { get; set; } = TimeSpan.FromSeconds(StratCommitBuffer.IdleCommitSeconds);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>How many undo entries are available.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>Edits applied since the last commit.</summary>
    public bool HasPending => _buffer.HasPending;

    /// <summary>The ops the next commit would write, merged.</summary>
    public IReadOnlyList<PatchOp> PendingOps => _buffer.Pending;

    /// <summary>The open strat's file held uncommitted edits from an earlier session; they are pending again.</summary>
    public bool RecoveredPending { get; private set; }

    /// <summary>Working-copy writes completed, successful or not. Tests wait on it.</summary>
    public int SaveCount { get; private set; }

    /// <summary>Commits that reached the log.</summary>
    public int CommitCount { get; private set; }

    /// <summary>One line for the tab: where the strat is saved, or why it is not.</summary>
    public string StatusText { get; private set; }

    /// <summary>Raised after any change to the document, its revision or the status line.</summary>
    public event Action? Changed;

    /// <summary>
    ///     The ops as they reached the document, in order, for an edit, an undo or a redo: what a projection
    ///     (the canvas's annotation and token layers) replays as a migration.
    /// </summary>
    public event Action<IReadOnlyList<PatchOp>>? OpsApplied;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
        Cancel(ref _autosave);
        Cancel(ref _idle);
        _saveSerializer.Dispose();
    }

    /// <summary>
    ///     Opens a strat: commits and releases the one open before, checks this one out of the store, and re-opens
    ///     any uncommitted edits its working copy holds as pending. Null document on the result when it is absent
    ///     or unreadable; the session is then empty.
    /// </summary>
    /// <param name="id">The strat's id.</param>
    public StratLoadResult Open(Guid id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Close();

        StratLoadResult result = _store.Load(id);
        if (result.Document is not { } loaded)
        {
            Issues = result.Issues;
            StatusText = result.IsUnreadable ? "the strat file could not be read: it will not be overwritten" : Describe();
            Changed?.Invoke();
            return result;
        }

        IReadOnlyList<PatchOp> recovered = _store.PendingOps(loaded);
        StratDocument document = StratStore.WithPending(loaded, false);
        DateTime now = _utcNow();
        foreach (PatchOp op in recovered)
        {
            _buffer.Record(op, now);
        }

        _checkOut = _store.CheckOut(id, this);
        Document = document;
        RecoveredPending = recovered.Count > 0;
        Issues = StratValidator.Validate(document, index: _store.Index);
        _commitFailure = null;
        _writeFailed = false;

        // The file on disk already says what the document says, marker and all.
        Version++;
        _lastSavedVersion = Version;
        StatusText = Describe();
        Changed?.Invoke();
        if (RecoveredPending)
        {
            ScheduleIdleCommit();
        }

        return result;
    }

    /// <summary>
    ///     Commits pending edits and releases the strat: tab deactivate, demo swap, another strat opened, shutdown.
    ///     An edit the store refuses stays in the working copy, marked pending, for the next session. Never throws.
    /// </summary>
    public void Close()
    {
        if (Document is null)
        {
            return;
        }

        Commit();
        if (HasPending)
        {
            Flush();
        }

        Cancel(ref _autosave);
        Cancel(ref _idle);
        _checkOut?.Dispose();
        _checkOut = null;
        _buffer.Drain();
        _undo.Clear();
        _redo.Clear();
        Document = null;
        Issues = [];
        RecoveredPending = false;
        _commitFailure = null;
        Version++;
        StatusText = Describe();
        Changed?.Invoke();
    }

    /// <summary>One edit as one undo entry.</summary>
    /// <param name="op">The op; its <c>from</c> is read from the document, whatever the caller put there.</param>
    public void Apply(PatchOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        Apply([op]);
    }

    /// <summary>
    ///     Several ops as one undo entry: one gesture (a step deleted with the branches that named it, an erase of
    ///     several strokes). Atomic: an op whose path does not resolve throws and the document is left as it was.
    /// </summary>
    /// <param name="ops">The ops, in order.</param>
    /// <exception cref="InvalidOperationException">No strat is open, or an op does not apply.</exception>
    public void Apply(IReadOnlyList<PatchOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (ops.Count == 0)
        {
            return;
        }

        IReadOnlyList<PatchOp> applied = ApplyCore(ops);
        _undo.Add(applied);
        if (_undo.Count > MaxHistoryEntries)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        Touch(applied);
    }

    /// <summary>Undoes the newest entry. False when there is none. The undo is itself an edit the next commit writes.</summary>
    public bool Undo()
    {
        if (Document is null || _undo.Count == 0)
        {
            return false;
        }

        IReadOnlyList<PatchOp> entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IReadOnlyList<PatchOp> applied = ApplyCore([.. entry.Reverse().Select(o => o.Inverse())]);
        _redo.Add(entry);
        Touch(applied);
        return true;
    }

    /// <summary>Redoes the newest undone entry. False when there is none.</summary>
    public bool Redo()
    {
        if (Document is null || _redo.Count == 0)
        {
            return false;
        }

        IReadOnlyList<PatchOp> entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        IReadOnlyList<PatchOp> applied = ApplyCore(entry);
        _undo.Add(applied);
        Touch(applied);
        return true;
    }

    /// <summary>A status change as one edit: the route <see cref="StratStore.SetStatus" /> takes while the strat is open here.</summary>
    /// <param name="status">The new status.</param>
    public void SetStatus(StratStatus status)
    {
        if (Document is not { } document || string.Equals(document.Status, status.ToString(), StringComparison.Ordinal))
        {
            return;
        }

        Apply(PatchOp.ReplaceOp("/status", null, JsonValue.Create(status.ToString())));
    }

    /// <summary>
    ///     Writes the pending edits as one history entry and moves the revision. Null when nothing is pending or no
    ///     strat is open. A refused or failed commit keeps the edits pending and says why on the status line.
    /// </summary>
    /// <param name="summary">The history line's summary; free text, the ops are the truth.</param>
    public StratSaveResult? Commit(string? summary = null)
    {
        if (Document is not { } document || !_buffer.HasPending)
        {
            return null;
        }

        Cancel(ref _idle);
        IReadOnlyList<PatchOp> ops = _buffer.Drain();
        StratDocument candidate = StratStore.WithPending(document, false);
        StratSaveResult result;
        try
        {
            _saveSerializer.Wait();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        try
        {
            result = _store.Save(candidate, ops, summary);
            if (result.Saved)
            {
                // Save stamps what it committed onto the document it was handed; the live one follows.
                document.Revision = candidate.Revision;
                document.ModifiedUtc = candidate.ModifiedUtc;
                document.Map = candidate.Map;
                _lastSavedVersion = Version;
                CommitCount++;
                _commitFailure = null;
                _writeFailed = false;
            }
            else
            {
                DateTime now = _utcNow();
                foreach (PatchOp op in ops)
                {
                    _buffer.Record(op, now);
                }

                _commitFailure = result.Reason;
            }
        }
        finally
        {
            _saveSerializer.Release();
        }

        Issues = result.Issues;
        RecoveredPending &= !result.Saved;
        StatusText = Describe();
        Changed?.Invoke();
        return result;
    }

    /// <summary>Writes the working copy now if it is behind the document. Never throws.</summary>
    public async Task FlushAsync()
    {
        Cancel(ref _autosave);
        await WriteWorkingCopyAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Blocking flush for the moments with no later: close and shutdown. Every await inside is
    ///     <c>ConfigureAwait(false)</c>, so there is no context to deadlock on, and the payload is small.
    /// </summary>
    public void Flush() => FlushAsync().GetAwaiter().GetResult();

    // Applies ops to a copy of the document, filling each op's `from` and resolving `-`, and swaps the copy in
    // only when every op applied. Returns the ops as applied.
    private List<PatchOp> ApplyCore(IReadOnlyList<PatchOp> ops)
    {
        StratDocument document = Document ?? throw new InvalidOperationException("no strat is open");
        JsonNode? root = StratHistory.ToNode(document);
        List<PatchOp> applied = new(ops.Count);
        foreach (PatchOp op in ops)
        {
            ArgumentNullException.ThrowIfNull(op);
            PatchOp normalized = Normalize(root, op);
            root = StratHistory.ApplyAll(root, [normalized]);
            applied.Add(normalized);
        }

        StratDocument next = root?.Deserialize(StratJsonContext.Default.StratDocument)
                             ?? throw new InvalidOperationException("the ops removed the strat");

        Document = next;
        return applied;
    }

    private static PatchOp Normalize(JsonNode? root, PatchOp op)
    {
        if (op.Path.Length == 0 || op.Path is "/id" or "/revision" or "/modifiedUtc")
        {
            throw new InvalidOperationException($"'{op.Path}' is not an edit: the whole strat, its id and its revision stamps are the store's");
        }

        int slash = op.Path.LastIndexOf('/');
        string parentPath = op.Path[..slash];
        string token = op.Path[(slash + 1)..];
        JsonNode? parent = StratHistory.ValueAt(root, parentPath);

        switch (op.Op)
        {
            case PatchOp.Add when parent is JsonArray array && token == "-":
                // The log names the concrete index: the inverse of an append has to say which element it removes.
                return PatchOp.AddOp(parentPath + "/" + array.Count.ToString(CultureInfo.InvariantCulture), op.Value?.DeepClone());
            case PatchOp.Add when parent is JsonObject obj && obj.ContainsKey(Unescape(token)):
                // An add over a member is a replace; as an add its inverse would remove the member outright.
                return PatchOp.ReplaceOp(op.Path, obj[Unescape(token)]?.DeepClone(), op.Value?.DeepClone());
            case PatchOp.Add:
                return PatchOp.AddOp(op.Path, op.Value?.DeepClone());
            case PatchOp.Remove:
                return PatchOp.RemoveOp(op.Path, StratHistory.ValueAt(root, op.Path)?.DeepClone());
            case PatchOp.Replace:
                return PatchOp.ReplaceOp(op.Path, StratHistory.ValueAt(root, op.Path)?.DeepClone(), op.Value?.DeepClone());
            default:
                throw new InvalidOperationException($"unsupported op '{op.Op}'");
        }
    }

    private static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private void Touch(IReadOnlyList<PatchOp> applied)
    {
        DateTime now = _utcNow();
        foreach (PatchOp op in applied)
        {
            _buffer.Record(op, now);
        }

        Version++;
        _commitFailure = null;
        StatusText = Describe();
        Changed?.Invoke();
        OpsApplied?.Invoke(applied);
        ScheduleAutosave();
        ScheduleIdleCommit();
    }

    private void ScheduleAutosave()
    {
        if (_disposed || Document is null)
        {
            return;
        }

        Cancel(ref _autosave);
        CancellationTokenSource cts = new();
        _autosave = cts;
        _ = AfterDelay(AutoSaveDelay, () => _ = WriteWorkingCopyAsync(), cts.Token);
    }

    // The idle rule is "no edit for the delay", and the timer restarts on every edit, so its firing is the rule.
    private void ScheduleIdleCommit()
    {
        if (_disposed || Document is null)
        {
            return;
        }

        Cancel(ref _idle);
        CancellationTokenSource cts = new();
        _idle = cts;
        _ = AfterDelay(IdleCommitDelay, () => Commit(), cts.Token);
    }

    private async Task AfterDelay(TimeSpan delay, Action action, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _post(() =>
        {
            if (!_disposed && !ct.IsCancellationRequested)
            {
                action();
            }
        });
    }

    private async Task WriteWorkingCopyAsync()
    {
        if (_disposed || Document is null)
        {
            return;
        }

        // Snapshot on the calling thread before any wait: the document is UI-thread state, and the version taken
        // with it is what lets a slower writer stand down.
        int version = Version;
        if (version <= _lastSavedVersion)
        {
            return;
        }

        StratDocument snapshot = Document.Clone();
        bool pending = _buffer.HasPending;

        try
        {
            await _saveSerializer.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (version <= _lastSavedVersion)
            {
                return; // a commit or a newer snapshot already reached the store
            }

            bool written = await Task.Run(() => _store.WriteWorkingCopy(snapshot, pending)).ConfigureAwait(false);
            if (written)
            {
                _lastSavedVersion = version;
            }

            _writeFailed = !written;
            SaveCount++;
        }
        finally
        {
            _saveSerializer.Release();
        }

        _post(() =>
        {
            StatusText = Describe();
            Changed?.Invoke();
        });
    }

    private static void Cancel(ref CancellationTokenSource? cts)
    {
        CancellationTokenSource? old = cts;
        cts = null;
        if (old is null)
        {
            return;
        }

        old.Cancel();
        old.Dispose();
    }

    private string Describe()
    {
        if (Document is not { } document)
        {
            return "no strat open";
        }

        // A refusal is the validator's, not the disk's, so it is said on every host.
        if (_commitFailure is not null)
        {
            return "strat could not be saved: " + _commitFailure;
        }

        // The browser's store is a dictionary. Naming a destination there would read as a promise the next
        // reload breaks, so the line says what happens instead, in the tag session's words.
        if (_isBrowser() || !_store.IsPersistent)
        {
            return _isBrowser()
                ? "session only: this browser tab forgets strats when it reloads"
                : "session only: strats are not saved";
        }

        if (_writeFailed)
        {
            return "strat could not be saved";
        }

        string revision = "revision " + document.Revision.ToString(CultureInfo.InvariantCulture);
        if (!HasPending)
        {
            return revision + " · saved";
        }

        string prefix = RecoveredPending ? "recovered edits from the last session · " : "";
        return prefix + revision + " · edits commit on save, on leaving the tab or after "
               + IdleCommitDelay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s idle";
    }
}

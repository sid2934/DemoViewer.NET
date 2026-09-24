#region

using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>
///     One edit to a <see cref="TagDocument" />. The inverse is computed when the delta is applied, not
///     stored in it, the <c>DocDelta</c> rule: a <see cref="Remove" />'s inverse needs the instance it removed.
/// </summary>
public abstract record TagDelta
{
    private TagDelta()
    {
    }

    /// <summary>Inserts an instance at <paramref name="Index" />; an index out of range appends.</summary>
    public sealed record Add(TagInstance Instance, int Index = -1) : TagDelta;

    /// <summary>Removes the instance with this id.</summary>
    public sealed record Remove(Guid Id) : TagDelta;

    /// <summary>Replaces the instance with this id: labels, note, positions and span edits.</summary>
    public sealed record Replace(Guid Id, TagInstance Instance) : TagDelta;

    /// <summary>Several deltas as one undo entry: a code press plus its panel-flow labels.</summary>
    public sealed record Batch(IReadOnlyList<TagDelta> Items) : TagDelta;
}

/// <summary>
///     The live tag document for the open demo (tag-store.md §3.9): undo and redo, the non-undoable
///     external path the refresh pass uses, debounced autosave and the one-line status.
///     <para>
///         <b>History holds copies.</b> Every instance entering the document or the history is cloned, so a
///         refresh that rewrites facts in place can never reach back into an undo entry, and undoing a
///         human edit after a refresh restores the human fields it recorded while keeping the refreshed
///         <c>round</c>, <c>facts</c>, <c>factsStamp</c> and resolved places.
///     </para>
///     <para>
///         <b>Autosave follows <c>AnnotationSessionController</c>.</b> Debounced, the document cloned and its
///         <see cref="Version" /> read on the calling (UI) thread, the write off it, serialized so the last
///         writer is the newest, and a slower snapshot stands down. A document with no instances that is not
///         already stored is never written: opening a demo must not leave a file behind.
///     </para>
///     <para>
///         UI-thread affine like the annotation document: every mutating call is made on one thread.
///         <see cref="Changed" /> may be raised from the thread pool after a save.
///     </para>
/// </summary>
public sealed class TagSession : IDisposable
{
    /// <summary>Undo entries kept; the oldest is dropped. The annotation document's cap.</summary>
    public const int MaxHistoryEntries = 200;

    private readonly Func<bool> _isBrowser;
    private readonly List<TagDelta> _redo = [];
    private readonly Func<string, IReadOnlyList<CachedRound>?>? _roundsFor;

    // Saves are serialized, the annotation controller's reason: cancelling the debounce does not stop a
    // write already under way, and two in flight could land the older one last.
    private readonly SemaphoreSlim _saveSerializer = new(1, 1);
    private readonly TagStore? _store;
    private readonly List<TagDelta> _undo = [];
    private readonly Func<DateTime> _utcNow;

    private IDisposable? _checkOut;
    private CancellationTokenSource? _debounce;
    private bool _disposed;
    private int _lastSavedVersion = -1;
    private IReadOnlyList<CachedRound>? _rounds;
    private bool _saveFailed;

    /// <summary>Creates a session. Every dependency is optional so a headless test needs no container.</summary>
    /// <param name="store">The store, or null to run session-only.</param>
    /// <param name="roundsFor">
    ///     Demo path to its cached rounds (frame clock), for deriving <c>round</c>; null leaves rounds unset.
    /// </param>
    public TagSession(TagStore? store, Func<string, IReadOnlyList<CachedRound>?>? roundsFor = null)
        : this(store, roundsFor, OperatingSystem.IsBrowser, () => DateTime.UtcNow)
    {
    }

    /// <summary>Test seam: the host predicate and the clock injected.</summary>
    internal TagSession(TagStore? store, Func<string, IReadOnlyList<CachedRound>?>? roundsFor,
        Func<bool> isBrowser, Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(isBrowser);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _roundsFor = roundsFor;
        _isBrowser = isBrowser;
        _utcNow = utcNow;
        StatusText = Describe();
    }

    /// <summary>The attached document, or null before <see cref="AttachAsync" />.</summary>
    public TagDocument? Document { get; private set; }

    /// <summary>The attached demo's path, or null.</summary>
    public string? DemoPath { get; private set; }

    /// <summary>Bumped on every change, undoable or not. Never goes backwards.</summary>
    public int Version { get; private set; }

    /// <summary>How long changes coalesce before a save: the annotation controller's 750 ms. Shortened by tests.</summary>
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(750);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>How many undo entries are available.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>The stored document was written against a different parse. It loaded; spans may be off.</summary>
    public bool ClockMismatch { get; private set; }

    /// <summary>The stored file names a different demo. It is not touched, and nothing is saved.</summary>
    public bool DemoMismatch { get; private set; }

    /// <summary>The stored file could not be read. It is not overwritten, and nothing is saved.</summary>
    public bool Unreadable { get; private set; }

    /// <summary>Saves completed, successful or not. Tests wait on it.</summary>
    public int SaveCount { get; private set; }

    /// <summary>One line for the palette: where tags are saved, or why they are not.</summary>
    public string StatusText { get; private set; }

    /// <summary>Raised after any change to the document or the status line. May fire off the UI thread.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Document is not null)
        {
            Detach();
        }

        _disposed = true;
        CancelDebounce();
        ReleaseCheckOut();
        _saveSerializer.Dispose();
    }

    /// <summary>
    ///     The demo's identity for <see cref="AttachAsync" />: the hash the caller already has
    ///     (<c>IModuleContext.DemoSha256</c>), else the shared helper off the UI thread, the fallback
    ///     tag-store.md §3.5 keeps for a demo Content Identity has not reached. Null when the file cannot
    ///     be read.
    /// </summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    /// <param name="knownSha256">The hash, when the caller has it.</param>
    public static async Task<DemoIdentity?> IdentityForAsync(string demoPath, string? knownSha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);

        string? sha = string.IsNullOrEmpty(knownSha256)
            ? await Task.Run(() => DemoContentHash.TryCompute(demoPath)).ConfigureAwait(false)
            : knownSha256;
        if (sha is null)
        {
            return null;
        }

        long size = 0;
        try
        {
            size = new FileInfo(demoPath).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only.
        }

        return new DemoIdentity(sha, Path.GetFileName(demoPath), size);
    }

    /// <summary>
    ///     Binds the session to a demo: flushes and releases the previous one, checks the new document out
    ///     of the store, surfaces a clock mismatch, and derives <c>round</c> for any instance without one.
    /// </summary>
    /// <param name="demo">The demo's identity.</param>
    /// <param name="clock">The clock the session's parse is on.</param>
    /// <param name="demoPath">Path to the <c>.dem</c>, for the rounds lookup.</param>
    public async Task AttachAsync(DemoIdentity demo, ClockIdentity clock, string demoPath)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(clock);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await FlushAsync().ConfigureAwait(true);
        if (Document is not null)
        {
            Detach();
        }

        DemoPath = demoPath;
        _rounds = string.IsNullOrEmpty(demoPath) ? null : _roundsFor?.Invoke(demoPath);
        _saveFailed = false;

        TagLoadResult result = _store?.Load(demo.Sha256, clock) ?? TagLoadResult.Empty(null);
        ClockMismatch = result.ClockMismatch;
        DemoMismatch = result.DemoMismatch;
        Unreadable = result.Unreadable;
        Document = result.Document ?? TagDocument.Create(demo, clock);
        _checkOut = _store?.CheckOut(Document.Demo.Sha256, this);

        // Loading is not editing, so nothing here is undoable; but a derived round is a change to the
        // stored document, so it bumps the version and saves like a refresh does.
        bool derived = false;
        foreach (TagInstance instance in Document.Instances)
        {
            if (instance.Round is null && RoundAt(instance.FromTick) is { } round)
            {
                instance.Round = round;
                derived = true;
            }
        }

        Version++;
        _lastSavedVersion = derived ? -1 : Version;
        StatusText = Describe();
        Changed?.Invoke();
        if (derived)
        {
            ScheduleSave();
        }
    }

    /// <summary>Flushes, releases the document and writes the store's index. The session is then unattached.</summary>
    public void Detach()
    {
        if (Document is not null)
        {
            Flush();
        }

        ReleaseCheckOut();
        _store?.SaveIndex();
        Document = null;
        DemoPath = null;
        _undo.Clear();
        _redo.Clear();
        ClockMismatch = false;
        DemoMismatch = false;
        Unreadable = false;
        Version++;
        StatusText = Describe();
        Changed?.Invoke();
    }

    /// <summary>
    ///     Applies an edit as one undo entry. Ticks are validated before anything changes, so a bad batch
    ///     leaves the document as it was. An <see cref="TagDelta.Add" /> without a round gets one derived.
    /// </summary>
    /// <param name="delta">The edit.</param>
    public void Apply(TagDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        TagDocument document = Document ?? throw new InvalidOperationException("no demo is attached");
        Validate(delta);

        TagDelta inverse = ApplyCore(document, delta, true);
        _undo.Add(inverse);
        if (_undo.Count > MaxHistoryEntries)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        Touch();
    }

    /// <summary>Undoes the newest entry. False when there is none.</summary>
    public bool Undo() => Step(_undo, _redo);

    /// <summary>Redoes the newest undone entry. False when there is none.</summary>
    public bool Redo() => Step(_redo, _undo);

    /// <summary>
    ///     A change that is not the user's: the refresh pass, routed here by <c>TagStore.Update</c> while this
    ///     session holds the document. No history entry; the version moves and the autosave is scheduled.
    /// </summary>
    /// <param name="mutate">The change.</param>
    public void ApplyExternal(Action<TagDocument> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (_disposed || Document is null)
        {
            return;
        }

        mutate(Document);
        Touch();
    }

    /// <summary>Writes any pending change now: on demo swap, tab deactivate and shutdown. Never throws.</summary>
    public async Task FlushAsync()
    {
        CancelDebounce();
        await SaveNowAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Blocking flush for the moments with no later: disposal and shutdown. Every await inside is
    ///     <c>ConfigureAwait(false)</c>, so there is no context to deadlock on, and the payload is small.
    /// </summary>
    public void Flush() => FlushAsync().GetAwaiter().GetResult();

    /// <summary>The number of the round containing <paramref name="tick" />, or null without rounds or before the first.</summary>
    /// <param name="rounds">Cached rounds, frame clock.</param>
    /// <param name="tick">A frame-clock tick.</param>
    public static int? RoundAt(IReadOnlyList<CachedRound>? rounds, int tick)
    {
        if (rounds is null)
        {
            return null;
        }

        // The latest start at or before the tick. ClipWindows.RoundStartFor answers the same question
        // with the start tick rather than the round number, which is why it is not called here.
        CachedRound? best = null;
        foreach (CachedRound round in rounds)
        {
            if (round.StartTickFrameClock <= tick && (best is null || round.StartTickFrameClock > best.StartTickFrameClock))
            {
                best = round;
            }
        }

        return best?.Number;
    }

    private int? RoundAt(int tick) => RoundAt(_rounds, tick);

    private void ReleaseCheckOut()
    {
        _checkOut?.Dispose();
        _checkOut = null;
    }

    private bool Step(List<TagDelta> from, List<TagDelta> to)
    {
        if (Document is null || from.Count == 0)
        {
            return false;
        }

        TagDelta entry = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(ApplyCore(Document, entry, false));
        Touch();
        return true;
    }

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
        ScheduleSave();
    }

    private static void Validate(TagDelta delta)
    {
        switch (delta)
        {
            case TagDelta.Add add:
                ValidateSpan(add.Instance);
                break;
            case TagDelta.Replace replace:
                ValidateSpan(replace.Instance);
                break;
            case TagDelta.Batch batch:
                foreach (TagDelta item in batch.Items)
                {
                    Validate(item);
                }

                break;
        }
    }

    private static void ValidateSpan(TagInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.FromTick > instance.ToTick)
        {
            throw new ArgumentException($"fromTick {instance.FromTick} is after toTick {instance.ToTick}",
                nameof(instance));
        }
    }

    // Applies one delta and returns its inverse. `stamp` is true for a user's edit and false for undo and
    // redo, which restore the recorded instance as it was, times included.
    private TagDelta ApplyCore(TagDocument document, TagDelta delta, bool stamp)
    {
        switch (delta)
        {
            case TagDelta.Add add:
            {
                TagInstance instance = add.Instance.Clone();
                if (stamp)
                {
                    if (instance.Id == Guid.Empty)
                    {
                        instance.Id = Guid.NewGuid();
                    }

                    DateTime now = _utcNow();
                    if (instance.CreatedUtc == default)
                    {
                        instance.CreatedUtc = now;
                    }

                    instance.ModifiedUtc = now;
                    instance.Round ??= RoundAt(instance.FromTick);
                }

                int at = add.Index >= 0 && add.Index <= document.Instances.Count ? add.Index : document.Instances.Count;
                document.Instances.Insert(at, instance);
                return new TagDelta.Remove(instance.Id);
            }

            case TagDelta.Remove remove:
            {
                int at = document.Instances.FindIndex(i => i.Id == remove.Id);
                if (at < 0)
                {
                    return new TagDelta.Batch([]);
                }

                TagInstance removed = document.Instances[at];
                document.Instances.RemoveAt(at);
                return new TagDelta.Add(removed, at);
            }

            case TagDelta.Replace replace:
            {
                int at = document.Instances.FindIndex(i => i.Id == replace.Id);
                if (at < 0)
                {
                    return new TagDelta.Batch([]);
                }

                TagInstance current = document.Instances[at];
                TagInstance next = replace.Instance.Clone();
                next.Id = replace.Id;
                // Derived fields are never the editor's: an edit or an undo carries over what the refresh
                // pass last wrote, and a moved start re-derives the round.
                KeepDerived(current, next);
                if (stamp)
                {
                    next.ModifiedUtc = _utcNow();
                    if (next.FromTick != current.FromTick)
                    {
                        next.Round = RoundAt(next.FromTick);
                    }
                }

                document.Instances[at] = next;
                return new TagDelta.Replace(replace.Id, current);
            }

            case TagDelta.Batch batch:
            {
                List<TagDelta> inverses = new(batch.Items.Count);
                foreach (TagDelta item in batch.Items)
                {
                    inverses.Add(ApplyCore(document, item, stamp));
                }

                inverses.Reverse();
                return new TagDelta.Batch(inverses);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(delta), delta, "unknown tag delta");
        }
    }

    // An edit, an undo or a redo changes what a person typed, never what the refresh pass wrote: the
    // derived fields of the instance being displaced carry over onto the one replacing it. A place is
    // kept only where the same point is still there; a point the change brings back keeps its own.
    private static void KeepDerived(TagInstance current, TagInstance restored)
    {
        if (current.FromTick == restored.FromTick)
        {
            restored.Round = current.Round;
        }

        restored.Facts = current.Facts.Select(Copy).ToList();
        restored.FactsStamp = current.FactsStamp;

        List<TagPosition> currentPoints =
            [.. current.Positions, .. current.Movements.SelectMany(m => new[] { m.From, m.To })];
        foreach (TagPosition point in restored.Positions.Concat(restored.Movements.SelectMany(m => new[] { m.From, m.To })))
        {
            TagPosition? same = currentPoints.FirstOrDefault(p =>
                p.X.Equals(point.X) && p.Y.Equals(point.Y) && p.LevelMinZ.Equals(point.LevelMinZ) && p.Tick == point.Tick);
            if (same is not null)
            {
                point.Place = same.Place;
                point.PlaceSource = same.PlaceSource;
            }
        }
    }

    private static TagLabel Copy(TagLabel label) => new(label.Group, label.Value) { Extra = label.Extra };

    private void ScheduleSave()
    {
        if (_disposed || _store is null || Document is null)
        {
            return;
        }

        CancelDebounce();
        CancellationTokenSource cts = new();
        _debounce = cts;
        _ = DelayThenSaveAsync(cts.Token);
    }

    private async Task DelayThenSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveNowAsync().ConfigureAwait(false);
    }

    private async Task SaveNowAsync()
    {
        if (_disposed || _store is null || Document is null || DemoMismatch || Unreadable)
        {
            return;
        }

        // Snapshot on the calling thread before any wait: the document is UI-thread state, and the
        // version taken with it is what lets a slower writer stand down.
        int version = Version;
        if (version <= _lastSavedVersion)
        {
            return;
        }

        TagDocument snapshot = Document.Clone();

        // Nothing to say and nothing stored to correct. A stored document is still rewritten when its
        // last instance goes: that is the user clearing their tags, and it has to stick.
        if (snapshot.Instances.Count == 0 && !_store.Contains(snapshot.Demo.Sha256))
        {
            return;
        }

        try
        {
            await _saveSerializer.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // disposed while this save waited; Dispose already flushed
        }

        try
        {
            if (version <= _lastSavedVersion)
            {
                return; // a newer snapshot already reached the store; this one would undo it
            }

            bool saved = await Task.Run(() => _store.Save(snapshot)).ConfigureAwait(false);
            if (saved)
            {
                _lastSavedVersion = version;
            }

            _saveFailed = !saved;
            SaveCount++;
            StatusText = Describe();
            Changed?.Invoke();
        }
        finally
        {
            _saveSerializer.Release();
        }
    }

    private void CancelDebounce()
    {
        CancellationTokenSource? cts = _debounce;
        _debounce = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private string Describe()
    {
        if (_store is null || Document is null)
        {
            return "session only: tags are not saved";
        }

        if (DemoMismatch)
        {
            return "an existing tag file belongs to a different demo: it will not be touched";
        }

        if (Unreadable)
        {
            return "the tag file could not be read: it will not be overwritten";
        }

        // The browser's store is a dictionary. Naming a destination there would read as a promise the
        // next reload breaks, so the line says what happens instead, in the annotation panel's words.
        if (_isBrowser() || !_store.IsPersistent)
        {
            return _isBrowser()
                ? "session only: this browser tab forgets tags when it reloads"
                : "session only: tags are not saved";
        }

        if (_saveFailed)
        {
            return "tags could not be saved: session only";
        }

        string prefix = ClockMismatch ? "loaded from a different parse: tag spans may be off · " : "";
        return prefix + "saving to " + _store.PathFor(Document.Demo.Sha256);
    }
}

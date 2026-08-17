#region

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Cs2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     How soon the user wants a demo's results. Higher = sooner.
///     <see cref="Foreground" /> never enters the background pump — it is the
///     <see cref="IDemoProcessingQueue.RequestForegroundAsync" /> fast-path (interactive gate slot);
///     the value exists so a coalesced item and the UI can report the highest tier that requested it.
/// </summary>
public enum DemoJobPriority
{
    /// <summary>Auto background work: Library tier-2, Highlights opt-in backfill. Newest-first within.</summary>
    Background = 0,

    /// <summary>
    ///     A user's manual/forced request (e.g. a Highlights "rescan this demo" click). Outranks
    ///     <see cref="Background" /> and bypasses the queue-size cap — a user action is never rejected.
    /// </summary>
    UserRequested = 1,

    /// <summary>A user opening a demo — highest, awaitable, preempts background.</summary>
    Foreground = 2
}

/// <summary>Lifecycle of a queued item (drives the UI badge).</summary>
public enum DemoQueueItemState
{
    /// <summary>Waiting for a worker slot.</summary>
    Queued,

    /// <summary>Being parsed / post-processed right now.</summary>
    Running,

    /// <summary>Parsed and every owner's post-processing ran.</summary>
    Completed,

    /// <summary>The parse threw (corrupt / unreadable). Only this item is affected.</summary>
    Failed,

    /// <summary>Removed by the user or cancelled by its last owner.</summary>
    Cancelled,

    /// <summary>
    ///     Not admitted: the background tier was at <see cref="IDemoProcessingQueue.MaxQueueSize" />.
    ///     The submitter keeps it in its durable backlog and re-submits on
    ///     <see cref="IDemoProcessingQueue.CapacityAvailable" />.
    /// </summary>
    Rejected
}

/// <summary>
///     A background work item. <see cref="OnParsed" /> runs INSIDE the gate
///     slot while the <see cref="ParsedDemo" /> is still held — the memory-safety contract: heavy
///     post-processing (entity-replay score extraction, bare analysis eval) must not run while another
///     parse could start. Coalesced owners' <see cref="OnParsed" /> handlers run sequentially on the one
///     parse.
/// </summary>
/// <param name="Path">The .dem path — the coalescing key AND the file the background worker reads.</param>
/// <param name="OwnerTag">Submitting module identity (per-owner removal + UI display).</param>
/// <param name="Priority">
///     <see cref="DemoJobPriority.Background" /> (auto) or
///     <see cref="DemoJobPriority.UserRequested" /> (manual/forced). Foreground goes via
///     <see cref="IDemoProcessingQueue.RequestForegroundAsync" />.
/// </param>
/// <param name="OrderHint">Within-tier ordering, higher = sooner (file mtime ticks ⇒ newest-first).</param>
/// <param name="OnParsed">Runs inside the slot after a successful parse (the owner's post-processing).</param>
/// <param name="OnFailed">Runs on a parse failure (the owner marks its own row failed). Optional.</param>
/// <param name="DisplayName">Human label for the UI (e.g. file name). Optional.</param>
public sealed record DemoProcessingRequest(
    string Path,
    string OwnerTag,
    DemoJobPriority Priority,
    long OrderHint,
    Action<ParsedDemo> OnParsed,
    Action<Exception>? OnFailed = null,
    string? DisplayName = null);

/// <summary>
///     An immutable, thread-safe snapshot of one queue item (for code/tests that must read state
///     without touching the UI-thread-bound <see cref="IDemoProcessingQueue.Items" /> mirror).
/// </summary>
public sealed record DemoQueueItemSnapshot(
    Guid Id,
    string Path,
    string? DisplayName,
    IReadOnlyList<string> Owners,
    DemoJobPriority Priority,
    DemoQueueItemState State,
    string? Error);

/// <summary>A handle to a submitted background item — read its state, await completion, or cancel it.</summary>
public interface IDemoQueueHandle
{
    /// <summary>The item's id (also the <see cref="IDemoProcessingQueue.RemoveByUser" /> key).</summary>
    Guid Id { get; }

    /// <summary>Live state (thread-safe read).</summary>
    DemoQueueItemState State { get; }

    /// <summary>Completes when the item reaches a terminal state (Completed/Failed/Cancelled/Rejected).</summary>
    Task Completion { get; }

    /// <summary>Cancels THIS owner's submission (per-owner removal — a coalesced co-owner survives).</summary>
    void Cancel();
}

/// <summary>
///     The single global source all background demo parse/analyse work is pulled from
///     (demo-processing-queue). Lives in the shared App project and must COMPILE for WASM — no
///     ASP.NET, no physical-file assumptions in the abstraction, no blocking waits.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The user-facing feature IS a processing queue; the 'Queue' suffix is intentional.")]
public interface IDemoProcessingQueue
{
    // ── Observable state ──────────────────────────────────────────────────────

    /// <summary>
    ///     UI-bindable mirror of the queue (mutated on the post thread). Reconciled by id so item
    ///     identity/selection survive.
    /// </summary>
    ReadOnlyObservableCollection<DemoQueueItem> Items { get; }

    // ── Controls / settings ───────────────────────────────────────────────────

    /// <summary>
    ///     Max concurrent heavy parses (forwards to <see cref="HeavyJobGate" />). DEFAULT 1 — the
    ///     safe one-at-a-time invariant; &gt; 1 can exhaust RAM.
    /// </summary>
    int MaxConcurrency { get; set; }

    /// <summary>Max background-tier items held at once (Foreground/UserRequested bypass it).</summary>
    int MaxQueueSize { get; set; }

    /// <summary>
    ///     Master enable for background processing (the persisted "disable" switch). Foreground
    ///     always runs regardless.
    /// </summary>
    bool BackgroundEnabled { get; set; }

    /// <summary>True while background processing is transiently paused.</summary>
    bool IsPaused { get; }

    // ── Counts (status line) ──────────────────────────────────────────────────

    /// <summary>Items waiting for a slot.</summary>
    int QueuedCount { get; }

    /// <summary>Items being parsed right now.</summary>
    int RunningCount { get; }
    // ── Foreground (awaitable, highest priority) ──────────────────────────────

    /// <summary>
    ///     A user opening a demo — highest priority, AWAITABLE (returns that demo's
    ///     <see cref="ParsedDemo" />), and bypasses pause/disable/size-cap BY CONSTRUCTION. Parses the
    ///     caller's in-hand <paramref name="bytes" /> under the interactive gate slot (preempts
    ///     background between demos; throws <see cref="ReelInProgressException" /> during a reel).
    ///     Best-effort: if a non-null <paramref name="path" /> matches an in-flight item, awaits THAT
    ///     parse instead of starting a redundant one. Correctness never depends on the pump.
    /// </summary>
    Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    // ── Background (fire-and-forget, coalesced) ───────────────────────────────

    /// <summary>
    ///     Submits background work, coalesced BY PATH across owners (one parse, every owner's
    ///     <see cref="DemoProcessingRequest.OnParsed" /> runs). Returns a handle whose
    ///     <see cref="IDemoQueueHandle.State" /> is <see cref="DemoQueueItemState.Rejected" /> ONLY when
    ///     the background tier is at <see cref="MaxQueueSize" /> and this path is not already
    ///     queued/running (Foreground/UserRequested never rejected).
    /// </summary>
    IDemoQueueHandle SubmitBackground(DemoProcessingRequest request);

    /// <summary>A thread-safe immutable snapshot of every item (state reads off the UI thread).</summary>
    IReadOnlyList<DemoQueueItemSnapshot> Snapshot();

    /// <summary>Raised (posted) on any queue state change — for a status line / toolbar refresh.</summary>
    event Action? Changed;

    /// <summary>
    ///     Raised (posted) when the background tier drops below <see cref="MaxQueueSize" /> — feeders
    ///     re-submit their pending backlog (idempotent via coalescing).
    /// </summary>
    event Action? CapacityAvailable;

    // ── Removal ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The user (UI) removes ANY item. Queued → dropped; Running → cancelled after its
    ///     (non-abortable) parse finishes, result discarded, no post-processing.
    /// </summary>
    void RemoveByUser(Guid itemId);

    /// <summary>
    ///     A module cancels ITS OWN submission for <paramref name="path" />; a coalesced co-owner
    ///     keeps the item alive.
    /// </summary>
    void CancelOwned(string ownerTag, string path);

    /// <summary>Pause background processing (transient; in-flight parses finish; foreground unaffected).</summary>
    void Pause();

    /// <summary>Resume background processing.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Pause/Resume is the domain vocabulary for the queue control.")]
    void Resume();
}

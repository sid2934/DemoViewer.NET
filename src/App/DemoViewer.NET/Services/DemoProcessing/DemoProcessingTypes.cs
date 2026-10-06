#region

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     How soon the user wants a demo's results. Higher = sooner.
///     <see cref="Foreground" /> never enters the background pump: it is the
///     <see cref="IDemoProcessingQueue.RequestForegroundAsync" /> fast-path (interactive gate slot);
///     the value exists so a coalesced item and the UI can report the highest tier that requested it.
/// </summary>
public enum DemoJobPriority
{
    /// <summary>Auto background work: Library tier-2, Highlights opt-in backfill. Newest-first within.</summary>
    Background = 0,

    /// <summary>
    ///     A user's manual/forced request (e.g. a Highlights "rescan this demo" click). Outranks
    ///     <see cref="Background" /> and bypasses the queue-size cap: a user action is never rejected.
    /// </summary>
    UserRequested = 1,

    /// <summary>A user opening a demo, highest, awaitable, preempts background.</summary>
    Foreground = 2
}

/// <summary>What a queue item does. Every heavy background job in the app is one of these.</summary>
public enum QueueJobKind
{
    /// <summary>A demo parse and its owners' post-processing (<see cref="IDemoProcessingQueue.SubmitBackground" />).</summary>
    DemoProcessing,

    /// <summary>A user-started Pack Export.</summary>
    PackExport,

    /// <summary>The one-off re-encode of pre-gzip sidecars.</summary>
    SidecarMigration,

    /// <summary>The heap compaction after the queue drains.</summary>
    HeapCompaction,

    /// <summary>A store writing its file. Light: needs no heavy slot and runs beside a parse.</summary>
    StoreSave,

    /// <summary>A store reading its file at startup. Light.</summary>
    StoreLoad,

    /// <summary>A section building what it shows. Light.</summary>
    SectionCompute,

    /// <summary>
    ///     A job an extension submitted. Its label, rank and light flag come from the extension's declared
    ///     kind, named by <see cref="QueueJobRequest.ExtensionKind" />.
    /// </summary>
    Extension,

    /// <summary>The library's copy detection and header reads. Runs with the background switch off.</summary>
    LibraryScan,

    /// <summary>An extension feed check, a download being staged, or the staging cleanup at startup. Light.</summary>
    ExtensionUpdate,

    /// <summary>
    ///     A user opening a demo (<see cref="IDemoProcessingQueue.BeginOpen" />). It sits at the front, ignores
    ///     pause and the background switch, and no other heavy item starts while it is active.
    /// </summary>
    DemoOpen,

    /// <summary>The scheduler asking every pass about the demos marked dirty, then submitting their visits. Light.</summary>
    Scheduling,

    /// <summary>The record passes reading cached records, no demo file. Light.</summary>
    RecordPass,

    /// <summary>
    ///     A slice of one library folder's walk. Light: it holds no heavy slot, and a folder read that does not
    ///     answer within a moment ends the slice instead of holding the lane while it waits.
    /// </summary>
    LibraryListing
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
///     slot while the <see cref="ParsedDemo" /> is still held, the memory-safety contract: heavy
///     post-processing (entity-replay score extraction, bare analysis eval) must not run while another
///     parse could start. Coalesced owners' <see cref="OnParsed" /> handlers run sequentially on the one
///     parse.
/// </summary>
/// <param name="Path">The .dem path, the coalescing key AND the file the background worker reads.</param>
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
/// <param name="NeedsUserCommands">
///     False when <see cref="OnParsed" /> never reads user commands. The parse leaves them out only when no
///     owner on the entry needs them.
/// </param>
/// <param name="OnForward">
///     The owner's post-processing on a forward pass, or null when it needs the retained parse. The entry is
///     read forward only when every owner on it supplies one.
/// </param>
/// <param name="ForwardNeeds">What the forward pass must produce for <see cref="OnForward" />.</param>
public sealed record DemoProcessingRequest(
    string Path,
    string OwnerTag,
    DemoJobPriority Priority,
    long OrderHint,
    Action<ParsedDemo> OnParsed,
    Action<Exception>? OnFailed = null,
    string? DisplayName = null,
    bool NeedsUserCommands = true,
    Action<ForwardDemoResult>? OnForward = null,
    ForwardNeeds ForwardNeeds = ForwardNeeds.None);

/// <summary>What a running <see cref="QueueJobRequest" /> body gets from the queue.</summary>
public interface IQueueJobContext
{
    /// <summary>Fires when the user cancels the item or the app shuts down.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Progress for the list: <paramref name="done" /> of <paramref name="total" />, and a short detail.</summary>
    void Report(int done, int total, string? detail = null);

    /// <summary>
    ///     Releases the heavy-job slot, waits until a background slot is free again (an interactive open, a reel
    ///     or an export goes first) and takes it back. Call between batches of a long job.
    /// </summary>
    Task StepAsideAsync();

    /// <summary>
    ///     Gives the slot up for the rest of the item, for a body that takes its own gate slots. The item still
    ///     counts as the one running job.
    /// </summary>
    void ReleaseSlot();

    /// <summary>Tells the queue this item parsed a demo, so it compacts the heap after it.</summary>
    void NoteDemoParsed()
    {
    }
}

/// <summary>
///     A queue item that is not a demo parse: it runs <see cref="RunAsync" /> on the queue worker, holding a
///     background heavy-job slot. It runs exclusively: it starts only when nothing else is running, and nothing
///     starts while it runs, whatever MaxConcurrency allows demo parses.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Title">The line the queue list shows.</param>
/// <param name="OwnerTag">Submitting module.</param>
/// <param name="Priority"><see cref="DemoJobPriority.Background" /> or <see cref="DemoJobPriority.UserRequested" />.</param>
/// <param name="RunAsync">The work. Must not take a heavy-job gate slot while it holds the queue's.</param>
/// <param name="Key">
///     A submit with the same kind and key while one is still queued joins it instead of adding another; while
///     one is running it queues one rerun, which later submits join.
/// </param>
/// <param name="Target">A file the item is about (the row tooltip), or null.</param>
/// <param name="OrderHint">Within a priority and kind, higher = sooner.</param>
/// <param name="ReplacePending">A keyed submit replaces the queued item's work instead of keeping the first.</param>
/// <param name="Preemptible">
///     Whether a user's item may stop it. False for work that cannot stop part-way (a file write), which
///     would otherwise keep running beside the user's item while counted as stopped.
/// </param>
/// <param name="Serial">Items sharing it never run at the same time; one runs, the rest wait.</param>
/// <param name="ExtensionKind">With <see cref="QueueJobKind.Extension" />, the declared kind id that labels and ranks the item.</param>
/// <param name="Level">
///     Where it sits among items of its <paramref name="Priority" />: <see cref="PassLevel.Backlog" /> runs ahead of
///     other background work. Null takes <see cref="PassLevel.UserRequested" /> for a user's item and
///     <see cref="PassLevel.Background" /> otherwise.
/// </param>
public sealed record QueueJobRequest(
    QueueJobKind Kind,
    string Title,
    string OwnerTag,
    DemoJobPriority Priority,
    Func<IQueueJobContext, Task> RunAsync,
    string? Key = null,
    string? Target = null,
    long OrderHint = 0,
    bool ReplacePending = false,
    bool Preemptible = true,
    string? Serial = null,
    string? ExtensionKind = null,
    PassLevel? Level = null);

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
    string? Error,
    QueueJobKind Kind = QueueJobKind.DemoProcessing,
    double? Progress = null,
    string? Detail = null,
    string? ExtensionKind = null);

/// <summary>A handle to a submitted background item: read its state, await completion, or cancel it.</summary>
public interface IDemoQueueHandle
{
    /// <summary>The item's id (also the <see cref="IDemoProcessingQueue.RemoveByUser" /> key).</summary>
    Guid Id { get; }

    /// <summary>Live state (thread-safe read).</summary>
    DemoQueueItemState State { get; }

    /// <summary>Completes when the item reaches a terminal state (Completed/Failed/Cancelled/Rejected).</summary>
    Task Completion { get; }

    /// <summary>Cancels THIS owner's submission (per-owner removal, a coalesced co-owner survives).</summary>
    void Cancel();
}

/// <summary>
///     The shell's loaded demo, lent to the queue so a visit of that demo runs on the parse the shell already
///     holds instead of reading the file again. The shell hands out a hold only while the demo is loaded and
///     waits for every hold to end before it releases the parse.
/// </summary>
public interface IShellDemoLease
{
    /// <summary>
    ///     The held parse of <paramref name="path" /> when it is the loaded demo, else null. The caller
    ///     disposes the hold when its passes are done.
    /// </summary>
    IHeldParse? TryHold(string path);

    /// <summary>The loaded demo's path without taking a hold, or null. Read under the queue's lock: no work, no locks.</summary>
    string? LoadedPath => null;
}

/// <summary>A hold on the shell's parse: the parse stays loaded until this is disposed.</summary>
public interface IHeldParse : IDisposable
{
    /// <summary>The loaded demo's parse, decoded with every message category.</summary>
    ParsedDemo Parsed { get; }
}

/// <summary>
///     The single global source all background demo parse/analyse work is pulled from
///     (demo-processing-queue). Lives in the shared App project and must COMPILE for WASM: no
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
    ///     Max concurrent heavy parses (forwards to <see cref="HeavyJobGate" />). DEFAULT 1: the
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

    /// <summary>
    ///     The shell's loaded demo. When set, a visit of that demo runs its passes on the held parse and reads
    ///     nothing. A stand-in queue ignores it.
    /// </summary>
    IShellDemoLease? ShellDemo
    {
        get => null;
        set
        {
        }
    }

    // ── Counts (status line) ──────────────────────────────────────────────────

    /// <summary>Items waiting for a slot.</summary>
    int QueuedCount { get; }

    /// <summary>Items being parsed right now.</summary>
    int RunningCount { get; }

    /// <summary>Queued plus running items of one kind.</summary>
    int ActiveCount(QueueJobKind kind);

    /// <summary>Queued plus running items of one extension-declared kind.</summary>
    int ActiveCount(string extensionKind) => 0;
    // ── Foreground (awaitable, highest priority) ──────────────────────────────

    /// <summary>
    ///     A user opening a demo, highest priority, AWAITABLE (returns that demo's
    ///     <see cref="ParsedDemo" />), and bypasses pause/disable/size-cap BY CONSTRUCTION. Parses the
    ///     caller's in-hand <paramref name="bytes" /> under the interactive gate slot (preempts
    ///     background between demos; throws <see cref="ReelInProgressException" /> during a reel).
    ///     Best-effort: if a non-null <paramref name="path" /> matches an in-flight item, awaits THAT
    ///     parse instead of starting a redundant one. Correctness never depends on the pump.
    /// </summary>
    Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts a user's demo open as a <see cref="QueueJobKind.DemoOpen" /> item at the front of the queue.
    ///     A newer open replaces this one. The caller runs the stages and ends the item through the ticket. A
    ///     queued visit of the same demo, and one submitted while the open is active, waits for the open and
    ///     runs on its parse through <see cref="IDemoOpenTicket.RunPassesAsync" /> instead of reading the file.
    /// </summary>
    /// <param name="path">The demo's path, the key for joining a running parse of it; null when it has none.</param>
    /// <param name="fileName">The file name the list shows.</param>
    IDemoOpenTicket BeginOpen(string? path, string fileName) =>
        new PassThroughDemoOpen((bytes, ct) => RequestForegroundAsync(path, bytes, ct));

    // ── Background (fire-and-forget, coalesced) ───────────────────────────────

    /// <summary>
    ///     Submits background work, coalesced BY PATH across owners (one parse, every owner's
    ///     <see cref="DemoProcessingRequest.OnParsed" /> runs). Returns a handle whose
    ///     <see cref="IDemoQueueHandle.State" /> is <see cref="DemoQueueItemState.Rejected" /> ONLY when
    ///     the background tier is at <see cref="MaxQueueSize" /> and this path is not already
    ///     queued/running (Foreground/UserRequested never rejected).
    /// </summary>
    IDemoQueueHandle SubmitBackground(DemoProcessingRequest request);

    /// <summary>
    ///     Submits passes for one demo's visit, coalesced by path: a queued visit of the demo takes them, a
    ///     running one takes those its read can serve, and the demo is read once in the mode the passes on it
    ///     need. In the slot the passes run in <see cref="IDemoPass.After" /> order, each asked again first.
    ///     Rejected under the same size cap as <see cref="SubmitBackground" />. The scheduler submits every
    ///     demo through this member, so a stand-in queue must handle it.
    /// </summary>
    IDemoQueueHandle SubmitVisit(DemoVisitRequest request);

    /// <summary>
    ///     Submits a job that is not a demo parse. It is never rejected for size, obeys pause and cancel, and runs
    ///     exclusively: never beside another item, even when demo parses may run side by side.
    /// </summary>
    IDemoQueueHandle SubmitJob(QueueJobRequest request);

    /// <summary>A thread-safe immutable snapshot of every item (state reads off the UI thread).</summary>
    IReadOnlyList<DemoQueueItemSnapshot> Snapshot();

    /// <summary>Raised (posted) on any queue state change, for a status line / toolbar refresh.</summary>
    event Action? Changed;

    /// <summary>
    ///     Raised (posted) when the background tier drops below <see cref="MaxQueueSize" />: feeders
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

    /// <summary>
    ///     A module cancels EVERY submission it owns: its queued jobs are dropped, a running one is told
    ///     through its token and finishes its current unit, and its attachment leaves every coalesced parse
    ///     (a co-owner keeps the parse alive). An open is never touched.
    /// </summary>
    void CancelOwned(string ownerTag);

    /// <summary>
    ///     Takes one pass off the queued or running visit of <paramref name="path" />, leaving the visit's other
    ///     passes, and ends the visit when nothing else wants it. A pass already taking its turn is not stopped
    ///     here; it sees its own cancellation.
    /// </summary>
    void CancelPass(string path, IDemoPass pass)
    {
    }

    /// <summary>Pause background processing (transient; in-flight parses finish; foreground unaffected).</summary>
    void Pause();

    /// <summary>Resume background processing.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Pause/Resume is the domain vocabulary for the queue control.")]
    void Resume();
}

/// <summary>A user's demo open as a queue item, driven by the caller from start to end.</summary>
public interface IDemoOpenTicket : IDisposable
{
    /// <summary>Fires when a newer open replaces this one, the user removes it, or the app shuts down.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>True when a newer open replaced this one; that open owns the shell from then on.</summary>
    bool IsSuperseded { get; }

    /// <summary>
    ///     Parses the bytes in hand, or joins a running parse of the same demo. Waits while a heavy item that
    ///     cannot stop holds the slot. Throws <see cref="OperationCanceledException" /> once cancelled.
    /// </summary>
    Task<ParsedDemo> ParseAsync(ReadOnlyMemory<byte> bytes);

    /// <summary>The stage the list shows, and the fraction done.</summary>
    void Report(double progress, string stage);

    /// <summary>
    ///     Runs the demo's visit on the shell's parse, from a worker: first <paramref name="plan" />, which may
    ///     submit the demo's passes (they queue behind this open), then every pass queued behind this open, in
    ///     <see cref="IDemoPass.After" /> order. Those passes read the parse in hand instead of the file. The
    ///     task completes when they have run; the caller then ends the item.
    /// </summary>
    /// <param name="parsed">The parse this open produced or joined.</param>
    /// <param name="plan">Plans the demo's visit at the open level, or null when nothing plans visits.</param>
    Task RunPassesAsync(ParsedDemo parsed, Action? plan = null);

    /// <summary>Ends the item as completed, or cancelled when it was cancelled.</summary>
    void Complete();

    /// <summary>Ends the item as failed.</summary>
    void Fail(Exception failure);
}

/// <summary>An open with no queue item: a host without the queue, or a queue double.</summary>
public sealed class PassThroughDemoOpen(Func<ReadOnlyMemory<byte>, CancellationToken, Task<ParsedDemo>> parse)
    : IDemoOpenTicket
{
    public CancellationToken CancellationToken => CancellationToken.None;

    public bool IsSuperseded => false;

    public Task<ParsedDemo> ParseAsync(ReadOnlyMemory<byte> bytes) => parse(bytes, CancellationToken.None);

    public void Report(double progress, string stage)
    {
    }

    /// <inheritdoc />
    public Task RunPassesAsync(ParsedDemo parsed, Action? plan = null) =>
        plan is null ? Task.CompletedTask : Task.Run(plan);

    public void Complete()
    {
    }

    public void Fail(Exception failure)
    {
    }

    public void Dispose()
    {
    }
}

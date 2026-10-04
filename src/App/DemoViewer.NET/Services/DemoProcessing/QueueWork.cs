namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Runs a piece of off-UI-thread work as an item of the processing queue, so the user sees it and can
///     pause or remove it. A host with no queue (the browser, most tests) runs it on the pool instead.
/// </summary>
public static class QueueWork
{
    /// <summary>Submits <paramref name="work" /> and returns a task that completes when it ran or never will.</summary>
    /// <param name="queue">The processing queue, or null to run on the pool.</param>
    /// <param name="kind">The item's kind; the light kinds run beside a parse.</param>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="owner">The submitting module.</param>
    /// <param name="work">The work, given the item's cancellation token.</param>
    /// <param name="priority">UserRequested for work a click asked for: it goes first and stops background work.</param>
    /// <param name="key">One queued item per key; a newer submit replaces the queued one's work.</param>
    /// <param name="preemptible">
    ///     True when the work checks <see cref="ThrowIfStopped" /> (or its token) at its natural boundaries, so
    ///     a user's item may stop it and it runs again later. False for work that cannot stop part-way.
    /// </param>
    /// <param name="serial">Items sharing it never run at the same time.</param>
    /// <param name="extensionKind">With <see cref="QueueJobKind.Extension" />, the declared kind id.</param>
    public static Task Run(IDemoProcessingQueue? queue, QueueJobKind kind, string title, string owner,
        Action<CancellationToken> work, DemoJobPriority priority = DemoJobPriority.Background, string? key = null,
        bool preemptible = false, string? serial = null, string? extensionKind = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (queue is null || Bypass)
        {
            return Task.Run(() => work(CancellationToken.None));
        }

        if (_userAction.Value && priority < DemoJobPriority.UserRequested)
        {
            priority = DemoJobPriority.UserRequested;
        }

        IDemoQueueHandle handle = queue.SubmitJob(new QueueJobRequest(kind, title, owner, priority, ctx =>
        {
            CancellationToken outer = _current.Value;
            _current.Value = ctx.CancellationToken;
            try
            {
                work(ctx.CancellationToken);
            }
            finally
            {
                _current.Value = outer;
            }

            return Task.CompletedTask;
        }, key, ReplacePending: key is not null, Preemptible: preemptible, Serial: serial, ExtensionKind: extensionKind));

        // A disposed queue refuses without running; the work still has to happen.
        return handle.State == DemoQueueItemState.Rejected ? Task.Run(() => work(CancellationToken.None)) : handle.Completion;
    }

    /// <summary>
    ///     <see cref="Run" /> for asynchronous work that reports progress: the body gets the item's own
    ///     <see cref="IQueueJobContext" /> (its token, <see cref="IQueueJobContext.Report" />). A host with no
    ///     queue gets a context whose token never fires and whose report is a no-op. The queue records a body
    ///     that throws as a failed item and still completes the returned task.
    /// </summary>
    /// <param name="queue">The processing queue, or null to run on the pool.</param>
    /// <param name="kind">The item's kind.</param>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="owner">The submitting module.</param>
    /// <param name="work">The work, given the item's context.</param>
    /// <param name="priority">UserRequested for work a click asked for.</param>
    /// <param name="key">One queued item per key; a newer submit replaces the queued one's work.</param>
    public static Task RunJob(IDemoProcessingQueue? queue, QueueJobKind kind, string title, string owner,
        Func<IQueueJobContext, Task> work, DemoJobPriority priority = DemoJobPriority.Background, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (queue is null || Bypass)
        {
            return Task.Run(() => work(PoolContext.Instance));
        }

        if (_userAction.Value && priority < DemoJobPriority.UserRequested)
        {
            priority = DemoJobPriority.UserRequested;
        }

        IDemoQueueHandle handle = queue.SubmitJob(new QueueJobRequest(kind, title, owner, priority, async ctx =>
        {
            CancellationToken outer = _current.Value;
            _current.Value = ctx.CancellationToken;
            try
            {
                await work(ctx).ConfigureAwait(false);
            }
            finally
            {
                _current.Value = outer;
            }
        }, key, ReplacePending: key is not null, Preemptible: false));

        return handle.State == DemoQueueItemState.Rejected ? Task.Run(() => work(PoolContext.Instance)) : handle.Completion;
    }

    private static readonly AsyncLocal<bool> _userAction = new();
    private static readonly AsyncLocal<CancellationToken> _current = new();

    // The context a body gets when there is no queue: nothing to report to, nothing that stops it.
    private sealed class PoolContext : IQueueJobContext
    {
        public static PoolContext Instance { get; } = new();
        public CancellationToken CancellationToken => CancellationToken.None;

        public void Report(int done, int total, string? detail = null)
        {
        }

        public Task StepAsideAsync() => Task.CompletedTask;

        public void ReleaseSlot()
        {
        }
    }

    /// <summary>
    ///     Throws when the queue item running this work has been stopped: by the user, or for a user's item.
    ///     Work run through a delegate that carries no token calls this at its natural boundaries (per demo,
    ///     per card, per section). Outside a queue item it never throws.
    /// </summary>
    public static void ThrowIfStopped() => _current.Value.ThrowIfCancellationRequested();

    /// <summary>True when an exception is the queue stopping the work, which must reach the queue.</summary>
    public static bool IsStop(Exception ex) => ex is OperationCanceledException && _current.Value.IsCancellationRequested;

    /// <summary>
    ///     Marks the work submitted inside the scope, and in what it awaits, as asked for by the user: it
    ///     goes to the front of the queue. Section builds use one runner for a click and for a store
    ///     change; the click's handler opens the scope.
    /// </summary>
    public static IDisposable UserAction()
    {
        bool outer = _userAction.Value;
        _userAction.Value = true;
        return new Scope(() => _userAction.Value = outer);
    }

    /// <summary>Test seam: runs everything on the pool, the behaviour before the queue took this work.</summary>
    internal static bool Bypass { get; set; }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    /// <summary>
    ///     The app's queue, for the few sites that sit too deep to be handed one (a strat's working-copy save,
    ///     a tag document's save). Null, as in tests and on the browser, runs on the pool.
    /// </summary>
    public static IDemoProcessingQueue? Ambient { get; set; }

    /// <summary>
    ///     <see cref="Run" /> with a result: <paramref name="fallback" /> when the item was removed before it
    ///     ran. Unkeyed, so every call runs.
    /// </summary>
    public static async Task<T> RunAsync<T>(IDemoProcessingQueue? queue, QueueJobKind kind, string title, string owner,
        Func<T> work, T fallback, DemoJobPriority priority = DemoJobPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(work);
        T result = fallback;
        Exception? failure = null;
        await Run(queue, kind, title, owner, _ =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex) when (!IsStop(ex))
            {
                failure = ex;
            }
        }, priority).ConfigureAwait(false);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        return result;
    }

    /// <summary>A schedule delegate for a store's <see cref="CoalescedWriter{T}" />: one save item per store.</summary>
    /// <param name="queue">The processing queue, or null for the pool.</param>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="owner">The submitting module.</param>
    /// <param name="key">The store's save key.</param>
    public static Func<Action, Task> Saves(IDemoProcessingQueue? queue, string title, string owner, string key) =>
        drain => Run(queue, QueueJobKind.StoreSave, title, owner, _ => drain(), key: key);

    /// <summary>A fire-and-forget runner for one section's builds: the newest queued build replaces the older.</summary>
    /// <param name="queue">The processing queue, or null for the pool.</param>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="owner">The submitting module.</param>
    /// <param name="key">The section's key.</param>
    public static Action<Action> Section(IDemoProcessingQueue? queue, string title, string owner, string key) =>
        work => _ = Run(queue, QueueJobKind.SectionCompute, title, owner, _ => work(), key: key, preemptible: true);
}

#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Work to run on one demo's parse as part of the demo's visit: it joins a queued or compatible running
///     visit of the demo, runs on the parse the shell holds when the demo is loaded, and otherwise has the
///     demo read once for it and every pass that joins.
/// </summary>
/// <param name="Path">The .dem path.</param>
/// <param name="Label">The owner chip on the queue row.</param>
/// <param name="Owner">Who the work belongs to: <see cref="IDemoProcessingQueue.CancelOwned(string)" /> with it cancels this job.</param>
/// <param name="Level">Why the demo is read. Decides where the visit sits in the queue.</param>
/// <param name="Work">
///     The work. It runs in the visit's slot after the registered passes and holds the slot until its task ends,
///     so it must not wait on another queue item.
/// </param>
/// <param name="UserCommands">True when the work reads player inputs.</param>
/// <param name="DisplayName">The row label, or null for the file name.</param>
public sealed record DemoJobRequest(
    string Path,
    string Label,
    string Owner,
    PassLevel Level,
    Func<DemoJobInput, Task> Work,
    bool UserCommands = false,
    string? DisplayName = null);

/// <summary>What a demo job's work gets: the demo's parse, for as long as its turn lasts.</summary>
public sealed class DemoJobInput
{
    private ParsedDemo? _parsed;

    internal DemoJobInput(string path, ParsedDemo parsed, CancellationToken cancellationToken)
    {
        DemoPath = path;
        _parsed = parsed;
        CancellationToken = cancellationToken;
    }

    /// <summary>The demo.</summary>
    public string DemoPath { get; }

    /// <summary>The parse. Throws once the job's turn has ended, so a kept input cannot hold the parse.</summary>
    public ParsedDemo Parsed => _parsed ?? throw new ObjectDisposedException(nameof(DemoJobInput), "The job's turn on the parse has ended.");

    /// <summary>Fires when the job or its visit is cancelled, or the app shuts down.</summary>
    public CancellationToken CancellationToken { get; }

    internal void End() => _parsed = null;
}

/// <summary>A submitted <see cref="DemoJobRequest" />: its state, its completion and its cancel.</summary>
public sealed class DemoJob
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private CancellationTokenSource? _running;
    private bool _cancelRequested;
    private IDemoProcessingQueue? _queue;
    private DemoJobPass? _pass;
    private string _path = "";
    private int _state = (int)DemoQueueItemState.Queued;

    private DemoJob()
    {
    }

    /// <summary>Queued, running, or how it ended.</summary>
    public DemoQueueItemState State => (DemoQueueItemState)Volatile.Read(ref _state);

    /// <summary>Why it failed, when it did.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Completes when the job ends, however it ends. Never faults.</summary>
    public Task Completion => _done.Task;

    internal bool CancelRequested => Volatile.Read(ref _cancelRequested);

    /// <summary>
    ///     Cancels this job only: a queued job leaves its visit, which another pass may still need; a running
    ///     one sees its token fire.
    /// </summary>
    public void Cancel()
    {
        if (_done.Task.IsCompleted)
        {
            return;
        }

        CancellationTokenSource? running;
        lock (_gate)
        {
            _cancelRequested = true;
            running = _running;
        }

        running?.Cancel();

        if (_queue is { } queue && _pass is { } pass)
        {
            queue.CancelPass(_path, pass);
        }
    }

    /// <summary>Queues <paramref name="request" /> on the demo's visit.</summary>
    public static DemoJob Submit(IDemoProcessingQueue queue, DemoJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        ArgumentNullException.ThrowIfNull(request.Work);

        DemoJob job = new() { _queue = queue, _path = request.Path };
        DemoJobPass pass = new(request, job);
        job._pass = pass;
        IDemoQueueHandle handle = queue.SubmitVisit(new DemoVisitRequest(request.Path, request.Level, [pass], 0,
            request.DisplayName, (_, outcome, error) => job.End(outcome, error)));
        if (handle.State == DemoQueueItemState.Rejected)
        {
            job.Finish(DemoQueueItemState.Rejected, null);
            return job;
        }

        // A visit that ends without giving this pass a turn (removed from the list while queued) ends the job too.
        _ = handle.Completion.ContinueWith(_ => job.Finish(DemoQueueItemState.Cancelled, null),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return job;
    }

    /// <summary>
    ///     Joins the demo's visit and hands out its parse. The visit's slot stays held, so no other demo is read
    ///     beside it, until the returned hold is disposed or the visit is cancelled, which the hold's token tells.
    /// </summary>
    /// <param name="queue">The processing queue.</param>
    /// <param name="path">The .dem path.</param>
    /// <param name="label">The owner chip on the queue row.</param>
    /// <param name="owner">Who the hold belongs to.</param>
    /// <param name="level">Why the demo is read.</param>
    /// <param name="cancellationToken">Cancels the wait, and the hold's visit with it.</param>
    public static async Task<DemoLease> LeaseAsync(IDemoProcessingQueue queue, string path, string label, string owner,
        PassLevel level, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<DemoLease> handed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DemoJob job = Submit(queue, new DemoJobRequest(path, label, owner, level, input =>
        {
            TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DemoLease lease = new(input.Parsed, () => released.TrySetResult(), input.CancellationToken);
            if (!handed.TrySetResult(lease))
            {
                return Task.CompletedTask;
            }

            return released.Task.WaitAsync(input.CancellationToken);
        }));

        await using CancellationTokenRegistration registration = cancellationToken.Register(job.Cancel);
        _ = job.Completion.ContinueWith(_ =>
        {
            if (job.Error is { } error)
            {
                handed.TrySetException(error);
            }
            else
            {
                handed.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await handed.Task.ConfigureAwait(false);
    }

    // The pass's token source while the work runs, so a cancel reaches it; null when the turn ends.
    internal void Running(CancellationTokenSource? source)
    {
        bool cancel;
        lock (_gate)
        {
            _running = source;
            cancel = source is not null && _cancelRequested;
        }

        if (source is not null)
        {
            Interlocked.CompareExchange(ref _state, (int)DemoQueueItemState.Running, (int)DemoQueueItemState.Queued);
        }

        if (cancel)
        {
            source!.Cancel();
        }
    }

    private void End(PassOutcome outcome, Exception? error) =>
        Finish(outcome switch
        {
            PassOutcome.Ran => DemoQueueItemState.Completed,
            PassOutcome.Failed or PassOutcome.ParseFailed => DemoQueueItemState.Failed,
            _ => DemoQueueItemState.Cancelled
        }, outcome is PassOutcome.Failed or PassOutcome.ParseFailed ? error : null);

    private void Finish(DemoQueueItemState state, Exception? error)
    {
        if (_done.Task.IsCompleted)
        {
            return;
        }

        lock (_done)
        {
            if (_done.Task.IsCompleted)
            {
                return;
            }

            Error = error;
            Volatile.Write(ref _state, (int)state);
            _done.SetResult();
        }
    }
}

/// <summary>A demo's parse handed out of its visit; the visit's slot is held until this is disposed.</summary>
public sealed class DemoLease : IHeldParse
{
    private readonly Action _release;
    private int _released;

    internal DemoLease(ParsedDemo parsed, Action release, CancellationToken stopped)
    {
        Parsed = parsed;
        Stopped = stopped;
        _release = release;
    }

    /// <inheritdoc />
    public ParsedDemo Parsed { get; }

    /// <summary>Fires when the visit is cancelled: stop using the parse and dispose.</summary>
    public CancellationToken Stopped { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }
}

// The one-shot pass a demo job rides on: always wants the demo unless cancelled, reads the retained parse,
// runs after every registered pass on the visit.
internal sealed class DemoJobPass(DemoJobRequest request, DemoJob job) : IDemoPass
{
    private readonly PassNeeds _needs = new(ParseMode.Retained, ForwardNeeds.None, request.UserCommands);

    public string Id => request.Label;

    public string Owner => request.Owner;

    public IReadOnlyList<string> After => [];

    public PassNeeds Needs(VisitedDemo demo) => _needs;

    public PassInterest Interest(VisitedDemo demo, PassLevel level) =>
        job.CancelRequested ? PassInterest.No : PassInterest.Yes;

    // Must stay synchronous: it runs in the visit's slot, and the slot is held until the work's task ends.
    public void Run(PassInput input)
    {
        if (input.Retained is not { } parsed)
        {
            throw new InvalidOperationException("A demo job runs on the retained parse.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(input.CancellationToken);
        DemoJobInput context = new(input.Demo.Path, parsed, linked.Token);
        job.Running(linked);
        try
        {
            request.Work(context).GetAwaiter().GetResult();
        }
        finally
        {
            job.Running(null);
            context.End();
        }
    }
}

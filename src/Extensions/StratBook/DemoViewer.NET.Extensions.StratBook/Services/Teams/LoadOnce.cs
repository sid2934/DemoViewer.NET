namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     A store's one-time load, run by whoever gets there first: normally its startup queue item, but a
///     caller that needs the data before that item has started runs it inline rather than waiting on a
///     queue that may be busy or paused. Every save path calls <see cref="Ensure" /> first, so nothing is
///     ever written over a file that has not been read.
/// </summary>
internal sealed class LoadOnce
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _load;
    private int _loadingThread;
    private int _started;

    /// <param name="load">Reads the store. Its exceptions are the store's to handle; one that escapes still completes the load.</param>
    public LoadOnce(Action load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
    }

    /// <summary>Completes once the load has run.</summary>
    public Task Completion => _done.Task;

    /// <summary>True once the load has run.</summary>
    public bool IsDone => _done.Task.IsCompleted;

    /// <summary>Runs the load if nobody has started it, else waits for the one in progress.</summary>
    public void Ensure()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _loadingThread = Environment.CurrentManagedThreadId;
            try
            {
                _load();
            }
            finally
            {
                _loadingThread = 0;
                _done.TrySetResult();
            }

            return;
        }

        // The load itself reaching a guarded member must not wait on itself.
        if (!_done.Task.IsCompleted && _loadingThread != Environment.CurrentManagedThreadId)
        {
            _done.Task.GetAwaiter().GetResult();
        }
    }
}

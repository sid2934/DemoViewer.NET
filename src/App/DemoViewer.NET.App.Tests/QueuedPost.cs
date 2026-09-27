namespace DemoViewer.NET.AppTests;

/// <summary>
///     A <c>post</c> for view models whose workers marshal results back: actions queue until the test
///     drains them on its own thread, the way the dispatcher runs them on the UI thread in the app. An
///     inline post instead runs a worker's collection writes on the worker while the test thread reads the
///     same collection.
/// </summary>
internal sealed class QueuedPost
{
    private readonly Lock _gate = new();
    private readonly Queue<Action> _queue = new();

    /// <summary>Queues an action; pass this as the view model's post delegate.</summary>
    /// <param name="action">What the worker marshals back.</param>
    public void Post(Action action)
    {
        lock (_gate)
        {
            _queue.Enqueue(action);
        }
    }

    /// <summary>Runs every queued action, including any a drained action queues, on the calling thread.</summary>
    public void Drain()
    {
        while (true)
        {
            Action? next;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next))
                {
                    return;
                }
            }

            next();
        }
    }
}

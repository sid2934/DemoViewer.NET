namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     One store's saves: the newest snapshot wins, at most one write is scheduled at a time, and a failed
///     write is logged and never stops later ones. <see cref="Flush" /> writes what is pending on the
///     calling thread and never throws, for shutdown.
/// </summary>
/// <typeparam name="T">The snapshot a write takes; it must not change after it is posted.</typeparam>
public sealed class CoalescedWriter<T> where T : class
{
    private readonly Lock _gate = new();
    private readonly Action<Exception> _failed;
    private readonly Func<Action, Task> _schedule;
    private readonly Action<T> _write;
    private readonly Lock _writeGate = new();
    private int _generation;
    private T? _pending;
    private bool _scheduled;

    /// <param name="write">Writes one snapshot. May throw; the writer logs it through <paramref name="failed" />.</param>
    /// <param name="failed">Told about a write that threw.</param>
    /// <param name="schedule">
    ///     Runs a drain later and completes when it has run or will never run: a processing-queue item in
    ///     the app, the pool without a queue. Null writes inline.
    /// </param>
    public CoalescedWriter(Action<T> write, Action<Exception> failed, Func<Action, Task>? schedule = null)
    {
        _write = write;
        _failed = failed;
        _schedule = schedule ?? (drain =>
        {
            drain();
            return Task.CompletedTask;
        });
    }

    /// <summary>True while a snapshot waits to be written.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Replaces the pending snapshot and makes sure a drain is scheduled.</summary>
    /// <param name="snapshot">What the next write writes.</param>
    public void Post(T snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int generation;
        lock (_gate)
        {
            _pending = snapshot;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            generation = ++_generation;
        }

        Task done;
        try
        {
            done = _schedule(Drain);
        }
        catch (Exception ex)
        {
            _failed(ex);
            done = Task.CompletedTask;
        }

        // A drain that was cancelled or refused before it ran must not leave the writer thinking one is
        // on its way; the pending snapshot waits for the next Post or Flush.
        done.ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_generation == generation)
                {
                    _scheduled = false;
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    ///     Writes the pending snapshot now, waiting at most <paramref name="timeout" /> for a write already
    ///     in progress. Never throws. False when the wait timed out or the write failed.
    /// </summary>
    /// <param name="timeout">How long to wait for an in-progress write; infinite when null.</param>
    public bool Flush(TimeSpan? timeout = null)
    {
        bool entered = false;
        try
        {
            entered = _writeGate.TryEnter(timeout ?? Timeout.InfiniteTimeSpan);
            return entered && WritePendingLocked();
        }
        catch (Exception ex)
        {
            _failed(ex);
            return false;
        }
        finally
        {
            if (entered)
            {
                _writeGate.Exit();
            }
        }
    }

    private void Drain()
    {
        lock (_gate)
        {
            // A Post from here on schedules another drain rather than relying on this one.
            _scheduled = false;
            _generation++;
        }

        lock (_writeGate)
        {
            WritePendingLocked();
        }
    }

    private bool WritePendingLocked()
    {
        T? snapshot;
        lock (_gate)
        {
            snapshot = _pending;
            _pending = null;
        }

        if (snapshot is null)
        {
            return true;
        }

        try
        {
            _write(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _failed(ex);
            return false;
        }
    }
}

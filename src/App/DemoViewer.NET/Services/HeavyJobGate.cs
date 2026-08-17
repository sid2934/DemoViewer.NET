#region

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     The machine-wide heavy-parse invariant, made explicit
///     (docs/demo-processing-queue.md): a 16 GB machine holds at most <see cref="MaxConcurrency" />
///     multi-GB demo parses at a time — DEFAULT 1. Consumers: the global demo-processing queue's
///     background workers, and the shell's interactive demo load.
///     <list type="bullet">
///         <item>
///             <b>Interactive preemption:</b> an interactive acquisition raises a pending flag;
///             background workers yield BETWEEN demos (their acquisition loop re-checks the flag
///             while it is up) so a user's load never waits behind a queue of scans — at most the
///             single in-flight background parse.
///         </item>
///         <item>
///             <b>Reel sessions:</b> reel generation (CS2 + OBS on the same 16 GB) raises an
///             exclusive flag for its whole session: background work pauses, every in-flight parse
///             is drained, and an interactive demo load is REFUSED with a clear message rather than
///             silently queued (<see cref="ReelInProgressException" />).
///         </item>
///         <item>
///             <b>Resizable concurrency:</b> <see cref="MaxConcurrency" />
///             is apply-forward — a change affects newly-started parses, not ones already running —
///             so concurrency is a plain integer compared under this gate's one lock. There is NO
///             semaphore and NO permit accounting to get subtly wrong; at <c>MaxConcurrency == 1</c>
///             every path is behaviourally identical to the historical <c>SemaphoreSlim(1,1)</c> gate.
///             Values &gt; 1 are advanced/opt-in and can exhaust RAM (see docs/demo-processing-queue.md).
///         </item>
///     </list>
///     <para>
///         <b>WASM-safe:</b> every wait is <c>await Task.Delay</c> — no <c>SemaphoreSlim.Wait</c>,
///         no <c>.Result</c> — so the single-threaded browser head never deadlocks. Waits poll every
///         <see cref="PollIntervalMs" /> ms (unchanged from the historical gate); parses take
///         seconds, so the poll latency is negligible.
///     </para>
/// </summary>
public sealed class HeavyJobGate : IDisposable
{
    /// <summary>
    ///     The hard ceiling on <see cref="MaxConcurrency" /> — headroom for a hypothetical big
    ///     machine, NOT a recommendation. Two concurrent multi-GB parses OOM a 16 GB box.
    /// </summary>
    public const int HardCapConcurrency = 8;

    private const int PollIntervalMs = 100;

    private readonly object _sync = new();
    private bool _disposed;
    private int _held;
    private int _interactivePending;
    private int _maxConcurrency = 1;
    private int _reelSessions;

    /// <summary>
    ///     Max concurrent heavy parses. DEFAULT 1 (the safe one-at-a-time invariant). Clamped to
    ///     <c>[1, <see cref="HardCapConcurrency" />]</c>. Apply-forward: growing lets the next
    ///     background poll cycle admit more workers; shrinking lets the excess drain naturally (no
    ///     new start until <c>_held</c> falls below the new max) and takes effect immediately when
    ///     the gate is idle.
    /// </summary>
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
            lock (_sync)
            {
                _maxConcurrency = Math.Clamp(value, 1, HardCapConcurrency);
            }
        }
    }

    /// <summary>True while a reel session owns the machine (background paused, interactive refused).</summary>
    public bool IsReelActive
    {
        get
        {
            lock (_sync)
            {
                return _reelSessions > 0;
            }
        }
    }

    /// <summary>True while an interactive job is waiting or holding — background workers yield.</summary>
    public bool IsInteractivePending
    {
        get
        {
            lock (_sync)
            {
                return _interactivePending > 0;
            }
        }
    }

    /// <summary>Heavy parses currently in flight (interactive + background). Reel drains to 0.</summary>
    public int InFlight
    {
        get
        {
            lock (_sync)
            {
                return _held;
            }
        }
    }

    /// <summary>
    ///     Non-blocking peek used by the queue pump as its budget check: <c>true</c> when a
    ///     background parse could START right now (a free slot, no interactive pending, no reel). The
    ///     authoritative gate check still happens in <see cref="AcquireBackgroundAsync" /> — this
    ///     only spares the pump from spawning a worker that would immediately poll-block.
    /// </summary>
    public bool CanStartBackground
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _reelSessions == 0 && _interactivePending == 0 && _held < _maxConcurrency;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    /// <summary>
    ///     Acquires the gate for an interactive (user-facing) heavy job. Backgrounds yield to it.
    /// </summary>
    /// <exception cref="ReelInProgressException">A reel session is active (refusal policy).</exception>
    public async Task<IDisposable> AcquireInteractiveAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_reelSessions > 0)
            {
                throw new ReelInProgressException();
            }

            // The pending flag is held only while WAITING — it is what makes background yield. Once
            // this job HOLDS a slot, background may use OTHER slots when MaxConcurrency > 1.
            _interactivePending++;
        }

        bool pendingCleared = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_reelSessions > 0)
                    {
                        // A reel started while we waited — same refusal, never a silent queue.
                        _interactivePending--;
                        pendingCleared = true;
                        throw new ReelInProgressException();
                    }

                    if (_held < _maxConcurrency)
                    {
                        _held++;
                        _interactivePending--;
                        pendingCleared = true;
                        return new Releaser(ReleaseHeld);
                    }
                }

                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                if (!pendingCleared)
                {
                    _interactivePending--;
                }
            }

            throw;
        }
    }

    /// <summary>
    ///     Acquires the gate for a background job (a queue worker). Yields rather than queues: while
    ///     an interactive job is pending or a reel session is active or every slot is held, the
    ///     caller keeps polling — so a background worker drains one demo at a time and steps aside at
    ///     the next demo boundary.
    /// </summary>
    public async Task<IDisposable> AcquireBackgroundAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_reelSessions == 0 && _interactivePending == 0 && _held < _maxConcurrency)
                {
                    _held++;
                    return new Releaser(ReleaseHeld);
                }
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Marks a reel session for its whole duration AND drains every in-flight holder: the flag
    ///     goes up first (stops every new acquisition), then we poll until <c>_held == 0</c> —
    ///     The CS2+OBS-vs-parse overlap must not happen even for a background parse that was
    ///     already mid-demo when the reel started. No hold is kept after the drain (a reel doesn't
    ///     parse). Dispose to end.
    /// </summary>
    public async Task<IDisposable> EnterReelSessionAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _reelSessions++;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_held == 0)
                    {
                        break;
                    }
                }

                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                _reelSessions--;
            }

            throw;
        }

        return new Releaser(() =>
        {
            lock (_sync)
            {
                _reelSessions--;
            }
        });
    }

    private void ReleaseHeld()
    {
        lock (_sync)
        {
            _held--;
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                release();
            }
        }
    }
}

/// <summary>
///     An interactive heavy job was refused because a highlight reel is rendering. The
///     message is the user-facing copy.
/// </summary>
public sealed class ReelInProgressException() : InvalidOperationException(
    "A highlight reel is being generated — try again when it finishes.");

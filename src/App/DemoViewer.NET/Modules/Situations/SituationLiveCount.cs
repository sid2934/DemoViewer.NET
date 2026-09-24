#region

using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The live-count contract (round-index.md §3.11) as one object: <see cref="ISituationIndex.Count" />
///     off the UI thread, the caller's requests debounced, the newest request cancelling the pending
///     one, and a stale answer discarded by sequence number rather than shown. The count itself is
///     microseconds; the debounce exists because a token drag raises a document change per pointer
///     move, and the sequence check exists because a request that was already past its delay when the
///     next one arrived still finishes and must not overwrite the newer answer.
/// </summary>
public sealed class SituationLiveCount : IDisposable
{
    /// <summary>About one frame at pointer rate: long enough to fold a drag, short enough to read as live.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _delay;
    private readonly Lock _gate = new();
    private readonly ISituationIndex _index;
    private readonly Action<Action> _post;

    private bool _disposed;
    private CancellationTokenSource? _pending;
    private int _sequence;

    /// <param name="index">The index the count runs against.</param>
    /// <param name="post">Marshals <see cref="Counted" /> onto the UI thread; inline in a test.</param>
    /// <param name="delay">The debounce; zero counts on the next pool hop, which a test wants.</param>
    public SituationLiveCount(ISituationIndex index, Action<Action> post, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(post);
        _index = index;
        _post = post;
        _delay = delay ?? DefaultDelay;
    }

    /// <summary>The answer to the newest request, on the post thread: the query it counted and the count.</summary>
    public event Action<SituationQuery, int>? Counted;

    /// <summary>The newest request's run, for a test to await; completed when nothing is in flight.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
    }

    /// <summary>
    ///     Counts <paramref name="query" /> after the debounce, cancelling whatever was pending. Every
    ///     call moves the sequence, so an older run that slips past its cancellation is discarded when
    ///     it posts.
    /// </summary>
    /// <param name="query">The query as it stands now.</param>
    public void Request(SituationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource cts = new();
        int sequence;
        lock (_gate)
        {
            CancelLocked();
            _pending = cts;
            sequence = ++_sequence;
        }

        Pending = RunAsync(query, sequence, cts.Token);
    }

    /// <summary>Drops the pending request: there is nothing to count (no map, or the index is not ready).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            CancelLocked();
            _sequence++;
        }
    }

    private void CancelLocked()
    {
        if (_pending is null)
        {
            return;
        }

        _pending.Cancel();
        _pending.Dispose();
        _pending = null;
    }

    private async Task RunAsync(SituationQuery query, int sequence, CancellationToken ct)
    {
        try
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();
            int count = _index.Count(query);
            ct.ThrowIfCancellationRequested();

            _post(() =>
            {
                // The sequence is read on the post thread, so a run that finished after a newer request
                // was made sees that request's number and stays silent.
                if (!_disposed && sequence == Volatile.Read(ref _sequence))
                {
                    Counted?.Invoke(query, count);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request, or dropped: nothing to show.
        }
    }
}

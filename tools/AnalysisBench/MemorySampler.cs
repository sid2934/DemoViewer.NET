#region

using System.Diagnostics;

#endregion

namespace AnalysisBench;

/// <summary>
///     Background poller that tracks the high-water marks of managed-heap size and process RSS for the
///     duration of a bench run. Used to prove where the demo buffer lives: a managed <c>byte[]</c> shows
///     up in <see cref="PeakManagedHeapBytes" />, a memory-mapped view does not (it is file-backed OS
///     pages, visible only in RSS, and evictable, unlike heap).
///     <para>
///         Sampling is polling-based, so the reported peaks are lower bounds: a spike shorter than the
///         sample interval can be missed. Compare runs at the same interval.
///     </para>
/// </summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Process _process = Process.GetCurrentProcess();
    private long _peakHeap;
    private long _peakRss;

    private MemorySampler(TimeSpan interval) => _loop = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            Sample();
            try
            {
                await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Sample();
    });

    /// <summary>Highest observed managed heap size (GC heap only, excludes mapped file pages).</summary>
    public long PeakManagedHeapBytes => Interlocked.Read(ref _peakHeap);

    /// <summary>Highest observed process working set (RSS). Includes mapped file pages currently resident.</summary>
    public long PeakRssBytes => Interlocked.Read(ref _peakRss);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Sampling is best-effort telemetry; never fail a bench run over it.
        }

        _cts.Dispose();
        _process.Dispose();
    }

    /// <summary>Starts sampling immediately. Dispose to stop.</summary>
    public static MemorySampler Start(TimeSpan? interval = null) =>
        new(interval ?? TimeSpan.FromMilliseconds(25));

    private void Sample()
    {
        // HeapSizeBytes is the committed managed heap at the last GC-info refresh; GetTotalMemory(false)
        // is the allocated-and-not-yet-collected estimate. Take the larger so a run that never GCs at
        // the sampling instant still reports a meaningful high-water mark.
        long heap = Math.Max(GC.GetGCMemoryInfo().HeapSizeBytes, GC.GetTotalMemory(false));
        Max(ref _peakHeap, heap);

        try
        {
            _process.Refresh();
            Max(ref _peakRss, _process.WorkingSet64);
        }
        catch (InvalidOperationException)
        {
            // Process metrics unavailable on this platform/state: RSS stays at whatever we saw.
        }
    }

    private static void Max(ref long slot, long candidate)
    {
        long seen = Interlocked.Read(ref slot);
        while (candidate > seen)
        {
            long prior = Interlocked.CompareExchange(ref slot, candidate, seen);
            if (prior == seen)
            {
                return;
            }

            seen = prior;
        }
    }
}

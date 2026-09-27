#region

using System.Runtime;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     The one explicit compacting collection in the app: a one-shot LOH compaction plus an aggressive
///     blocking gen2, twice around the finalizer queue. Workstation GC does not compact the LOH on its
///     own, so without this freed demo-sized arrays stay committed as fragmentation.
/// </summary>
public static class HeapCompactor
{
    /// <summary>Runs on the thread pool: the blocking gen2 is long enough to hitch the UI.</summary>
    public static Task CompactAsync() => Task.Run(static () =>
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    });
}

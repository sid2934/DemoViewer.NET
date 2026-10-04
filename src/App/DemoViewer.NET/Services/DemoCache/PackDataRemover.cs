#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Deletes a pack's declared stores: resolves every
///     <see cref="StoreDescriptor" />'s paths against <see cref="StoreRoot.Config" /> or
///     <see cref="StoreRoot.Cache" />, counts what is there, deletes on request, and strips the pack's
///     payload and stamps from every demo cache record and index row. One instance serves every pack; a
///     pack hands it its own id, descriptors and facet ids through <see cref="IExtensionDataRemoval" />.
///     <para>
///         <b>Path safety.</b> A literal path is refused when it is rooted, empty, <c>"."</c>, carries a
///         <c>".."</c> segment, or resolves outside the root (including to the root itself). A demo-sidecar
///         pattern (<c>"demos/*&lt;suffix&gt;"</c>) lists the directory once and matches file names by
///         ordinal suffix in managed code; the suffix is never handed to a filesystem glob, so it cannot
///         widen to match the core record sidecars that live beside it. Deletion never follows a reparse
///         point (file or directory) and never removes a <c>.dem</c> file, whatever a descriptor names.
///     </para>
///     <para>
///         <b>Stamps.</b> A <see cref="DemoCacheRecord.PackStamps" /> id is a facet (an evaluator id), not
///         a pack id; the caller passes the pack's own facet ids (its contributed evaluators). A record is
///         touched only when at least one of its stamps matches, which assumes every pack writer stamps
///         whatever it writes to <see cref="DemoCacheRecord.Packs" /> (true of every existing writer).
///     </para>
///     <para>
///         <b>A file that will not delete</b> (locked, permission denied) is skipped, not thrown: the rest
///         of the pack's stores still go. <see cref="ExtensionDataRemovalResult.Skipped" /> and
///         <see cref="ExtensionDataRemovalResult.FirstSkippedPath" /> carry the count and the first path, and
///         each skip is logged once.
///     </para>
/// </summary>
public sealed class PackDataRemover(DemoCacheStore store, string? configRoot, string? cacheRoot, IDemoProcessingQueue? queue = null)
{
    private static ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ShellCategory);

    /// <summary>
    ///     Counts what is on disk right now; never deletes anything. Runs through the queue, user priority,
    ///     on serial <paramref name="ownerTag" /> so it never overlaps the pack's own release item (which
    ///     shares that serial by the same convention, e.g. <c>StratBookLifecycle.Owner</c>).
    /// </summary>
    /// <param name="descriptors">The pack's declared stores.</param>
    /// <param name="ownerTag">The queue item's owner and serial (cancelled together with the pack's other work).</param>
    /// <param name="title">The queue list's line for this item.</param>
    public Task<ExtensionDataInventory> InventoryAsync(IReadOnlyList<StoreDescriptor> descriptors, string ownerTag, string title) =>
        RunSerial(title, ownerTag, () => Inventory(descriptors), ExtensionDataInventory.Empty);

    /// <summary>
    ///     Deletes every descriptor's files and strips <paramref name="packId" />'s payload and
    ///     <paramref name="facetIds" />'s stamps from the demo cache. Runs through the queue, user priority,
    ///     on serial <paramref name="ownerTag" /> so it never overlaps the pack's own release item; a pack
    ///     re-enabled before this runs cancels it by owner tag first, so it drops without touching anything
    ///     (<see cref="ExtensionDataRemovalResult.Ran" /> is false).
    /// </summary>
    /// <param name="packId">The pack's own id: the key its payload rides <see cref="DemoCacheRecord.Packs" /> under.</param>
    /// <param name="descriptors">The pack's declared stores.</param>
    /// <param name="facetIds">The pack's evaluator ids: which <see cref="DemoCacheRecord.PackStamps" /> entries are its to strip.</param>
    /// <param name="ownerTag">The queue item's owner and serial.</param>
    /// <param name="title">The queue list's line for this item.</param>
    /// <param name="stillOff">
    ///     Evaluated once, inside the queued job, right before any file is touched. False aborts with
    ///     <see cref="ExtensionDataRemovalResult.NotRun" /> and deletes nothing: the caller's own pre-check
    ///     (immediately before calling this) closes the gap before the job is submitted, this one closes
    ///     the gap between submission and the job actually running (it may have sat behind another item on
    ///     the same serial). Null (most callers, every test that does not care) never aborts.
    /// </param>
    public Task<ExtensionDataRemovalResult> DeleteAsync(string packId, IReadOnlyList<StoreDescriptor> descriptors,
        IReadOnlyList<string> facetIds, string ownerTag, string title, Func<bool>? stillOff = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(facetIds);
        return RunSerial(title, ownerTag, () => Delete(packId, descriptors, facetIds, stillOff), ExtensionDataRemovalResult.NotRun);
    }

    // QueueWork.RunAsync has no serial parameter; this is QueueWork.Run plus a captured result, with "ran"
    // kept apart from the task's own completion so a drop (CancelOwned, before it started) answers the
    // fallback rather than a cancellation exception.
    private async Task<T> RunSerial<T>(string title, string ownerTag, Func<T> work, T fallback)
    {
        bool ran = false;
        T result = fallback;
        Task task = QueueWork.Run(queue, QueueJobKind.SectionCompute, title, ownerTag, _ =>
        {
            result = work();
            ran = true;
        }, DemoJobPriority.UserRequested, serial: ownerTag);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return ran ? result : fallback;
    }

    private ExtensionDataInventory Inventory(IReadOnlyList<StoreDescriptor> descriptors)
    {
        List<StoreInventoryItem> items = [];
        foreach (StoreDescriptor descriptor in descriptors)
        {
            Tally tally = new();
            Measure(descriptor, actuallyDelete: false, tally);
            items.Add(new StoreInventoryItem(descriptor, tally.Count, tally.Bytes));
        }

        return new ExtensionDataInventory(items);
    }

    private ExtensionDataRemovalResult Delete(string packId, IReadOnlyList<StoreDescriptor> descriptors, IReadOnlyList<string> facetIds,
        Func<bool>? stillOff)
    {
        if (stillOff?.Invoke() == false)
        {
            return ExtensionDataRemovalResult.NotRun;
        }

        List<StoreInventoryItem> items = [];
        Tally totals = new();
        foreach (StoreDescriptor descriptor in descriptors)
        {
            Tally tally = new();
            Measure(descriptor, actuallyDelete: true, tally);
            items.Add(new StoreInventoryItem(descriptor, tally.Count, tally.Bytes));
            totals.Skipped += tally.Skipped;
            totals.FirstSkippedPath ??= tally.FirstSkippedPath;
        }

        int recordsUpdated = StripRecords(packId, facetIds);
        return new ExtensionDataRemovalResult(true, new ExtensionDataInventory(items), recordsUpdated, totals.Skipped, totals.FirstSkippedPath);
    }

    private void Measure(StoreDescriptor descriptor, bool actuallyDelete, Tally tally)
    {
        string? root = descriptor.Root == StoreRoot.Config ? configRoot : cacheRoot;
        if (root is null)
        {
            return;
        }

        foreach (string entry in descriptor.Paths)
        {
            int star = entry.IndexOf('*', StringComparison.Ordinal);
            if (star < 0)
            {
                MeasureAndDeletePath(ResolveSafe(root, entry), actuallyDelete, tally);
            }
            else
            {
                MeasureAndDeleteSuffixMatches(ResolveSafe(root, entry[..star]), entry[(star + 1)..], actuallyDelete, tally);
            }
        }
    }

    // A literal file or directory. Recurses by hand (never Directory.Delete(path, recursive: true)) so a
    // reparse point is skipped rather than followed, at every level, and a directory is removed only after
    // everything inside it that could be deleted has been.
    private static void MeasureAndDeletePath(string? path, bool actuallyDelete, Tally tally)
    {
        if (path is null || IsReparsePoint(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            if (!path.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
            {
                MeasureAndDeleteFile(path, actuallyDelete, tally);
            }

            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            MeasureAndDeletePath(child, actuallyDelete, tally);
        }

        if (actuallyDelete)
        {
            try
            {
                Directory.Delete(path, false);
            }
            catch (IOException)
            {
                // Something under it survived on purpose (a reparse point, a .dem); leave the folder.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // Every file directly under dir (never a subdirectory: the demo sidecars are flat) whose name ends
    // with suffix by ordinal comparison. The directory's own file list comes from the filesystem with no
    // pattern argument; the suffix match happens here, in managed code, not in a glob.
    private static void MeasureAndDeleteSuffixMatches(string? dir, string suffix, bool actuallyDelete, Tally tally)
    {
        if (dir is null || IsReparsePoint(dir) || !Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MeasureAndDeletePath(file, actuallyDelete, tally);
        }
    }

    private static void MeasureAndDeleteFile(string path, bool actuallyDelete, Tally tally)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException ex)
        {
            Skip(path, "read", ex, tally);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            Skip(path, "read", ex, tally);
            return;
        }

        if (actuallyDelete)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                Skip(path, "delete", ex, tally);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Skip(path, "delete", ex, tally);
                return;
            }
        }

        tally.Count++;
        tally.Bytes += length;
    }

    private static void Skip(string path, string verb, Exception ex, Tally tally)
    {
        tally.Skipped++;
        tally.FirstSkippedPath ??= path;
        AppLog.OperationFailed(Log, verb + " '" + path + "'", ex);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false; // doesn't exist; the caller's own Exists check decides what happens next
        }
        catch (UnauthorizedAccessException)
        {
            return true; // unreadable: refuse rather than guess
        }
    }

    // Refuses a path that is rooted, empty, ".", carries a ".." segment, or resolves outside root
    // (including to root itself, which would otherwise let a descriptor delete the whole app-data root).
    private static string? ResolveSafe(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative == ".")
        {
            return null;
        }

        if (Path.IsPathRooted(relative))
        {
            return null;
        }

        foreach (string segment in relative.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return null;
            }
        }

        string rootFull = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? candidate : null;
    }

    // Strips packId's payload and every stamp whose id is in facetIds from every record that has one, and
    // the mirrored index row with it (DemoCacheStore.Upsert re-derives DemoCacheIndexEntry.PackStamps from
    // the record on every write). A row whose sidecar will not load is skipped: UpdateExisting would create
    // a fresh record for a path that has none, which a delete must never do.
    private int StripRecords(string packId, IReadOnlyList<string> facetIds)
    {
        HashSet<string> facets = new(facetIds, StringComparer.Ordinal);
        int updated = 0;
        using (store.BeginBatch())
        {
            foreach (DemoCacheIndexEntry entry in store.Index)
            {
                if (!entry.PackStamps.Any(s => facets.Contains(s.Id)) || store.TryLoadRecord(entry.Path) is null)
                {
                    continue;
                }

                store.UpdateExisting(entry.Path, record =>
                {
                    record.Packs.Remove(packId);
                    record.PackStamps.RemoveAll(s => facets.Contains(s.Id));
                });
                updated++;
            }
        }

        store.SaveIndex();
        return updated;
    }

    // Mutable accumulator threaded through the recursive walk: count and bytes of what moved (or would),
    // plus how many entries were skipped and the first one's path, for the status line.
    private sealed class Tally
    {
        public int Count;
        public long Bytes;
        public int Skipped;
        public string? FirstSkippedPath;
    }
}

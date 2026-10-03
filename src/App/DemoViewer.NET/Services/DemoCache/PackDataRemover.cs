#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Deletes a pack's declared stores (architecture doc §7.4, §8, item 24): resolves every
///     <see cref="StoreDescriptor" />'s paths against <see cref="StoreRoot.Config" /> or
///     <see cref="StoreRoot.Cache" />, counts what is there, deletes on request, and strips the pack's
///     payload and stamps from every demo cache record and index row. One instance serves every pack; a
///     pack hands it its own id, descriptors and facet ids through <see cref="IPackDataRemoval" />.
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
/// </summary>
public sealed class PackDataRemover(DemoCacheStore store, string? configRoot, string? cacheRoot, IDemoProcessingQueue? queue = null)
{
    /// <summary>Counts what is on disk right now; never deletes anything. Runs through the queue, user priority.</summary>
    /// <param name="descriptors">The pack's declared stores.</param>
    /// <param name="ownerTag">The queue item's owner (cancelled together with the pack's other work).</param>
    /// <param name="title">The queue list's line for this item.</param>
    public Task<PackDataInventory> InventoryAsync(IReadOnlyList<StoreDescriptor> descriptors, string ownerTag, string title) =>
        QueueWork.RunAsync(queue, QueueJobKind.SectionCompute, title, ownerTag, () => Inventory(descriptors),
            PackDataInventory.Empty, DemoJobPriority.UserRequested);

    /// <summary>
    ///     Deletes every descriptor's files and strips <paramref name="packId" />'s payload and
    ///     <paramref name="facetIds" />'s stamps from the demo cache. Runs through the queue, user priority,
    ///     owned by <paramref name="ownerTag" /> so a pack re-enabled before this runs drops it instead
    ///     (<see cref="PackDataRemovalResult.Ran" /> is false, nothing is touched).
    /// </summary>
    public Task<PackDataRemovalResult> DeleteAsync(string packId, IReadOnlyList<StoreDescriptor> descriptors,
        IReadOnlyList<string> facetIds, string ownerTag, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(facetIds);
        return QueueWork.RunAsync(queue, QueueJobKind.SectionCompute, title, ownerTag,
            () => Delete(packId, descriptors, facetIds), PackDataRemovalResult.NotRun, DemoJobPriority.UserRequested);
    }

    private PackDataInventory Inventory(IReadOnlyList<StoreDescriptor> descriptors) =>
        new([.. descriptors.Select(d => Measure(d, actuallyDelete: false))]);

    private PackDataRemovalResult Delete(string packId, IReadOnlyList<StoreDescriptor> descriptors, IReadOnlyList<string> facetIds)
    {
        PackDataInventory removed = new([.. descriptors.Select(d => Measure(d, actuallyDelete: true))]);
        int recordsUpdated = StripRecords(packId, facetIds);
        return new PackDataRemovalResult(true, removed, recordsUpdated);
    }

    private StoreInventoryItem Measure(StoreDescriptor descriptor, bool actuallyDelete)
    {
        string? root = descriptor.Root == StoreRoot.Config ? configRoot : cacheRoot;
        if (root is null)
        {
            return new StoreInventoryItem(descriptor, 0, 0);
        }

        int count = 0;
        long bytes = 0;
        foreach (string entry in descriptor.Paths)
        {
            int star = entry.IndexOf('*', StringComparison.Ordinal);
            (int c, long b) = star < 0
                ? MeasureAndDeletePath(ResolveSafe(root, entry), actuallyDelete)
                : MeasureAndDeleteSuffixMatches(ResolveSafe(root, entry[..star]), entry[(star + 1)..], actuallyDelete);
            count += c;
            bytes += b;
        }

        return new StoreInventoryItem(descriptor, count, bytes);
    }

    // A literal file or directory. Recurses by hand (never Directory.Delete(path, recursive: true)) so a
    // reparse point is skipped rather than followed, at every level, and a directory is removed only after
    // everything inside it that could be deleted has been.
    private static (int Count, long Bytes) MeasureAndDeletePath(string? path, bool actuallyDelete)
    {
        if (path is null || IsReparsePoint(path))
        {
            return (0, 0);
        }

        if (File.Exists(path))
        {
            return path.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) ? (0, 0) : MeasureAndDeleteFile(path, actuallyDelete);
        }

        if (!Directory.Exists(path))
        {
            return (0, 0);
        }

        int count = 0;
        long bytes = 0;
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            (int c, long b) = MeasureAndDeletePath(child, actuallyDelete);
            count += c;
            bytes += b;
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

        return (count, bytes);
    }

    // Every file directly under dir (never a subdirectory: the demo sidecars are flat) whose name ends
    // with suffix by ordinal comparison. The directory's own file list comes from the filesystem with no
    // pattern argument; the suffix match happens here, in managed code, not in a glob.
    private static (int Count, long Bytes) MeasureAndDeleteSuffixMatches(string? dir, string suffix, bool actuallyDelete)
    {
        if (dir is null || IsReparsePoint(dir) || !Directory.Exists(dir))
        {
            return (0, 0);
        }

        int count = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (int c, long b) = MeasureAndDeletePath(file, actuallyDelete);
            count += c;
            bytes += b;
        }

        return (count, bytes);
    }

    private static (int Count, long Bytes) MeasureAndDeleteFile(string path, bool actuallyDelete)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return (0, 0);
        }

        if (actuallyDelete)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                return (0, 0);
            }
            catch (UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }

        return (1, length);
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
}

#region

using System.Text.Json;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library's folder walk and the stale-row prune it feeds: a row is dropped only on evidence that its
///     demo is gone, never because a folder or part of one could not be listed. Real files in temp folders; the
///     reader seam fails or stalls chosen folders.
/// </summary>
[NotInParallel]
public class DemoLibraryScanTests
{
    private static readonly Action<Action> _inline = a => a();

    [Test]
    public async Task ASubdirectoryThatFailsToList_KeepsItsRows_WhileADeletedDemoIsPruned()
    {
        string root = NewTempDir();
        try
        {
            string kept = WriteDemo(root, "ok/a.dem", 3);
            string gone = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "ok")).FullName, "gone.dem");
            string hidden = WriteDemo(root, "bad/b.dem", 4);
            string bad = Path.GetDirectoryName(hidden)!;
            string dataPath = SeedLibrary(root, [root], Row(kept), Row(hidden), Row(gone, 9));

            using DemoLibraryService svc = new(_inline, dataPath);
            svc.FolderReader = new ScriptedReader { FailRead = d => d == bad ? new IOException("stale NFS file handle") : null };
            await svc.RescanAsync();

            string[] rows = CachedPaths(svc, dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(kept);
                await Assert.That(rows).Contains(hidden).Because("a subdirectory that could not be listed is no evidence");
                await Assert.That(rows).DoesNotContain(gone).Because("the rest of the folder was read and the demo is not in it");
                await Assert.That(svc.Entries.Select(e => e.FilePath)).IsEquivalentTo([kept]);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task ARootThatCannotBeListed_PrunesNothing()
    {
        string root = NewTempDir();
        try
        {
            string a = WriteDemo(root, "a.dem", 3);
            string gone = Path.Combine(root, "gone.dem");
            string dataPath = SeedLibrary(root, [root], Row(a), Row(gone, 9));

            using DemoLibraryService svc = new(_inline, dataPath);
            svc.FolderReader = new ScriptedReader { FailRead = d => d == root ? new IOException("Operation timed out") : null };
            await svc.RescanAsync();

            await Assert.That(CachedPaths(svc, dataPath)).IsEquivalentTo(Sorted(a, gone));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // An automount point, or a mount point whose volume is not up, exists and lists empty.
    [Test]
    public async Task AnEmptyFolderWithCachedRows_IsNotReached_AndPrunesNothing()
    {
        string root = NewTempDir();
        string other = NewTempDir();
        try
        {
            string mount = Directory.CreateDirectory(Path.Combine(root, "mnt")).FullName;
            string[] cached = [Path.Combine(mount, "x.dem"), Path.Combine(mount, "sub", "y.dem")];
            string local = WriteDemo(other, "local.dem", 5);
            string localGone = Path.Combine(other, "deleted.dem");
            string dataPath = SeedLibrary(other, [mount, other], Row(cached[0], 7), Row(cached[1], 8), Row(local),
                Row(localGone, 6));

            using DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();

            string[] rows = CachedPaths(svc, dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(cached[0]);
                await Assert.That(rows).Contains(cached[1]);
                await Assert.That(rows).DoesNotContain(localGone).Because("the other folder was reached");
            }
        }
        finally
        {
            Cleanup(root);
            Cleanup(other);
        }
    }

    [Test]
    public async Task AMissingFolder_KeepsItsRows_AndARemovedFolderLosesThem()
    {
        string root = NewTempDir();
        try
        {
            string present = WriteDemo(root, "here/a.dem", 3);
            string detached = Path.Combine(root, "volume");
            string onDetached = Path.Combine(detached, "b.dem");
            string removed = Path.Combine(root, "removed", "c.dem");
            string dataPath = SeedLibrary(root, [Path.GetDirectoryName(present)!, detached], Row(present), Row(onDetached, 4),
                Row(removed, 5));

            using DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();

            string[] rows = CachedPaths(svc, dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(onDetached).Because("a registered folder that is not there is detached, not emptied");
                await Assert.That(rows).DoesNotContain(removed).Because("no registered folder covers it");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // A folder that never resolved this session may be a link whose real path covers any row.
    [Test]
    public async Task AFolderThatNeverResolved_SuspendsTheOutOfScopePrune()
    {
        string root = NewTempDir();
        try
        {
            string reachable = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
            string a = WriteDemo(reachable, "a.dem", 3);
            string gone = Path.Combine(reachable, "gone.dem");
            string hung = Path.Combine(root, "nfs");
            string elsewhere = Path.Combine(root, "elsewhere", "x.dem");
            string dataPath = SeedLibrary(root, [reachable, hung], Row(a), Row(gone, 4), Row(elsewhere, 5));

            using DemoLibraryService svc = new(_inline, dataPath);
            svc.FolderReader = new ScriptedReader { FailResolve = f => f == hung ? new IOException("Operation timed out") : null };
            await svc.RescanAsync();

            string[] rows = CachedPaths(svc, dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(elsewhere);
                await Assert.That(rows).DoesNotContain(gone);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The copy's row carries its hash; dropping it would re-read both files on every scan.
    [Test]
    public async Task ACopyElsewhereInTheLibrary_KeepsItsRow()
    {
        string root = NewTempDir();
        try
        {
            string first = WriteDemo(root, "a/m.dem", 6, 9);
            string copy = WriteDemo(root, "b/m.dem", 6, 9);
            string dataPath = SeedLibrary(root, [root], Row(first), Row(copy));

            using DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();

            using (Assert.Multiple())
            {
                await Assert.That(svc.Entries.Count).IsEqualTo(1);
                await Assert.That(CachedPaths(svc, dataPath)).IsEquivalentTo(Sorted(first, copy));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task AFastFolder_ShowsItsDemos_WhileASlowOneIsStillListing()
    {
        string fast = NewTempDir();
        string slow = NewTempDir();
        GatedReader reader = new(slow);
        try
        {
            string a = WriteDemo(fast, "a.dem", 3);
            string b = WriteDemo(slow, "b.dem", 4);
            using DemoLibraryService svc = new(_inline, SeedLibrary(fast, [slow, fast]));
            svc.FolderReader = reader;
            svc.Timing = Timing(TimeSpan.FromSeconds(30));

            Task scan = svc.RescanAsync();
            await WaitForAsync(() => EntryPaths(svc).Contains(a), "the fast folder's demo");
            await Assert.That(scan.IsCompleted).IsFalse().Because("the slow folder has not answered yet");

            reader.Release();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(EntryPaths(svc)).IsEquivalentTo(Sorted(a, b));
        }
        finally
        {
            reader.Release();
            Cleanup(fast);
            Cleanup(slow);
        }
    }

    [Test]
    public async Task AFolderThatDoesNotAnswer_IsNotReached_IsNotReadAgainWhileHung_AndIsListedWhenItAnswers()
    {
        string fast = NewTempDir();
        string slow = NewTempDir();
        GatedReader reader = new(slow);
        try
        {
            string a = WriteDemo(fast, "a.dem", 3);
            string b = WriteDemo(slow, "b.dem", 4);
            string gone = Path.Combine(slow, "gone.dem");
            string dataPath = SeedLibrary(fast, [slow, fast], Row(b), Row(gone, 5));
            using DemoLibraryService svc = new(_inline, dataPath);
            svc.FolderReader = reader;
            svc.Timing = Timing(TimeSpan.FromMilliseconds(300));

            await svc.RescanAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.Multiple())
            {
                await Assert.That(EntryPaths(svc)).IsEquivalentTo([a]);
                await Assert.That(CachedPaths(svc, dataPath)).IsEquivalentTo(Sorted(b, gone)).Because("nothing under it is pruned");
            }

            await svc.RescanAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(reader.BlockedReads).IsEqualTo(1).Because("the hung read is waited on, not repeated");

            reader.Release();
            await WaitForAsync(() => EntryPaths(svc).Contains(b), "the folder listed again once its read answered");
            await WaitForAsync(() => !CachedPaths(svc, dataPath).Contains(gone), "the reached folder's deleted demo pruned");
        }
        finally
        {
            reader.Release();
            Cleanup(fast);
            Cleanup(slow);
        }
    }

    [Test]
    public async Task ANewerRescan_StopsTheOlderOnesListing_InsteadOfQueueingBehindIt()
    {
        string slow = NewTempDir();
        GatedReader reader = new(slow);
        try
        {
            string b = WriteDemo(slow, "b.dem", 4);
            using DemoLibraryService svc = new(_inline, SeedLibrary(slow, [slow]));
            svc.FolderReader = reader;
            svc.Timing = Timing(TimeSpan.FromSeconds(30));

            Task first = svc.RescanAsync();
            await WaitForAsync(() => reader.BlockedReads == 1, "the first scan's read");
            Task second = svc.RescanAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            using (Assert.Multiple())
            {
                await Assert.That(second.IsCompleted).IsFalse();
                await Assert.That(reader.BlockedReads).IsEqualTo(1)
                    .Because("the second scan waits on the first one's read instead of starting another beside it");
            }

            reader.Release();
            await second.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(EntryPaths(svc)).IsEquivalentTo([b]);
        }
        finally
        {
            reader.Release();
            Cleanup(slow);
        }
    }

    [Test]
    public async Task AFolderThatHasNotAnswered_HoldsNoQueueLane()
    {
        string slow = NewTempDir();
        GatedReader reader = new(slow);
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            _ => throw new InvalidOperationException("nothing is parsed here"));
        try
        {
            WriteDemo(slow, "b.dem", 4);
            using DemoLibraryService svc = new(_inline, SeedLibrary(slow, [slow]));
            svc.FolderReader = reader;
            svc.QueueOverride = queue;
            svc.Timing = Timing(TimeSpan.FromSeconds(30));

            Task scan = svc.RescanAsync();
            await WaitForAsync(() => reader.BlockedReads == 1, "the hung read");
            await WaitForAsync(() => !queue.Snapshot().Any(s => s.Kind == QueueJobKind.LibraryListing
                                                               && s.State == DemoQueueItemState.Running),
                "the listing slice to end while its read is outstanding");

            Task heavy = QueueWork.Run(queue, QueueJobKind.LibraryScan, "other heavy job", "test", _ => { });
            Task light = QueueWork.Run(queue, QueueJobKind.SectionCompute, "other light job", "test", _ => { });
            await Task.WhenAll(heavy, light).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(scan.IsCompleted).IsFalse();

            reader.Release();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            reader.Release();
            Cleanup(slow);
        }
    }

    // ── Helpers ──

    // Blocks every read of one directory until released.
    internal sealed class GatedReader(string blocked) : ILibraryFolderReader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockedReads;

        public int BlockedReads => Volatile.Read(ref _blockedReads);

        public void Release() => _gate.TrySetResult();

        public string ResolveRoot(string folder) => FileSystemLibraryFolderReader.Instance.ResolveRoot(folder);

        public LibraryDirectoryListing Read(string directory)
        {
            if (directory == blocked)
            {
                Interlocked.Increment(ref _blockedReads);
                _gate.Task.Wait(TimeSpan.FromMinutes(1));
            }

            return FileSystemLibraryFolderReader.Instance.Read(directory);
        }
    }

    private static string[] Sorted(params string[] paths) => [.. paths.Order(StringComparer.Ordinal)];

    private static LibraryScanTiming Timing(TimeSpan noAnswer) =>
        new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), noAnswer);

    // Entries is mutated on whichever thread the inline post runs on.
    private static string[] EntryPaths(DemoLibraryService svc)
    {
        while (true)
        {
            try
            {
                return [.. svc.Entries.Select(e => e.FilePath).Order(StringComparer.Ordinal)];
            }
            catch (InvalidOperationException)
            {
                // modified mid-read: read again
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(10);
        }
    }

    internal sealed class ScriptedReader : ILibraryFolderReader
    {
        public Func<string, Exception?>? FailResolve { get; init; }
        public Func<string, Exception?>? FailRead { get; init; }

        public string ResolveRoot(string folder) =>
            FailResolve?.Invoke(folder) is { } ex ? throw ex : FileSystemLibraryFolderReader.Instance.ResolveRoot(folder);

        public LibraryDirectoryListing Read(string directory) =>
            FailRead?.Invoke(directory) is { } ex ? throw ex : FileSystemLibraryFolderReader.Instance.Read(directory);
    }

    // Canonical, so paths compare equal to what the walk reports (macOS temp sits behind /var -> /private/var).
    internal static string NewTempDir() =>
        DemoLibraryService.CanonicalizeDirectory(
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dvscan_" + Guid.NewGuid().ToString("N"))).FullName);

    internal static string WriteDemo(string root, string relative, int size, byte fill = 1)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, size).ToArray());
        return path;
    }

    // An indexed row; a missing file gets a made-up key.
    internal static DemoLibraryCacheEntry Row(string path, long missingSize = 0)
    {
        FileInfo file = new(path);
        return new DemoLibraryCacheEntry
        {
            Path = path,
            Size = file.Exists ? file.Length : missingSize,
            ModifiedTicks = file.Exists ? file.LastWriteTime.Ticks : 1,
            Map = "de_test",
            Players = [],
            ScoreComputed = true,
            FullyIndexed = true
        };
    }

    internal static string SeedLibrary(string dir, string[] folders, params DemoLibraryCacheEntry[] rows)
    {
        string dataPath = Path.Combine(dir, "library-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(dataPath, JsonSerializer.Serialize(new DemoLibraryData { Folders = [.. folders], Cache = [.. rows] }));
        return dataPath;
    }

    internal static string[] CachedPaths(DemoLibraryService svc, string dataPath)
    {
        svc.Save();
        DemoLibraryData? data = JsonSerializer.Deserialize<DemoLibraryData>(File.ReadAllText(dataPath));
        return [.. (data?.Cache ?? []).Select(c => c.Path).Order(StringComparer.Ordinal)];
    }

    internal static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

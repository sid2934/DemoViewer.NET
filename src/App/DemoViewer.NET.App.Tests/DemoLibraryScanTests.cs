#region

using System.Text.Json;
using DemoViewer.NET.Modules.Library;

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

            await Assert.That(CachedPaths(svc, dataPath)).IsEquivalentTo([a, gone]);
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
                await Assert.That(CachedPaths(svc, dataPath)).IsEquivalentTo([first, copy]);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Helpers ──

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

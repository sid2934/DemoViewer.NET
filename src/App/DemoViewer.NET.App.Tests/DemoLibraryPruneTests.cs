#region

using System.Text.Json;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Stale-metadata pruning. <c>Reconcile</c> has always dropped the UI entry for a vanished demo but never
///     the persisted cache row behind it, so the cache only grew: on the reference library 354 of 719 rows
///     described files that no longer existed, 332 of them under a folder the user had removed outright.
///     <para>
///         The load-bearing case here is the LAST test: a configured folder on a detached volume enumerates
///         zero files and looks identical, from the file list alone, to a folder whose demos were all
///         deleted. Pruning on absence alone would wipe an external library's cache the first time it was
///         unplugged.
///     </para>
/// </summary>
[NotInParallel]
public class DemoLibraryPruneTests
{
    private static readonly Action<Action> _inline = a => a();

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvprune_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Writes a library.json holding rows for paths that may or may not exist, plus the folder list.
    private static string SeedCache(string dir, IEnumerable<string> folders, params string[] cachedPaths)
    {
        string dataPath = Path.Combine(dir, "library.json");
        File.WriteAllText(dataPath, JsonSerializer.Serialize(new DemoLibraryData
        {
            Folders = [.. folders],
            Cache =
            [
                .. cachedPaths.Select(p => new DemoLibraryCacheEntry
                {
                    Path = p,
                    Size = 10,
                    ModifiedTicks = 20,
                    Map = "de_dust2",
                    Players = ["someone"],
                    DurationSeconds = 100,
                    CtScore = 13,
                    Score = 9,
                    ScoreComputed = true,
                    FullyIndexed = true
                })
            ]
        }));
        return dataPath;
    }

    private static List<string> CachedPaths(string dataPath) =>
    [
        .. (JsonSerializer.Deserialize<DemoLibraryData>(File.ReadAllText(dataPath))?.Cache ?? [])
        .Select(c => c.Path)
    ];

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            /* best-effort */
        }
    }

    /// <summary>A demo deleted from a folder the scan actually read is gone for good: drop its row.</summary>
    [Test]
    public async Task Prunes_ARowWhoseFileWasDeletedFromAScannedFolder()
    {
        string dir = TempDir();
        string demos = Path.Combine(dir, "demos");
        Directory.CreateDirectory(demos);
        try
        {
            string live = Path.Combine(demos, "live.dem");
            File.WriteAllBytes(live, new byte[16]);
            string deleted = Path.Combine(demos, "deleted.dem"); // never created

            string dataPath = SeedCache(dir, [demos], live, deleted);

            DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();
            svc.Save();

            List<string> rows = CachedPaths(dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(live);
                await Assert.That(rows).DoesNotContain(deleted)
                    .Because("the folder was read and the file genuinely is not in it");
            }

            svc.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A row under no registered folder at all is out of scope: nothing can index it again without the
    ///     user re-adding the folder, which re-indexes anyway. This is the 332-row case on the reference
    ///     library (an old <c>/Volumes/Demos</c> registration long since removed).
    /// </summary>
    [Test]
    public async Task Prunes_ARowThatSitsUnderNoRegisteredFolder()
    {
        string dir = TempDir();
        string demos = Path.Combine(dir, "demos");
        string retired = Path.Combine(dir, "retired");
        Directory.CreateDirectory(demos);
        Directory.CreateDirectory(retired);
        try
        {
            string live = Path.Combine(demos, "live.dem");
            File.WriteAllBytes(live, new byte[16]);

            // The file still EXISTS, but its folder is no longer registered, out of scope either way.
            string orphan = Path.Combine(retired, "orphan.dem");
            File.WriteAllBytes(orphan, new byte[16]);

            string dataPath = SeedCache(dir, [demos], live, orphan);

            DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();
            svc.Save();

            List<string> rows = CachedPaths(dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(live);
                await Assert.That(rows).DoesNotContain(orphan);
            }

            svc.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     THE SAFETY CASE. A registered folder that is unreachable, an unplugged external drive, an
    ///     unmounted network share, enumerates nothing. Its rows must survive, or the first launch without
    ///     the drive silently destroys the cache for that entire library and re-plugging costs a full
    ///     re-index of every demo on it.
    /// </summary>
    [Test]
    public async Task Keeps_RowsUnderARegisteredFolderThatWasUnreachable()
    {
        string dir = TempDir();
        string demos = Path.Combine(dir, "demos");
        string detached = Path.Combine(dir, "detached-volume");
        Directory.CreateDirectory(demos);
        Directory.CreateDirectory(detached);
        try
        {
            string live = Path.Combine(demos, "live.dem");
            File.WriteAllBytes(live, new byte[16]);
            string onDetached = Path.Combine(detached, "away.dem");
            File.WriteAllBytes(onDetached, new byte[16]);

            // BOTH folders are registered; then the second one goes away, exactly as a volume unmounting.
            string dataPath = SeedCache(dir, [demos, detached], live, onDetached);
            Directory.Delete(detached, true);

            DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();
            svc.Save();

            List<string> rows = CachedPaths(dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(rows).Contains(live);
                await Assert.That(rows).Contains(onDetached)
                    .Because("an unreachable folder is not evidence its demos were deleted");
            }

            svc.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     With every registered folder unreachable the library is entirely offline. Pruning then would
    ///     delete the whole cache, so the pass must decline to prune anything at all.
    /// </summary>
    [Test]
    public async Task Keeps_EverythingWhenNoRegisteredFolderIsReachable()
    {
        string dir = TempDir();
        string detached = Path.Combine(dir, "detached-volume");
        Directory.CreateDirectory(detached);
        try
        {
            string a = Path.Combine(detached, "a.dem");
            string b = Path.Combine(detached, "b.dem");
            File.WriteAllBytes(a, new byte[16]);
            File.WriteAllBytes(b, new byte[16]);

            string dataPath = SeedCache(dir, [detached], a, b);
            Directory.Delete(detached, true);

            DemoLibraryService svc = new(_inline, dataPath);
            await svc.RescanAsync();
            svc.Save();

            await Assert.That(CachedPaths(dataPath)).HasCount(2)
                .Because("every root was offline — pruning would wipe the entire cache");

            svc.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A stale row in the unified cache costs a whole sidecar FILE, not a line of JSON, so the prune has
    ///     to reach that store too.
    /// </summary>
    [Test]
    public async Task Prunes_TheUnifiedCacheSidecarToo()
    {
        string dir = TempDir();
        string demos = Path.Combine(dir, "demos");
        Directory.CreateDirectory(demos);
        try
        {
            string live = Path.Combine(demos, "live.dem");
            File.WriteAllBytes(live, new byte[16]);
            string deleted = Path.Combine(demos, "deleted.dem");

            string dataPath = SeedCache(dir, [demos], live, deleted);

            DemoCacheStore cache = new(Path.Combine(dir, "cache"));
            cache.Update(deleted, 10, 20, r => DemoCacheStore.StampParse(r));
            string sidecar = cache.SidecarPathFor(deleted)!;
            await Assert.That(File.Exists(sidecar)).IsTrue();

            DemoLibraryService svc = new(_inline, dataPath, demoCache: cache);
            await svc.RescanAsync();

            using (Assert.Multiple())
            {
                await Assert.That(cache.TryGetIndex(deleted)).IsNull();
                await Assert.That(File.Exists(sidecar)).IsFalse()
                    .Because("a stale sidecar is real disk, not a line of JSON");
            }

            svc.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }
}

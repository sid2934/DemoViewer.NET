#region

using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Library;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Validates the demo-library indexer end-to-end on a real demo (isolated via a symlink into a temp
///     folder so only one file is indexed): tier-1 map read, tier-2 players/duration from a full parse, and
///     the (path,size,mtime)-keyed disk cache round-tripping to a fresh service without re-parsing.
///     Runs the service with an <b>inline</b> post so <c>RescanAsync</c> completes synchronously.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class DemoLibraryServiceTests
{
    private static readonly Action<Action> _inline = a => a();

    private static (string dir, string dataPath) MakeTempLibraryWith(string demoPath)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string link = Path.Combine(dir, "match.dem");
        File.CreateSymbolicLink(link, demoPath);
        string dataPath = Path.Combine(dir, "library.json");
        return (dir, dataPath);
    }

    private static ParsedDemo SyntheticDemo(int tickRate = 64) => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / tickRate, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    ///     Queue-path tier-2 (demo-processing-queue.md): RescanAsync submits tier-2 to the shared
    ///     queue (returning before it drains), and the END-OF-BACKLOG Save persists the cache so a second
    ///     launch loads from cache WITHOUT re-parsing. Uses a fake queue parser so no real demo is needed.
    /// </summary>
    [Test]
    public async Task QueuePath_PersistsCache_SoSecondLaunchDoesNotReparse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.dem"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "b.dem"), [4, 5, 6]);
        string dataPath = Path.Combine(dir, "library.json");
        try
        {
            int parses1 = 0;
            DemoProcessingQueue queue1 = new(new HeavyJobGate(), a => a(),
                _ =>
                {
                    Interlocked.Increment(ref parses1);
                    return SyntheticDemo();
                });
            DemoLibraryService svc = new(_inline, dataPath);
            DemoEvaluationCoordinator coord1 = new([svc], queue1, svc.Tier2Backlog);
            svc.Coordinator = coord1;
            using (svc)
            using (coord1)
            {
                await svc.AddFoldersAsync([dir]); // rescan submits tier-2 to the queue (fire-and-forget)
                await WaitForAsync(
                    () => svc.Entries.Count == 2 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                    "queue tier-2 drained");
            }

            await Assert.That(parses1).IsEqualTo(2).Because("both demos parsed once via the queue");
            await Assert.That(File.Exists(dataPath)).IsTrue().Because("end-of-backlog Save persisted the cache");

            // Second launch: a THROWING parser proves the cached demos are not re-parsed.
            DemoProcessingQueue queue2 = new(new HeavyJobGate(), a => a(),
                _ => throw new InvalidOperationException("a cached demo must not re-parse"));
            using DemoLibraryService svc2 = new(_inline, dataPath);
            using DemoEvaluationCoordinator coord2 = new([svc2], queue2, svc2.Tier2Backlog);
            svc2.Coordinator = coord2;
            await svc2.RescanAsync();
            await Task.Delay(150); // let any (wrongly) submitted tier-2 run and throw
            await Assert.That(svc2.Entries.Count).IsEqualTo(2);
            await Assert.That(svc2.Entries.All(e => e.State == DemoIndexState.Indexed)).IsTrue()
                .Because("cache hit → Indexed without re-parsing");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    [Test]
    public async Task Indexes_Map_Players_AndCaches()
    {
        string? demo = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem")
                       ?? DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem");
        if (demo is null)
        {
            throw new SkipTestException("no demo present");
        }

        (string dir, string dataPath) = MakeTempLibraryWith(demo);
        try
        {
            using DemoLibraryService svc = new(_inline, dataPath);
            await svc.AddFoldersAsync([dir]); // triggers a full rescan (tier1 + tier2), synchronous under Inline

            await Assert.That(svc.Entries.Count).IsEqualTo(1);
            DemoEntry entry = svc.Entries[0];
            Console.WriteLine($"[lib] map={entry.MapName} state={entry.State} players={entry.Players.Count} " +
                              $"dur={entry.DurationSeconds:F0}s");

            await Assert.That(entry.MapName).IsNotNull();
            await Assert.That(entry.MapName!).StartsWith("de_");
            await Assert.That(entry.State).IsEqualTo(DemoIndexState.Indexed);
            await Assert.That(entry.Players.Count).IsGreaterThan(0);
            await Assert.That(entry.DurationSeconds).IsGreaterThan(0);
            await Assert.That(File.Exists(dataPath)).IsTrue(); // cache persisted

            // A fresh service over the same folder + cache must load the demo already Indexed (cache hit,
            // no re-parse) after reconciliation.
            using DemoLibraryService svc2 = new(_inline, dataPath);
            await svc2.AddFoldersAsync([dir]); // folder already persisted → this is a no-op add, so rescan explicitly
            await svc2.RescanAsync();
            await Assert.That(svc2.Entries.Count).IsEqualTo(1);
            await Assert.That(svc2.Entries[0].State).IsEqualTo(DemoIndexState.Indexed);
            await Assert.That(svc2.Entries[0].Players.Count).IsEqualTo(entry.Players.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Dedup (Phase 1, canonical path): the SAME physical file reachable from two registered folders —
    ///     here a real folder plus a directory symlink pointing at it — must appear as ONE card and be
    ///     processed ONCE. Before canonicalization the two registrations produced distinct path strings
    ///     (real/a.dem vs link/a.dem) → two cards + two full parses.
    /// </summary>
    [Test]
    public async Task Scan_DeduplicatesSameFile_AcrossSymlinkedFolders()
    {
        string real = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string link = Path.Combine(Path.GetTempPath(), "dvlink_" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(Path.Combine(real, "a.dem"), [1, 2, 3]);
        try
        {
            Directory.CreateSymbolicLink(link, real); // link/ is a symlink to real/

            int parses = 0;
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
                _ =>
                {
                    Interlocked.Increment(ref parses);
                    return SyntheticDemo();
                });
            using DemoLibraryService svc = new(_inline, Path.Combine(real, "library.json"));
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([real, link]); // same physical folder registered twice
            await WaitForAsync(
                () => svc.Entries.Count >= 1 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "indexed");

            await Assert.That(svc.Entries.Count).IsEqualTo(1)
                .Because("the same file via a symlinked folder appears once");
            await Assert.That(parses).IsEqualTo(1).Because("and is processed once");
        }
        finally
        {
            try
            {
                Directory.Delete(link, false);
            }
            catch
            {
                /* remove just the symlink */
            }

            try
            {
                Directory.Delete(real, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Coordinator cutover (Phase 3a): a demo whose parse THROWS is marked Failed and its tier-2 backlog
    ///     entry is cleared synchronously, so a subsequent CapacityAvailable / ConsiderAll does NOT re-submit
    ///     it. Guards the infinite-reparse trap (Wants must go false on failure, not just on success).
    /// </summary>
    [Test]
    public async Task CorruptDemo_MarkedFailed_NotReparsedOnReconsider()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "bad.dem"), [1, 2, 3]);
        try
        {
            int parses = 0;
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
                _ =>
                {
                    Interlocked.Increment(ref parses);
                    throw new InvalidOperationException("corrupt");
                });
            using DemoLibraryService svc = new(_inline, Path.Combine(dir, "library.json"));
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([dir]);
            await WaitForAsync(
                () => svc.Entries.Count == 1 && svc.Entries[0].State == DemoIndexState.Failed,
                "demo marked Failed");

            coord.ConsiderAll(); // explicit re-poll (as CapacityAvailable would)
            await Task.Delay(120); // let any (wrong) re-submit run

            await Assert.That(parses).IsEqualTo(1)
                .Because("a corrupt demo is parsed once and never re-submitted on re-consideration");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Interactive-open fan-out (Phase 4a): a pending library demo that the queue REJECTED to the
    ///     coordinator backlog (its background tier was full) and is then OPENED interactively must fill its
    ///     card from the open's already-parsed demo — never a second background parse. This is the exact
    ///     double-parse Phase 4a kills: after <see cref="DemoLibraryService.OnParsedOpportunistically" /> the
    ///     demo is no longer <see cref="DemoLibraryService.Wants" />-ed, so the capacity re-feed skips it.
    /// </summary>
    [Test]
    public async Task OpenFanOut_IndexesFromHeldParse_NoRedundantBackgroundParse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "one.dem"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(dir, "two.dem"), [4, 5, 6]);
        try
        {
            ConcurrentBag<string> parsedNames = new();
            using ManualResetEventSlim release = new(false);
            // One background slot → whichever demo submits first occupies the worker (blocked on release);
            // the other is REJECTED to the coordinator backlog. All parses block, so exactly one runs.
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
                path =>
                {
                    parsedNames.Add(Path.GetFileName(path));
                    release.Wait(3000); // occupy the single worker until the "open" lands
                    return SyntheticDemo();
                })
            {
                MaxQueueSize = 1
            };

            using DemoLibraryService svc = new(_inline, Path.Combine(dir, "library.json"));
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([dir]);
            // Exactly one demo occupies the worker; the OTHER is the rejected-to-backlog "target".
            await WaitForAsync(() => parsedNames.Count == 1, "one demo occupies the worker");
            string heldName = parsedNames.First();
            string targetName = heldName == "one.dem" ? "two.dem" : "one.dem";
            // Use the CANONICAL path the service stored (macOS temp is a symlink: /var → /private/var).
            string canonicalTarget = svc.Entries.Single(e => e.FileName == targetName).FilePath;

            await Assert.That(svc.Wants(canonicalTarget)).IsTrue()
                .Because("the target is pending, rejected to the coordinator backlog, awaiting a slot");
            await Assert.That(parsedNames.Contains(targetName)).IsFalse().Because("the target hasn't parsed");

            // Simulate the interactive open handing over its already-parsed demo.
            svc.OnParsedOpportunistically(canonicalTarget, SyntheticDemo());

            await Assert.That(svc.Wants(canonicalTarget)).IsFalse()
                .Because("the held parse indexed the target + cleared its backlog → no longer wanted");
            await Assert.That(svc.Entries.Single(e => e.FileName == targetName).State)
                .IsEqualTo(DemoIndexState.Indexed).Because("the card filled in from the open's parse");

            // Free the worker: its completion fires CapacityAvailable → the coordinator re-considers the backlog.
            release.Set();
            await WaitForAsync(
                () => svc.Entries.Count == 2 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "both demos indexed");
            await Task.Delay(100); // allow any (wrong) target re-submit to run

            await Assert.That(parsedNames.Contains(targetName)).IsFalse()
                .Because("the target was indexed from the open's parse — never background-parsed");
            await Assert.That(parsedNames.Count(n => n == heldName)).IsEqualTo(1)
                .Because("only the worker-held demo was background-parsed, exactly once");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Content dedup (Phase 4b): the SAME bytes copied into two DIFFERENT real folders (not a symlink —
    ///     a genuine copy, which canonical-path dedup cannot catch) must appear as ONE card and be processed
    ///     ONCE. The primary is the lexicographically-smallest path; the other folder surfaces as a copy hint.
    /// </summary>
    [Test]
    public async Task Scan_DeduplicatesByContent_AcrossCopiesInDifferentFolders()
    {
        string dirA = Path.Combine(Path.GetTempPath(), "dvlibA_" + Guid.NewGuid().ToString("N"));
        string dirB = Path.Combine(Path.GetTempPath(), "dvlibB_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        byte[] bytes = [9, 8, 7, 6, 5, 4, 3, 2, 1, 0];
        await File.WriteAllBytesAsync(Path.Combine(dirA, "copy.dem"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(dirB, "copy.dem"), bytes); // byte-identical, distinct real path
        try
        {
            int parses = 0;
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
                _ =>
                {
                    Interlocked.Increment(ref parses);
                    return SyntheticDemo();
                });
            using DemoLibraryService svc = new(_inline, Path.Combine(dirA, "library.json"));
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([dirA, dirB]);
            await WaitForAsync(
                () => svc.Entries.Count == 1 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "the copy indexed once");

            await Assert.That(svc.Entries.Count).IsEqualTo(1).Because("byte-identical copies appear once");
            await Assert.That(parses).IsEqualTo(1).Because("and are processed once");
            await Assert.That(svc.Entries[0].HasDuplicates).IsTrue()
                .Because("the primary card knows a copy exists in the other folder");
        }
        finally
        {
            try
            {
                Directory.Delete(dirA, true);
            }
            catch
            {
                /* best-effort cleanup */
            }

            try
            {
                Directory.Delete(dirB, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Interactive-open fan-out over a REAL demo (Phase 4a, real-frame coverage): the synthetic sibling
    ///     proves the no-reparse control flow; this proves the actual work — handing a real, fully-parsed
    ///     demo to <see cref="DemoLibraryService.OnParsedOpportunistically" /> runs the real entity-decode
    ///     score replay through the opportunistic hook and fills the card (players + duration + final score),
    ///     while the background queue never parses the file. As in the synthetic sibling, the demo is held in
    ///     the coordinator BACKLOG (rejected — the single background slot is occupied by a blocking sacrifice
    ///     file), which is exactly the case fan-out uniquely covers (a queued item would instead coalesce
    ///     onto the foreground open). Closes the gap where every other Phase-4 test used an empty-frame demo.
    /// </summary>
    [Test]
    public async Task OpenFanOut_RealDemo_FillsCardFromHeldParse_NoBackgroundParse()
    {
        string? demo = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem")
                       ?? DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem");
        if (demo is null)
        {
            throw new SkipTestException("no demo present");
        }

        (string dir, string dataPath) = MakeTempLibraryWith(demo);
        string realName = Path.GetFileName(demo);
        try
        {
            ConcurrentBag<string> parsedNames = new();
            using ManualResetEventSlim release = new(false);
            // A just-created sacrifice file — its mtime is newer than the (older) real demo, so it is
            // considered first and occupies the single background slot; the real demo is then REJECTED to
            // the coordinator backlog. It blocks so the slot never frees before the "open" lands.
            await File.WriteAllBytesAsync(Path.Combine(dir, "zzz_block.dem"), [1, 2, 3]);
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
                path =>
                {
                    parsedNames.Add(Path.GetFileName(path));
                    // Hold well past the real ~half-GB parse below (a short timeout would free the slot
                    // mid-parse, letting the coordinator re-submit the real demo before the open lands).
                    if (Path.GetFileName(path) == "zzz_block.dem")
                    {
                        release.Wait(60_000);
                    }

                    return SyntheticDemo();
                })
            {
                MaxQueueSize = 1
            };

            using DemoLibraryService svc = new(_inline, dataPath);
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([dir]);
            await WaitForAsync(() => parsedNames.Contains("zzz_block.dem"), "the sacrifice occupies the worker");
            DemoEntry entry = svc.Entries.Single(e => Path.GetFileName(e.FilePath) == realName);
            string canonical = entry.FilePath; // the symlink resolves to the real demo path
            await Assert.That(svc.Wants(canonical)).IsTrue()
                .Because("the real demo is pending, rejected to the coordinator backlog");
            await Assert.That(parsedNames.Contains(realName)).IsFalse().Because("it hasn't been parsed");

            // Simulate the interactive open: parse the real demo and hand it over.
            byte[] bytes = await File.ReadAllBytesAsync(demo);
            ParsedDemo real = await Task.Run(() => DemoParser.Parse(bytes.AsMemory()));
            svc.OnParsedOpportunistically(canonical, real);
            // Free the worker now (the card is already indexed, Wants is false — the capacity re-poll must
            // not (re)parse the real demo). Releasing before the assertions also avoids a stuck 60 s waiter
            // if one fails.
            release.Set();

            // The REAL entity-decode path ran through the opportunistic hook → real card data.
            await Assert.That(entry.State).IsEqualTo(DemoIndexState.Indexed);
            await Assert.That(entry.Players.Count).IsGreaterThan(0).Because("players decoded from the real parse");
            await Assert.That(entry.DurationSeconds).IsGreaterThan(0);
            await Assert.That(entry.HasScore).IsTrue().Because("the final score replayed over real frames");
            await Assert.That(svc.Wants(canonical)).IsFalse().Because("indexed from the open → no longer wanted");

            await Task.Delay(150); // let any (wrong) capacity re-submit run
            await Assert.That(parsedNames.Contains(realName)).IsFalse()
                .Because("the open parse filled the card; the real demo was never background-parsed");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Distinct demos that happen to share an exact byte SIZE must NOT be deduped — the size pre-filter
    ///     triggers a hash, but the differing content hashes keep them as two separate cards (no false merge).
    /// </summary>
    [Test]
    public async Task Scan_SameSizeDifferentContent_NotDeduped()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "a.dem"), [1, 1, 1, 1]);
        await File.WriteAllBytesAsync(Path.Combine(dir, "b.dem"), [2, 2, 2, 2]); // same size, different bytes
        try
        {
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), _ => SyntheticDemo());
            using DemoLibraryService svc = new(_inline, Path.Combine(dir, "library.json"));
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([dir]);
            await WaitForAsync(() => svc.Entries.Count == 2 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "both distinct demos indexed");

            await Assert.That(svc.Entries.Count).IsEqualTo(2).Because("equal size but different content ≠ duplicate");
            await Assert.That(svc.Entries.Any(e => e.HasDuplicates)).IsFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Shadow promotion (Phase 4b): when the PRIMARY copy (smallest path) is deleted, a surviving shadow
    ///     becomes the new primary. It may never have been parsed on its own path, so it must enter the
    ///     tier-2 backlog and index — the card must not go blank / stuck Pending.
    /// </summary>
    [Test]
    public async Task Scan_PrimaryDeleted_ShadowPromoted_AndIndexed()
    {
        // Two folders; the primary lives in the lexicographically-smaller path. Force that by naming.
        string dir1 = Path.Combine(Path.GetTempPath(), "aaa_" + Guid.NewGuid().ToString("N"));
        string dir2 = Path.Combine(Path.GetTempPath(), "zzz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        byte[] bytes = [5, 5, 5, 5, 5, 5];
        string primaryFile = Path.Combine(dir1, "m.dem");
        await File.WriteAllBytesAsync(primaryFile, bytes);
        await File.WriteAllBytesAsync(Path.Combine(dir2, "m.dem"), bytes);
        try
        {
            int parses = 0;
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
                _ =>
                {
                    Interlocked.Increment(ref parses);
                    return SyntheticDemo();
                });
            using DemoLibraryService svc = new(_inline, Path.Combine(dir1, "library.json"));
            using DemoEvaluationCoordinator coord = new([svc], queue, svc.Tier2Backlog);
            svc.Coordinator = coord;

            await svc.AddFoldersAsync([dir1, dir2]);
            await WaitForAsync(() => svc.Entries.Count == 1 && svc.Entries[0].State == DemoIndexState.Indexed,
                "primary indexed once");
            await Assert.That(parses).IsEqualTo(1);

            // Delete the primary; the shadow in dir2 must be promoted and (re)indexed on its own path.
            File.Delete(primaryFile);
            await svc.RescanAsync();
            // The surviving copy is under dir2; compare by the unique folder-name segment (not the full path
            // — macOS temp is a symlink /var → /private/var, so the stored canonical path is prefixed).
            string dir2Leaf = Path.GetFileName(dir2);
            await WaitForAsync(
                () => svc.Entries.Count == 1 && svc.Entries[0].State == DemoIndexState.Indexed
                                             && svc.Entries[0].FilePath.Contains(dir2Leaf, StringComparison.Ordinal),
                "shadow promoted to primary and indexed");

            await Assert.That(svc.Entries.Count).IsEqualTo(1).Because("still one card — now the surviving copy");
            await Assert.That(svc.Entries[0].HasDuplicates).IsFalse().Because("only one copy remains");
            await Assert.That(parses).IsEqualTo(2).Because("the promoted shadow was parsed on its own path");
        }
        finally
        {
            try
            {
                Directory.Delete(dir1, true);
            }
            catch
            {
                /* best-effort cleanup */
            }

            try
            {
                Directory.Delete(dir2, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    [Test]
    public async Task PrettifyMap_StripsPrefix_AndTitleCases()
    {
        await Assert.That(DemoEntry.PrettifyMap("de_dust2")).IsEqualTo("Dust2");
        await Assert.That(DemoEntry.PrettifyMap("de_nuke")).IsEqualTo("Nuke");
        await Assert.That(DemoEntry.PrettifyMap("cs_office")).IsEqualTo("Office");
        await Assert.That(DemoEntry.PrettifyMap(null)).IsEqualTo("Unknown");
    }

    /// <summary>
    ///     macOS AppleDouble sidecars ("._name.dem") — the ~368 B resource-fork/xattr companions written when
    ///     a demo is copied across a non-native filesystem (SMB/exFAT/NFS) — match the "*.dem" glob but are not
    ///     demos; parsing one produced a bogus failed "Unknown" library card. The scan must skip them.
    /// </summary>
    [Test]
    public async Task Scan_SkipsAppleDoubleSidecars()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A regular .dem file — enumerated (its junk bytes fail to parse → Failed, but it IS an entry).
            await File.WriteAllBytesAsync(Path.Combine(dir, "match730.dem"), new byte[4096]);
            // Its AppleDouble sidecar sitting right next to it — must be excluded from the index entirely.
            await File.WriteAllBytesAsync(Path.Combine(dir, "._match730.dem"), new byte[368]);

            using DemoLibraryService svc = new(_inline, Path.Combine(dir, "library.json"));
            await svc.AddFoldersAsync([dir]); // Inline post → synchronous rescan

            await Assert.That(svc.Entries.Count).IsEqualTo(1);
            await Assert.That(svc.Entries[0].FileName).IsEqualTo("match730.dem");
            await Assert.That(svc.Entries.Any(e => e.FileName.StartsWith("._", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    ///     Bulk adds must raise ONE Reset, not N Add events — a large folder's reconcile pass
    ///     otherwise triggers a full filter + container rebuild per entry (O(N²) UI churn).
    /// </summary>
    [Test]
    public async Task BulkAddRange_RaisesSingleResetEvent()
    {
        BulkObservableCollection<int> col = new();
        List<NotifyCollectionChangedAction> events = new();
        col.CollectionChanged += (_, e) => events.Add(e.Action);

        col.AddRange(Enumerable.Range(0, 250));

        await Assert.That(col.Count).IsEqualTo(250);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsEqualTo(NotifyCollectionChangedAction.Reset);

        // Empty range → no event at all.
        col.AddRange([]);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    // ── P1.1 chunk C: settings-backed folder path ─────────────────────────────────────────────────

    /// <summary>
    ///     When constructed WITH a settings service, the configured folder list is the one in
    ///     <c>AppSettings.Library.Folders</c> — not library.json — so the two P1.1 consumers stay in sync.
    /// </summary>
    [Test]
    public async Task SettingsBacked_ReadsFoldersFromAppSettings()
    {
        string cfgDir = NewTempDir();
        string folderA = NewTempDir();
        string folderB = NewTempDir();
        try
        {
            SettingsService settings = new(cfgDir);
            settings.Write(s => s.Library.Folders = [folderA, folderB]);

            using DemoLibraryService svc = new(_inline, Path.Combine(cfgDir, "library.json"), settings);

            await Assert.That(svc.Folders.Contains(folderA)).IsTrue();
            await Assert.That(svc.Folders.Contains(folderB)).IsTrue();
            await Assert.That(svc.SettingsBacking).IsNotNull();
        }
        finally
        {
            Cleanup(cfgDir);
            Cleanup(folderA);
            Cleanup(folderB);
        }
    }

    /// <summary>
    ///     Add/Remove folder writes through to settings.json (the settings service is authoritative), so the
    ///     change is durable and visible to every AppSettings consumer — and a Remove shrinks the on-disk
    ///     list (no stale folder left behind).
    /// </summary>
    [Test]
    public async Task SettingsBacked_AddRemoveFolder_WritesThroughToSettingsJson()
    {
        string cfgDir = NewTempDir();
        string folder = NewTempDir(); // must exist — AddFoldersAsync ignores non-existent paths
        string settingsPath = Path.Combine(cfgDir, "settings.json");
        try
        {
            SettingsService settings = new(cfgDir);
            using DemoLibraryService svc = new(_inline, Path.Combine(cfgDir, "library.json"), settings);

            await svc.AddFoldersAsync([folder]); // Inline post → synchronous rescan (empty folder → no demos)

            await Assert.That(File.Exists(settingsPath)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(settingsPath)).Contains(folder);
            await Assert.That(settings.Current.Library.Folders.Contains(folder)).IsTrue();

            await svc.RemoveFolderAsync(folder);

            await Assert.That(settings.Current.Library.Folders.Contains(folder)).IsFalse()
                .Because("RemoveFolder writes the shrunk list back through settings");
            await Assert.That((await File.ReadAllTextAsync(settingsPath)).Contains(folder)).IsFalse()
                .Because("the removed folder must not linger in settings.json");
        }
        finally
        {
            Cleanup(cfgDir);
            Cleanup(folder);
        }
    }

    /// <summary>
    ///     An upgrading install (legacy folders in library.json, empty settings) lifts those folders into
    ///     settings.json exactly once; a subsequent settings-backed service then reads the (now populated)
    ///     settings, not re-migrating.
    /// </summary>
    [Test]
    public async Task SettingsBacked_LiftsLegacyLibraryJsonFolders_Once()
    {
        string cfgDir = NewTempDir();
        string legacyFolder = NewTempDir();
        string libJson = Path.Combine(cfgDir, "library.json");
        try
        {
            WriteLegacyLibraryJson(libJson, legacyFolder);

            SettingsService settings = new(cfgDir);
            await Assert.That(settings.Current.Library.Folders.Length).IsEqualTo(0)
                .Because("settings starts empty on this upgrading install");

            using DemoLibraryService svc = new(_inline, libJson, settings);

            await Assert.That(svc.Folders.Contains(legacyFolder)).IsTrue();
            await Assert.That(settings.Current.Library.Folders.Contains(legacyFolder)).IsTrue()
                .Because("the one-time migration lifts library.json's folders into settings.json");

            // A fresh settings + service now sources folders from settings (already populated) — the lift
            // does not run again / duplicate.
            SettingsService settings2 = new(cfgDir);
            using DemoLibraryService svc2 = new(_inline, libJson, settings2);
            await Assert.That(svc2.Folders.Count(f => f == legacyFolder)).IsEqualTo(1)
                .Because("the migrated folder appears exactly once, read from settings");
        }
        finally
        {
            Cleanup(cfgDir);
            Cleanup(legacyFolder);
        }
    }

    /// <summary>
    ///     A truly clean install (no library.json, empty settings) has nothing to migrate, so the settings
    ///     service is NOT written on construction — <see cref="SettingsService.NeedsFirstRun" /> stays true
    ///     for the first-run experience.
    /// </summary>
    [Test]
    public async Task SettingsBacked_CleanInstall_NeedsFirstRunStaysTrue()
    {
        string cfgDir = NewTempDir();
        try
        {
            SettingsService settings = new(cfgDir);
            await Assert.That(settings.NeedsFirstRun).IsTrue();

            using DemoLibraryService svc = new(_inline, Path.Combine(cfgDir, "library.json"), settings);

            await Assert.That(svc.Folders.Count).IsEqualTo(0);
            await Assert.That(settings.NeedsFirstRun).IsTrue()
                .Because("nothing to migrate → no settings.json write → the first-run flag survives");
            await Assert.That(File.Exists(Path.Combine(cfgDir, "settings.json"))).IsFalse();
        }
        finally
        {
            Cleanup(cfgDir);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvlibcfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            /* best-effort cleanup */
        }
    }

    // Writes a legacy-shape library.json (folders owned by the cache file, current schema) — the pre-P1.1
    // persistence that the settings migration must lift.
    private static void WriteLegacyLibraryJson(string path, params string[] folders)
    {
        DemoLibraryData data = new()
        {
            SchemaVersion = DemoLibraryCacheEntry.CurrentSchema,
            Folders = [.. folders],
            Cache = []
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data));
    }
}

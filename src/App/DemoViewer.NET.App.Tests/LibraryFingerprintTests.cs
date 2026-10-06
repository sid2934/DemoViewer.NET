#region

using System.Collections.Concurrent;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using static DemoViewer.NET.AppTests.DemoLibraryScanTests;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library recognising a demo it already knows by the fingerprint of a new path, without reading that
///     path in full or parsing it again, and a full read later overruling a fingerprint that matched wrongly.
///     Synthetic files with a small fingerprint window; every content read is counted.
/// </summary>
[NotInParallel]
public class LibraryFingerprintTests
{
    private const int Size = 4096;
    private static readonly Action<Action> _inline = a => a();

    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static byte[] Bytes(int seed)
    {
        byte[] bytes = new byte[Size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Write(string root, string relative, byte[] bytes)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class Library : IDisposable
    {
        private readonly DemoScheduler _scheduler;
        private readonly DemoProcessingQueue _queue;

        public Library(string dataDir, DemoCacheStore store, string? dataPath = null)
        {
            Store = store;
            _queue = new DemoProcessingQueue(new HeavyJobGate(), a => a(), path =>
            {
                Parses.AddOrUpdate(path, 1, (_, n) => n + 1);
                return SyntheticDemo();
            });
            Service = new DemoLibraryService(_inline, dataPath ?? Path.Combine(dataDir, $"library-{Guid.NewGuid():N}.json"),
                demoCache: store)
            {
                Time = Settled,
                ContentReader = Reader,
                QueueOverride = _queue
            };
            _scheduler = new DemoScheduler([Service], _queue, Service.Tier2Backlog);
            Service.Scheduler = _scheduler;
        }

        public DemoLibraryService Service { get; }
        public DemoCacheStore Store { get; }
        public CountingContentReader Reader { get; } = new();
        public ConcurrentDictionary<string, int> Parses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int TotalParses => Parses.Values.Sum();

        public void Dispose()
        {
            Service.Dispose();
            _scheduler.Dispose();
            _queue.Dispose();
        }
    }

    [Test]
    public async Task ASecondMountOfTheSameShare_IsRecognisedByFingerprint_WithoutAFullReadOrASecondParse()
    {
        string nfs = NewTempDir();
        string smb = NewTempDir();
        try
        {
            byte[] bytes = Bytes(1);
            string onNfs = Write(nfs, "m.dem", bytes);
            string onSmb = Write(smb, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(nfs, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([nfs]);
            await WaitForAsync(() => svc.Entries is [{ State: DemoIndexState.Indexed }]
                                     && library.Store.TryGetByContentId(sha) is { ContentFingerprint: not null },
                "the first mount's demo indexed and hashed");
            using (Assert.Multiple())
            {
                await Assert.That(library.TotalParses).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(onNfs)).IsEqualTo(1).Because("tier 2 hashes what it parsed");
                await Assert.That(library.Reader.WindowReads(onNfs)).IsEqualTo(0)
                    .Because("the fingerprint is taken in the same read as the hash");
            }

            await svc.AddFoldersAsync([smb]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => svc.Entries is [{ HasDuplicates: true }], "the copy folded into the card");

            using (Assert.Multiple())
            {
                await Assert.That(library.TotalParses).IsEqualTo(1).Because("the second mount is not parsed");
                await Assert.That(library.Reader.FullReads(onSmb)).IsEqualTo(0).Because("nor read in full");
                await Assert.That(library.Reader.WindowReads(onSmb)).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(onNfs)).IsEqualTo(1);
                await Assert.That(svc.Entries[0].FilePath).IsEqualTo(onNfs);
                await Assert.That(svc.Entries[0].DuplicateFolders).IsEquivalentTo([smb]);
                await Assert.That(library.Store.LocationOf(onSmb)?.ContentId).IsEqualTo(sha);
                await Assert.That(library.Store.LocationOf(onSmb)?.Location.Confirmed).IsFalse();
                await Assert.That(library.Store.Contents.Count).IsEqualTo(1);
            }

            await svc.RescanAsync();
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(library.Reader.WindowReads(onSmb)).IsEqualTo(1).Because("a listed location is not read again");

            // The first mount goes away: the second shows the demo from what the cache knows.
            await svc.RemoveFolderAsync(nfs);
            await WaitForAsync(() => svc.Entries is [{ State: DemoIndexState.Indexed } only] && only.FilePath == onSmb,
                "the second mount's path is the card");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(svc.Entries[0].MapName).IsEqualTo("de_test");
                await Assert.That(library.TotalParses).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(onSmb)).IsEqualTo(0);
                await Assert.That(library.Store.LocationOf(onNfs)).IsNull();
                await Assert.That(library.Store.LocationOf(onSmb)?.ContentId).IsEqualTo(sha);
            }
        }
        finally
        {
            Cleanup(nfs);
            Cleanup(smb);
        }
    }

    // A scan that attaches and parses nothing still saves, so the next launch reads neither file again.
    [Test]
    public async Task AnAttachedCopy_IsKnownAfterARestart_WithoutBeingReadAgain()
    {
        string nfs = NewTempDir();
        string smb = NewTempDir();
        string cache = NewTempDir();
        try
        {
            byte[] bytes = Bytes(5);
            Write(nfs, "m.dem", bytes);
            string onSmb = Write(smb, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            string dataPath = Path.Combine(cache, "library.json");
            using (Library first = new(cache, new DemoCacheStore(cache), dataPath))
            {
                await first.Service.AddFoldersAsync([nfs]);
                await WaitForAsync(() => first.Store.TryGetByContentId(sha) is { ContentFingerprint: not null }
                                         && first.Service.Tier2Backlog().Count == 0,
                    "the first mount indexed");
                await first.Service.AddFoldersAsync([smb]);
                await first.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.That(first.Store.LocationOf(onSmb)?.ContentId).IsEqualTo(sha);
            }

            using Library second = new(cache, new DemoCacheStore(cache), dataPath);
            await second.Service.RescanAsync();
            await second.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => second.Service.Entries is [{ State: DemoIndexState.Indexed, HasDuplicates: true }],
                "the card and its copy, from the saved cache");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(second.Store.LocationOf(onSmb)?.Location.Confirmed).IsFalse();
                await Assert.That(second.Reader.WindowReads(onSmb)).IsEqualTo(0);
                await Assert.That(second.Reader.FullReads(onSmb)).IsEqualTo(0);
                await Assert.That(second.TotalParses).IsEqualTo(0);
            }
        }
        finally
        {
            Cleanup(nfs);
            Cleanup(smb);
            Cleanup(cache);
        }
    }

    [Test]
    public async Task TwoCopiesListedInOneScan_AreFingerprintedOnce_AndOnlyOneIsReadInFullAndParsed()
    {
        string nfs = NewTempDir();
        string smb = NewTempDir();
        try
        {
            byte[] bytes = Bytes(2);
            string onNfs = Write(nfs, "m.dem", bytes);
            string onSmb = Write(smb, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(nfs, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([nfs, smb]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => svc.Entries is [{ State: DemoIndexState.Indexed, HasDuplicates: true }]
                                     && library.Store.TryGetByContentId(sha) is not null,
                "one card, indexed, with its copy");
            string primary = svc.Entries[0].FilePath;
            string shadow = primary == onNfs ? onSmb : onNfs;

            await svc.RescanAsync();
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(library.TotalParses).IsEqualTo(1);
                await Assert.That(library.Parses.Keys).IsEquivalentTo([primary]);
                await Assert.That(library.Reader.FullReads(primary)).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(shadow)).IsEqualTo(0);
                await Assert.That(library.Reader.WindowReads(onNfs)).IsEqualTo(1);
                await Assert.That(library.Reader.WindowReads(onSmb)).IsEqualTo(1);
                await Assert.That(library.Store.LocationOf(shadow)).IsEqualTo((sha, library.Store.LocationOf(shadow)!.Value.Location))
                    .Because("the next scan attaches the copy to the content its twin's parse hashed");
                await Assert.That(library.Store.LocationOf(shadow)?.Location.Confirmed).IsFalse();
            }
        }
        finally
        {
            Cleanup(nfs);
            Cleanup(smb);
        }
    }

    [Test]
    public async Task AFingerprintMatchTheFullHashContradicts_IsSplitOut_AndTheMatchedDemoKeepsItsData()
    {
        string nfs = NewTempDir();
        string smb = NewTempDir();
        try
        {
            byte[] original = Bytes(3);
            byte[] other = (byte[])original.Clone();
            other[Size / 2] ^= 0xFF; // same size, same head and tail windows, different bytes
            string onNfs = Write(nfs, "m.dem", original);
            string onSmb = Write(smb, "m.dem", other);
            string sha = DemoContentHash.Compute(original);
            string otherSha = DemoContentHash.Compute(other);
            using Library library = new(nfs, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([nfs]);
            await WaitForAsync(() => library.Store.TryGetByContentId(sha) is { ContentFingerprint: not null },
                "the first demo hashed");
            library.Store.UpdateExisting(onNfs, r =>
            {
                r.Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }];
                DemoCacheStore.StampAnalysis(r);
            });

            await svc.AddFoldersAsync([smb]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => svc.Entries is [{ HasDuplicates: true }], "shown as a copy while the fingerprints match");
            await Assert.That(library.Store.LocationOf(onSmb)?.ContentId).IsEqualTo(sha);

            // Something reads the file in full: here the library's own parse of it.
            DemoEntry entry = new()
            {
                FilePath = onSmb,
                FileName = "m.dem",
                Directory = smb,
                FileSizeBytes = Size,
                Modified = new FileInfo(onSmb).LastWriteTime
            };
            svc.IndexTier2Core(entry, SyntheticDemo());

            DemoCacheRecord? atNfs = library.Store.TryLoadRecord(onNfs);
            DemoCacheRecord? atSmb = library.Store.TryLoadRecord(onSmb);
            using (Assert.Multiple())
            {
                await Assert.That(library.Reader.FullReads(onSmb)).IsEqualTo(1);
                await Assert.That(library.Store.LocationOf(onSmb)?.ContentId).IsEqualTo(otherSha);
                await Assert.That(library.Store.LocationOf(onSmb)?.Location.Confirmed).IsTrue();
                await Assert.That(atSmb?.Scoreboard).IsEmpty().Because("the other file's analysis never ran");
                await Assert.That(atSmb?.Analysis.IsPresent).IsFalse();
                await Assert.That(atNfs?.Scoreboard.Count).IsEqualTo(1);
                await Assert.That(library.Store.TryGetByContentId(sha)?.Locations.Select(l => l.Path)).IsEquivalentTo([onNfs]);
            }

            await svc.RescanAsync();
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => svc.Entries.Count == 2, "two demos, two cards");
            await Assert.That(svc.Entries.Any(e => e.HasDuplicates)).IsFalse();
        }
        finally
        {
            Cleanup(nfs);
            Cleanup(smb);
        }
    }

    [Test]
    public async Task ADemoMovedInsideAFolder_IsRecognisedInTheScanThatLosesItsOldPath()
    {
        string root = NewTempDir();
        try
        {
            byte[] bytes = Bytes(4);
            string before = Write(root, "a/m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(root, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([root]);
            await WaitForAsync(() => library.Store.TryGetByContentId(sha) is { ContentFingerprint: not null },
                "the demo hashed at its first path");

            // The move: a new path holds the bytes and the listing no longer shows the old one.
            string after = Write(root, "b/renamed.dem", bytes);
            svc.FolderReader = new HidingReader(before);
            await svc.RescanAsync();
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => svc.Entries is [{ State: DemoIndexState.Indexed } only] && only.FilePath == after,
                "the moved demo's card");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(library.TotalParses).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(after)).IsEqualTo(0);
                await Assert.That(library.Reader.WindowReads(after)).IsEqualTo(1);
                await Assert.That(library.Store.LocationOf(before)).IsNull().Because("the old path goes once the new one matched");
                await Assert.That(library.Store.LocationOf(after)?.ContentId).IsEqualTo(sha);
                await Assert.That(svc.Entries[0].MapName).IsEqualTo("de_test");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The fingerprint reads run in light slices like the header reads: one that does not answer holds no lane,
    // and the scan's caller does not wait for it.
    [Test]
    public async Task AFingerprintReadThatDoesNotAnswer_HoldsNoLane_AndTheScanDoesNotWaitForIt()
    {
        string root = NewTempDir();
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            _ => throw new InvalidOperationException("nothing is parsed here"));
        CountingContentReader reader = new() { Block = p => p.Contains($"{Path.DirectorySeparatorChar}b{Path.DirectorySeparatorChar}") };
        try
        {
            string first = WriteDemo(root, "a/m.dem", 6, 9);
            string copy = WriteDemo(root, "b/m.dem", 6, 9);
            using DemoLibraryService svc = new(_inline, SeedLibrary(root, [root], Row(first), Row(copy)));
            svc.QueueOverride = queue;
            svc.Time = Settled;
            svc.ContentReader = reader;

            await svc.RescanAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => reader.Blocked > 0, "the copy's fingerprint read is stuck");
            await QueueWork.Run(queue, QueueJobKind.LibraryListing, "another light job", "test", _ => { })
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(svc.CopiesResolved.IsCompleted).IsFalse();

            reader.Release();
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(svc.Entries.Count).IsEqualTo(1);
        }
        finally
        {
            reader.Release();
            Cleanup(root);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }

    // Lists a folder as the real file system does, minus one file.
    private sealed class HidingReader(string hidden) : ILibraryFolderReader
    {
        public string ResolveRoot(string folder) => FileSystemLibraryFolderReader.Instance.ResolveRoot(folder);

        public LibraryDirectoryListing Read(string directory)
        {
            LibraryDirectoryListing listing = FileSystemLibraryFolderReader.Instance.Read(directory);
            return listing with
            {
                Demos = [.. listing.Demos.Where(d => !string.Equals(d.Path, hidden, StringComparison.OrdinalIgnoreCase))]
            };
        }
    }
}

/// <summary>Counts the library's full and window reads per path, with a small window; can hold chosen reads.</summary>
internal sealed class CountingContentReader : ILibraryContentReader
{
    private readonly ConcurrentDictionary<string, int> _full = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, int> _windows = new(StringComparer.OrdinalIgnoreCase);
    private int _blocked;

    public int WindowBytes { get; init; } = 256;

    public Func<string, bool>? Block { get; init; }

    public int Blocked => Volatile.Read(ref _blocked);

    public int FullReads(string path) => _full.GetValueOrDefault(path);

    public int WindowReads(string path) => _windows.GetValueOrDefault(path);

    public void Release() => _gate.TrySetResult();

    public (string? Sha256, DemoContentFingerprint? Fingerprint) HashAndFingerprint(string path, TimeProvider time)
    {
        _full.AddOrUpdate(path, 1, (_, n) => n + 1);
        return DemoContentFingerprint.TryComputeWithContentHash(path, time, null, WindowBytes);
    }

    public DemoContentFingerprint? Fingerprint(string path, TimeProvider time)
    {
        _windows.AddOrUpdate(path, 1, (_, n) => n + 1);
        if (Block?.Invoke(path) == true)
        {
            Interlocked.Increment(ref _blocked);
            _gate.Task.Wait(TimeSpan.FromMinutes(1));
        }

        return DemoContentFingerprint.TryCompute(path, time, null, WindowBytes);
    }
}

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

    internal static byte[] Bytes(int seed)
    {
        byte[] bytes = new byte[Size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    internal static string Write(string root, string relative, byte[] bytes)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    internal sealed class Library : IDisposable
    {
        private readonly DemoScheduler _scheduler;
        private readonly DemoProcessingQueue _queue;

        // parseHashes: the parse hashes the bytes it read and hands the hash to the cache, as the app's queue does.
        public Library(string dataDir, DemoCacheStore store, string? dataPath = null, bool parseHashes = false)
        {
            Store = store;
            _queue = new DemoProcessingQueue(new HeavyJobGate(), a => a(), path =>
            {
                Parses.AddOrUpdate(path, 1, (_, n) => n + 1);
                if (parseHashes)
                {
                    using MemoryStream bytes = new(File.ReadAllBytes(path));
                    (string sha, DemoContentFingerprint fingerprint) =
                        DemoContentFingerprint.ComputeWithContentHash(bytes, Reader.WindowBytes);
                    store.NoteContentRead(path, sha, fingerprint, bytes.Length, File.GetLastWriteTimeUtc(path));
                }

                return SyntheticDemo();
            });
            Service = new DemoLibraryService(_inline, dataPath ?? Path.Combine(dataDir, $"library-{Guid.NewGuid():N}.json"),
                demoCache: store)
            {
                Time = Settled,
                ContentReader = Reader,
                HeaderReader = Headers,
                QueueOverride = _queue,
                ParseHashesContent = parseHashes
            };
            _scheduler = new DemoScheduler([Service], _queue, Service.Tier2Backlog);
            Service.Scheduler = _scheduler;
        }

        public DemoLibraryService Service { get; }
        public DemoProcessingQueue Queue => _queue;
        public DemoCacheStore Store { get; }
        public CountingContentReader Reader { get; } = new();
        public CountingHeaderReader Headers { get; } = new();
        public ConcurrentDictionary<string, int> Parses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int TotalParses => Parses.Values.Sum();

        public void Dispose()
        {
            Service.Dispose();
            _scheduler.Dispose();
            _queue.Dispose();
        }
    }

    // The parse has the bytes in hand: tier 2 takes the hash and fingerprint it handed the cache, and reads
    // nothing of the file itself.
    [Test]
    public async Task AParseThatHashesWhatItRead_LeavesTier2NothingToRead()
    {
        string folder = NewTempDir();
        try
        {
            byte[] bytes = Bytes(9);
            string path = Write(folder, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(folder, new DemoCacheStore(null), parseHashes: true);

            await library.Service.AddFoldersAsync([folder]);
            await WaitForAsync(() => library.Service.Entries is [{ State: DemoIndexState.Indexed }]
                                     && library.Store.TryGetByContentId(sha) is { ContentFingerprint: not null },
                "the demo indexed and hashed");

            using (Assert.Multiple())
            {
                await Assert.That(library.TotalParses).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(path)).IsEqualTo(0);
                await Assert.That(library.Reader.WindowReads(path)).IsEqualTo(0);
                await Assert.That(library.Store.TryGetIndex(path)!.Sha256).IsEqualTo(sha);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
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
                await Assert.That(library.Headers.Reads(onSmb)).IsEqualTo(0)
                    .Because("a possible copy's header is read only once its fingerprint places it nowhere");
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

            // The first mount goes away: the second shows the demo from what the cache knows. The background
            // switch holds back the full read that later confirms it.
            library.Queue.BackgroundEnabled = false;
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

            // No confirmed path is left, so one background full read confirms the second mount's.
            library.Queue.BackgroundEnabled = true;
            await svc.Confirmed.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.Multiple())
            {
                await Assert.That(library.Reader.FullReads(onSmb)).IsEqualTo(1);
                await Assert.That(library.Store.TryGetByContentId(sha)?.Path).IsEqualTo(onSmb);
                await Assert.That(library.TotalParses).IsEqualTo(1);
            }
        }
        finally
        {
            Cleanup(nfs);
            Cleanup(smb);
        }
    }

    // A known demo stored before fingerprints were: a scan listing its path takes one, so a later mount of the
    // same bytes is recognised without a full read even after this path is gone.
    [Test]
    public async Task AKnownDemoWithoutAFingerprint_GetsOneFromItsListedPath()
    {
        string folder = NewTempDir();
        try
        {
            byte[] bytes = Bytes(11);
            string path = Write(folder, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            DemoCacheStore store = new(null);
            FileInfo info = new(path);
            DemoCacheRecord record = new() { Path = path, Size = info.Length, ModifiedTicks = info.LastWriteTime.Ticks, Map = "de_old" };
            record.SetContentHash(sha, null);
            DemoCacheStore.StampHeader(record);
            DemoCacheStore.StampParse(record);
            store.Upsert(record);
            using Library library = new(folder, store);

            await library.Service.AddFoldersAsync([folder]);
            await library.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(library.Store.TryGetByContentId(sha)?.ContentFingerprint?.WindowBytes)
                    .IsEqualTo(library.Reader.WindowBytes);
                await Assert.That(library.Reader.WindowReads(path)).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(path)).IsEqualTo(0);
                await Assert.That(library.TotalParses).IsEqualTo(0);
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // A known demo with no fingerprint whose only path is gone: a file of its size on a new mount can only be
    // told apart by a full read. That one read places it, and nothing parses it.
    [Test]
    public async Task ACopyOfADemoWithNoFingerprintAndNoPathLeft_IsHashedOnce_AndNotParsed()
    {
        string folder = NewTempDir();
        try
        {
            byte[] bytes = Bytes(12);
            string path = Write(folder, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            DemoCacheStore store = new(null);
            DemoCacheRecord record = new() { Path = "/gone/m.dem", Size = bytes.Length, ModifiedTicks = 1, Map = "de_old" };
            record.SetContentHash(sha, null);
            DemoCacheStore.StampHeader(record);
            DemoCacheStore.StampParse(record);
            store.Upsert(record);
            store.Detach("/gone/m.dem");
            using Library library = new(folder, store);

            await library.Service.AddFoldersAsync([folder]);
            await library.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => library.Service.Entries is [{ State: DemoIndexState.Indexed }], "the card filled");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(library.TotalParses).IsEqualTo(0);
                await Assert.That(library.Reader.FullReads(path)).IsEqualTo(1);
                await Assert.That(library.Store.TryGetByContentId(sha)?.Path).IsEqualTo(path);
                await Assert.That(library.Store.TryGetByContentId(sha)?.ContentFingerprint).IsNotNull();
                await Assert.That(library.Service.Entries[0].MapName).IsEqualTo("de_old");
            }
        }
        finally
        {
            Cleanup(folder);
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

            // The move: a new path holds the bytes and the listing no longer shows the old one. The background
            // switch holds back the full read that later confirms the new path.
            string after = Write(root, "b/renamed.dem", bytes);
            svc.FolderReader = new HidingReader(before);
            library.Queue.BackgroundEnabled = false;
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

            library.Queue.BackgroundEnabled = true;
            await svc.Confirmed.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(library.Store.TryGetByContentId(sha)?.Path).IsEqualTo(after);
            await Assert.That(library.TotalParses).IsEqualTo(1);
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

    internal static async Task WaitForAsync(Func<bool> condition, string what)
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

/// <summary>Counts the library's header reads per path; reads the real header.</summary>
internal sealed class CountingHeaderReader : ILibraryHeaderReader
{
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);

    public int Reads(string path) => _reads.GetValueOrDefault(path);

    public LibraryDemoHeader? Read(string path)
    {
        _reads.AddOrUpdate(path, 1, (_, n) => n + 1);
        return FileSystemLibraryHeaderReader.Instance.Read(path);
    }
}

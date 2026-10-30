#region

using System.Text.Json;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     What reaches <c>library.json</c>, and when. Two things make that non-obvious. A scan's save runs as a
///     LIGHT queue item, so it is on disk beside the tier-2 parse in the heavy lane rather than after it; and
///     a tier-2 result lands in the in-memory cache alone until the every-12-demos checkpoint, the end of the
///     backlog, or the service closing persists it.
/// </summary>
[NotInParallel]
public class DemoLibrarySaveTests
{
    private static readonly Action<Action> _inline = a => a();

    /// <summary>A parse with no frames: tier 2 records it as computed with no score, which is the honest answer.</summary>
    private static ParsedDemo ScorelessDemo(string map = "de_test") => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        map, 6400, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static DemoEntry Entry(string path)
    {
        FileInfo file = new(path);
        return new DemoEntry
        {
            FilePath = path,
            FileName = file.Name,
            Directory = file.DirectoryName ?? "",
            FileSizeBytes = file.Length,
            Modified = file.LastWriteTime
        };
    }

    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvsave_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteDemo(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static List<DemoLibraryCacheEntry> ReadRows(string dataPath) =>
        File.Exists(dataPath)
            ? JsonSerializer.Deserialize<DemoLibraryData>(File.ReadAllText(dataPath))?.Cache ?? []
            : [];

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

    /// <summary>
    ///     A tier-2 result that no checkpoint covered is persisted when the service closes. One enriched demo
    ///     is short of the every-12 save and <c>IndexTier2Core</c> drains no backlog, so nothing before
    ///     <c>Dispose</c> can have written the row.
    /// </summary>
    [Test]
    public async Task AnEnrichedRowShortOfACheckpoint_IsPersistedWhenTheServiceCloses()
    {
        string dir = TempRoot();
        try
        {
            string path = WriteDemo(dir, "a.dem");
            string dataPath = Path.Combine(dir, "library.json");

            DemoLibraryService svc = new(_inline, dataPath);
            svc.IndexTier2Core(Entry(path), ScorelessDemo());
            bool savedBeforeClose = File.Exists(dataPath);

            svc.Dispose();

            List<DemoLibraryCacheEntry> rows = ReadRows(dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(savedBeforeClose).IsFalse()
                    .Because("one enriched demo is short of the every-12-demos checkpoint");
                await Assert.That(rows.Select(r => r.Path)).IsEquivalentTo([path]);
                await Assert.That(rows is [{ ScoreComputed: true, FullyIndexed: true }]).IsTrue()
                    .Because("the row the parse wrote has to be on disk, or the next launch re-parses the demo");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The cache lock must be free while the snapshot serialises. Serialising under it blocks every
    ///     writer and every reader for the whole serialize, which measured 5.2 ms against 0.27 ms for the
    ///     copy at 2000 rows, with the serializer's allocation inside the lock too.
    ///     <para>
    ///         <c>IndexTier2Core</c> direct, and one demo only: the every-12 checkpoint and
    ///         <c>ClearTier2Backlog</c> both call <c>Save</c>, which would block on the gate this save holds
    ///         and look exactly like the failure being hunted.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheCacheLock_IsFreeWhileTheSnapshotSerialises()
    {
        string dir = TempRoot();
        try
        {
            string path = WriteDemo(dir, "a.dem");
            string dataPath = Path.Combine(dir, "library.json");

            using DemoLibraryService svc = new(_inline, dataPath);
            Func<DemoLibraryData, byte[]> real = svc.SerializeSnapshot;
            bool mutatedWhileSerialising = false;
            svc.SerializeSnapshot = data =>
            {
                svc.SerializeSnapshot = real;

                // Needs the cache lock. If the serialize still held it this blocks until the timeout.
                Task write = Task.Run(() => svc.IndexTier2Core(Entry(path), ScorelessDemo()));
                mutatedWhileSerialising = write.Wait(TimeSpan.FromSeconds(1));
                return real(data);
            };

            svc.Save();

            await Assert.That(mutatedWhileSerialising).IsTrue()
                .Because("a writer must not wait on the cache lock for the duration of a serialize");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     And the snapshot must be COPIES. A row mutated after the snapshot was taken must not reach the
    ///     file this save is writing: the rows are live objects whose fields move one at a time, so a
    ///     reference snapshot persists a half-written one.
    /// </summary>
    [Test]
    public async Task ARowMutatedWhileSerialising_DoesNotReachThisSavesFile()
    {
        string dir = TempRoot();
        try
        {
            string path = WriteDemo(dir, "a.dem");
            string dataPath = Path.Combine(dir, "library.json");

            using DemoLibraryService svc = new(_inline, dataPath);

            // A row in the snapshot, persisted, so the mutation below is a CHANGE to a row already taken.
            svc.IndexTier2Core(Entry(path), ScorelessDemo());
            svc.Save();
            await Assert.That(ReadRows(dataPath) is [{ Map: "de_test" }]).IsTrue()
                .Because("the snapshot this test mutates has to start from a known row");

            Func<DemoLibraryData, byte[]> real = svc.SerializeSnapshot;
            svc.SerializeSnapshot = data =>
            {
                svc.SerializeSnapshot = real;

                // The same demo again, different map: this rewrites the LIVE row's fields, which is the row
                // the snapshot would be aliasing.
                Task write = Task.Run(() => svc.IndexTier2Core(Entry(path), ScorelessDemo("de_mutated")));
                write.Wait(TimeSpan.FromSeconds(1));
                return real(data);
            };

            svc.Save();

            await Assert.That(ReadRows(dataPath) is [{ Map: "de_test" }]).IsTrue()
                .Because("this save's file is the snapshot it took, not the cache as it later became");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     THE write-ordering guard. Two saves overlap in the app: the scan's light-lane save and the one the
    ///     tier-2 parse triggers. The first save here holds the gate with the empty cache it snapshotted while
    ///     a tier-2 row lands, and the second must not be able to read the cache until that write is done, or
    ///     the older snapshot replaces the row on disk.
    /// </summary>
    [Test]
    public async Task AnOlderSnapshot_CannotOverwriteTheRowAParseJustWrote()
    {
        string dir = TempRoot();
        try
        {
            string path = WriteDemo(dir, "a.dem");
            string dataPath = Path.Combine(dir, "library.json");

            using DemoLibraryService svc = new(_inline, dataPath);
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            svc.BeforeSaveWrite = () =>
            {
                svc.BeforeSaveWrite = null;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };

            Task older = Task.Run(svc.Save);
            entered.Wait(TimeSpan.FromSeconds(10));

            svc.IndexTier2Core(Entry(path), ScorelessDemo());
            Task newer = Task.Run(svc.Save);
            await Task.WhenAny(newer, Task.Delay(500));
            bool newerOvertookIt = newer.IsCompleted;

            release.Set();
            await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(10));

            List<DemoLibraryCacheEntry> rows = ReadRows(dataPath);
            using (Assert.Multiple())
            {
                await Assert.That(newerOvertookIt).IsFalse()
                    .Because("a save cannot read the cache while another save is still writing the file");
                await Assert.That(rows is [{ ScoreComputed: true, FullyIndexed: true }]).IsTrue()
                    .Because("the older snapshot must not land on top of the row written after it was taken");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }
}

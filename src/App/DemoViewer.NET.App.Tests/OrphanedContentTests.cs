#region

using System.Text.Json;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A hashed demo whose last path goes away is orphaned, not deleted: its record and siblings stay until a
///     path holding its bytes comes back, which takes the row back with every tier, or the grace period runs
///     out. An explicit remove still deletes at once.
/// </summary>
public class OrphanedContentTests
{
    private const string Suffix = ".note.json";

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-orphan-{Guid.NewGuid():N}");

    private static DemoCacheRecord Analysed(string path, string sha, long size = 1000, long mtime = 2000)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = size,
            ModifiedTicks = mtime,
            Sha256 = sha,
            Map = "de_nuke",
            Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }],
            AnalysisState = DemoAnalysisState.Indexed
        };
        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    private static string[] Files(string root) =>
        Directory.Exists(Path.Combine(root, "demos"))
            ? [.. Directory.EnumerateFiles(Path.Combine(root, "demos")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Test]
    public async Task DetachingTheLastPath_OrphansTheRow_AndKeepsItsFiles()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a"));
            store.WriteSibling("/m/a.dem", Suffix, "facts");
            store.SaveIndex();
            string[] before = Files(root);

            List<string?> changes = [];
            store.Changed += changes.Add;
            store.Detach("/m/a.dem");

            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetIndex("/m/a.dem")).IsNull();
                await Assert.That(store.TryGetByContentId("sha-a")).IsNull();
                await Assert.That(store.Contents).IsEmpty();
                await Assert.That(store.HoldsContent("sha-a")).IsTrue();
                await Assert.That(store.OrphanAt("/m/a.dem")).IsEqualTo("sha-a");
                await Assert.That(store.TryGetOrphan("sha-a")!.OrphanedSinceUtcTicks).IsNotNull();
                await Assert.That(store.TryGetOrphan("sha-a")!.Path).IsEqualTo("/m/a.dem");
                await Assert.That(Files(root)).IsEquivalentTo(before).Because("nothing is deleted on a detach");
                await Assert.That(changes.Single()).IsEqualTo("/m/a.dem");
            }

            store.SaveIndex();
            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.TryGetIndex("/m/a.dem")).IsNull().Because("an orphan stays orphaned across a reload");
                await Assert.That(reopened.TryGetOrphan("sha-a")).IsNotNull();
                await Assert.That(reopened.Contents).IsEmpty();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task DetachingOnePathOfTwo_KeepsTheRowListedAtTheOther()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Analysed("/m/a.dem", "sha-a"));
        store.Upsert(Analysed("/n/a.dem", "sha-a"));
        store.Detach("/m/a.dem");

        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetOrphan("sha-a")).IsNull();
            await Assert.That(store.TryGetByContentId("sha-a")!.Path).IsEqualTo("/n/a.dem");
            await Assert.That(store.TryLoadRecord("/n/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
        }
    }

    [Test]
    public async Task ARowWithNoHash_GoesOnADetach()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Update("/m/x.dem", 10, 20, DemoCacheStore.StampHeader);
            store.Detach("/m/x.dem");

            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetIndex("/m/x.dem")).IsNull();
                await Assert.That(store.OrphanAt("/m/x.dem")).IsNull()
                    .Because("a row known only by its path cannot be told from another file there");
                await Assert.That(Files(root)).IsEmpty();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task TheSameFileBackAtItsPath_TakesTheRowBack_WithEveryTier()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Analysed("/m/a.dem", "sha-a"));
        store.WriteSibling("/m/a.dem", Suffix, "facts");
        store.Detach("/m/a.dem");

        using (Assert.Multiple())
        {
            await Assert.That(store.Reattach("/m/a.dem", 1000, 9999)).IsFalse().Because("another write time is another file");
            await Assert.That(store.Reattach("/m/a.dem", 1000, 2000)).IsTrue();
            await Assert.That(store.TryGetOrphan("sha-a")).IsNull();
            await Assert.That(store.LocationOf("/m/a.dem")!.Value.Location.Confirmed).IsTrue();
            await Assert.That(store.TryGetByContentId("sha-a")!.Tier).IsEqualTo(DemoCacheTier.Analysis);
            await Assert.That(store.TryLoadRecord("/m/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
            await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("facts");
        }
    }

    [Test]
    public async Task TheBytesFoundAtANewPath_TakeTheRowBack()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Analysed("/m/a.dem", "sha-a"));
        store.WriteSibling("/m/a.dem", Suffix, "facts");
        store.Detach("/m/a.dem");

        bool attached = store.AttachUnconfirmed("sha-a", "/smb/a.dem", 1000, 3000);
        using (Assert.Multiple())
        {
            await Assert.That(attached).IsTrue();
            await Assert.That(store.TryGetOrphan("sha-a")).IsNull();
            await Assert.That(store.TryGetIndex("/smb/a.dem")!.Tier).IsEqualTo(DemoCacheTier.Analysis);
            await Assert.That(store.TryGetIndex("/smb/a.dem")!.Locations).HasCount(1);
        }

        // A full read there confirms it, and a tier write at the new path keeps what the content had.
        store.ConfirmLocation("/smb/a.dem", "sha-a", 1000);
        store.UpdateExisting("/smb/a.dem", r => r.Server = "s");
        using (Assert.Multiple())
        {
            await Assert.That(store.TryLoadRecord("/smb/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
            await Assert.That(store.TryReadSibling("/smb/a.dem", Suffix)).IsEqualTo("facts");
        }
    }

    [Test]
    public async Task AParseOfTheSameBytesAtANewPath_JoinsTheOrphan_AndKeepsItsTiers()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a"));
            store.Detach("/m/a.dem");

            DemoCacheRecord parsed = new() { Path = "/k/a.dem", Size = 1000, ModifiedTicks = 5000, Sha256 = "sha-a", Map = "de_nuke" };
            DemoCacheStore.StampParse(parsed);
            store.Upsert(parsed);

            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetOrphan("sha-a")).IsNull();
                await Assert.That(store.TryLoadRecord("/k/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21)
                    .Because("the analysis the orphan held is filled into the new write");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task OtherBytesWrittenAtAHashedPath_OrphanTheOldContent()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Analysed("/m/a.dem", "sha-a"));
        store.WriteSibling("/m/a.dem", Suffix, "old facts");

        store.Upsert(new DemoCacheRecord { Path = "/m/a.dem", Size = 1200, ModifiedTicks = 3000, Sha256 = "sha-b" });

        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetOrphan("sha-a")).IsNotNull();
            await Assert.That(store.TryGetIndex("/m/a.dem")!.Sha256).IsEqualTo("sha-b");
            await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsNull().Because("the old demo's sibling is not the new one's");
        }

        store.AttachUnconfirmed("sha-a", "/n/a.dem", 1000, 2000);
        await Assert.That(store.TryReadSibling("/n/a.dem", Suffix)).IsEqualTo("old facts");
    }

    [Test]
    public async Task AnOrphanWhoseFilesSitUnderItsPathsKey_KeepsThem_WhenAnotherFileIsWrittenThere()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            // No index save yet: a new row's files are still under the path's key.
            store.Upsert(Analysed("/m/a.dem", "sha-a"));
            store.WriteSibling("/m/a.dem", Suffix, "facts");
            store.Detach("/m/a.dem");
            store.Update("/m/a.dem", 50, 60, DemoCacheStore.StampHeader);

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Analysis.IsPresent).IsFalse();
                await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsNull();
                await Assert.That(store.TryGetOrphan("sha-a")).IsNotNull();
            }

            store.AttachUnconfirmed("sha-a", "/n/a.dem", 1000, 2000);
            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetIndex("/n/a.dem")!.Tier).IsEqualTo(DemoCacheTier.Analysis);
                await Assert.That(store.TryReadSibling("/n/a.dem", Suffix)).IsEqualTo("facts");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task TwoOrphansThatHadOnePath_AreToldApartByTheFilesStamps()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Analysed("/m/a.dem", "sha-old", 1000, 2000));
        store.Detach("/m/a.dem");
        store.Upsert(Analysed("/m/a.dem", "sha-new", 1500, 3000));
        store.Detach("/m/a.dem");

        await Assert.That(store.Reattach("/m/a.dem", 1000, 2000)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetIndex("/m/a.dem")!.Sha256).IsEqualTo("sha-old");
            await Assert.That(store.TryGetOrphan("sha-new")).IsNotNull();
        }

        store.Detach("/m/a.dem");
        store.Remove("/m/a.dem");
        using (Assert.Multiple())
        {
            await Assert.That(store.HoldsContent("sha-old")).IsFalse().Because("an explicit remove takes every orphan that had the path");
            await Assert.That(store.HoldsContent("sha-new")).IsFalse();
        }
    }

    [Test]
    public async Task TheSweep_DeletesOnlyOrphansPastTheGracePeriod()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/old.dem", "sha-old"));
            store.Upsert(Analysed("/m/live.dem", "sha-live"));
            store.WriteSibling("/m/old.dem", Suffix, "facts");
            store.Detach("/m/old.dem");
            long since = store.TryGetOrphan("sha-old")!.OrphanedSinceUtcTicks!.Value;

            TimeSpan grace = TimeSpan.FromDays(14);
            DateTime due = new DateTime(since, DateTimeKind.Utc) + grace;
            await Assert.That(store.ExpireOrphans(due - TimeSpan.FromSeconds(1), grace)).IsEqualTo(0);
            await Assert.That(store.HoldsContent("sha-old")).IsTrue();

            List<string?> changes = [];
            store.Changed += changes.Add;
            int expired = store.ExpireOrphans(due, grace);

            using (Assert.Multiple())
            {
                await Assert.That(expired).IsEqualTo(1);
                await Assert.That(store.HoldsContent("sha-old")).IsFalse();
                await Assert.That(Files(root).Where(f => f.StartsWith("sha-old", StringComparison.Ordinal))).IsEmpty();
                await Assert.That(store.TryGetByContentId("sha-live")).IsNotNull();
                await Assert.That(changes is [null]).IsTrue().Because("the sweep raises one change for the lot");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task TheQueuedSweep_RunsOnTheQueue_AndSavesTheIndex()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a"));
            store.Detach("/m/a.dem");
            store.SaveIndex();
            DateTime later = DateTime.UtcNow.AddDays(15);

            using HeavyJobGate gate = new();
            using DemoProcessingQueue queue = new(gate, a => a(), _ => throw new NotSupportedException(),
                _ => throw new NotSupportedException(), () => Task.CompletedTask);
            IDemoQueueHandle? handle = OrphanSweep.Submit(queue, store, TimeSpan.FromDays(14), () => later);
            await Assert.That(handle).IsNotNull();
            await handle!.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Completed);
                await Assert.That(new DemoCacheStore(root).HoldsContent("sha-a")).IsFalse();
                await Assert.That(OrphanSweep.Submit(queue, new DemoCacheStore(null), TimeSpan.FromDays(14))).IsNull();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task ACrashAfterTheSweepDeletedFiles_LeavesARowTheNextSweepFinishes()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a"));
            store.Detach("/m/a.dem");
            store.SaveIndex();

            // The sweep deleted the files and died before the index save.
            DemoCacheStore crashed = new(root);
            crashed.ExpireOrphans(DateTime.UtcNow.AddDays(30), TimeSpan.FromDays(14));
            await Assert.That(Files(root)).IsEmpty();

            DemoCacheStore reopened = new(root);
            await Assert.That(reopened.HoldsContent("sha-a")).IsTrue().Because("the index on disk still names it");
            await Assert.That(reopened.ExpireOrphans(DateTime.UtcNow.AddDays(30), TimeSpan.FromDays(14))).IsEqualTo(1);
            reopened.SaveIndex();
            await Assert.That(new DemoCacheStore(root).HoldsContent("sha-a")).IsFalse();
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task AnExplicitRemove_DeletesAtOnce_LiveOrOrphaned()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a"));
            store.Upsert(Analysed("/m/b.dem", "sha-b"));
            store.WriteSibling("/m/b.dem", Suffix, "facts");
            store.Remove("/m/a.dem");
            store.Detach("/m/b.dem");
            store.Remove("/m/b.dem");

            using (Assert.Multiple())
            {
                await Assert.That(store.HoldsContent("sha-a")).IsFalse();
                await Assert.That(store.HoldsContent("sha-b")).IsFalse();
                await Assert.That(Files(root)).IsEmpty();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task AVersion5Index_LoadsWithNoOrphans_AndSavesAsVersion6()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore first = new(root);
            first.Upsert(Analysed("/m/a.dem", "sha-a"));
            first.SaveIndex();
            string index = Path.Combine(root, "index.json");
            string v5 = File.ReadAllText(index).Replace("\"Version\":6", "\"Version\":5", StringComparison.Ordinal);
            File.WriteAllText(index, v5);

            DemoCacheStore store = new(root);
            await Assert.That(store.TryGetByContentId("sha-a")).IsNotNull();
            store.Detach("/m/a.dem");
            store.SaveIndex();

            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(index));
            using (Assert.Multiple())
            {
                await Assert.That(saved.RootElement.GetProperty("Version").GetInt32()).IsEqualTo(6);
                await Assert.That(new DemoCacheStore(root).TryGetOrphan("sha-a")).IsNotNull();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task UpdateOrphans_ChangesTheRecordAndKeepsTheRowOrphaned()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = Analysed("/m/a.dem", "sha-a");
        record.PackStamps.Add(new PackStamp("facet", 1, null));
        store.Upsert(record);
        store.Detach("/m/a.dem");

        int updated = store.UpdateOrphans(r => r.PackStamps.Count > 0, r => r.PackStamps.Clear());
        using (Assert.Multiple())
        {
            await Assert.That(updated).IsEqualTo(1);
            await Assert.That(store.TryGetOrphan("sha-a")!.PackStamps).IsEmpty();
        }

        store.Reattach("/m/a.dem", 1000, 2000);
        using (Assert.Multiple())
        {
            await Assert.That(store.TryLoadRecord("/m/a.dem")!.PackStamps).IsEmpty();
            await Assert.That(store.TryLoadRecord("/m/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
        }
    }
}

#region

using System.Text.Json;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The demo cache keyed by content: one row per hash listing every path, a provisional row per path until
///     the hash is known, files renamed to the hash only after an index naming it is saved, and the re-key of
///     an index written when rows were paths.
/// </summary>
public class ContentKeyedCacheTests
{
    private const string Suffix = ".note.json";

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-content-key-{Guid.NewGuid():N}");

    private static string Demos(string root) => Path.Combine(root, "demos");

    private static DemoCacheRecord Record(string path, string? sha, string map = "de_nuke", long size = 1000, long mtime = 2000) => new()
    {
        Path = path,
        Size = size,
        ModifiedTicks = mtime,
        Sha256 = sha,
        Map = map
    };

    private static DemoCacheRecord Analysed(string path, string sha)
    {
        DemoCacheRecord record = Record(path, sha);
        record.Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }];
        record.AnalysisState = DemoAnalysisState.Indexed;
        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    // What a version-4 cache left on disk: rows by path, files named by the path's key.
    private static void WriteVersion4(string root, params (DemoCacheRecord Record, string? Sibling)[] demos)
    {
        Directory.CreateDirectory(Demos(root));
        foreach ((DemoCacheRecord record, string? sibling) in demos)
        {
            string key = DemoCacheStore.StableKey(record.Path);
            File.WriteAllBytes(Path.Combine(Demos(root), key + DemoCacheStore.RecordSuffix), SidecarJson.SerializeGzip(record, null));
            if (sibling is not null)
            {
                File.WriteAllText(Path.Combine(Demos(root), key + Suffix), sibling);
            }
        }

        File.WriteAllText(Path.Combine(root, "index.json"), JsonSerializer.Serialize(new
        {
            Version = 4,
            LegacyMigrationVersion = 1,
            Entries = demos.Select(d => d.Record.ToIndexEntry()).ToList()
        }));
    }

    private static string[] Files(string root) =>
        [.. Directory.EnumerateFiles(Demos(root)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Test]
    public async Task AVersion4Index_LoadsByContent_ReadsBeforeThePass_AndThePassMovesTheFiles()
    {
        string root = TempRoot();
        try
        {
            WriteVersion4(root,
                (Record("/m/a.dem", "sha-x"), "from a"),
                (Analysed("/m/b.dem", "sha-x"), "from b"),
                (Record("/m/c.dem", "sha-c"), null),
                (Record("/m/d.dem", null), "from d"));

            DemoCacheStore store = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(store.ContentKeyMigrationVersion).IsEqualTo(0);
                await Assert.That(store.Count).IsEqualTo(4).Because("every path still has its row view");
                await Assert.That(store.RowsForContentId("sha-x").Select(r => r.Path))
                    .IsEquivalentTo(["/m/a.dem", "/m/b.dem"], CollectionOrdering.Matching);
                await Assert.That(store.TryGetIndex("/m/a.dem")!.Locations).HasCount(2);
                await Assert.That(store.TryGetIndex("/m/a.dem")!.Tier).IsEqualTo(DemoCacheTier.Analysis)
                    .Because("the copy with the most tiers speaks for the row");
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Path).IsEqualTo("/m/a.dem");
                await Assert.That(store.TryLoadRecord("/m/c.dem")).IsNotNull();
                await Assert.That(store.TryLoadRecord("/m/d.dem")).IsNotNull();
                await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("from b");
                await Assert.That(store.TryReadSibling("/m/d.dem", Suffix)).IsEqualTo("from d");
            }

            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(store, batchSize: 1);

            string d = DemoCacheStore.StableKey("/m/d.dem");
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(result.Failed).IsEqualTo(0);
                await Assert.That(Files(root)).IsEquivalentTo(
                    [d + DemoCacheStore.RecordSuffix, d + Suffix, "sha-c.json.gz", "sha-x.json.gz", "sha-x" + Suffix],
                    CollectionOrdering.Matching)
                    .Because("hashed rows move to the hash, the duplicate copy goes, the unhashed row stays put");
                await Assert.That(store.ContentKeyMigrationVersion).IsEqualTo(ContentKeyMigration.CurrentVersion);
            }

            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.ContentKeyMigrationVersion).IsEqualTo(ContentKeyMigration.CurrentVersion);
                await Assert.That(reopened.TryLoadRecord("/m/b.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(reopened.TryLoadRecord("/m/b.dem")!.Path).IsEqualTo("/m/b.dem");
                await Assert.That(reopened.TryLoadRecord("/m/c.dem")!.Map).IsEqualTo("de_nuke");
                await Assert.That(reopened.TryReadSibling("/m/b.dem", Suffix)).IsEqualTo("from b");
                await Assert.That(reopened.SidecarPathFor("/m/a.dem")).EndsWith("sha-x.json.gz");
                await Assert.That((await ContentKeyMigration.RunAsync(reopened)).Completed).IsFalse()
                    .Because("the marker ends the pass for good");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AVersion4IndexWithoutHashes_MovesNothing_AndIsMarkedDone()
    {
        string root = TempRoot();
        try
        {
            WriteVersion4(root, (Record("/m/a.dem", null), "a"), (Record("/m/b.dem", null), null));
            string[] before = Files(root);

            DemoCacheStore store = new(root);
            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(store);

            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(result.Settled).IsEqualTo(0);
                await Assert.That(Files(root)).IsEquivalentTo(before, CollectionOrdering.Matching);
                await Assert.That(new DemoCacheStore(root).TryLoadRecord("/m/a.dem")).IsNotNull();
                await Assert.That(new DemoCacheStore(root).ContentKeyMigrationVersion).IsEqualTo(ContentKeyMigration.CurrentVersion);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Two copies indexed apart, the lesser one's files still under its old name after the better one's
    ///     moved: a crash between the two moves. A read serves the better copy, and the pass finishes the move
    ///     without the lesser record ever replacing it.
    /// </summary>
    [Test]
    public async Task ACrashBetweenTheTwoMovesOfARow_StillReadsTheBetterCopy()
    {
        string root = TempRoot();
        try
        {
            WriteVersion4(root, (Analysed("/m/a.dem", "sha-x"), "note a"), (Record("/m/b.dem", "sha-x"), "note b"));
            DemoCacheStore first = new(root);
            await Assert.That(first.TrySaveIndex()).IsTrue();

            string a = DemoCacheStore.StableKey("/m/a.dem");
            foreach (string file in Directory.EnumerateFiles(Demos(root), a + ".*").ToList())
            {
                File.Move(file, Path.Combine(Demos(root), "sha-x" + Path.GetFileName(file)[a.Length..]));
            }

            DemoCacheStore store = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/b.dem")!.Scoreboard).HasCount(1);
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Scoreboard).HasCount(1);
                await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("note a");
            }

            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(store);
            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(reopened.TryLoadRecord("/m/b.dem")!.Scoreboard).HasCount(1);
                await Assert.That(reopened.TryReadSibling("/m/b.dem", Suffix)).IsEqualTo("note a");
                await Assert.That(Files(root)).IsEquivalentTo(["sha-x.json.gz", "sha-x" + Suffix], CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Two copies indexed apart, only the lesser one carrying a pack's payload: the merged row keeps the
    ///     better copy's tiers and the lesser copy's payload, stamp and round facts.
    /// </summary>
    [Test]
    public async Task TwoCopiesIndexedApart_MergeIntoOneRecord_KeepingEachCopysData()
    {
        string root = TempRoot();
        try
        {
            DemoCacheRecord packed = Record("/m/b.dem", "sha-x");
            packed.Packs["pack"] = JsonSerializer.SerializeToElement(7);
            packed.SetStamp(new PackStamp("pack", 1, null));
            packed.RoundFacts = new RoundFactsRows { Schema = RoundFactsRecords.Schema };
            WriteVersion4(root, (Analysed("/m/a.dem", "sha-x"), null), (packed, null));

            DemoCacheStore store = new(root);
            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(store);
            DemoCacheStore reopened = new(root);
            DemoCacheRecord merged = reopened.TryLoadRecord("/m/a.dem")!;
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(merged.Scoreboard).HasCount(1);
                await Assert.That(merged.Packs["pack"].GetInt32()).IsEqualTo(7);
                await Assert.That(merged.Stamp("pack")).IsNotNull();
                await Assert.That(merged.RoundFacts).IsNotNull();
                await Assert.That(store.TryGetIndex("/m/b.dem")!.Stamp("pack")).IsNotNull()
                    .Because("the row is projected from the merged record");
                await Assert.That(reopened.TryGetIndex("/m/b.dem")!.Stamp("pack")).IsNotNull();
                await Assert.That(Files(root)).IsEquivalentTo(["sha-x.json.gz"], CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A crash after files were renamed and before the index that names them was saved, both ways round:
    ///     the version-4 index still on disk, and the re-keyed index saved before the renames. Every record
    ///     and sibling still reads, and the next pass finishes.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ACrashBetweenRenameAndIndexSave_LosesNothing(bool savedFirst)
    {
        string root = TempRoot();
        try
        {
            WriteVersion4(root, (Analysed("/m/a.dem", "sha-a"), "note a"), (Record("/m/b.dem", "sha-b"), null));

            DemoCacheStore crashed = new(root);
            if (savedFirst)
            {
                await Assert.That(crashed.TrySaveIndex()).IsTrue();
            }

            foreach (string row in crashed.UnsettledRows())
            {
                await Assert.That(crashed.SettleRow(row)).IsTrue();
            }

            // The process dies here: the index on disk still names the old keys.
            await Assert.That(File.Exists(Path.Combine(Demos(root), "sha-a.json.gz"))).IsTrue();

            DemoCacheStore store = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(store.TryLoadRecord("/m/b.dem")).IsNotNull();
                await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("note a");
            }

            // A write before the pass lands under the name the index still gives, and the pass carries it over.
            store.UpdateExisting("/m/a.dem", r => r.Map = "de_ancient");
            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(store);
            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(reopened.TryLoadRecord("/m/a.dem")!.Map).IsEqualTo("de_ancient");
                await Assert.That(reopened.TryLoadRecord("/m/a.dem")!.Scoreboard).HasCount(1);
                await Assert.That(reopened.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("note a");
                await Assert.That(Files(root).Any(f => f.StartsWith(DemoCacheStore.StableKey("/m/a.dem"), StringComparison.Ordinal)))
                    .IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The re-keyed index saved and the app closed before the pass ran: the next launch still knows which
    ///     rows have files under an old name, and the pass moves them.
    /// </summary>
    [Test]
    public async Task ARekeyedIndexSavedBeforeThePass_StillNamesTheOldKeys()
    {
        string root = TempRoot();
        try
        {
            WriteVersion4(root, (Analysed("/m/a.dem", "sha-a"), "note a"), (Record("/m/b.dem", null), null));
            await Assert.That(new DemoCacheStore(root).TrySaveIndex()).IsTrue();

            DemoCacheStore store = new(root);
            string pathKey = DemoCacheStore.StableKey("/m/a.dem");
            using (Assert.Multiple())
            {
                await Assert.That(store.UnsettledRows()).IsEquivalentTo(["sha-a"]);
                await Assert.That(store.ContentKeyMigrationVersion).IsEqualTo(0);
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Scoreboard).HasCount(1);
                await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("note a");
            }

            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(store);
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(result.Settled).IsEqualTo(1);
                await Assert.That(Files(root).Any(f => f.StartsWith(pathKey, StringComparison.Ordinal))).IsFalse();
                await Assert.That(Files(root)).Contains("sha-a.json.gz");
                await Assert.That(Files(root)).Contains("sha-a" + Suffix);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AHashLearnedLater_MovesTheRecordAndItsSiblings_OnlyOnceTheIndexIsSaved()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Update("/m/c.dem", 1000, 2000, DemoCacheStore.StampHeader);
            store.WriteSibling("/m/c.dem", Suffix, "before the hash");
            store.SaveIndex();
            string pathKey = DemoCacheStore.StableKey("/m/c.dem");
            await Assert.That(Files(root)).IsEquivalentTo([pathKey + DemoCacheStore.RecordSuffix, pathKey + Suffix],
                CollectionOrdering.Matching);

            store.Update("/m/c.dem", 1000, 2000, r => r.Sha256 = "sha-z");
            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetByContentId("sha-z")!.Path).IsEqualTo("/m/c.dem");
                await Assert.That(Files(root).All(f => f.StartsWith(pathKey, StringComparison.Ordinal))).IsTrue()
                    .Because("nothing is renamed before an index naming the hash is on disk");
                await Assert.That(new DemoCacheStore(root).TryLoadRecord("/m/c.dem")!.Sha256).IsEqualTo("sha-z")
                    .Because("the old index still finds the newest record under the path's key");
            }

            store.SaveIndex();
            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(Files(root)).IsEquivalentTo(["sha-z.json.gz", "sha-z" + Suffix],
                    CollectionOrdering.Matching);
                await Assert.That(reopened.TryReadSibling("/m/c.dem", Suffix)).IsEqualTo("before the hash");
                await Assert.That(reopened.TryLoadRecord("/m/c.dem")!.Header.IsPresent).IsTrue();
                await Assert.That(reopened.SidecarPathFor("/m/c.dem")).EndsWith("sha-z.json.gz");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AFileModifiedInPlace_SplitsIntoANewContentRow()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-old"));
            store.Upsert(Analysed("/m/copy.dem", "sha-old"));
            store.Upsert(Analysed("/m/solo.dem", "sha-solo"));
            store.SaveIndex();

            // Both files rewritten: a parse sees new bytes, so the drift check hands back a fresh record.
            store.Update("/m/a.dem", 5000, 6000, r => r.SetContentHash("sha-new", null));
            store.Update("/m/solo.dem", 5000, 6000, r => r.SetContentHash("sha-solo2", null));
            store.SaveIndex();

            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.TryGetByContentId("sha-new")!.Path).IsEqualTo("/m/a.dem");
                await Assert.That(reopened.TryLoadRecord("/m/a.dem")!.Analysis.IsPresent).IsFalse()
                    .Because("the old bytes' analysis never describes the new ones");
                await Assert.That(reopened.TryGetIndex("/m/a.dem")!.Size).IsEqualTo(5000);
                await Assert.That(reopened.RowsForContentId("sha-old").Select(r => r.Path)).IsEquivalentTo(["/m/copy.dem"]);
                await Assert.That(reopened.TryLoadRecord("/m/copy.dem")!.Scoreboard.Single().Kills).IsEqualTo(21)
                    .Because("the copy still holds the old bytes and keeps everything");
                await Assert.That(reopened.TryGetByContentId("sha-solo")).IsNull();
                await Assert.That(reopened.TryGetByContentId("sha-solo2")!.Path).IsEqualTo("/m/solo.dem");
                await Assert.That(reopened.TryGetOrphan("sha-solo")).IsNotNull()
                    .Because("no path holds the old bytes any more, so they wait for a path to come back");
                await Assert.That(File.Exists(Path.Combine(Demos(root), "sha-solo.json.gz"))).IsTrue();
                await Assert.That(reopened.Count).IsEqualTo(3);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A path leaving a row whose files still sit under that path's key: the row's other paths keep their
    ///     record and siblings, before a save, after a crash, and after the rename pass.
    /// </summary>
    [Test]
    [Arguments("/m/a.dem", "/m/b.dem")]
    [Arguments("/m/b.dem", "/m/a.dem")]
    public async Task AFileModifiedInPlace_BeforeItsRowSettled_LeavesTheOtherPathsFiles(string modified, string kept)
    {
        string root = TempRoot();
        try
        {
            WriteVersion4(root,
                (Record("/m/a.dem", "sha-x"), "from a"),
                (Analysed("/m/b.dem", "sha-x"), "from b"));

            DemoCacheStore store = new(root);
            store.Update(modified, 5000, 6000, r => r.SetContentHash("sha-new", null));
            store.WriteSibling(modified, Suffix, "new bytes");

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord(kept)!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(store.TryReadSibling(kept, Suffix)).IsEqualTo("from b");
                await Assert.That(store.TryLoadRecord(modified)!.Analysis.IsPresent).IsFalse();
                await Assert.That(store.TryReadSibling(modified, Suffix)).IsEqualTo("new bytes");
            }

            DemoCacheStore crashed = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(crashed.TryLoadRecord(kept)!.Scoreboard.Single().Kills).IsEqualTo(21)
                    .Because("the index on disk never names the modified path's key for the kept row's files");
                await Assert.That(crashed.TryReadSibling(kept, Suffix)).IsEqualTo("from b");
            }

            store.SaveIndex();
            DemoCacheStore reopened = new(root);
            ContentKeyMigrationResult result = await ContentKeyMigration.RunAsync(reopened, batchSize: 1);
            using (Assert.Multiple())
            {
                await Assert.That(result.Failed).IsEqualTo(0);
                await Assert.That(reopened.TryLoadRecord(kept)!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(reopened.TryReadSibling(kept, Suffix)).IsEqualTo("from b");
                await Assert.That(reopened.TryGetByContentId("sha-new")!.Path).IsEqualTo(modified);
                await Assert.That(reopened.TryReadSibling(modified, Suffix)).IsEqualTo("new bytes");
                await Assert.That(reopened.TryLoadRecord(modified)!.Analysis.IsPresent).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Copies upserted in one session share the first path's key until a save. That path leaving, by a
    ///     rewrite or a remove and a new file, never overwrites the copy's record.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task APathLeavingAnUnsavedRow_NeverOverwritesTheCopysRecord(bool removeFirst)
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-old"));
            store.Upsert(Analysed("/m/copy.dem", "sha-old"));
            store.WriteSibling("/m/copy.dem", Suffix, "old bytes");

            if (removeFirst)
            {
                store.Remove("/m/a.dem");
                store.Upsert(Record("/m/a.dem", null, "de_inferno", 5000, 6000));
            }
            else
            {
                store.Update("/m/a.dem", 5000, 6000, r => r.SetContentHash("sha-new", null));
            }

            store.WriteSibling("/m/a.dem", Suffix, "new bytes");

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/copy.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(store.TryReadSibling("/m/copy.dem", Suffix)).IsEqualTo("old bytes");
                await Assert.That(store.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("new bytes");
            }

            store.SaveIndex();
            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.TryLoadRecord("/m/copy.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(reopened.TryReadSibling("/m/copy.dem", Suffix)).IsEqualTo("old bytes");
                await Assert.That(reopened.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("new bytes");
                await Assert.That(reopened.TryLoadRecord("/m/a.dem")!.Scoreboard).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A row whose own name cannot be written still moves off the leaving path's key.</summary>
    [Test]
    public async Task APathLeavingAnUnsavedRow_WhenTheRowsNameIsBlocked_StillLeavesTheCopysRecord()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-old"));
            store.Upsert(Analysed("/m/copy.dem", "sha-old"));
            store.WriteSibling("/m/copy.dem", Suffix, "old bytes");
            Directory.CreateDirectory(Path.Combine(Demos(root), "sha-old" + DemoCacheStore.RecordSuffix));

            store.Update("/m/a.dem", 5000, 6000, r => r.Map = "de_inferno");
            store.WriteSibling("/m/a.dem", Suffix, "new bytes");
            store.SaveIndex();

            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/copy.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(store.TryReadSibling("/m/copy.dem", Suffix)).IsEqualTo("old bytes");
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Map).IsEqualTo("de_inferno");
                await Assert.That(reopened.TryLoadRecord("/m/copy.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(reopened.TryReadSibling("/m/copy.dem", Suffix)).IsEqualTo("old bytes");
                await Assert.That(reopened.TryReadSibling("/m/a.dem", Suffix)).IsEqualTo("new bytes");
                await Assert.That(reopened.TryLoadRecord("/m/a.dem")!.Scoreboard).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A copy reaching the hash at a lower tier joins the row and erases nothing the row holds.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ACopyJoiningARow_KeepsEveryTierTheRowHeld(bool onDisk)
    {
        string? root = onDisk ? TempRoot() : null;
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-x"));
            store.SaveIndex();
            store.Update("/m/b.dem", 1000, 3000, r =>
            {
                r.Map = "de_nuke";
                DemoCacheStore.StampHeader(r);
            });

            store.Update("/m/b.dem", 1000, 3000, r => r.Sha256 = "sha-x");
            store.SaveIndex();

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/b.dem")!.Scoreboard.Single().Kills).IsEqualTo(21);
                await Assert.That(store.TryLoadRecord("/m/b.dem")!.Header.IsPresent).IsTrue();
                await Assert.That(store.TryLoadRecord("/m/b.dem")!.ModifiedTicks).IsEqualTo(3000)
                    .Because("each path reads with its own write time");
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Analysis.IsPresent).IsTrue();
                await Assert.That(store.TryLoadRecord("/m/a.dem")!.Locations).HasCount(2);
                await Assert.That(store.RowsForContentId("sha-x")).HasCount(2);
            }

            store.Remove("/m/a.dem");
            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoadRecord("/m/b.dem")!.Analysis.IsPresent).IsTrue()
                    .Because("removing one path keeps the row while another path holds the bytes");
                await Assert.That(store.TryGetByContentId("sha-x")!.Path).IsEqualTo("/m/b.dem");
            }

            if (root is not null)
            {
                await Assert.That(Files(root)).IsEquivalentTo(["sha-x.json.gz"]);
            }
        }
        finally
        {
            if (root is not null)
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task Contents_ListsADemoOnce_FromItsPrimary_AndLoadRecordsReadsEachDemoOnce()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Record("/n/x.dem", "sha-x"));
        store.Upsert(Record("/m/x.dem", "sha-x"));
        store.Upsert(Record("/m/y.dem", null));

        using (Assert.Multiple())
        {
            await Assert.That(store.Index).HasCount(3).Because("the index still holds a view per path");
            await Assert.That(store.Contents.Select(e => e.Path))
                .IsEquivalentTo(["/m/x.dem", "/m/y.dem"], CollectionOrdering.Any);
            await Assert.That(store.LoadRecords().Select(r => r.Path))
                .IsEquivalentTo(["/m/x.dem", "/m/y.dem"], CollectionOrdering.Any);
            await Assert.That(store.TryGetPrimary("/n/x.dem")!.Path).IsEqualTo("/m/x.dem");
            await Assert.That(store.TryGetPrimary("/n/X.DEM")).IsSameReferenceAs(store.TryGetPrimary("/m/x.dem"));
            await Assert.That(store.TryGetPrimary("/m/z.dem")).IsNull();
        }
    }

    [Test]
    public async Task DemoKeyOf_IsTheHashForAHashedPath_AndThePathOtherwise()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Record("/n/x.dem", "sha-x"));
        store.Upsert(Record("/m/x.dem", "sha-x"));
        store.Upsert(Record("/m/y.dem", null));

        using (Assert.Multiple())
        {
            await Assert.That(store.DemoKeyOf("/n/x.dem")).IsEqualTo("sha-x");
            await Assert.That(store.DemoKeyOf("/M/X.dem")).IsEqualTo("sha-x");
            await Assert.That(store.DemoKeyOf("/m/y.dem")).IsEqualTo("/m/y.dem");
            await Assert.That(store.DemoKeyOf("/m/z.dem")).IsEqualTo("/m/z.dem");
            await Assert.That(store.SameDemo("/n/x.dem", "/m/x.dem")).IsTrue();
            await Assert.That(store.SameDemo("/m/y.dem", "/M/Y.dem")).IsTrue();
            await Assert.That(store.SameDemo("/m/x.dem", "/m/y.dem")).IsFalse();
        }

        store.Remove("/n/x.dem");
        await Assert.That(store.SameDemo("/n/x.dem", "/m/x.dem")).IsFalse().Because("a path no row lists is only itself");
    }
}

#region

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="PackDataRemover" />: path resolution refuses
///     anything outside the root, deletion never follows a reparse point or touches a <c>.dem</c> file,
///     and stripping a record touches only the pack's own facets.
/// </summary>
[NotInParallel]
public class PackDataRemoverTests
{
    private const string PackId = "net.demoviewer.pack.fake";
    private const string OtherPackId = "net.demoviewer.pack.other";

    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"dv-remove-{tag}-{Guid.NewGuid():N}");

    [Test]
    public async Task Inventory_ThenDelete_CountFilesAndBytes_UnderBothRoots()
    {
        string configRoot = TempRoot("config");
        string cacheRoot = TempRoot("cache");
        Directory.CreateDirectory(Path.Combine(configRoot, "strats", "me"));
        await File.WriteAllTextAsync(Path.Combine(configRoot, "strats", "index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(configRoot, "strats", "me", "book.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(configRoot, "teams.json"), "{}");
        Directory.CreateDirectory(Path.Combine(cacheRoot, "round-index"));
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "round-index", "abc.dvri.json"), "{}");
        try
        {
            StoreDescriptor strats = new("strats", "Strats", StoreRoot.Config, ["strats"], true);
            StoreDescriptor teams = new("teams", "Teams", StoreRoot.Config, ["teams.json"], true);
            StoreDescriptor roundIndex = new("round-index", "Round Index", StoreRoot.Cache, ["round-index"], false);
            PackDataRemover remover = new(new DemoCacheStore(null), configRoot, cacheRoot);

            ExtensionDataInventory before = await remover.InventoryAsync([strats, teams, roundIndex], "fake", "count");
            using (Assert.Multiple())
            {
                await Assert.That(before.Items.First(i => i.Descriptor.Id == "strats").FileCount).IsEqualTo(2);
                await Assert.That(before.Items.First(i => i.Descriptor.Id == "teams").FileCount).IsEqualTo(1);
                await Assert.That(before.Items.First(i => i.Descriptor.Id == "round-index").FileCount).IsEqualTo(1);
                await Assert.That(before.TotalBytes).IsGreaterThan(0);
                await Assert.That(before.UserWorkItems.Select(i => i.Descriptor.Id)).IsEquivalentTo(["strats", "teams"]);
            }

            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [strats, teams, roundIndex], [], "fake", "delete");

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue();
                await Assert.That(result.Removed.Items.Sum(i => i.FileCount)).IsEqualTo(4);
                await Assert.That(Directory.Exists(Path.Combine(configRoot, "strats"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(configRoot, "teams.json"))).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(cacheRoot, "round-index"))).IsFalse();
                await Assert.That(Directory.Exists(configRoot)).IsTrue().Because("only the store paths go, never the root itself");
            }
        }
        finally
        {
            Directory.Delete(configRoot, true);
            Directory.Delete(cacheRoot, true);
        }
    }

    [Test]
    [Arguments("..")]
    [Arguments("../outside.txt")]
    [Arguments("sub/../../outside.txt")]
    [Arguments(".")]
    public async Task Delete_RefusesAPathThatEscapesOrIsTheRootItself(string relative)
    {
        string root = TempRoot("escape");
        Directory.CreateDirectory(root);
        string outsideDir = Path.Combine(Path.GetDirectoryName(root)!, "dv-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        string outsideFile = Path.Combine(outsideDir, "precious.txt");
        await File.WriteAllTextAsync(outsideFile, "keep me");
        try
        {
            StoreDescriptor descriptor = new("escape", "Escape", StoreRoot.Config, [relative], false);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null);

            ExtensionDataInventory inventory = await remover.InventoryAsync([descriptor], "fake", "count");
            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

            using (Assert.Multiple())
            {
                await Assert.That(inventory.Items[0].FileCount).IsEqualTo(0);
                await Assert.That(result.Removed.Items[0].FileCount).IsEqualTo(0);
                await Assert.That(File.Exists(outsideFile)).IsTrue().Because($"'{relative}' must never resolve outside the root");
                await Assert.That(Directory.Exists(root)).IsTrue().Because("the root itself must survive even when a path tries to name it");
            }
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outsideDir, true);
        }
    }

    [Test]
    public async Task Delete_RefusesARootedPath()
    {
        string root = TempRoot("rooted");
        Directory.CreateDirectory(root);
        string outsideFile = Path.Combine(Path.GetTempPath(), "dv-outside-rooted-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "keep me");
        try
        {
            StoreDescriptor descriptor = new("rooted", "Rooted", StoreRoot.Config, [outsideFile], false);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null);

            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

            await Assert.That(result.Removed.Items[0].FileCount).IsEqualTo(0);
            await Assert.That(File.Exists(outsideFile)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, true);
            File.Delete(outsideFile);
        }
    }

    [Test]
    public async Task Inventory_ResolvesATrailingSlashAndForwardSlashSeparators_RegardlessOfPlatform()
    {
        string root = TempRoot("separators");
        Directory.CreateDirectory(Path.Combine(root, "nested", "inner"));
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "inner", "file.json"), "x");
        try
        {
            // A trailing slash on a directory entry, and a nested entry written with a forward slash
            // whatever the platform's own separator is: both must resolve under root, never be refused.
            StoreDescriptor trailing = new("trailing", "Trailing", StoreRoot.Config, ["nested/"], false);
            StoreDescriptor nested = new("nested", "Nested", StoreRoot.Config, ["nested/inner"], false);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null);

            ExtensionDataInventory inventory = await remover.InventoryAsync([trailing, nested], "fake", "count");

            using (Assert.Multiple())
            {
                await Assert.That(inventory.Items[0].FileCount).IsEqualTo(1).Because("a trailing slash must still resolve the directory");
                await Assert.That(inventory.Items[1].FileCount).IsEqualTo(1)
                    .Because("a forward-slash nested path must resolve regardless of the platform's own separator");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Delete_SuffixPattern_MatchesByOrdinalSuffix_NeverARecordSidecarOrTheIndex()
    {
        string cacheRoot = TempRoot("sidecars");
        string demos = Path.Combine(cacheRoot, "demos");
        Directory.CreateDirectory(demos);
        await File.WriteAllTextAsync(Path.Combine(demos, "abc.grenades.json.gz"), "g1");
        await File.WriteAllTextAsync(Path.Combine(demos, "abc.grenades.paths.json.gz"), "g2");
        await File.WriteAllTextAsync(Path.Combine(demos, "abc.json.gz"), "record sidecar");
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "index.json"), "index");
        try
        {
            StoreDescriptor descriptor = new("grenade-sidecars", "Grenade sidecars", StoreRoot.Cache,
                ["demos/*.grenades.json.gz", "demos/*.grenades.paths.json.gz"], false);
            PackDataRemover remover = new(new DemoCacheStore(null), null, cacheRoot);

            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

            using (Assert.Multiple())
            {
                await Assert.That(result.Removed.Items[0].FileCount).IsEqualTo(2);
                await Assert.That(File.Exists(Path.Combine(demos, "abc.grenades.json.gz"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(demos, "abc.grenades.paths.json.gz"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(demos, "abc.json.gz"))).IsTrue().Because("a core record sidecar must never match a pack's suffix");
                await Assert.That(File.Exists(Path.Combine(cacheRoot, "index.json"))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Test]
    public async Task Delete_NeverDeletesADemFile_EvenWhenADescriptorNamesOne()
    {
        string root = TempRoot("dem");
        Directory.CreateDirectory(root);
        string demFile = Path.Combine(root, "match.dem");
        await File.WriteAllTextAsync(demFile, "demo bytes");
        try
        {
            StoreDescriptor descriptor = new("dem", "Dem", StoreRoot.Config, ["match.dem"], false);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null);

            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

            await Assert.That(result.Removed.Items[0].FileCount).IsEqualTo(0);
            await Assert.That(File.Exists(demFile)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Delete_NeverFollowsAReparsePoint()
    {
        string root = TempRoot("symlink");
        string outsideDir = TempRoot("symlink-target");
        Directory.CreateDirectory(Path.Combine(root, "strats"));
        Directory.CreateDirectory(outsideDir);
        string preciousFile = Path.Combine(outsideDir, "precious.txt");
        await File.WriteAllTextAsync(preciousFile, "keep me");
        string link = Path.Combine(root, "strats", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outsideDir);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("symlink creation needs elevation on this Windows runner");
        }

        try
        {
            StoreDescriptor descriptor = new("strats", "Strats", StoreRoot.Config, ["strats"], true);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null);

            await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

            await Assert.That(File.Exists(preciousFile)).IsTrue().Because("deleting through the symlink must never reach its target");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outsideDir, true);
        }
    }

    [Test]
    public async Task Delete_StripsPackPayloadAndStamps_WithoutTouchingOtherPacksOrCoreFields()
    {
        string cacheRoot = TempRoot("strip");
        try
        {
            DemoCacheStore store = new(cacheRoot);
            DemoCacheRecord touched = ParsedRecord("/d/a.dem", map: "de_nuke");
            touched.Players.Add(new CachedPlayerInfo { Slot = 0, Name = "p1", SteamId64 = "1", Team = 2 });
            touched.Packs[PackId] = System.Text.Json.JsonSerializer.SerializeToElement("mine");
            touched.Packs[OtherPackId] = System.Text.Json.JsonSerializer.SerializeToElement("not mine");
            touched.SetStamp(new PackStamp("facet-a", 1, "fp-a"));
            touched.SetStamp(new PackStamp("facet-b", 1, "fp-b"));
            touched.SetStamp(new PackStamp("other-facet", 1, "fp-other"));
            store.Upsert(touched);

            DemoCacheRecord untouched = ParsedRecord("/d/b.dem", map: "de_dust2");
            untouched.Packs[OtherPackId] = System.Text.Json.JsonSerializer.SerializeToElement("also not mine");
            untouched.SetStamp(new PackStamp("other-facet", 1, "fp-other"));
            store.Upsert(untouched);
            store.SaveIndex();

            PackDataRemover remover = new(store, null, null);
            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [], ["facet-a", "facet-b"], "fake", "strip");

            await Assert.That(result.RecordsUpdated).IsEqualTo(1);

            DemoCacheStore reopened = new(cacheRoot);
            DemoCacheRecord a = reopened.TryLoadRecord("/d/a.dem")!;
            DemoCacheIndexEntry aEntry = reopened.TryGetIndex("/d/a.dem")!;
            DemoCacheRecord b = reopened.TryLoadRecord("/d/b.dem")!;

            using (Assert.Multiple())
            {
                await Assert.That(a.Packs.ContainsKey(PackId)).IsFalse();
                await Assert.That(a.Packs[OtherPackId].GetString()).IsEqualTo("not mine");
                await Assert.That(a.PackStamps.Select(s => s.Id)).IsEquivalentTo(["other-facet"]);
                await Assert.That(aEntry.PackStamps.Select(s => s.Id)).IsEquivalentTo(["other-facet"]);
                await Assert.That(a.Map).IsEqualTo("de_nuke");
                await Assert.That(a.Players.Count).IsEqualTo(1).Because("core fields must survive the strip");

                await Assert.That(b.Packs[OtherPackId].GetString()).IsEqualTo("also not mine");
                await Assert.That(b.PackStamps.Select(s => s.Id)).IsEquivalentTo(["other-facet"]);
            }
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Test]
    public async Task Delete_SkipsARowWhoseSidecarDoesNotLoad_RatherThanFabricatingOne()
    {
        string cacheRoot = TempRoot("missing-sidecar");
        try
        {
            DemoCacheStore seed = new(cacheRoot);
            DemoCacheRecord record = ParsedRecord("/d/missing.dem");
            record.SetStamp(new PackStamp("facet-a", 1, "fp-a"));
            seed.Upsert(record);
            seed.SaveIndex();

            string sidecar = seed.SidecarPathFor("/d/missing.dem")!;
            File.Delete(sidecar);
            await Assert.That(File.Exists(sidecar)).IsFalse();

            // A fresh store: seed's capacity-1 record cache would otherwise still answer the deleted file.
            DemoCacheStore store = new(cacheRoot);
            PackDataRemover remover = new(store, null, null);
            ExtensionDataRemovalResult result = await remover.DeleteAsync(PackId, [], ["facet-a"], "fake", "strip");

            using (Assert.Multiple())
            {
                await Assert.That(result.RecordsUpdated).IsEqualTo(0);
                await Assert.That(File.Exists(sidecar)).IsFalse().Because("stripping must never fabricate a sidecar for a row it could not load");
            }
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Test]
    public async Task DeleteAsync_RunsThroughTheQueue_NotDirectlyOnThePool()
    {
        string root = TempRoot("queue");
        Directory.CreateDirectory(Path.Combine(root, "strats"));
        try
        {
            using ManualResetEventSlim blockerStarted = new(false);
            using ManualResetEventSlim release = new(false);
            const string serial = "fake";
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a());
            queue.SubmitJob(new QueueJobRequest(QueueJobKind.SectionCompute, "blocker", "test", DemoJobPriority.UserRequested,
                _ =>
                {
                    blockerStarted.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return Task.CompletedTask;
                }, Serial: serial));
            await Task.Run(() => blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            StoreDescriptor descriptor = new("strats", "Strats", StoreRoot.Config, ["strats"], true);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null, queue);
            Task<ExtensionDataRemovalResult> delete = remover.DeleteAsync(PackId, [descriptor], [], serial, "delete");

            await Task.Delay(150);
            using (Assert.Multiple())
            {
                await Assert.That(delete.IsCompleted).IsFalse().Because("the delete shares a serial with the blocker, so it waits");
                await Assert.That(Directory.Exists(Path.Combine(root, "strats"))).IsTrue();
            }

            release.Set();
            ExtensionDataRemovalResult result = await delete;

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(root, "strats"))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The second of the blocker's two checks: a re-enable landing after the
    ///     delete job was already submitted, while it still sits behind another item on the same serial,
    ///     must still abort before touching a file. <c>stillOff</c> is flipped false while the job is held
    ///     behind a blocker, proving the predicate is read at job-start time, not capture time.
    /// </summary>
    [Test]
    public async Task DeleteAsync_StillOffFalseWhenTheJobActuallyRuns_AbortsWithoutTouchingFiles()
    {
        string root = TempRoot("stillOff");
        Directory.CreateDirectory(Path.Combine(root, "strats"));
        await File.WriteAllTextAsync(Path.Combine(root, "strats", "index.json"), "{}");
        try
        {
            using ManualResetEventSlim blockerStarted = new(false);
            using ManualResetEventSlim release = new(false);
            const string serial = "fake";
            DemoProcessingQueue queue = new(new HeavyJobGate(), a => a());
            queue.SubmitJob(new QueueJobRequest(QueueJobKind.SectionCompute, "blocker", "test", DemoJobPriority.UserRequested,
                _ =>
                {
                    blockerStarted.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return Task.CompletedTask;
                }, Serial: serial));
            await Task.Run(() => blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            bool stillOff = true;
            StoreDescriptor descriptor = new("strats", "Strats", StoreRoot.Config, ["strats"], true);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null, queue);
            Task<ExtensionDataRemovalResult> delete = remover.DeleteAsync(PackId, [descriptor], [], serial, "delete", stillOff: () => stillOff);

            await Task.Delay(150);
            stillOff = false; // the pack was re-enabled while the job still sat behind the blocker
            release.Set();
            ExtensionDataRemovalResult result = await delete;

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(root, "strats"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(root, "strats", "index.json"))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Delete_ALockedFile_IsSkippedNotThrown_AndCountedInTheResult()
    {
        string root = TempRoot("locked");
        string dir = Path.Combine(root, "strats");
        Directory.CreateDirectory(dir);
        string locked = Path.Combine(dir, "locked.json");
        await File.WriteAllTextAsync(locked, "{}");
        try
        {
            StoreDescriptor descriptor = new("strats", "Strats", StoreRoot.Config, ["strats"], true);
            PackDataRemover remover = new(new DemoCacheStore(null), root, null);

            ExtensionDataRemovalResult result = await DeleteWithTheFileUnremovable(remover, descriptor, dir, locked);

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue().Because("the rest of the store still goes");
                await Assert.That(result.Skipped).IsEqualTo(1);
                await Assert.That(result.FirstSkippedPath).IsEqualTo(locked);
                await Assert.That(File.Exists(locked)).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // An open handle does not stop File.Delete on Unix (unlink succeeds regardless; POSIX governs removal
    // through the CONTAINING directory's write permission, not the file's own or an open reader's).
    // Windows refuses a delete of a file open without FileShare.Delete; Unix refuses a delete inside a
    // directory whose own write bit is off. Either way the directory's permissions are restored before the
    // caller's own cleanup runs, win or lose.
    private static async Task<ExtensionDataRemovalResult> DeleteWithTheFileUnremovable(
        PackDataRemover remover, StoreDescriptor descriptor, string dir, string locked)
    {
        if (OperatingSystem.IsWindows())
        {
            await using FileStream open = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");
        }

        UnixFileMode original = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            return await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");
        }
        finally
        {
            File.SetUnixFileMode(dir, original);
        }
    }

    /// <summary>
    ///     A legacy-shape record (flat pack fields, folded into <see cref="DemoCacheRecord.Packs" /> and
    ///     <see cref="DemoCacheRecord.PackStamps" /> on read) must strip clean in the very read
    ///     that folds it: no stamp, no payload, and the fold must not resurrect the flat fields on the next
    ///     load (there is no sidecar write between the fold and the strip to re-derive from if it did).
    /// </summary>
    [Test]
    public async Task Delete_FoldsAndStripsAnOldShapeRecord_InTheSameRead()
    {
        string cacheRoot = TempRoot("legacy");
        try
        {
            RoundFactsRows rows = Facts(Round(1, 1000, 2000), Round(2, 3000, 4000));
            Directory.CreateDirectory(Path.Combine(cacheRoot, "demos"));
            string key = DemoCacheStore.StableKey("/d/legacy.dem");

            JsonObject record = new()
            {
                ["Path"] = "/d/legacy.dem",
                ["Size"] = 10,
                ["ModifiedTicks"] = 20,
                ["Parse"] = new JsonObject { ["Schema"] = 1, ["ComputedAtTicks"] = 5 },
                ["RoundFacts"] = JsonSerializer.SerializeToNode(rows),
                ["RoundFactsFingerprint"] = "rf-A",
                ["RoundIndex"] = new JsonObject { ["Schema"] = 1, ["ComputedAtTicks"] = 100 },
                ["RoundIndexState"] = 1,
                ["RoundIndexFingerprint"] = "fp",
                ["RoundIndexRowCount"] = 12,
                ["SuggestionsFingerprint"] = null,
                ["SuggestionCount"] = 0,
                ["Grenades"] = new JsonObject { ["Schema"] = 0, ["ComputedAtTicks"] = 0 },
                ["GrenadeState"] = 0,
                ["GrenadeCount"] = 0,
                ["GrenadeWalker"] = null,
                ["GrenadeInputCoverage"] = 0
            };
            await File.WriteAllBytesAsync(Path.Combine(cacheRoot, "demos", key + ".json.gz"),
                SidecarJson.Gzip(Encoding.UTF8.GetBytes(record.ToJsonString())));

            JsonObject indexRow = new()
            {
                ["Path"] = "/d/legacy.dem",
                ["Size"] = 10,
                ["ModifiedTicks"] = 20,
                ["ParseSchema"] = 1,
                ["RoundFactsSchema"] = rows.Schema,
                ["RoundFactsFingerprint"] = "rf-A",
                ["RoundIndexSchema"] = 1,
                ["RoundIndexComputedAtTicks"] = 100,
                ["RoundIndexState"] = 1,
                ["RoundIndexFingerprint"] = "fp",
                ["RoundIndexRowCount"] = 12
            };
            await File.WriteAllTextAsync(Path.Combine(cacheRoot, "index.json"),
                new JsonObject { ["Version"] = 2, ["LegacyMigrationVersion"] = 1, ["Entries"] = new JsonArray(indexRow) }.ToJsonString());

            DemoCacheStore store = new(cacheRoot);
            PackDataRemover remover = new(store, null, null);
            string[] facetIds =
            [
                RoundFactsEvaluator.EvaluatorId, RoundIndexEvaluator.EvaluatorId,
                SuggestedTagsService.EvaluatorId, GrenadeIndexEvaluator.EvaluatorId
            ];

            // The legacy fold always writes the payload under LegacyPackFields.PackId: the literal the old
            // Strat Book sidecars carried, not whatever pack id a caller happens to pass.
            ExtensionDataRemovalResult result = await remover.DeleteAsync(LegacyPackFields.PackId, [], facetIds, "fake", "strip");

            await Assert.That(result.RecordsUpdated).IsEqualTo(1).Because("the fold on read already promoted the flat fields to a payload and stamps");

            DemoCacheStore reopened = new(cacheRoot);
            DemoCacheRecord reloaded = reopened.TryLoadRecord("/d/legacy.dem")!;
            DemoCacheIndexEntry entry = reopened.TryGetIndex("/d/legacy.dem")!;

            using (Assert.Multiple())
            {
                await Assert.That(reloaded.PackStamps).IsEmpty();
                await Assert.That(reloaded.Packs).IsEmpty().Because("the fold's payload must not come back on the next load");
                await Assert.That(entry.PackStamps).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }
}

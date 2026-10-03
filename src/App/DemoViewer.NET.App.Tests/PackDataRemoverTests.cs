#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="PackDataRemover" /> (strat-book-plugin.md §7.4, §8, item 24): path resolution refuses
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

            PackDataInventory before = await remover.InventoryAsync([strats, teams, roundIndex], "fake", "count");
            using (Assert.Multiple())
            {
                await Assert.That(before.Items.First(i => i.Descriptor.Id == "strats").FileCount).IsEqualTo(2);
                await Assert.That(before.Items.First(i => i.Descriptor.Id == "teams").FileCount).IsEqualTo(1);
                await Assert.That(before.Items.First(i => i.Descriptor.Id == "round-index").FileCount).IsEqualTo(1);
                await Assert.That(before.TotalBytes).IsGreaterThan(0);
                await Assert.That(before.UserWorkItems.Select(i => i.Descriptor.Id)).IsEquivalentTo(["strats", "teams"]);
            }

            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [strats, teams, roundIndex], [], "fake", "delete");

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

            PackDataInventory inventory = await remover.InventoryAsync([descriptor], "fake", "count");
            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

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

            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

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

            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

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

            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [descriptor], [], "fake", "delete");

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
            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [], ["facet-a", "facet-b"], "fake", "strip");

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
            PackDataRemovalResult result = await remover.DeleteAsync(PackId, [], ["facet-a"], "fake", "strip");

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
            Task<PackDataRemovalResult> delete = remover.DeleteAsync(PackId, [descriptor], [], serial, "delete");

            await Task.Delay(150);
            using (Assert.Multiple())
            {
                await Assert.That(delete.IsCompleted).IsFalse().Because("the delete shares a serial with the blocker, so it waits");
                await Assert.That(Directory.Exists(Path.Combine(root, "strats"))).IsTrue();
            }

            release.Set();
            PackDataRemovalResult result = await delete;

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
}

#region

using System.Text;
using DemoViewer.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     A declared store opens as the folder its descriptor names and nothing wider: a one-folder store is that
///     folder, a one-file store admits that file alone, and a path that would leave either is refused. Writes
///     land whole, and the one in-place write, <see cref="IExtensionFolder.Append" />, adds a durable line. The
///     extension's own folders open the same way, with the host's per-demo data out of reach of the cache one
///     and whatever an older build left beside it moved down into it.
/// </summary>
public class ExtensionStoresTests
{
    private const string Id = "dev.example.stores";

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvextstores_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ExtensionStores Declared(string config, string cache, params StoreDescriptor[] stores) => new(Id, config, cache, stores);

    [Test]
    public async Task AFolderStore_OpensAtItsDeclaredPath_AndWritesLandUnderIt()
    {
        string root = NewRoot();
        try
        {
            ExtensionStores stores = Declared(root, Path.Combine(root, "cache"),
                new StoreDescriptor("notes", "Notes", StoreRoot.Config, [Id], IsUserWork: true));
            IExtensionFolder folder = stores.Open("notes")!;

            folder.WriteAtomic("today.json", Encoding.UTF8.GetBytes("one"));
            folder.WriteAtomic("today.json", Encoding.UTF8.GetBytes("two"));
            folder.WriteAtomic("deep/later.json", stream => stream.Write("three"u8));
            folder.Append("log.jsonl", "{\"a\":1}");
            folder.Append("log.jsonl", "{\"a\":2}");

            using (Assert.Multiple())
            {
                await Assert.That(folder.Path).IsEqualTo(Path.Combine(root, Id));
                await Assert.That(Encoding.UTF8.GetString(folder.Read("today.json")!)).IsEqualTo("two");
                await Assert.That(Encoding.UTF8.GetString(folder.Read("deep/later.json")!)).IsEqualTo("three");
                await Assert.That(Encoding.UTF8.GetString(folder.Read("log.jsonl")!)).IsEqualTo("{\"a\":1}\n{\"a\":2}\n");
                await Assert.That(folder.Read("missing.json")).IsNull();
                await Assert.That(folder.FileExists("today.json")).IsTrue();
                await Assert.That(folder.FileExists("missing.json")).IsFalse();
                await Assert.That(folder.LastWriteTimeUtc("today.json")).IsNotNull();
                await Assert.That(folder.LastWriteTimeUtc("missing.json")).IsNull();
                await Assert.That(Directory.GetFiles(Path.Combine(root, Id)).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal))
                    .IsEquivalentTo(["log.jsonl", "today.json"], CollectionOrdering.Matching)
                    .Because("the temporary file was moved over the target, never left beside it");
                await Assert.That(folder.EnumerateFiles("", "*.json").Order(StringComparer.Ordinal)).IsEquivalentTo(["today.json"], CollectionOrdering.Matching);
                await Assert.That(folder.EnumerateFiles("deep", "*").Order(StringComparer.Ordinal))
                    .IsEquivalentTo([Path.Combine("deep", "later.json")], CollectionOrdering.Matching);
                await Assert.That(folder.EnumerateDirectories("")).IsEquivalentTo(["deep"], CollectionOrdering.Matching);
                await Assert.That(folder.EnumerateFiles("nowhere", "*")).IsEmpty().Because("a missing folder lists nothing");
            }

            using (Stream stream = folder.OpenRead("today.json")!)
            using (StreamReader reader = new(stream))
            {
                await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("two");
            }

            folder.Move("today.json", "kept/yesterday.json", overwrite: false);
            folder.Delete("deep/later.json");
            folder.Delete("deep/later.json");
            using (Assert.Multiple())
            {
                await Assert.That(folder.FileExists("today.json")).IsFalse();
                await Assert.That(Encoding.UTF8.GetString(folder.Read("kept/yesterday.json")!)).IsEqualTo("two");
                await Assert.That(folder.FileExists("deep/later.json")).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AOneFileStore_OpensAsThatFile_AndReachesNothingBesideIt()
    {
        string root = NewRoot();
        try
        {
            ExtensionStores stores = Declared(root, root,
                new StoreDescriptor("teams", "Teams", StoreRoot.Config, [Id + ".json"], IsUserWork: true));
            IExtensionFile file = stores.OpenFile("teams")!;
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{}");

            bool before = file.Exists;
            file.WriteAtomic("{}"u8);
            file.WriteAtomic(stream => stream.Write("{\"v\":2}"u8));

            using (Assert.Multiple())
            {
                await Assert.That(file.Path).IsEqualTo(Path.Combine(root, Id + ".json"));
                await Assert.That(before).IsFalse();
                await Assert.That(file.Exists).IsTrue();
                await Assert.That(file.LastWriteTimeUtc).IsNotNull();
                await Assert.That(file.Read()).IsEquivalentTo("{\"v\":2}"u8.ToArray(), CollectionOrdering.Matching);
                await Assert.That(Directory.GetFiles(root).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal))
                    .IsEquivalentTo([Id + ".json", "settings.json"], CollectionOrdering.Matching)
                    .Because("the temporary file was moved over the target, never left beside it");
            }

            using (Stream stream = file.OpenRead()!)
            using (StreamReader reader = new(stream))
            {
                await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("{\"v\":2}");
            }

            file.Delete();
            file.Delete();
            using (Assert.Multiple())
            {
                await Assert.That(file.Exists).IsFalse();
                await Assert.That(file.Read()).IsNull();
                await Assert.That(file.OpenRead()).IsNull();
                await Assert.That(File.Exists(Path.Combine(root, "settings.json"))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheExtensionSaysWhichKindItDeclared_AndTheDiskOnlyContradictsIt()
    {
        string root = NewRoot();
        try
        {
            ExtensionStores stores = Declared(root, root,
                new StoreDescriptor("file", "File", StoreRoot.Config, [Id + ".json"], IsUserWork: true),
                new StoreDescriptor("folder", "Folder", StoreRoot.Cache, [Id], IsUserWork: false));

            using (Assert.Multiple())
            {
                await Assert.That(stores.OpenFile("file")!.Path).IsEqualTo(Path.Combine(root, Id + ".json"));
                await Assert.That(stores.Open("folder")!.Path).IsEqualTo(Path.Combine(root, Id));
                await Assert.That(stores.Open("file")!.Path).IsEqualTo(Path.Combine(root, Id + ".json"))
                    .Because("nothing is on disk yet, so the extension's word stands");
            }

            stores.Open("folder")!.Create();
            stores.OpenFile("file")!.WriteAtomic("{}"u8);
            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(Path.Combine(root, Id))).IsTrue();
                await Assert.That(() => stores.OpenFile("folder")).Throws<InvalidOperationException>();
                await Assert.That(() => stores.Open("file")).Throws<InvalidOperationException>();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [Arguments("../escape.json")]
    [Arguments("sub/../../escape.json")]
    [Arguments("")]
    [Arguments(".")]
    public async Task APathThatLeavesTheFolder_IsRefused(string path)
    {
        string root = NewRoot();
        try
        {
            IExtensionFolder folder = Declared(root, root, new StoreDescriptor("s", "S", StoreRoot.Config, [Id], IsUserWork: true)).Open("s")!;

            using (Assert.Multiple())
            {
                await Assert.That(() => folder.WriteAtomic(path, new byte[] { 1 })).Throws<ArgumentException>();
                await Assert.That(() => folder.Read(path)).Throws<ArgumentException>();
                await Assert.That(() => folder.Append(path, "x")).Throws<ArgumentException>();
                await Assert.That(() => folder.Move("a.json", path, true)).Throws<ArgumentException>();
                await Assert.That(() => folder.Delete(path)).Throws<ArgumentException>();
            }

            await Assert.That(File.Exists(Path.Combine(root, "escape.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(root, Id, "escape.json"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AStoreNotDeclared_OrDeclaringSeveralPaths_DoesNotOpen()
    {
        string root = NewRoot();
        try
        {
            ExtensionStores stores = Declared(root, root,
                new StoreDescriptor("many", "Many", StoreRoot.Cache, ["demos/*" + Id + ".json", Id], IsUserWork: false),
                new StoreDescriptor("pattern", "Pattern", StoreRoot.Cache, ["demos/*" + Id + ".json"], IsUserWork: false));

            using (Assert.Multiple())
            {
                await Assert.That(() => stores.Open("unknown")).Throws<ArgumentException>();
                await Assert.That(() => stores.Open("many")).Throws<ArgumentException>();
                await Assert.That(() => stores.Open("pattern")).Throws<ArgumentException>();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task WithoutAFilesystem_EveryStoreOpensAsNull()
    {
        ExtensionStores stores = new(Id, null, null, [new StoreDescriptor("s", "S", StoreRoot.Config, [Id], IsUserWork: true)]);

        using (Assert.Multiple())
        {
            await Assert.That(stores.Open("s")).IsNull();
            await Assert.That(stores.Own(StoreRoot.Config)).IsNull();
            await Assert.That(stores.Own(StoreRoot.Cache)).IsNull();
        }
    }

    [Test]
    public async Task TheExtensionsOwnFolders_AreTheConfigFolderItself_AndTheCacheFoldersFilesSubfolder()
    {
        string root = NewRoot();
        try
        {
            ExtensionStores stores = Declared(root, Path.Combine(root, "cache"));

            IExtensionFolder config = stores.Own(StoreRoot.Config)!;
            IExtensionFolder cache = stores.Own(StoreRoot.Cache)!;
            config.WriteAtomic("notes/today.json", "one"u8);
            cache.WriteAtomic("index.bin", new byte[] { 1, 2, 3 });

            using (Assert.Multiple())
            {
                await Assert.That(config.Path).IsEqualTo(Path.Combine(root, ExtensionFolders.DataDirectoryName, Id));
                await Assert.That(cache.Path).IsEqualTo(Path.Combine(root, "cache", ExtensionFolders.DataDirectoryName, Id,
                    ExtensionFolders.OwnCacheDirectoryName));
                await Assert.That(Encoding.UTF8.GetString(config.Read("notes/today.json")!)).IsEqualTo("one");
                await Assert.That(cache.Read("index.bin")).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
                await Assert.That(config.Read("missing.json")).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ThePerDemoDataTheHostKeeps_IsNotReachableFromTheOwnCacheFolder()
    {
        string root = NewRoot();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            ExtensionStores stores = Declared(root, cacheRoot);
            string hostData = Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName, Id, ExtensionDemoDataStore.DirectoryName);
            Directory.CreateDirectory(hostData);
            await File.WriteAllTextAsync(Path.Combine(hostData, "index.json.gz"), "host");

            IExtensionFolder cache = stores.Own(StoreRoot.Cache)!;
            IExtensionFolder config = stores.Own(StoreRoot.Config)!;
            cache.WriteAtomic("demo-data/index.json.gz", "mine"u8);
            config.WriteAtomic("demo-data/mine.json", "mine"u8);

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(hostData, "index.json.gz"))).IsEqualTo("host")
                    .Because("the extension's own demo-data is a folder of its own, under files");
                await Assert.That(Encoding.UTF8.GetString(cache.Read("demo-data/index.json.gz")!)).IsEqualTo("mine");
                await Assert.That(() => cache.Read("../" + ExtensionDemoDataStore.DirectoryName + "/index.json.gz")).Throws<ArgumentException>();
                await Assert.That(() => cache.Read(Path.Combine(hostData, "index.json.gz"))).Throws<ArgumentException>()
                    .Because("a rooted path is refused whether or not it names something real");
                await Assert.That(Encoding.UTF8.GetString(config.Read("demo-data/mine.json")!)).IsEqualTo("mine")
                    .Because("the config folder has no host-owned part");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The two members that let a store stay on the folder instead of forking to a string root:
    ///     DirectoryExists, the companion to FileExists for a store with subfolders of its own, and
    ///     SetLastWriteTimeUtc, which is how a cache that evicts by recency says keep this one.
    /// </summary>
    [Test]
    public async Task AFolder_AnswersForASubfolder_AndTouchesAFilesWriteTime()
    {
        string root = NewRoot();
        try
        {
            IExtensionFolder folder = Declared(root, root).Own(StoreRoot.Config)!;
            folder.Create();
            folder.WriteAtomic("clips/one.gif", new byte[] { 1 });

            using (Assert.Multiple())
            {
                await Assert.That(folder.DirectoryExists("clips")).IsTrue();
                await Assert.That(folder.DirectoryExists("nope")).IsFalse();
                await Assert.That(folder.DirectoryExists("")).IsTrue().Because("an empty path is the folder itself");
                await Assert.That(folder.FileExists("clips/one.gif")).IsTrue();
                await Assert.That(folder.DirectoryExists("clips/one.gif")).IsFalse().Because("a file is not a folder");
            }

            DateTime touched = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            folder.SetLastWriteTimeUtc("clips/one.gif", touched);
            await Assert.That(folder.LastWriteTimeUtc("clips/one.gif")).IsEqualTo(touched)
                .Because("the write side and the read side are the same clock");

            folder.SetLastWriteTimeUtc("clips/missing.gif", touched);
            await Assert.That(folder.FileExists("clips/missing.gif")).IsFalse()
                .Because("touching a path with no file leaves it alone rather than creating one");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task WhatAnOlderBuildLeftInTheCacheFolder_MovesIntoFiles_AndNeverOverTheNewerCopy()
    {
        string root = NewRoot();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            string own = Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName, Id);
            string files = Path.Combine(own, ExtensionFolders.OwnCacheDirectoryName);
            Directory.CreateDirectory(Path.Combine(own, "strat-mining"));
            Directory.CreateDirectory(Path.Combine(own, ExtensionDemoDataStore.DirectoryName));
            Directory.CreateDirectory(files);
            await File.WriteAllTextAsync(Path.Combine(own, "grenade-lineups.json.gz"), "lineups");
            await File.WriteAllTextAsync(Path.Combine(own, "strat-mining", "detected.json"), "detected");
            await File.WriteAllTextAsync(Path.Combine(own, ExtensionDemoDataStore.DirectoryName, "index.json.gz"), "host");
            await File.WriteAllTextAsync(Path.Combine(own, "team-index.json"), "stale");
            await File.WriteAllTextAsync(Path.Combine(files, "team-index.json"), "current");

            IExtensionFolder cache = Declared(root, cacheRoot).Own(StoreRoot.Cache)!;

            using (Assert.Multiple())
            {
                await Assert.That(Encoding.UTF8.GetString(cache.Read("grenade-lineups.json.gz")!)).IsEqualTo("lineups")
                    .Because("the file holding hand-tuned state moves rather than being left behind and re-minted");
                await Assert.That(Encoding.UTF8.GetString(cache.Read("strat-mining/detected.json")!)).IsEqualTo("detected")
                    .Because("a folder moves whole, not just the files beside it");
                await Assert.That(Encoding.UTF8.GetString(cache.Read("team-index.json")!)).IsEqualTo("current")
                    .Because("a target already there is the newer copy");
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(own, "team-index.json"))).IsEqualTo("stale")
                    .Because("the one entry that could not move stays where it was rather than being dropped");
                await Assert.That(File.Exists(Path.Combine(own, "grenade-lineups.json.gz"))).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(own, "strat-mining"))).IsFalse();
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(own, ExtensionDemoDataStore.DirectoryName, "index.json.gz")))
                    .IsEqualTo("host").Because("the per-demo data is the host's and stays beside files");
            }

            // A later start sweeps again and finds nothing left to do: the move is idempotent, so a crash
            // part-way through one run cannot strand an entry.
            IExtensionFolder second = Declared(root, cacheRoot).Own(StoreRoot.Cache)!;
            using (Assert.Multiple())
            {
                await Assert.That(Encoding.UTF8.GetString(second.Read("team-index.json")!)).IsEqualTo("current");
                await Assert.That(Directory.EnumerateFileSystemEntries(own).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal))
                    .IsEquivalentTo([ExtensionDemoDataStore.DirectoryName, ExtensionFolders.OwnCacheDirectoryName, "team-index.json"],
                        CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task WithNothingToMove_TheOwnCacheFolder_IsNotCreatedUntilSomethingIsWritten()
    {
        string root = NewRoot();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            IExtensionFolder cache = Declared(root, cacheRoot).Own(StoreRoot.Cache)!;

            using (Assert.Multiple())
            {
                await Assert.That(cache.Read("anything.json")).IsNull();
                await Assert.That(Directory.Exists(cacheRoot)).IsFalse().Because("a read of an empty store leaves no folders behind");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AnAppendedLine_IsOnTheDisk_AndATornLastLineIsNotJoinedToIt()
    {
        string root = NewRoot();
        try
        {
            IExtensionFolder folder = Declared(root, root).Own(StoreRoot.Config)!;
            folder.Append("fresh.jsonl", "{\"a\":1}");
            folder.Append("fresh.jsonl", "{\"a\":2}");
            folder.WriteAtomic("torn.jsonl", "{\"a\":1}\n{\"a\":2"u8);
            folder.Append("torn.jsonl", "{\"a\":3}");

            using (Assert.Multiple())
            {
                await Assert.That(Encoding.UTF8.GetString(folder.Read("fresh.jsonl")!)).IsEqualTo("{\"a\":1}\n{\"a\":2}\n");
                await Assert.That(Encoding.UTF8.GetString(folder.Read("torn.jsonl")!)).IsEqualTo("{\"a\":1}\n{\"a\":2\n{\"a\":3}\n")
                    .Because("the torn record keeps a line of its own, so a reader skips it instead of this one");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ARefusedStore_IsDroppedFromTheContext_AndReported()
    {
        StoreDescriptor own = new("own", "Own", StoreRoot.Config, [Id], IsUserWork: true);
        StoreDescriptor settings = new("settings", "Settings", StoreRoot.Config, ["settings.json"], IsUserWork: true);
        Pack pack = new(Id, [own, settings]);
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        await using ServiceProvider sp = new ServiceCollection().AddSingleton<ExtensionShellHub>().BuildServiceProvider();

        IReadOnlyList<StoreDescriptor> accepted = PackContributions.AcceptedStores(pack, guard);
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp), guard: guard);

        using (Assert.Multiple())
        {
            await Assert.That(accepted).IsEquivalentTo([own], CollectionOrdering.Matching);
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
            await Assert.That(contributions.Stores).IsEquivalentTo([own], CollectionOrdering.Matching).Because("the contributions read the context's list");
        }
    }

    private sealed class Pack(string id, IEnumerable<StoreDescriptor> stores) : IExtension
    {
        public string Id => id;
        public string FeatureId => "pack." + id;
        public IEnumerable<ExtensionFeature> Features => [];
        public IEnumerable<StoreDescriptor> Stores => stores;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

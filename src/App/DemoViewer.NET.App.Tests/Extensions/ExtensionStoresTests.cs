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
///     land whole, and the one in-place write, <see cref="IExtensionFolder.Append" />, adds a line.
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

        await Assert.That(stores.Open("s")).IsNull();
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

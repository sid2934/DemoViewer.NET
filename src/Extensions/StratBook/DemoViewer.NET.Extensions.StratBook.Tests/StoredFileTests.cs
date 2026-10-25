#region

using System.Text;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

public class StoredFileTests
{
    [Test]
    public async Task AFileInTheExtensionsFolder_IsCopiedOnce_FromWhereAnOlderBuildKeptIt()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-stored-file-{Guid.NewGuid():N}");
        try
        {
            ExtensionContext.StorageView storage = new("dev.example.stored", Path.Combine(root, "config"), Path.Combine(root, "cache"));
            int legacyReads = 0;
            byte[] legacy = Encoding.UTF8.GetBytes("{\"Dismissed\":[\"k\"]}");
            StoredFile file = StoredFile.In(storage, StoreRoot.Config, StratMiningFiles.StatePath, () =>
            {
                legacyReads++;
                return legacy;
            });

            byte[]? first = file.Read();
            byte[]? copied = await storage.ReadAsync(StoreRoot.Config, StratMiningFiles.StatePath);
            file.Write(Encoding.UTF8.GetBytes("{}"));
            byte[]? afterWrite = file.Read();
            using (Assert.Multiple())
            {
                await Assert.That(first).IsEquivalentTo(legacy, CollectionOrdering.Matching);
                await Assert.That(copied).IsEquivalentTo(legacy, CollectionOrdering.Matching).Because("the copy lands in the extension's own folder");
                await Assert.That(Encoding.UTF8.GetString(afterWrite!)).IsEqualTo("{}")
                    .Because("once copied, the extension's file is the store, not the old one");
                await Assert.That(legacyReads).IsEqualTo(1);
                await Assert.That(file.Name).IsEqualTo("strat-mining.json");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task AFileInADeclaredStore_ReadsAndWritesThroughTheStoresFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-stored-file-{Guid.NewGuid():N}");
        try
        {
            StoredFile file = StoredFile.In(new ExtensionFile(Path.Combine(root, "teams.json")))!;

            byte[]? before = file.Read();
            file.Write(Encoding.UTF8.GetBytes("{}"));
            using (Assert.Multiple())
            {
                await Assert.That(before).IsNull();
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "teams.json"))).IsEqualTo("{}");
                await Assert.That(Encoding.UTF8.GetString(file.Read()!)).IsEqualTo("{}");
                await Assert.That(file.Name).IsEqualTo("teams.json");
                await Assert.That(StoredFile.In(null)).IsNull().Because("no store (the browser) is a session-only file");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task NoFileAndNothingToCopy_ReadsAsNull_AndWritesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-stored-file-{Guid.NewGuid():N}");
        try
        {
            ExtensionContext.StorageView storage = new("dev.example.stored", Path.Combine(root, "config"), Path.Combine(root, "cache"));
            StoredFile file = StoredFile.In(storage, StoreRoot.Cache, StratMiningFiles.DetectedPath, () => null);
            await Assert.That(file.Read()).IsNull();
            await Assert.That(Directory.Exists(root)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}

#region

using System.Text;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;

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
                await Assert.That(first).IsEquivalentTo(legacy);
                await Assert.That(copied).IsEquivalentTo(legacy).Because("the copy lands in the extension's own folder");
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

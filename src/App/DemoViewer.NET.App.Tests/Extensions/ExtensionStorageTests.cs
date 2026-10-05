#region

using System.Text;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The extension's atomic write and read: a file lands whole under the extension's own folder, a rewrite
///     replaces it without leaving a temporary file, and a path that would leave the folder is refused.
/// </summary>
public class ExtensionStorageTests
{
    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvextstore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Test]
    public async Task AWrite_LandsUnderTheExtensionsFolder_AndARewriteReplacesItWhole()
    {
        string root = NewRoot();
        try
        {
            ExtensionContext.StorageView storage = new("dev.example.store", root, Path.Combine(root, "cache"));

            await Assert.That(await storage.WriteAtomicAsync(StoreRoot.Config, "notes/today.json", Encoding.UTF8.GetBytes("one")))
                .IsTrue();
            await storage.WriteAtomicAsync(StoreRoot.Config, "notes/today.json", Encoding.UTF8.GetBytes("two"));
            await storage.WriteAtomicAsync(StoreRoot.Cache, "index.bin", new byte[] { 1, 2, 3 });

            string folder = Path.Combine(root, ExtensionFolders.DataDirectoryName, "dev.example.store", "notes");
            using (Assert.Multiple())
            {
                await Assert.That(Encoding.UTF8.GetString((await storage.ReadAsync(StoreRoot.Config, "notes/today.json"))!))
                    .IsEqualTo("two");
                await Assert.That(Directory.GetFiles(folder)).IsEquivalentTo([Path.Combine(folder, "today.json")])
                    .Because("the temporary file was moved over the target, never left beside it");
                await Assert.That(await storage.ReadAsync(StoreRoot.Cache, "index.bin")).IsEquivalentTo(new byte[] { 1, 2, 3 });
                await Assert.That(await storage.ReadAsync(StoreRoot.Config, "missing.json")).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [Arguments("../escape.json")]
    [Arguments("notes/../../escape.json")]
    [Arguments("")]
    [Arguments(".")]
    public async Task APathThatLeavesTheFolder_IsRefused(string path)
    {
        string root = NewRoot();
        try
        {
            ExtensionContext.StorageView storage = new("dev.example.store", root, root);

            await Assert.ThrowsAsync<ArgumentException>(async () => await storage.WriteAtomicAsync(StoreRoot.Config, path, new byte[] { 1 }));
            await Assert.ThrowsAsync<ArgumentException>(async () => await storage.ReadAsync(StoreRoot.Config, path));
            await Assert.That(File.Exists(Path.Combine(root, ExtensionFolders.DataDirectoryName, "escape.json"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ARootedPath_IsRefused()
    {
        string root = NewRoot();
        try
        {
            ExtensionContext.StorageView storage = new("dev.example.store", root, root);
            string rooted = Path.Combine(root, "outside.json");

            await Assert.ThrowsAsync<ArgumentException>(async () => await storage.WriteAtomicAsync(StoreRoot.Config, rooted, new byte[] { 1 }));
            await Assert.That(File.Exists(rooted)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

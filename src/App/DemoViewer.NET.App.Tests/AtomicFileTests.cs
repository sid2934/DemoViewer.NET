#region

using System.Text;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one whole-file write: a missing folder is created, a rewrite replaces the file, and a write that
///     throws halfway leaves the previous file and no temporary file beside it.
/// </summary>
public class AtomicFileTests
{
    [Test]
    public async Task AWrite_CreatesTheFolder_AndARewriteReplacesTheFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "a", "b", "file.json");
            AtomicFile.WriteAllText(path, "one");
            AtomicFile.WriteAllText(path, "two");

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("two");
                await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!)).IsEquivalentTo([path]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AWriteThatThrowsHalfway_LeavesThePreviousFile_AndNoTemporaryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "file.bin");
            AtomicFile.WriteAllBytes(path, [1, 2, 3]);

            await Assert.ThrowsAsync<IOException>(() =>
            {
                AtomicFile.Write(path, stream =>
                {
                    stream.Write(Encoding.UTF8.GetBytes("half"));
                    throw new IOException("disk gone");
                });
                return Task.CompletedTask;
            });

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 1, 2, 3 });
                await Assert.That(Directory.GetFiles(root)).IsEquivalentTo([path]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ACancelledAsyncWrite_LeavesThePreviousFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "file.bin");
            AtomicFile.WriteAllBytes(path, [7]);
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                AtomicFile.WriteAllBytesAsync(path, new byte[] { 8, 9 }, cancelled.Token));

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 7 });
                await Assert.That(Directory.GetFiles(root)).IsEquivalentTo([path]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services.DemoProcessing;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     How the queue reads a demo it parses: a settled local file is mapped and hashed only when asked, a file
///     that may still be written is read into memory and never hashed, and a read that stops delivering bytes
///     gives up rather than holding the heavy slot.
/// </summary>
public class DemoFileReadTests
{
    private static string TempFile(byte[] bytes, TimeSpan age)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-file-read-{Guid.NewGuid():N}.dem");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Test]
    public async Task ReadAll_ReturnsTheWholeFile()
    {
        byte[] bytes = [.. Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7))];
        string path = TempFile(bytes, TimeSpan.Zero);
        try
        {
            await Assert.That(DemoFileRead.ReadAll(path, TimeSpan.FromSeconds(10), CancellationToken.None))
                .IsEquivalentTo(bytes, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A FIFO with no writer never answers the open: what a dead network mount does to a read.
    [Test]
    public async Task ReadAll_GivesUp_WhenTheReadDeliversNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("needs a FIFO");
        }

        string fifo = Path.Combine(Path.GetTempPath(), $"dv-file-read-{Guid.NewGuid():N}.fifo");
        using (Process mk = Process.Start("mkfifo", fifo))
        {
            await mk.WaitForExitAsync();
        }

        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            Assert.Throws<IOException>(() => DemoFileRead.ReadAll(fifo, TimeSpan.FromMilliseconds(300), CancellationToken.None));
            await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // Lets the abandoned open return.
            using (new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
            }

            File.Delete(fifo);
        }
    }

    [Test]
    public async Task ASettledLocalFile_IsMapped_AndHashedOnlyWhenAsked()
    {
        byte[] bytes = [.. Enumerable.Range(0, 10_000).Select(i => (byte)i)];
        string path = TempFile(bytes, TimeSpan.FromHours(1));
        try
        {
            List<DemoContentRead> reads = [];
            byte[]? wanted = new DemoFileRead(TimeProvider.System, _ => true, reads.Add).Prepare(path, CancellationToken.None);
            byte[]? unwanted = new DemoFileRead(TimeProvider.System, _ => false, reads.Add).Prepare(path, CancellationToken.None);

            using (Assert.Multiple())
            {
                await Assert.That(wanted).IsNull().Because("a settled local file is mapped");
                await Assert.That(unwanted).IsNull();
                await Assert.That(reads).HasCount(1);
                await Assert.That(reads[0].Sha256).IsEqualTo(DemoContentHash.Compute(bytes));
                await Assert.That(reads[0].Stat.Length).IsEqualTo(bytes.LongLength);
                await Assert.That(reads[0].Fingerprint?.Size).IsEqualTo(bytes.LongLength);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AFileThatMayStillBeWritten_IsReadIntoMemory_AndNotHashed()
    {
        byte[] bytes = [.. Enumerable.Range(0, 10_000).Select(i => (byte)(i * 3))];
        string path = TempFile(bytes, TimeSpan.Zero);
        try
        {
            List<DemoContentRead> reads = [];
            byte[]? read = new DemoFileRead(TimeProvider.System, _ => true, reads.Add).Prepare(path, CancellationToken.None);

            using (Assert.Multiple())
            {
                await Assert.That(read).IsEquivalentTo(bytes, TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(reads).IsEmpty();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

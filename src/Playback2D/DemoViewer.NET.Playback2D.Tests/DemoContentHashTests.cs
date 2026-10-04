#region

using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The shared demo content key. Every <c>.dvann.json</c> and <c>GraphBreakpoints.v2.json</c> already on
///     disk was written by one of three private copies of the same digest, so the helper they now forward
///     to must produce the same string for the same bytes, whether it streams a file or reads a span.
/// </summary>
public class DemoContentHashTests
{
    private static string Reference(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Test]
    public async Task FileAndSpan_AgreeWithTheReferenceDigest_OnACommittedFixture()
    {
        string path = Path.Combine(FixtureCorpus.Root, "manifest.json");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        string expected = Reference(bytes);

        using (Assert.Multiple())
        {
            await Assert.That(DemoContentHash.Compute(path)).IsEqualTo(expected);
            await Assert.That(DemoContentHash.Compute(bytes)).IsEqualTo(expected);
            await Assert.That(DemoContentHash.TryCompute(path)).IsEqualTo(expected);
            await Assert.That(AnnotationStore.ComputeDemoKey(path)).IsEqualTo(expected)
                .Because("the annotation sidecar's demo.sha256 must keep matching files already on disk");
            await Assert.That(expected.Length).IsEqualTo(64);
            await Assert.That(expected.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')).IsTrue()
                .Because("the key is lowercase hex everywhere it is written");
        }
    }

    /// <summary>
    ///     A demo is far larger than one read, so the file overload must hash across buffer boundaries.
    ///     Deterministic bytes, so a failure reproduces.
    /// </summary>
    [Test]
    public async Task File_StreamsAcrossBufferBoundaries()
    {
        string path = Path.Combine(Path.GetTempPath(), "dvhash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            byte[] bytes = new byte[3 * 1024 * 1024 + 17];
            new Random(12345).NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);

            string expected = Reference(bytes);
            using (Assert.Multiple())
            {
                await Assert.That(DemoContentHash.Compute(path)).IsEqualTo(expected);
                await Assert.That(DemoContentHash.Compute(bytes)).IsEqualTo(expected);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AMissingFile_IsNullForTry_AndThrowsForCompute()
    {
        string missing = Path.Combine(Path.GetTempPath(), "dvhash-missing-" + Guid.NewGuid().ToString("N") + ".dem");

        await Assert.That(DemoContentHash.TryCompute(missing)).IsNull();
        await Assert.That(AnnotationStore.ComputeDemoKey(missing)).IsEqualTo("")
            .Because("the annotation store's callers already read an empty key as unknown");

        FileNotFoundException? thrown = await Assert.ThrowsAsync<FileNotFoundException>(() =>
        {
            DemoContentHash.Compute(missing);
            return Task.CompletedTask;
        });
        await Assert.That(thrown).IsNotNull();
    }
}

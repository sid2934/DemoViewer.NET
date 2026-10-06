#region

using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The cheap candidate identity: size plus the hashes of the first and last window. It must read only
///     the two windows, agree with the full content hash where the windows cover the whole file, and refuse
///     a file that may still be written.
/// </summary>
public class DemoContentFingerprintTests
{
    private const int Window = 100_000;

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string TempFile(byte[] bytes, TimeSpan age)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-fingerprint-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(10);

    [Test]
    public async Task ALargeFile_ReadsExactlyTwoWindows_AndHashesEachEnd()
    {
        byte[] bytes = Bytes(1_000_003, 1);
        CountingStream stream = new(new MemoryStream(bytes));

        DemoContentFingerprint fp = DemoContentFingerprint.Compute(stream, Window);

        using (Assert.Multiple())
        {
            await Assert.That(stream.BytesRead).IsEqualTo(2L * Window)
                .Because("the bytes between the windows are what a network read must not pay for");
            await Assert.That(fp.Size).IsEqualTo(bytes.Length);
            await Assert.That(fp.WindowBytes).IsEqualTo(Window);
            await Assert.That(fp.Head).IsEqualTo(Sha(bytes.AsSpan(0, Window)));
            await Assert.That(fp.Tail).IsEqualTo(Sha(bytes.AsSpan(bytes.Length - Window)));
        }
    }

    [Test]
    public async Task TheDefaultWindow_ReadsOnlyTwoWindows()
    {
        byte[] bytes = Bytes(2 * DemoContentFingerprint.DefaultWindowBytes + 4_321, 2);
        CountingStream stream = new(new MemoryStream(bytes));

        DemoContentFingerprint fp = DemoContentFingerprint.Compute(stream, DemoContentFingerprint.DefaultWindowBytes);

        using (Assert.Multiple())
        {
            await Assert.That(stream.BytesRead).IsEqualTo(2L * DemoContentFingerprint.DefaultWindowBytes);
            await Assert.That(fp.Tail).IsEqualTo(Sha(bytes.AsSpan(bytes.Length - DemoContentFingerprint.DefaultWindowBytes)));
        }
    }

    [Test]
    public async Task OverlappingWindows_ReadTheFileOnce_AndBothEndsShareTheMiddle()
    {
        byte[] bytes = Bytes(Window + Window / 2, 3);
        CountingStream stream = new(new MemoryStream(bytes));

        DemoContentFingerprint fp = DemoContentFingerprint.Compute(stream, Window);

        using (Assert.Multiple())
        {
            await Assert.That(stream.BytesRead).IsEqualTo(bytes.Length);
            await Assert.That(fp.Head).IsEqualTo(Sha(bytes.AsSpan(0, Window)));
            await Assert.That(fp.Tail).IsEqualTo(Sha(bytes.AsSpan(bytes.Length - Window)));
        }
    }

    [Test]
    public async Task AFileNoLargerThanOneWindow_HasBothEndsEqualToTheContentHash()
    {
        byte[] bytes = Bytes(Window - 7, 4);
        string path = TempFile(bytes, Settled);
        try
        {
            DemoContentFingerprint? fp = DemoContentFingerprint.TryCompute(path, TimeProvider.System, windowBytes: Window);
            string content = DemoContentHash.Compute(path);

            await Assert.That(fp).IsNotNull();
            using (Assert.Multiple())
            {
                await Assert.That(fp!.Head).IsEqualTo(content);
                await Assert.That(fp.Tail).IsEqualTo(content);
                await Assert.That(fp.Size).IsEqualTo(bytes.Length);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AnEmptyFile_FingerprintsAsTheEmptyDigestAtBothEnds()
    {
        DemoContentFingerprint fp = DemoContentFingerprint.Compute(new MemoryStream(), Window);

        using (Assert.Multiple())
        {
            await Assert.That(fp.Size).IsEqualTo(0);
            await Assert.That(fp.Head).IsEqualTo(Sha([]));
            await Assert.That(fp.Tail).IsEqualTo(Sha([]));
        }
    }

    [Test]
    public async Task TheCombinedPass_MatchesTheContentHash_AndTheStandaloneFingerprint()
    {
        byte[] bytes = Bytes(3 * Window + 11, 5);
        string path = TempFile(bytes, Settled);
        try
        {
            (string? sha, DemoContentFingerprint? fp) =
                DemoContentFingerprint.TryComputeWithContentHash(path, TimeProvider.System, windowBytes: Window);
            DemoContentFingerprint? alone = DemoContentFingerprint.TryCompute(path, TimeProvider.System, windowBytes: Window);

            using (Assert.Multiple())
            {
                await Assert.That(sha).IsEqualTo(DemoContentHash.Compute(path))
                    .Because("the content id must not depend on which pass produced it");
                await Assert.That(fp).IsNotNull();
                await Assert.That(fp).IsEqualTo(alone);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AFileWrittenInTheSettleWindow_GetsNoFingerprint_ButStillGetsItsHash()
    {
        byte[] bytes = Bytes(2 * Window + 1, 6);
        string path = TempFile(bytes, TimeSpan.Zero);
        try
        {
            (string? sha, DemoContentFingerprint? fp) =
                DemoContentFingerprint.TryComputeWithContentHash(path, TimeProvider.System, windowBytes: Window);

            using (Assert.Multiple())
            {
                await Assert.That(DemoContentFingerprint.TryCompute(path, TimeProvider.System, windowBytes: Window))
                    .IsNull();
                await Assert.That(fp).IsNull();
                await Assert.That(sha).IsEqualTo(DemoContentHash.Compute(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AWriteDuringTheRead_WithholdsTheFingerprint()
    {
        byte[] bytes = Bytes(2 * Window + 1, 7);
        string path = TempFile(bytes, Settled);
        try
        {
            FileStat old = new(bytes.Length, DateTimeOffset.UtcNow - Settled);
            // IsSettled stats twice and the read is bracketed by two more: the last one sees a new write.
            Func<string, FileStat> Moving()
            {
                int calls = 0;
                return _ => ++calls < 4 ? old : old with { LastWriteUtc = DateTimeOffset.UtcNow };
            }

            (string? sha, DemoContentFingerprint? fp) =
                DemoContentFingerprint.TryComputeWithContentHash(path, TimeProvider.System, Moving(), Window);

            using (Assert.Multiple())
            {
                await Assert.That(DemoContentFingerprint.TryCompute(path, TimeProvider.System, Moving(), Window))
                    .IsNull();
                await Assert.That(fp).IsNull();
                await Assert.That(sha).IsEqualTo(DemoContentHash.Compute(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AMissingFile_IsNullEverywhere()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"dv-fingerprint-missing-{Guid.NewGuid():N}.bin");

        (string? sha, DemoContentFingerprint? fp) =
            DemoContentFingerprint.TryComputeWithContentHash(missing, TimeProvider.System);

        using (Assert.Multiple())
        {
            await Assert.That(DemoContentFingerprint.TryCompute(missing, TimeProvider.System)).IsNull();
            await Assert.That(sha).IsNull();
            await Assert.That(fp).IsNull();
        }
    }

    [Test]
    public async Task FingerprintsTakenWithDifferentWindows_NeverMatch()
    {
        byte[] bytes = Bytes(Window / 2, 8);

        DemoContentFingerprint small = DemoContentFingerprint.Compute(new MemoryStream(bytes), Window);
        DemoContentFingerprint large = DemoContentFingerprint.Compute(new MemoryStream(bytes), 2 * Window);

        using (Assert.Multiple())
        {
            await Assert.That(small.Head).IsEqualTo(large.Head)
                .Because("both windows cover the whole file, so only the window tells them apart");
            await Assert.That(small).IsNotEqualTo(large);
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
